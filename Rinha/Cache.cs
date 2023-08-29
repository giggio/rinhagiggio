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

public class CacheOptions
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

    public static async ValueTask AddAsync(Pessoa pessoa, CancellationToken token)
    {
        Debug.Assert(pessoa != null);
        Debug.Assert(pessoa.Apelido != null);
        using var _ = await asyncLock.WriterLockAsync(token);
        if (pessoas.TryAdd(pessoa.Id, pessoa))
            apelidos.Add(pessoa.Apelido);
    }

    public static async ValueTask AddRangeAsync(IAsyncEnumerable<Pessoa> pessoasNovas, CancellationToken token)
    {
        using var _ = await asyncLock.WriterLockAsync(token);
        await foreach (var pessoa in pessoasNovas)
        {
            Debug.Assert(pessoa != null);
            pessoas.Add(pessoa.Id, pessoa);
        }
    }

    public static bool Exists(string apelido) => apelidos.Contains(apelido);

    public static async ValueTask<bool> AnyAsync(Func<Pessoa, bool> predicate, CancellationToken token)
    {
        using var _ = await asyncLock.ReaderLockAsync(token);
        return pessoas.Values.Any(predicate);
    }

    public static async ValueTask<List<Pessoa>> WhereAsync(Func<Pessoa, bool> predicate, int take, CancellationToken token)
    {
        using var _ = await asyncLock.ReaderLockAsync(token);
        return pessoas.Values.Where(predicate)
            .Take(take)
            .ToList();
    }
}
