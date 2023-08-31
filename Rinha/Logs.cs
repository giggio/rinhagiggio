namespace Rinha;

public static partial class Logs
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Pessoa: {pessoa}")]
    public static partial void Pessoa(this ILogger logger, Pessoa pessoa);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "Quantidade: {quantidade}")]
    public static partial void CountPessoas(this ILogger logger, int quantidade);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Error creating pessoa: {error}")]
    public static partial void ErrorCreatingPessoa(this ILogger logger, string error);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Waiting for queue to empty. Current items enqueued: {count}.")]
    public static partial void WaitingForQueueToEmpty(this ILogger logger, int count);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "Background processer at a maximum, taking {items} items.")]
    public static partial void MaximumBackgroundBatchNumber(this ILogger logger, int items);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information, Message = "Background processer at a minimum, taking {items} items.")]
    public static partial void MinimumBackgroundBatchNumber(this ILogger logger, int items);

    [LoggerMessage(EventId = 7, Level = LogLevel.Information, Message = "Background processer is taking {items} items{optional}.")]
    public static partial void BackgroundBatchNumber(this ILogger logger, int items, string optional = "");

    [LoggerMessage(EventId = 8, Level = LogLevel.Information, Message = "No cache peer.")]
    public static partial void NoCachePeer(this ILogger logger);

    [LoggerMessage(EventId = 9, Level = LogLevel.Information, Message = "Cache peer is at {uri}.")]
    public static partial void CachePeer(this ILogger logger, Uri uri);

    [LoggerMessage(EventId = 10, Level = LogLevel.Information, Message = "Http endpoint is {httpEndpoint}. Grpc endpoint is {grpcEndpoint}.")]
    public static partial void ServerAddresses(this ILogger logger, string httpEndpoint, string grpcEndpoint);

    [LoggerMessage(EventId = 11, Level = LogLevel.Error, Message = "Http endpoint and/or Grpc endpoint not defined.")]
    public static partial void ServerAddressesNotFound(this ILogger logger);

    [LoggerMessage(EventId = 12, Level = LogLevel.Error, Message = "Could not connect to peer cache ({message}), got exception:\n{exceptionMessage}.")]
    public static partial void CacheDidNotRespond(this ILogger logger, string message, string exceptionMessage);

    [LoggerMessage(EventId = 13, Level = LogLevel.Trace, Message = "Buffering new item.")]
    public static partial void BufferingNewItem(this ILogger logger);

    [LoggerMessage(EventId = 14, Level = LogLevel.Debug, Message = "Writing {count} items to a collection in the queue.")]
    public static partial void AddingItemsToQueue(this ILogger logger, int count);

    [LoggerMessage(EventId = 15, Level = LogLevel.Debug, Message = "Flushing queue with {count} items.")]
    public static partial void FlushingQueue(this ILogger logger, int count);

    [LoggerMessage(EventId = 17, Level = LogLevel.Trace, Message = "Queue worker saving {count} items to DB.")]
    public static partial void SavingToDb(this ILogger logger, int count);

    [LoggerMessage(EventId = 18, Level = LogLevel.Trace, Message = "Queue was read synchronously ({quantityRead} items in collection). There were {count} collections in the queue.")]
    public static partial void QueueReadSynchronously(this ILogger logger, int quantityRead, int count);

    [LoggerMessage(EventId = 19, Level = LogLevel.Trace, Message = "Queue was read asynchronously ({quantityRead} items in collection). There were {count} collections in the queue.")]
    public static partial void QueueReadAsynchronously(this ILogger logger, int quantityRead, int count);

    [LoggerMessage(EventId = 20, Level = LogLevel.Error, Message = "Got unhandled exception at url {url}:\n{exceptionMessage}.")]
    public static partial void AppError(this ILogger logger, string url, string exceptionMessage);

    [LoggerMessage(EventId = 21, Level = LogLevel.Error, Message = "When saving to Db expected {rowsExpected}, but got {rowsSaved}.")]
    public static partial void DbWrongRowCountOnInsert(this ILogger logger, int rowsExpected, int rowsSaved);

    [LoggerMessage(EventId = 22, Level = LogLevel.Trace, Message = "Inserted {rowsCount} rows into database. Details:\n{details}")]
    public static partial void DbInserted(this ILogger logger, int rowsCount, string? details);

    [LoggerMessage(EventId = 23, Level = LogLevel.Trace, Message = "Pool of {typeName} rented an item, has {itemsCount} before renting.")]
    public static partial void PoolRentingItem(this ILogger logger, string typeName, int itemsCount);

    [LoggerMessage(EventId = 24, Level = LogLevel.Trace, Message = "Pool of {typeName} returned an item, had {itemsCount} after return.")]
    public static partial void PoolReturnedItem(this ILogger logger, string typeName, int itemsCount);

    [LoggerMessage(EventId = 25, Level = LogLevel.Information, Message = "Pool of {typeName} returning all items, had {itemsCount}.")]
    public static partial void PoolReturningAllItems(this ILogger logger, string typeName, int itemsCount);

    [LoggerMessage(EventId = 26, Level = LogLevel.Information, Message = "Pool of {typeName} created with {itemsCount}.")]
    public static partial void PoolCreated(this ILogger logger, string typeName, int itemsCount);

    [LoggerMessage(EventId = 27, Level = LogLevel.Information, Message = "Is leader: {isLeader}.")]
    public static partial void IsLeader(this ILogger logger, bool isLeader);
}

public sealed class AppLogs { }
