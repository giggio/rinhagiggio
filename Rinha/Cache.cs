using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.Client.Configuration;
using Microsoft.Extensions.Options;
using Nito.AsyncEx;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace Rinha;

public sealed class CacheService : Cache.CacheBase
{
    private readonly CancellationToken stoppingToken;
    private readonly Db db;
    private readonly CacheQueue cacheQueue;
    private readonly ILogger logger;

    public CacheService(Db db, IHostApplicationLifetime lifetime, CacheQueue cacheQueue, ILogger<CacheService> logger)
    {
        this.db = db;
        this.cacheQueue = cacheQueue;
        this.logger = logger;
        stoppingToken = lifetime.ApplicationStopping;
    }

    public override async Task StorePessoa(IAsyncStreamReader<PessoaRequest> requestStream, IServerStreamWriter<PessoaRequest> responseStream, ServerCallContext context)
    {
        logger.CacheServerConnected();
        var cancellationToken = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, context.CancellationToken).Token;
        var responseStreamTask = Task.Run(async () =>
        {
            while (!cancellationToken.IsCancellationRequested) // todo: evaluate if we should stop writing to the response steam in other events, for example, if somehow the stream is closed
            {
                try
                {
                    var pessoa = await cacheQueue.DequeueAsync(cancellationToken);
                    logger.CacheServerSendingPessoa(pessoa.Id.ToString());
                    await responseStream.WriteAsync(pessoa.ToPessoaRequest(), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    logger.CacheServerOperationCancelled();
                }
                catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
                {
                    logger.CacheServerClientCancelled("StorePessoaServerResponseMessage", ex);
                }
                catch (Exception ex)
                {
                    logger.CacheClientDidNotRespond("StorePessoaServerResponseMessage", ex);
                }
            }
        }, cancellationToken);
        try
        {
            await foreach (var pessoaRequest in requestStream.ReadAllAsync<PessoaRequest>(cancellationToken))
            {
                var pessoa = pessoaRequest.ToPessoa();
                logger.CacheServerReceivedPessoa(pessoa.Id.ToString());
                await CacheData.AddAsync(pessoa, stoppingToken);
            }
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
            logger.CacheServerClientCancelled("StorePessoaServerRequestMessage", ex);
        }
        catch (Exception ex)
        {
            logger.CacheClientDidNotRespond("StorePessoaServerRequestMessage", ex);
        }
        await responseStreamTask;
    }

    public override async Task<CountResponse> CountPessoas(Empty _, ServerCallContext context)
    {
        var count = await db.GetCountAsync(stoppingToken);
        logger.CacheServerCount(count);
        return new CountResponse { Count = count };
    }
}

public sealed class CacheOptions
{
    public Uri? PeerAddress { get; set; }
    public bool Leader { get; set; }
}

public sealed class PeerCacheClient : IDisposable
{
    private readonly GrpcChannel? channel;
    private readonly Cache.CacheClient? cacheClient;
    private readonly CacheQueue cacheQueue;
    private readonly ILogger<PeerCacheClient> logger;

    public PeerCacheClient(IOptions<CacheOptions> cacheOptions, CacheQueue cacheQueue, ILogger<PeerCacheClient> logger)
    {
        if (cacheOptions.Value.PeerAddress is { } peerAddress)
        {
            channel = GrpcChannel.ForAddress(peerAddress, new GrpcChannelOptions
            {
                HttpClient = new HttpClient
                {
                    DefaultRequestVersion = HttpVersion.Version20,
                    DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
                },
                DisposeHttpClient = true,
                ServiceConfig = new ServiceConfig
                {
                    MethodConfigs =
                    {
                        new MethodConfig
                        {
                            Names = { MethodName.Default },
                            RetryPolicy = new RetryPolicy
                            {
                                MaxAttempts = 5,
                                InitialBackoff = TimeSpan.FromSeconds(1),
                                MaxBackoff = TimeSpan.FromSeconds(10),
                                BackoffMultiplier = 1.5,
                                RetryableStatusCodes = { StatusCode.Unavailable }
                            }
                        }
                    }
                }
            });
            cacheClient = new Cache.CacheClient(channel);
            logger.CachePeer(peerAddress);
        }
        else
        {
            logger.NoCachePeer();
        }

        this.cacheQueue = cacheQueue;
        this.logger = logger;
    }

