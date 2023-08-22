using System.Text.Json.Serialization;

namespace Rinha;

public static class PessoasActions
{
    public static WebApplication MapPessoas(this WebApplication app)
    {
        var logger = app.Logger;
        app.MapPost("/pessoas", (Pessoa pessoa) =>
        {
            logger.Pessoa(pessoa);
            return StatusCodes.Status201Created;
        })
#if DEBUG
            .WithName("Cria pessoas").WithOpenApi();
#else
            ;
#endif
        app.MapGet("/pessoas/{id}", (Guid id) =>
        {
            Console.WriteLine("");
            return new Pessoa(id, "a", "b", new DateOnly(2010, 2, 3), null);
        })
#if DEBUG
            .WithName("Obtem uma pessoa").WithOpenApi();
#else
            ;
#endif
        app.MapGet("/pessoas", (string t) =>
        {
            Console.WriteLine("");
            return new List<Pessoa> { new(Guid.NewGuid(), "a", "b", new DateOnly(2010, 2, 3), null),
                new(Guid.NewGuid(), "a", "b", new DateOnly(2010, 2, 3), null) };
        })
#if DEBUG
            .WithName("Busca pessoas").WithOpenApi();
#else
            ;
#endif
        app.MapGet("/contagem-pessoas", () =>
        {
            Console.WriteLine("");
            return 4;
        })
#if DEBUG
            .WithName("Conta pessoa").WithOpenApi();
#else
            ;
#endif
        return app;
    }
}

public record Pessoa(Guid Id, string Apelido, string Nome, DateOnly Nascimento, string? Stack);

[JsonSerializable(typeof(Pessoa))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(List<Pessoa>))]
internal sealed partial class PessoaContext : JsonSerializerContext { }
