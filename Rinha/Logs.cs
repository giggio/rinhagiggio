namespace Rinha;

public static partial class Logs
{
    [LoggerMessage(EventId = 0, Level = LogLevel.Information, Message = "Pessoa: {pessoa}")]
    public static partial void Pessoa(this ILogger logger, Pessoa pessoa);
}