    public async ValueTask SendPessoasToPeerAsync(CancellationToken cancellationToken)
    {
        if (cacheClient is null)
            return;
        while (!cancellationToken.IsCancellationRequested)
        {
            AsyncDuplexStreamingCall<PessoaRequest, PessoaRequest>? stream = null;
            try
            {
                stream = cacheClient.StorePessoa(cancellationToken: cancellationToken);
                logger.CacheClientConnected();
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
            {
                if (cancellationToken.IsCancellationRequested)
                    logger.CacheClientClientCancelled("StorePessoaStream");
                else
                    logger.CacheClientServerCancelled("StorePessoaStream", ex);
            }
            catch (Exception ex)
            {
                logger.CacheServerDidNotRespond("StorePessoaStream", ex);
            }
            if (stream is null)
                continue;
            var responseTask = Task.Run(async () =>
            {
                while (!cancellationToken.IsCancellationRequested) // todo: evaluate if we should stop listing to the response steam in other events, for example, if somehow the stream is closed
                {
                    try
                    {
                        await foreach (var pessoaRequest in stream.ResponseStream.ReadAllAsync(cancellationToken))
                        {
                            var pessoaFromResponse = pessoaRequest.ToPessoa();
                            logger.CacheClientReceivedPessoa(pessoaFromResponse.Id.ToString());
                            await CacheData.AddAsync(pessoaFromResponse, cancellationToken);
                        }
                    }
                    catch (OperationCanceledException ex)
                    {
                        logger.CacheClientOperationCancelled(ex);
                    }
                    catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
                    {
                        if (cancellationToken.IsCancellationRequested)
                            logger.CacheClientClientCancelled("StorePessoaClientResponseMessage");
                        else
                            logger.CacheClientServerCancelled("StorePessoaClientResponseMessage", ex);
                    }
                    catch (Exception ex)
                    {
                        logger.CacheServerDidNotRespond("StorePessoaClientResponseMessage", ex);
                    }
                }
            }, CancellationToken.None);
            while (!cancellationToken.IsCancellationRequested) // todo: evaluate if we should stop sending requests in other events, for example, if somehow the stream is closed
            {
                try
                {
                    var pessoa = await cacheQueue.DequeueAsync(cancellationToken);
                    logger.CacheClientSendingPessoa(pessoa.Id.ToString());
                    var request = pessoa.ToPessoaRequest();
                    await stream.RequestStream.WriteAsync(request, cancellationToken);
                }
                catch (OperationCanceledException ex)
                {
                    logger.CacheClientOperationCancelled(ex);
                }
                catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
                {
                    if (cancellationToken.IsCancellationRequested)
                        logger.CacheClientClientCancelled("StorePessoaClientRequestMessage");
                    else
                        logger.CacheClientServerCancelled("StorePessoaClientRequestMessage", ex);
                    break;
                }
                catch (Exception ex)
                {
                    logger.CacheServerDidNotRespond("StorePessoaClientRequestMessage", ex);
                }
            }
            await Task.WhenAny(stream.RequestStream.CompleteAsync(), Task.Delay(3000, CancellationToken.None));
            stream.Dispose();
            await responseTask;
        }
    }

    public async ValueTask<int> CountAsync(CancellationToken cancellationToken)
    {
        try
        {
            return (await cacheClient!.CountPessoasAsync(new(), cancellationToken: cancellationToken, deadline: DateTime.UtcNow.AddSeconds(30))).Count;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
            if (cancellationToken.IsCancellationRequested)
                logger.CacheClientClientCancelled("Count");
            else
                logger.CacheClientServerCancelled("Count", ex);
            throw;
        }
        catch (Exception ex)
        {
            logger.CacheServerDidNotRespond("Count", ex);
            throw;
        }
    }

    public void Dispose() => channel?.Dispose();
}

public sealed class CacheStreamingWorker : BackgroundService
{
    private readonly PeerCacheClient peerCacheClient;
    private readonly ILogger<CacheStreamingWorker> logger;

    public CacheStreamingWorker(PeerCacheClient peerCacheClient, ILogger<CacheStreamingWorker> logger)
    {
        this.peerCacheClient = peerCacheClient;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            logger.StreamingWorkerStarting();
            try
            {
                await peerCacheClient.SendPessoasToPeerAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.StreamingWorkerError(ex);
            }
        }
    }
}

