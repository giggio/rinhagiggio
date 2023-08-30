using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Options;
using Nito.AsyncEx;
using System.Diagnostics;
using System.Net;

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
    private const int initialCapacity = 50_000;
    public static readonly Dictionary<Guid, Pessoa> pessoas = new(initialCapacity);
    public static readonly HashSet<string> apelidos = new(initialCapacity);
    private static readonly AsyncReaderWriterLock asyncLock = new();
    private static IBackgroundTaskQueue? queue;

    public static void SetQueue(IBackgroundTaskQueue queue) => CacheData.queue = queue;

    public static async ValueTask AddAsync(Pessoa pessoa, CancellationToken cancellationToken)
    {
        Debug.Assert(pessoa != null);
        Debug.Assert(pessoa.Apelido != null);
        var success = false;
        using (var _ = await asyncLock.WriterLockAsync(cancellationToken))
        {
            success = pessoas.TryAdd(pessoa.Id, pessoa);
        }
        if (success)
        {
            apelidos.Add(pessoa.Apelido);
            if (queue != null)
                await queue.QueueBackgroundWorkItemAsync(pessoa, cancellationToken);
        }

    }

    public static async ValueTask AddRangeAsync(IAsyncEnumerable<Pessoa> pessoasNovas, CancellationToken cancellationToken)
    {
        using var _ = await asyncLock.WriterLockAsync(cancellationToken);
        await foreach (var pessoa in pessoasNovas.WithCancellation(cancellationToken))
        {
            Debug.Assert(pessoa != null);
            pessoas.Add(pessoa.Id, pessoa);
        }
    }

    public static bool Exists(string apelido) => apelidos.Contains(apelido);

    public static async ValueTask<List<Pessoa>> WhereAsync(Func<Pessoa, bool> predicate, int take, CancellationToken cancellationToken)
    {
        using var _ = await asyncLock.ReaderLockAsync(cancellationToken);
        return pessoas.Values.Where(predicate)
            .Take(take)
            .ToList();
    }

    public static async ValueTask<int> CountAsync(CancellationToken cancellationToken)
    {
        using var _ = await asyncLock.ReaderLockAsync(cancellationToken);
        return pessoas.Values.Count;
    }
}
