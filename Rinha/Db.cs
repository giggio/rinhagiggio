using Microsoft.Extensions.Options;
using Nito.AsyncEx;
using Npgsql;
using System.Data;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Rinha;

public sealed class Db : IAsyncDisposable
{
    private readonly Pool<NpgsqlConnection> connectionPool;
    private const int maxNumberOfPessoas = 200;
    private readonly Pool<NpgsqlCommand>[] commandsPool = new Pool<NpgsqlCommand>[maxNumberOfPessoas];
    private readonly ILogger<Db> logger;
    private bool disposed;
    public Db(IOptions<DbConfig> configOption, ILogger<Db> logger, ILoggerFactory loggerFactory)
    {
        this.logger = logger;
        CreateCommands(loggerFactory);
        connectionPool = CreateConnections(configOption.Value, loggerFactory);
    }

    private static Pool<NpgsqlConnection> CreateConnections(DbConfig config, ILoggerFactory loggerFactory)
    {
        var connections = new List<NpgsqlConnection>(config.PoolSize);
        for (var i = 0; i < config.PoolSize; i++)
        {
            var conn = new NpgsqlConnection(config.ConnectionString);
            conn.Open();
            connections.Add(conn);
        }
        return new Pool<NpgsqlConnection>(connections, loggerFactory.CreateLogger<Pool<NpgsqlConnection>>());
    }

    private void CreateCommands(ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger<Pool<NpgsqlCommand>>();
        const int numberOfCommandsPerPoolForMultiplesOf2 = 20;
        const int numberOfCommandsPerPoolForNotMultiplesOf2 = 5;
        const string baseCommandText = """INSERT INTO "Pessoas" ("Id", "Apelido", "Nome", "Nascimento", "Stack") VALUES """;
        const string valuesText = " ($1, $2, $3, $4, $5)";
        for (var commandsPoolIndex = 0; commandsPoolIndex < maxNumberOfPessoas; commandsPoolIndex++)
        {
            var numberOfCommandsPerPool = commandsPoolIndex % 2 == 0 ? numberOfCommandsPerPoolForMultiplesOf2 : numberOfCommandsPerPoolForNotMultiplesOf2;
            var commands = new List<NpgsqlCommand>(numberOfCommandsPerPool);
            var command = new NpgsqlCommand(baseCommandText + string.Join(",", Enumerable.Repeat(valuesText, commandsPoolIndex + 1)));
            var commandTextBuilder = new StringBuilder(baseCommandText, (27 * (commandsPoolIndex + 1)) + baseCommandText.Length);
            for (var paramIndex = 0; paramIndex < commandsPoolIndex + 1; paramIndex++)
            {
                commandTextBuilder.Append("($");
                var baseParamPosition = 5 * paramIndex;
                commandTextBuilder.Append(baseParamPosition + 1);
                commandTextBuilder.Append(",$");
                commandTextBuilder.Append(baseParamPosition + 2);
                commandTextBuilder.Append(",$");
                commandTextBuilder.Append(baseParamPosition + 3);
                commandTextBuilder.Append(",$");
                commandTextBuilder.Append(baseParamPosition + 4);
                commandTextBuilder.Append(",$");
                commandTextBuilder.Append(baseParamPosition + 5);
                commandTextBuilder.Append("),");
                command.Parameters.Add(new NpgsqlParameter<Guid>() { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid });
                command.Parameters.Add(new NpgsqlParameter<string>() { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Varchar });
                command.Parameters.Add(new NpgsqlParameter<string>() { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Varchar });
                command.Parameters.Add(new NpgsqlParameter<DateOnly>() { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Date });
                command.Parameters.Add(new NpgsqlParameter<List<string>>() { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Varchar });
            }
            commandTextBuilder.Remove(commandTextBuilder.Length - 1, 1);
            command.CommandText = commandTextBuilder.ToString();
            commands.Add(command);
            for (var k = 0; k < numberOfCommandsPerPool - 1; k++)
                commands.Add(command.Clone());
            commandsPool[commandsPoolIndex] = new(commands, logger);
        }
    }

    public async Task AddAsync(Pessoa[] pessoas, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await using var connectionsPoolItem = await connectionPool.RentAsync(cancellationToken);
        var connection = connectionsPoolItem.Value;
        Debug.Assert(connection.State == ConnectionState.Open);
        var numberOfSteps = (int)Math.Ceiling((double)pessoas.Length / maxNumberOfPessoas);
        var step = 0;
        while (step < numberOfSteps)
        {
            var pessoasToInsert = pessoas.Skip(step++ * maxNumberOfPessoas).Take(maxNumberOfPessoas).ToArray();
            await using var commandPoolItem = await commandsPool[pessoasToInsert.Length - 1].RentAsync(cancellationToken);
            var command = commandPoolItem.Value;
            command.Connection = connection;
            for (var i = 0; i < pessoasToInsert.Length; i++)
            {
                var pessoa = pessoasToInsert[i];
                var baseIndex = i * 5;
                command.Parameters[baseIndex].Value = pessoa.Id;
                command.Parameters[baseIndex + 1].Value = pessoa.Apelido;
                command.Parameters[baseIndex + 2].Value = pessoa.Nome;
                command.Parameters[baseIndex + 3].Value = pessoa.Nascimento;
                command.Parameters[baseIndex + 4].Value = pessoa.Stack;
            }
            int rowCount;
            try
            {
                rowCount = await command.ExecuteNonQueryAsync(cancellationToken);
            }
            finally
            {
                command.Connection = null;
            }
            if (rowCount != pessoasToInsert.Length)
                logger.DbWrongRowCountOnInsert(rowCount, pessoasToInsert.Length);
            else
#if DEBUG
                logger.DbInserted(pessoasToInsert.Length, pessoas.Select(p => $"Id: {p.Id}, Apelido: {p.Apelido}").Aggregate("", (acc, value) => $"{acc}\n{value}"));
#else
                logger.DbInserted(pessoasToInsert.Length, null);
#endif
        }
    }