public class CacheQueue
{
    private readonly Channel<Pessoa> queue = Channel.CreateUnbounded<Pessoa>(new UnboundedChannelOptions()
    {
        AllowSynchronousContinuations = false,
        SingleReader = true,
        SingleWriter = false
    });

    public ValueTask<Pessoa> DequeueAsync(CancellationToken cancellationToken) => queue.Reader.ReadAsync(cancellationToken);

    public ValueTask EnqueueAsync(Pessoa pessoa, CancellationToken cancellationToken) => queue.Writer.WriteAsync(pessoa, cancellationToken);

    public async ValueTask EnqueueAsync(List<Pessoa> pessoas, CancellationToken cancellationToken) =>
        await Task.WhenAll(pessoas.Select(pessoa => queue.Writer.WriteAsync(pessoa, cancellationToken).AsTask()));
}

public static class CacheData
{
    private const int initialCapacity = 20_000;
    private static readonly Dictionary<Guid, Pessoa> pessoasDictionary = new(initialCapacity, GuidEqualityComparer.Default);
    public static readonly HashSet<string> apelidos = new(initialCapacity);
    private static readonly AsyncReaderWriterLock asyncLockForPessoasWriter = new();
    private static IBackgroundTaskQueue? queue;
    private const int bufferSize = 20_000;
    private static readonly List<Pessoa[]> pessoas = new(new[] { new Pessoa[bufferSize] });
    private static int numberOfPessoasCreated;

    public static void SetQueue(IBackgroundTaskQueue queue) => CacheData.queue = queue;

    public static async ValueTask AddAsync(Pessoa pessoa, CancellationToken cancellationToken)
    {
        Debug.Assert(pessoa != null);
        Debug.Assert(pessoa.Apelido != null);
        apelidos.Add(pessoa.Apelido);
        AddToPessoas(pessoa);
        if (queue != null)
            await queue.QueueBackgroundWorkItemAsync(pessoa, cancellationToken);
    }

    /// <summary>
    /// Gets all pessoas from all buckets.
    /// </summary>
    /// <remarks>
    /// This is how this is working:
    ///
    /// currently inserted: 0 (0)
    /// last inserted: 40_005 (3)
    /// take partial (start): 0
    /// take full: 1, 2
    /// take partial (end): 3
    ///
    /// currently inserted: 40_003 (3)
    /// last inserted: 40_005 (3)
    /// take partial (start): 3
    /// take full: null
    /// take partial (end): null
    ///
    /// currently inserted: 40_003 (3)
    /// last inserted: 60_005 (4)
    /// take partial (start): 3
    /// take full: null
    /// take partial (end): 4
    ///
    /// currently inserted: 40_003 (3)
    /// last inserted: 80_005 (5)
    /// take partial (start): 3
    /// take full: 4
    /// take partial (end): 5
    ///
    /// currently inserted: 40_003 (3)
    /// last inserted: 100_003 (6)
    /// take partial (start): 3
    /// take full: 4, 5
    /// take partial (end): 6
    /// </remarks>
    public static async ValueTask<Pessoa?> GetPessoaAsync(Guid id, CancellationToken cancellationToken)
    {
        using (var _ = await asyncLockForPessoasWriter.WriterLockAsync(cancellationToken))
        {
            var currentCount = pessoasDictionary.Count;
            var pessoasCreated = numberOfPessoasCreated;
            if (currentCount != pessoasCreated)
            {
                var arrayIndexForCurrent = (int)Math.Floor((double)currentCount / bufferSize);
                var arrayIndexForLast = (int)Math.Floor((double)pessoasCreated / bufferSize);
                var completeArraysCount = arrayIndexForLast - arrayIndexForCurrent - 1;
                if (completeArraysCount <= 0)
                    completeArraysCount = 0;
                var newPessoas = (arrayIndexForCurrent == arrayIndexForLast ? pessoas[arrayIndexForCurrent].Skip(currentCount % bufferSize).Take((pessoasCreated % bufferSize) - (currentCount % bufferSize)).Where(p => p is not null) : pessoas[arrayIndexForCurrent].Skip(currentCount % bufferSize))
                    .Concat(pessoas.Skip(arrayIndexForCurrent + 1).Take(completeArraysCount).SelectMany(p => p))
                    .Concat(arrayIndexForCurrent == arrayIndexForLast ? Array.Empty<Pessoa>() : pessoas.Last().Take(pessoasCreated % bufferSize).Where(p => p is not null));
                foreach (var newPessoa in newPessoas)
                    pessoasDictionary.Add(newPessoa.Id, newPessoa);
            }
            Debug.Assert(pessoasDictionary.Count == numberOfPessoasCreated);
        }
        return pessoasDictionary.GetValueOrDefault(id);
    }

