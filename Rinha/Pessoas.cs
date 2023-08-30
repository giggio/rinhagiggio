using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Rinha;

public static class PessoasActions
{
    public static WebApplication MapPessoas(this WebApplication app)
    {
        var logger = app.Logger;
        app.MapPost("/pessoas", async ValueTask<Results<Created, UnprocessableEntity>> (IServiceProvider provider, CancellationToken cancellationToken, Pessoa pessoa) =>
        {
            if (pessoa.Nascimento == default
                || string.IsNullOrWhiteSpace(pessoa.Apelido) || pessoa.Apelido.Length > 32
                || string.IsNullOrWhiteSpace(pessoa.Nome) || pessoa.Nome.Length > 100
                || (pessoa.Stack is not null && pessoa.Stack.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 32)))
            {
                return TypedResults.UnprocessableEntity();
            }

            var id = Guid.NewGuid();
            pessoa.Id = id;
            logger.Pessoa(pessoa);

            if (CacheData.Exists(pessoa.Apelido))
                return TypedResults.UnprocessableEntity();
            var cacheClient = provider.GetRequiredService<PeerCacheClient>();
            await cacheClient.NotifyNewAsync(pessoa);
            await CacheData.AddAsync(pessoa, cancellationToken);
            return TypedResults.Created($"/pessoas/{id}");
        })
#if DEBUG
        .WithName("Cria pessoas").WithOpenApi()
#endif
        ;

        app.MapGet("/pessoas/{id}", Results<Ok<Pessoa>, NotFound> (Guid id) =>
            CacheData.pessoas.TryGetValue(id, out var pessoa) ? TypedResults.Ok(pessoa!) : TypedResults.NotFound())
#if DEBUG
        .WithName("Obtem uma pessoa").WithOpenApi()
#endif
        ;

        app.MapGet("/pessoas", async ValueTask<Results<Ok<List<Pessoa>>, BadRequest>> (string? t, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(t))
                return TypedResults.BadRequest();
            var pessoas = await CacheData.WhereAsync(p => p.Apelido.Contains(t) || p.Nome.Contains(t) || (p.Stack is not null && p.Stack.Any(s => s.Contains(t))), 50, cancellationToken);
            return TypedResults.Ok(pessoas);
        })
#if DEBUG
        .WithName("Busca pessoas").WithOpenApi()
#endif
        ;

        app.MapGet("/contagem-pessoas", async (IOptions<CacheOptions> cacheOptions, IBackgroundTaskQueue queue, CancellationToken cancellationToken) =>
        {
            var cancellationTokenSource = new CancellationTokenSource(10_000);
            if (cacheOptions.Value.Leader)
            {
                try
                { await queue.FlushAsyncAndWaitToDrainAsync(cancellationTokenSource.Token); }
                catch { }
            }
            return await CacheData.CountAsync(cancellationToken);
        })
#if DEBUG
        .WithName("Conta pessoa").WithOpenApi()
#endif
        ;
        return app;
    }
}

public sealed record class Pessoa([Required] string Apelido, string Nome, DateOnly Nascimento, List<string>? Stack)
{
#if DEBUG
    [Swashbuckle.AspNetCore.Annotations.SwaggerSchema(ReadOnly = true)]
#endif
    public Guid Id { get; set; }
}

[JsonSerializable(typeof(Pessoa))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(List<Pessoa>))]
internal sealed partial class PessoaJsonContext : JsonSerializerContext { }

//internal sealed class RinhaContext : DbContext
//{
//    public RinhaContext(DbContextOptions options) : base(options) => ChangeTracker.AutoDetectChangesEnabled = false;

//    private static readonly Func<RinhaContext, IAsyncEnumerable<Pessoa>> getAll = EF.CompileAsyncQuery((RinhaContext context) => context.Pessoas);

//    public IAsyncEnumerable<Pessoa> GetAll() => getAll(this);

//    public required DbSet<Pessoa> Pessoas { get; set; }

//    protected override void OnModelCreating(ModelBuilder modelBuilder)
//    {
//        modelBuilder
//            .Entity<Pessoa>()
//            .Property(x => x.Apelido)
//            .HasMaxLength(32);
//        modelBuilder
//            .Entity<Pessoa>()
//            .Property(x => x.Nome)
//            .HasMaxLength(100);
//    }
//}
