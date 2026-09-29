using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Orbis.Stream.Core.Services;

/// <summary>
/// Replacement of the <c>ThreadPoolTaskExecutor</c> named "taskExecutor" (core 3, max 6,
/// queue capacity 20, thread prefix "Stream-Async-").
/// </summary>
public sealed class BackgroundTaskExecutor : IAsyncDisposable
{
    private readonly ConcurrentQueue<WorkItem> _queue = new();
    private readonly SemaphoreSlim _items = new(0);
    private readonly List<Worker> _workers = [];
    private readonly Lock _sync = new();
    private readonly ILogger _logger;
    private readonly int _corePoolSize;
    private readonly int _maxPoolSize;
    private readonly int _queueCapacity;
    private int _activeWorkers;
    private int _workerSequence;
    private bool _disposed;

    public BackgroundTaskExecutor(ILogger<BackgroundTaskExecutor> logger, int corePoolSize = 3, int maxPoolSize = 6, int queueCapacity = 20)
    {
        _logger = logger;
        _corePoolSize = Math.Max(1, corePoolSize);
        _maxPoolSize = Math.Max(_corePoolSize, maxPoolSize);
        _queueCapacity = Math.Max(1, queueCapacity);

        for (var index = 0; index < _corePoolSize; index++)
        {
            StartWorker();
        }
    }

    public int QueueLength
    {
        get
        {
            lock (_sync)
            {
                return _queue.Count;
            }
        }
    }

    public int ActiveWorkerCount
    {
        get
        {
            lock (_sync)
            {
                return _activeWorkers;
            }
        }
    }

    /// <summary>Queues work; throws when the executor is saturated, like Spring's <c>TaskRejectedException</c>.</summary>
    public void Execute(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_sync)
        {
            if (_queue.Count >= _queueCapacity && _activeWorkers >= _maxPoolSize)
            {
                throw new InvalidOperationException(
                    $"Rejected execution of task; the {nameof(BackgroundTaskExecutor)} queue is full.");
            }

            _queue.Enqueue(new WorkItem(work));
            _items.Release();

            if (_activeWorkers < _corePoolSize || (_queue.Count > 0 && _activeWorkers < _maxPoolSize))
            {
                StartWorker();
            }
        }
    }

    private void StartWorker()
    {
        var worker = new Worker(this);
        _workers.Add(worker);
        _activeWorkers++;
        worker.Thread.Start();
    }

    private async Task WorkerLoopAsync(Worker worker, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _items.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!_queue.TryDequeue(out var workItem))
            {
                continue;
            }

            try
            {
                workItem.Work();
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Background task failed on thread {ThreadName}", worker.Thread.Name);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Worker[] workers;
        lock (_sync)
        {
            workers = _workers.ToArray();
        }

        foreach (var worker in workers)
        {
            worker.Cancellation.Cancel();
        }

        _items.Release(workers.Length);

        foreach (var worker in workers)
        {
            try
            {
                await Task.Run(() => worker.Thread.Join(), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "Worker {ThreadName} did not stop cleanly", worker.Thread.Name);
            }
        }

        _items.Dispose();
    }

    private sealed record WorkItem(Action Work);

    private sealed class Worker
    {
        public Worker(BackgroundTaskExecutor owner)
        {
            Cancellation = new CancellationTokenSource();
            Thread = new Thread(() => _ = owner.WorkerLoopAsync(this, Cancellation.Token))
            {
                IsBackground = true,
                Name = $"Stream-Async-{++owner._workerSequence}"
            };
        }

        public Thread Thread { get; }

        public CancellationTokenSource Cancellation { get; }
    }
}
