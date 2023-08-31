using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Options;
using Nito.AsyncEx;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace Rinha;

public sealed class CacheService : Cache.CacheBase
{
    private static readonly Empty empty = new();
    private static readonly DateOnly unixEpoch = new(1970, 1, 1);

    public override async Task<Empty> StorePessoa(PessoaRequest pessoaRequest, ServerCallContext context)
    {
        await CacheData.AddAsync(new Pessoa(pessoaRequest.Apelido, pessoaRequest.Nome, unixEpoch.AddDays(pessoaRequest.Nascimento), new(pessoaRequest.Stack))
        {
            Id = new Guid(pessoaRequest.Id.Memory.Span, false)
        }, context.CancellationToken);
        return empty;
    }
}

public sealed class CacheOptions
{
    public Uri? PeerAddress { get; set; }
    public bool Leader { get; set; }
}

public sealed class PeerCacheClient : IDisposable
{
    private static readonly DateOnly unixEpoch = new(1970, 1, 1);
    private readonly GrpcChannel? channel;
    private readonly Cache.CacheClient? client;
    private readonly ILogger<PeerCacheClient> logger;

    public PeerCacheClient(IOptions<CacheOptions> cacheOptions, ILogger<PeerCacheClient> logger)
    {
        if (cacheOptions.Value.PeerAddress is { } peerAddress)
        {
            channel = GrpcChannel.ForAddress(peerAddress, new GrpcChannelOptions
            {
                HttpClient = new HttpClient
                {
                    DefaultRequestVersion = HttpVersion.Version20,
                    DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
                    Timeout = TimeSpan.FromSeconds(10),
                },
                DisposeHttpClient = true
            });
            client = new Cache.CacheClient(channel);
            //AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
            logger.CachePeer(peerAddress);
        }
        else
        {
            logger.NoCachePeer();
        }

        this.logger = logger;
    }

    public ValueTask NotifyNewAsync(Pessoa pessoa) => client is not null ? NotifyNewImplAsync(pessoa) : ValueTask.CompletedTask;

    private async ValueTask NotifyNewImplAsync(Pessoa pessoa)
    {
        PessoaRequest request = new()
        {
            Id = ByteString.CopyFrom(pessoa.Id.ToByteArray(false)),
            Apelido = pessoa.Apelido,
            Nome = pessoa.Nome,
            Nascimento = pessoa.Nascimento.DayNumber - unixEpoch.DayNumber
        };
        request.Stack.AddRange(pessoa.Stack);
        try
        {
            await client!.StorePessoaAsync(request);
        }
        catch (Exception ex)
        {
            logger.CacheDidNotRespond(ex.ToString());
        }
    }

    public void Dispose() => channel?.Dispose();
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
    /// <param name="id"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public static async ValueTask<Pessoa?> GetPessoaAsync(Guid id, CancellationToken cancellationToken)
    {
        using (var _ = await asyncLockForPessoasWriter.WriterLockAsync(cancellationToken))
        {
            var currentCount = pessoasDictionary.Count;
            var arrayCountForCurrent = (int)Math.Floor((double)currentCount / bufferSize);
            var pessoasCreated = numberOfPessoasCreated;
            if (currentCount != pessoasCreated)
            {
                var arrayCountForLast = (int)Math.Floor((double)pessoasCreated / bufferSize);
                var completeArraysCount = arrayCountForLast - arrayCountForCurrent - 1;
                if (completeArraysCount <= 0)
                    completeArraysCount = 0;
                var newPessoas = (arrayCountForCurrent == arrayCountForLast ? pessoas[arrayCountForCurrent].Skip(currentCount % bufferSize).Take((pessoasCreated % bufferSize) - (currentCount % bufferSize)).Where(p => p is not null) : pessoas[arrayCountForCurrent].Skip(currentCount % bufferSize))
                    .Concat(pessoas.Skip(arrayCountForCurrent).Take(completeArraysCount).SelectMany(p => p))
                    .Concat(arrayCountForCurrent == arrayCountForLast ? Array.Empty<Pessoa>() : pessoas.Last().Take(pessoasCreated % bufferSize).Where(p => p is not null));
                foreach (var newPessoa in newPessoas)
                    pessoasDictionary.Add(newPessoa.Id, newPessoa);
            }
        }
        return pessoasDictionary.GetValueOrDefault(id);
    }

    private static void AddToPessoas(Pessoa pessoa)
    {
        var positionInPessoasArray = (int)Math.Floor((numberOfPessoasCreated + 100d) / bufferSize);
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

    public static async ValueTask AddRangeAsync(IAsyncEnumerable<Pessoa> pessoasNovas, CancellationToken cancellationToken)
    {
        await foreach (var pessoa in pessoasNovas.WithCancellation(cancellationToken))
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

    public static int Count() => numberOfPessoasCreated - 1;

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
