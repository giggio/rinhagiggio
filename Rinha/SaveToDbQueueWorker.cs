using System.Threading.Channels;

namespace Rinha;

internal sealed class SaveToDbQueueWorker : BackgroundService, IDisposable
{
    private readonly ILogger<SaveToDbQueueWorker> logger;
    private readonly IDbContextFactory<RinhaContext> dbContextFactory;
    private readonly IBackgroundTaskQueue taskQueue;

    public SaveToDbQueueWorker(IBackgroundTaskQueue taskQueue, IDbContextFactory<RinhaContext> dbContextFactory, ILogger<SaveToDbQueueWorker> logger)
    {
        this.taskQueue = taskQueue;
        this.logger = logger;
        this.dbContextFactory = dbContextFactory;
    }

    private sbyte numberWaited;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var forcedFlush = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var dequeueTask = taskQueue.DequeueAsync(cancellationTokenSource.Token);
            List<Pessoa>? pessoas = null;
            if (dequeueTask.IsCompletedSuccessfully)
            {
                pessoas = dequeueTask.Result;
                logger.QueueReadSynchronously(pessoas.Count, taskQueue.QueuedItemsCount);
                // dequeue completed synchronously, increase the number of items batched per call
                if (forcedFlush)
                {
                    forcedFlush = false;
                }
                else if (--numberWaited <= -2)
                {
                    numberWaited = 0;
                    taskQueue.IncreaseNumberOfItems();
                }
            }
            else
            {
                if (forcedFlush)
                    forcedFlush = false;
                try
                {
                    pessoas = await dequeueTask;
                    logger.QueueReadAsynchronously(pessoas.Count, taskQueue.QueuedItemsCount);
                }
                catch (OperationCanceledException) // timed out waiting for items to be produced
                {
                    if (await taskQueue.FlushAsync(stoppingToken))
                        forcedFlush = true;
                    else
                        await Task.Delay(5_000, stoppingToken);
                }
                if (++numberWaited >= 2)
                {
                    numberWaited = 0;
                    taskQueue.DecreaseNumberOfItems();
                }
            }
            if (pessoas is not null)
            {
                try
                {
                    logger.SavingToDb(pessoas.Count);
                    using var db = dbContextFactory.CreateDbContext();
                    db.Pessoas.AddRange(pessoas);
                    await db.SaveChangesAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    logger.ErrorCreatingPessoa(ex.ToString());
                }
            }
        }
    }
}

public interface IBackgroundTaskQueue
{
    ValueTask QueueBackgroundWorkItemAsync(Pessoa pessoa, CancellationToken token);
    ValueTask<List<Pessoa>> DequeueAsync(CancellationToken cancellationToken);
    void IncreaseNumberOfItems();
    void DecreaseNumberOfItems();
    ValueTask<bool> FlushAsync(CancellationToken token);
    public int QueuedItemsCount { get; }
}

public sealed class NewPessoasBackgroundTaskQueue : IBackgroundTaskQueue
{
    private readonly Channel<List<Pessoa>> queue;
    private int powerOfItens;
    private int numberOfItems;
    private const int maxPowerOfItems = 5;
    private readonly LockableList<Pessoa> pessoas = new();
    private readonly ILogger<NewPessoasBackgroundTaskQueue> logger;

    public NewPessoasBackgroundTaskQueue(ILogger<NewPessoasBackgroundTaskQueue> logger)
    {
        var options = new UnboundedChannelOptions()
        {
            AllowSynchronousContinuations = false,
            SingleReader = false,
            SingleWriter = false
        };
        queue = Channel.CreateUnbounded<List<Pessoa>>(options);
        numberOfItems = (int)Math.Pow(2, powerOfItens);
        this.logger = logger;
        logger.BackgroundBatchNumber(numberOfItems);
    }

    public void IncreaseNumberOfItems()
    {
        if (powerOfItens == maxPowerOfItems)
        {
            logger.MaximumBackgroundBatchNumber(numberOfItems);
            return;
        }
        numberOfItems = (int)Math.Pow(2, ++powerOfItens);
        logger.BackgroundBatchNumber(numberOfItems, " (increased)");
    }

    public void DecreaseNumberOfItems()
    {
        if (powerOfItens == 0)
        {
            logger.MinimumBackgroundBatchNumber(numberOfItems);
            return;
        }
        numberOfItems = (int)Math.Pow(2, --powerOfItens);
        logger.BackgroundBatchNumber(numberOfItems, " (decreased)");
    }

    public async ValueTask QueueBackgroundWorkItemAsync(Pessoa pessoa, CancellationToken token)
    {
        logger.BufferingNewItem();
        if (pessoas.TryAddAndGetAll(pessoa, numberOfItems, out var pessoasToQueue))
        {
            logger.AddingItemsToQueue(pessoasToQueue!.Count);
            await queue.Writer.WriteAsync(pessoasToQueue, token);
        }
    }

    public async ValueTask<bool> FlushAsync(CancellationToken token)
    {

        if (pessoas.TryGetAll(out var pessoasToQueue))
        {
            logger.FlushingQueue(pessoasToQueue!.Count);
            await queue.Writer.WriteAsync(pessoasToQueue, token);
            return true;
        }
        return false;
    }

    public ValueTask<List<Pessoa>> DequeueAsync(CancellationToken cancellationToken) => queue.Reader.ReadAsync(cancellationToken);

    public int QueuedItemsCount => queue.Reader.Count;

    private class LockableList<T>
    {
        private List<T> items = new();

        public bool TryAddAndGetAll(T item, int maximum, out List<T>? thisItems)
        {
            lock (this)
            {
                if (items.Count >= maximum - 1)
                {
                    thisItems = items;
                    thisItems.Add(item);
                    items = new();
                    return true;
                }
            }
            items.Add(item);
            thisItems = null;
            return false;
        }

        public bool TryGetAll(out List<T>? thisItems)
        {
            lock (this)
            {
                if (items.Count > 0)
                {
                    thisItems = items;
                    items = new();
                    return true;
                }
            }
            thisItems = null;
            return false;
        }
    }
}