    public async IAsyncEnumerable<Pessoa> GetAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await using var connectionsPoolItem = await connectionPool.RentAsync(cancellationToken);
        var connection = connectionsPoolItem.Value;
        using var command = connection.CreateCommand();
        command.CommandText = """SELECT "Id","Apelido","Nome","Nascimento","Stack" FROM "Pessoas" """;
        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var pessoa = new Pessoa(reader.GetString(1), reader.GetString(2), reader.GetFieldValue<DateOnly>(3), reader.IsDBNull(4) ? null : reader.GetFieldValue<List<string>?>(4))
                {
                    Id = reader.GetGuid(0)
                };
                yield return pessoa;
            }
        }
        command.Connection = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        disposed = true;
        if (commandsPool is not null)
        {
            foreach (var commandPool in commandsPool)
            {
                var commands = await commandPool.ReturnAllAsync(CancellationToken.None);
                foreach (var command in commands)
                    command.Dispose();
            }
        }
        if (connectionPool is not null)
        {
            var connections = await connectionPool.ReturnAllAsync(CancellationToken.None);
            await Parallel.ForEachAsync(connections, (conn, _) => conn.DisposeAsync());
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, nameof(Db));

}

public sealed class Pool<T> where T : class
{
    private readonly Queue<T> queue;
    private readonly int poolSize;

    public Pool(IEnumerable<T> items, ILogger<Pool<T>> logger)
    {
        this.logger = logger;
        queue = new Queue<T>(items);
        poolSize = queue.Count;
        Debug.Assert(poolSize > 0);
        newItemEnquedSemaphore = new(poolSize);
        typeName = typeof(T).Name;
        logger.PoolCreated(typeName, poolSize);
    }

    private readonly AsyncLock mutex = new();
    private readonly SemaphoreSlim newItemEnquedSemaphore;
    private readonly string typeName;
    private readonly ILogger<Pool<T>> logger;

    public async ValueTask<PoolItem<T>> RentAsync(CancellationToken cancellationToken)
    {
        T? item = null;
        await newItemEnquedSemaphore.WaitAsync(cancellationToken);
        try
        {
            using (var _ = await mutex.LockAsync(cancellationToken))
            {
                logger.PoolRentingItem(typeName, queue.Count);
                item = queue.Dequeue();
            }
            var poolItem = new PoolItem<T>(item, ReturnPoolItemAsync);
            return poolItem;
        }
        catch
        {
            if (item != null)
            {
                using (var _ = await mutex.LockAsync(cancellationToken))
                    queue.Enqueue(item);
                newItemEnquedSemaphore.Release();
            }
            throw;
        }
    }

    public async ValueTask<List<T>> ReturnAllAsync(CancellationToken cancellationToken)
    {
        logger.PoolReturningAllItems(typeName, queue.Count);
        await Task.WhenAll(Enumerable.Range(1, poolSize).Select(_ => newItemEnquedSemaphore.WaitAsync(cancellationToken)));
        using var _ = await mutex.LockAsync(cancellationToken);
        var items = new List<T>();
        while (queue.TryDequeue(out var item))
            items.Add(item);
        return items;
    }

    private async ValueTask ReturnPoolItemAsync(PoolItem<T> poolItem)
    {
        using (var _ = await mutex.LockAsync())
            queue.Enqueue(poolItem.Value);
        logger.PoolReturnedItem(typeName, queue.Count);
        newItemEnquedSemaphore.Release();
    }

    public readonly struct PoolItem<TItem>
    {
        private readonly Func<PoolItem<TItem>, ValueTask> returnPoolItemAsync;

        public PoolItem(TItem value, Func<PoolItem<TItem>, ValueTask> returnPoolItemAsync)
        {
            Value = value;
            this.returnPoolItemAsync = returnPoolItemAsync;
        }

        public TItem Value { get; }

        public ValueTask DisposeAsync() => returnPoolItemAsync(this);
    }
}

public sealed class DbConfig
{
    public int PoolSize
    {
        get
        {
            if (ConnectionString is null)
                return 0;
            var connBuilder = new NpgsqlConnectionStringBuilder(ConnectionString);
            return !connBuilder.Pooling ? 0 : connBuilder.MaxPoolSize;
        }
    }
    public string? ConnectionString { get; internal set; }
}