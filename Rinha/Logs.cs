namespace Rinha;

public static partial class Logs
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Pessoa: {pessoa}")]
    public static partial void Pessoa(this ILogger logger, Pessoa pessoa);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "Quantidade: {quantidade}")]
    public static partial void CountPessoas(this ILogger logger, int quantidade);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Error creating pessoa: {error}")]
    public static partial void ErrorCreatingPessoa(this ILogger logger, string error);
}
