using System.Diagnostics;
using System.Threading.Channels;

namespace Rinha;

internal sealed class SaveToDbQueueWorker : BackgroundService, IDisposable
{
    private readonly ILogger<SaveToDbQueueWorker> logger;
    private readonly IBackgroundTaskQueue taskQueue;
    private readonly Db db;

    public SaveToDbQueueWorker(IBackgroundTaskQueue taskQueue, Db db, ILogger<SaveToDbQueueWorker> logger)
    {
        this.taskQueue = taskQueue;
        this.db = db;
        this.logger = logger;
    }

    private sbyte numberWaited;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Parallel.ForAsync(1, 5,
                new ParallelOptions { CancellationToken = stoppingToken, MaxDegreeOfParallelism = 4 },
                (i, token) => WorkQueueAsync(token));
        }
    }

    private async ValueTask WorkQueueAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var dequeueTask = taskQueue.DequeueAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
            List<Pessoa>? pessoas = null;
            if (dequeueTask.IsCompletedSuccessfully)
            {
                pessoas = dequeueTask.Result;
                logger.QueueReadSynchronously(pessoas.Count, taskQueue.QueuedItemsCount);
                if (--numberWaited <= -2)
                {
                    numberWaited = 0;
                    taskQueue.IncreaseNumberOfItems();
                }
            }
            else
            {
                try
                {
                    pessoas = await dequeueTask;
                    Debug.Assert(pessoas.All(p => p is not null));
                    logger.QueueReadAsynchronously(pessoas.Count, taskQueue.QueuedItemsCount);
                }
                catch (OperationCanceledException) // timed out waiting for items to be produced
                {
                    if (!await taskQueue.FlushAsync(stoppingToken))
                        await Task.Delay(5_000, stoppingToken);
                }
                if (++numberWaited >= 4)
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
                    await db.AddAsync(pessoas, stoppingToken);
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
    ValueTask QueueBackgroundWorkItemAsync(Pessoa pessoa, CancellationToken cancellationToken);
    ValueTask<List<Pessoa>> DequeueAsync(CancellationToken cancellationToken);
    void IncreaseNumberOfItems();
    void DecreaseNumberOfItems();
    ValueTask<bool> FlushAsync(CancellationToken cancellationToken);
    ValueTask FlushAsyncAndWaitToDrainAsync(CancellationToken cancellationToken);
    public int QueuedItemsCount { get; }
}

public sealed class NewPessoasBackgroundTaskQueue : IBackgroundTaskQueue
{
    private readonly Channel<List<Pessoa>> queue;
    private const int maxPowerOfItems = 8;
    private const int minPowerOfItems = 4;
    private int powerOfItens = minPowerOfItems;
    private int numberOfItems;
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
        if (powerOfItens == minPowerOfItems)
        {
            logger.MinimumBackgroundBatchNumber(numberOfItems);
            return;
        }
        numberOfItems = (int)Math.Pow(2, --powerOfItens);
        logger.BackgroundBatchNumber(numberOfItems, " (decreased)");
    }

    public async ValueTask QueueBackgroundWorkItemAsync(Pessoa pessoa, CancellationToken cancellationToken)
    {
        logger.BufferingNewItem();
        if (pessoas.TryAddAndGetAll(pessoa, numberOfItems, out var pessoasToQueue))
        {
            logger.AddingItemsToQueue(pessoasToQueue!.Count);
            Debug.Assert(pessoasToQueue.All(p => p is not null));
            await queue.Writer.WriteAsync(pessoasToQueue, cancellationToken);
        }
    }

    public async ValueTask<bool> FlushAsync(CancellationToken cancellationToken)
    {

        if (pessoas.TryGetAll(out var pessoasToQueue))
        {
            logger.FlushingQueue(pessoasToQueue!.Count);
            await queue.Writer.WriteAsync(pessoasToQueue, cancellationToken);
            return true;
        }
        return false;
    }

    public async ValueTask FlushAsyncAndWaitToDrainAsync(CancellationToken cancellationToken)
    {
        await FlushAsync(cancellationToken);
        while (QueuedItemsCount > 0)
        {
            logger.WaitingForQueueToEmpty(QueuedItemsCount);
            await Task.Delay(2_000, cancellationToken);
        }
    }

    public ValueTask<List<Pessoa>> DequeueAsync(CancellationToken cancellationToken) => queue.Reader.ReadAsync(cancellationToken);

    public int QueuedItemsCount => queue.Reader.Count;

    private sealed class LockableList<T>
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
