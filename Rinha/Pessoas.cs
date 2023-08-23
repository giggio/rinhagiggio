using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.OutputCaching;
using Npgsql;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Rinha;

public static class PessoasActions
{
    public static WebApplication MapPessoas(this WebApplication app)
    {
        var logger = app.Logger;
        app.MapPost("/pessoas", async Task<Results<Created, UnprocessableEntity>> (RinhaContext db, IOutputCacheStore cache, Pessoa pessoa) =>
        {
            if (pessoa.Nascimento == default
                || string.IsNullOrWhiteSpace(pessoa.Apelido) || pessoa.Apelido.Length > 32
                || string.IsNullOrWhiteSpace(pessoa.Nome) || pessoa.Nome.Length > 100
                || (pessoa.Stack is not null && pessoa.Stack.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 32)))
            {
                return TypedResults.UnprocessableEntity();
            }

            await cache.EvictByTagAsync("query", default);

            var id = Guid.NewGuid();
            pessoa.Id = id;
            logger.Pessoa(pessoa);
            try
            {
                await db.Pessoas.AddAsync(pessoa);
                await db.SaveChangesAsync();
            }
            catch (PostgresException ex)
            {
                if (ex.SqlState == "23505")
                    return TypedResults.UnprocessableEntity();
            }
            catch (Exception ex)
            {
                logger.ErrorCreatingPessoa(ex.ToString());
                return TypedResults.UnprocessableEntity();
            }
            return TypedResults.Created($"/pessoas/{id}");
        })
#if DEBUG
        .WithName("Cria pessoas").WithOpenApi()
        // todo: remove id from swagger
#endif
        ;

        app.MapGet("/pessoas/{id}", async Task<Results<Ok<Pessoa>, NotFound>> (RinhaContext db, Guid id) =>
        {
            var pessoa = await db.Pessoas.FirstOrDefaultAsync(p => p.Id == id);
            return pessoa is null ? TypedResults.NotFound() : TypedResults.Ok(pessoa);
        }).CacheOutput()
#if DEBUG
        .WithName("Obtem uma pessoa").WithOpenApi()
#endif
        ;

        app.MapGet("/pessoas", async Task<Results<Ok<List<Pessoa>>, BadRequest>> (RinhaContext db, string t) =>
        {
            if (string.IsNullOrWhiteSpace(t))
                return TypedResults.BadRequest();
            var pessoas = await db.Pessoas
                .Where(p => p.Apelido.Contains(t) || p.Nome.Contains(t) || (p.Stack != null && p.Stack.Any(s => s.Contains(t))))
                .OrderBy(p => p.Id)
                .Take(50)
                .ToListAsync();
            logger.CountPessoas(pessoas.Count);
            return TypedResults.Ok(pessoas);
        }).CacheOutput(c => c.SetVaryByQuery("t").Tag("query"))
#if DEBUG
        .WithName("Busca pessoas").WithOpenApi()
#endif
        ;

        app.MapGet("/contagem-pessoas", async (RinhaContext db) => await db.Pessoas.CountAsync())
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
    public RinhaContext(DbContextOptions options) : base(options) { }

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