    private static void AddToPessoas(Pessoa pessoa)
    {
        var positionInPessoasArray = (int)Math.Floor(((double)numberOfPessoasCreated) / bufferSize);
        if (pessoas.Count < positionInPessoasArray + 1)
        {
            lock (pessoas)
            {
                if (pessoas.Count < positionInPessoasArray + 1)
                    pessoas.Add(new Pessoa[bufferSize]);
            }
        }
        var indexForNewPessoa = (Interlocked.Increment(ref numberOfPessoasCreated) - 1) % bufferSize;
        pessoas[positionInPessoasArray][indexForNewPessoa] = pessoa;
    }

    public static void AddRange(List<Pessoa> pessoasNovas)
    {
        foreach (var pessoa in pessoasNovas)
        {
            Debug.Assert(pessoa != null);
            apelidos.Add(pessoa.Apelido);
            AddToPessoas(pessoa);
        }
    }

    public static bool Exists(string apelido) => apelidos.Contains(apelido);

    public static List<Pessoa> Where(Func<Pessoa, bool> predicate, int take)
    {
        var last = numberOfPessoasCreated - 1;
        var arrayCount = (int)Math.Floor((double)last / bufferSize);
        return pessoas.Take(arrayCount).SelectMany(p => p).Where(predicate)
            .Concat(pessoas.Last().Take(last % bufferSize).Where(predicate)).Take(take).ToList();
    }

    public static int Count() => numberOfPessoasCreated;

    private sealed class GuidEqualityComparer : IEqualityComparer<Guid> // adapted from https://stackoverflow.com/a/31580481/2723305, but using Span so we don't allocate
    {
        public static readonly GuidEqualityComparer Default = new();

        public bool Equals(Guid x, Guid y) => x.Equals(y);

        public int GetHashCode(Guid guid)
        {
            var bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref guid, 1));

            var hash1 = bytes[0] | ((uint)bytes[1] << 8) | ((uint)bytes[2] << 16) | ((uint)bytes[3] << 24);
            var hash2 = bytes[4] | ((uint)bytes[5] << 8) | ((uint)bytes[6] << 16) | ((uint)bytes[7] << 24);
            var hash3 = bytes[8] | ((uint)bytes[9] << 8) | ((uint)bytes[10] << 16) | ((uint)bytes[11] << 24);
            var hash4 = bytes[12] | ((uint)bytes[13] << 8) | ((uint)bytes[14] << 16) | ((uint)bytes[15] << 24);

            var hash = 37;

            unchecked
            {
                hash = (hash * 23) + (int)hash1;
                hash = (hash * 23) + (int)hash2;
                hash = (hash * 23) + (int)hash3;
                hash = (hash * 23) + (int)hash4;
            }

            return hash;
        }
    }
}

public static class AsyncStreamReaderExtensions
{
    private static readonly DateOnly unixEpoch = new(1970, 1, 1);
    public static PessoaRequest ToPessoaRequest(this Pessoa pessoa)
    {
        PessoaRequest request = new()
        {
            Id = ByteString.CopyFrom(pessoa.Id.ToByteArray(false)),
            Apelido = pessoa.Apelido,
            Nome = pessoa.Nome,
            Nascimento = pessoa.Nascimento.DayNumber - unixEpoch.DayNumber
        };
        if (pessoa.Stack is null)
            request.StackNull = true;
        else
            request.Stack.AddRange(pessoa.Stack);
        return request;
    }

    public static Pessoa ToPessoa(this PessoaRequest pessoaRequest)
    {
        var pessoa = new Pessoa(pessoaRequest.Apelido, pessoaRequest.Nome, unixEpoch.AddDays(pessoaRequest.Nascimento), pessoaRequest.StackNull ? null : new(pessoaRequest.Stack))
        {
            Id = new Guid(pessoaRequest.Id.Memory.Span, false)
        };
        return pessoa;
    }
}

