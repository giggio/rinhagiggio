using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Rinha;

public static class PessoasActions
{
    public static WebApplication MapPessoas(this WebApplication app)
    {
        var logger = app.Logger;
        app.MapPost("/pessoas", async Task<Results<Created, UnprocessableEntity>> (IServiceProvider provider, CancellationToken token, Pessoa pessoa) =>
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
            var db = provider.GetRequiredService<RinhaContext>();
            try
            {
                if (await db.PessoaWithApelidoExistsAsync(pessoa.Apelido, token))
                    return TypedResults.UnprocessableEntity();
                db.Pessoas.Add(pessoa);
                await db.SaveChangesAsync(token);
            }
            catch (PostgresException ex)
            {
                if (ex.SqlState != "23505")
                    logger.ErrorCreatingPessoa(ex.ToString());
                return TypedResults.UnprocessableEntity();
            }
            catch (Exception ex)
            {
                logger.ErrorCreatingPessoa(ex.ToString());
                return TypedResults.UnprocessableEntity();
            }
            var memoryCache = provider.GetRequiredService<IMemoryCache>();
            memoryCache.Set(id, pessoa, TimeSpan.FromSeconds(10));
            return TypedResults.Created($"/pessoas/{id}");
        })
#if DEBUG
        .WithName("Cria pessoas").WithOpenApi()
        // todo: remove id from swagger
#endif
        ;

        app.MapGet("/pessoas/{id}", async Task<Results<Ok<Pessoa>, NotFound>> (RinhaContext db, IMemoryCache memoryCache, CancellationToken token, Guid id) =>
        {
            if (memoryCache.TryGetValue(id, out Pessoa? pessoa))
                return TypedResults.Ok(pessoa);
            var pessoaFromDb = await db.GetPessoaByIdAsync(id, token);
            return pessoaFromDb is null ? TypedResults.NotFound() : TypedResults.Ok(pessoaFromDb);
        })
#if DEBUG
        .WithName("Obtem uma pessoa").WithOpenApi()
#endif
        ;

        app.MapGet("/pessoas", Results<Ok<IAsyncEnumerable<Pessoa>>, BadRequest> (IServiceProvider provider, string t) =>
        {
            if (string.IsNullOrWhiteSpace(t))
                return TypedResults.BadRequest();
            var db = provider.GetRequiredService<RinhaContext>();
            var pessoas = db.FindPessoas(t);
            return TypedResults.Ok(pessoas);
        })
#if DEBUG
        .WithName("Busca pessoas").WithOpenApi()
#endif
        ;

        app.MapGet("/contagem-pessoas", async (RinhaContext db, CancellationToken token) => await db.Pessoas.CountAsync(token))
#if DEBUG
        .WithName("Conta pessoa").WithOpenApi()
#endif
        ;
        return app;
    }
}

[Index(nameof(Apelido), IsUnique = true)]
public record Pessoa([Required] string Apelido, string Nome, DateOnly Nascimento, List<string>? Stack)
{
    public Guid Id { get; set; }
}

[JsonSerializable(typeof(Pessoa))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(List<Pessoa>))]
internal sealed partial class PessoaJsonContext : JsonSerializerContext { }

internal sealed class RinhaContext : DbContext
{
    public RinhaContext(DbContextOptions options) : base(options)
    {
        ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        ChangeTracker.AutoDetectChangesEnabled = false;
    }

    private static readonly Func<RinhaContext, Guid, CancellationToken, Task<Pessoa?>> getPessoaById =
        EF.CompileAsyncQuery((RinhaContext context, Guid id, CancellationToken token) => context.Pessoas.FirstOrDefault(p => p.Id == id));

    public Task<Pessoa?> GetPessoaByIdAsync(Guid id, CancellationToken token) => getPessoaById(this, id, token);

    private static readonly Func<RinhaContext, string, CancellationToken, Task<bool>> pessoaWithApelidoExists =
        EF.CompileAsyncQuery((RinhaContext context, string apelido, CancellationToken token) => context.Pessoas.Any(p => p.Apelido == apelido));

    public Task<bool> PessoaWithApelidoExistsAsync(string apelido, CancellationToken token) => pessoaWithApelidoExists(this, apelido, token);

    private static readonly Func<RinhaContext, string, IAsyncEnumerable<Pessoa>> findPessoas =
        EF.CompileAsyncQuery((RinhaContext context, string term) =>
            context.Pessoas
            .Where(p => EF.Functions.Like(p.Apelido, term) || EF.Functions.Like(p.Nome, term) || p.Stack!.Any(s => EF.Functions.Like(s, term)))
            .OrderBy(p => p.Id)
            .Take(50));

    public IAsyncEnumerable<Pessoa> FindPessoas(string term) => findPessoas(this, $"%{term}%");

    public required DbSet<Pessoa> Pessoas { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder
            .Entity<Pessoa>()
            .Property(x => x.Apelido)
            .HasMaxLength(32);
        modelBuilder
            .Entity<Pessoa>()
            .Property(x => x.Nome)
            .HasMaxLength(100);
    }
}
