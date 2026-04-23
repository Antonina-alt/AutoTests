namespace DynamicThreadPool;

public sealed class DynamicThreadPool : IDisposable
{
    public event EventHandler<ThreadPoolEventArgs>? WorkerStarted;
    public event EventHandler<ThreadPoolEventArgs>? WorkerStopped;
    public event EventHandler<ThreadPoolEventArgs>? TaskEnqueued;
    public event EventHandler<ThreadPoolEventArgs>? TaskStarted;
    public event EventHandler<ThreadPoolEventArgs>? TaskCompleted;
    public event EventHandler<ThreadPoolEventArgs>? TaskFailed;
    public event EventHandler<ThreadPoolEventArgs>? SnapshotCreated;
    public event EventHandler<ThreadPoolEventArgs>? HungWorkerDetected;
    public event EventHandler<ThreadPoolEventArgs>? PoolDisposed;
    
    private readonly ThreadPoolOptions _options;
    private readonly Queue<WorkItem> _queue = new();
    private readonly List<Thread> _workers = new();
    private readonly object _sync = new();

    private bool _isDisposed;
    private bool _isStopping;

    private int _busyWorkers;
    private int _pendingTasks;

    private long _enqueuedTasks;
    private long _completedTasks;
    private long _failedTasks;
    private long _createdWorkers;
    private long _retiredWorkers;

    private int _retiringWorkers;
    
    private readonly Thread? _monitorThread;
    
    private long _workerFailures;
    private long _recoveredWorkers;
    
    private readonly Dictionary<int, WorkerRuntimeInfo> _workerInfos = new();

    private long _hungWorkersDetected;
    private long _replacementWorkersStarted;

    public DynamicThreadPool(ThreadPoolOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();

        StartMinimumWorkers();

        _monitorThread = new Thread(MonitorLoop)
        {
            IsBackground = true,
            Name = "DynamicThreadPool-Monitor"
        };

        _monitorThread.Start();
    }

    public void Enqueue(Action action, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(action);

        ThrowIfDisposed();

        var workItem = new WorkItem(action, name);

        lock (_sync)
        {
            if (_isStopping)
                throw new InvalidOperationException("Thread pool is stopping. Cannot enqueue new tasks.");

            _queue.Enqueue(workItem);
            _pendingTasks++;
            Interlocked.Increment(ref _enqueuedTasks);

            Monitor.Pulse(_sync);
        }

        Log($"Task '{workItem.Name}' enqueued.");
        RaiseEvent(
            TaskEnqueued,
            $"Task '{workItem.Name}' enqueued.",
            taskName: workItem.Name);

        TryScaleUp();
    }

    public void WaitAll()
    {
        ThrowIfDisposed();

        lock (_sync)
        {
            while (_pendingTasks > 0)
            {
                Monitor.Wait(_sync);
            }
        }
    }

    public ThreadPoolSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            var workerCount = _workers.Count;
            var busyWorkers = _busyWorkers;
            var idleWorkers = workerCount - busyWorkers;
            var queueLength = _queue.Count;

            return new ThreadPoolSnapshot(
                WorkerCount: workerCount,
                BusyWorkers: busyWorkers,
                IdleWorkers: idleWorkers,
                QueueLength: queueLength,
                PendingTasks: _pendingTasks,
                EnqueuedTasks: Interlocked.Read(ref _enqueuedTasks),
                CompletedTasks: Interlocked.Read(ref _completedTasks),
                FailedTasks: Interlocked.Read(ref _failedTasks),
                CreatedWorkers: Interlocked.Read(ref _createdWorkers),
                RetiredWorkers: Interlocked.Read(ref _retiredWorkers),
                WorkerFailures: Interlocked.Read(ref _workerFailures),
                RecoveredWorkers: Interlocked.Read(ref _recoveredWorkers),
                HungWorkersDetected: Interlocked.Read(ref _hungWorkersDetected),
                ReplacementWorkersStarted: Interlocked.Read(ref _replacementWorkersStarted),
                TimestampUtc: DateTime.UtcNow);
        }
    }
    
    private void MonitorLoop()
    {
        while (true)
        {
            Thread.Sleep(_options.MonitorPeriod);

            bool shouldStop;

            lock (_sync)
            {
                shouldStop = _isStopping && _pendingTasks == 0;
            }

            var snapshot = GetSnapshot();
            RaiseEvent(
                SnapshotCreated,
                "Thread pool snapshot created.",
                snapshot: snapshot);

            Log(
                $"Snapshot | workers={snapshot.WorkerCount} " +
                $"busy={snapshot.BusyWorkers} " +
                $"idle={snapshot.IdleWorkers} " +
                $"queue={snapshot.QueueLength} " +
                $"pending={snapshot.PendingTasks} " +
                $"completed={snapshot.CompletedTasks} " +
                $"failed={snapshot.FailedTasks} " +
                $"created={snapshot.CreatedWorkers} " +
                $"retired={snapshot.RetiredWorkers} " +
                $"workerFailures={snapshot.WorkerFailures} " +
                $"recoveredWorkers={snapshot.RecoveredWorkers} " +
                $"hungDetected={snapshot.HungWorkersDetected} " +
                $"hungReplacements={snapshot.ReplacementWorkersStarted}");

            TryScaleUp();
            
            DetectAndReplaceHungWorkers();
            
            if (shouldStop)
                return;
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        List<Thread> workersToJoin;

        lock (_sync)
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
            _isStopping = true;

            Monitor.PulseAll(_sync);

            workersToJoin = _workers.ToList();
        }

        foreach (var worker in workersToJoin)
        {
            worker.Join();
        }
        
        _monitorThread?.Join();

        Log("Thread pool disposed.");
        RaiseEvent(PoolDisposed, "Thread pool disposed.");
    }

    private void StartMinimumWorkers()
    {
        for (var i = 0; i < _options.MinThreads; i++)
        {
            StartWorker("initial warm-up");
        }
    }
    
    private bool StartWorker(string? reason = null)
    {
        Thread? thread = null;

        lock (_sync)
        {
            if (_workers.Count >= _options.MaxThreads)
                return false;

            var workerNumber = (int)Interlocked.Increment(ref _createdWorkers);

            thread = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = $"DynamicThreadPool-Worker-{workerNumber}"
            };

            _workers.Add(thread);
            _workerInfos[thread.ManagedThreadId] = new WorkerRuntimeInfo
            {
                Thread = thread
            };
        }

        thread.Start();

        if (string.IsNullOrWhiteSpace(reason))
            Log($"Worker started: {thread.Name}");
        else
            Log($"Worker started: {thread.Name} | reason: {reason}");
        RaiseEvent(
            WorkerStarted,
            string.IsNullOrWhiteSpace(reason)
                ? $"Worker started: {thread.Name}"
                : $"Worker started: {thread.Name} | reason: {reason}",
            workerName: thread.Name);
        
        return true;
    }

    private void TryScaleUp()
    {
        bool shouldCreateWorker;
        string? reason = null;

        lock (_sync)
        {
            var workerCount = _workers.Count;
            var idleWorkers = workerCount - _busyWorkers;
            var queueLength = _queue.Count;
            var nowUtc = DateTime.UtcNow;

            var queuePressure = queueLength > idleWorkers + 1;
            var overdueTaskDetected = HasOverdueQueuedTaskUnsafe(nowUtc);

            shouldCreateWorker =
                workerCount < _options.MaxThreads &&
                queueLength > 0 &&
                (queuePressure || overdueTaskDetected);

            if (shouldCreateWorker)
            {
                if (overdueTaskDetected)
                    reason = "queue wait threshold exceeded";
                else if (queuePressure)
                    reason = "queue pressure";
            }
        }

        if (shouldCreateWorker)
        {
            StartWorker(reason);
        }
    }

    private void WorkerLoop()
{
    var retiredByIdleTimeout = false;
    var stoppedByPoolShutdown = false;
    var failedUnexpectedly = false;

    try
    {
        while (true)
        {
            WorkItem? workItem;

            lock (_sync)
            {
                while (_queue.Count == 0 && !_isStopping)
                {
                    var signaled = Monitor.Wait(_sync, _options.IdleTimeout);

                    if (!signaled && _queue.Count == 0)
                    {
                        var activeAfterRetire = _workers.Count - _retiringWorkers;

                        if (activeAfterRetire > _options.MinThreads)
                        {
                            _retiringWorkers++;
                            retiredByIdleTimeout = true;
                            Log($"Worker '{Thread.CurrentThread.Name}' retired due to idle timeout.");
                            return;
                        }
                    }
                }

                if (_queue.Count == 0 && _isStopping)
                {
                    stoppedByPoolShutdown = true;
                    break;
                }

                workItem = _queue.Dequeue();
                _busyWorkers++;
                if (_workerInfos.TryGetValue(Thread.CurrentThread.ManagedThreadId, out var workerInfo))
                {
                    workerInfo.IsExecutingTask = true;
                    workerInfo.CurrentTaskName = workItem.Name;
                    workerInfo.CurrentTaskStartedAtUtc = DateTime.UtcNow;
                    workerInfo.IsMarkedHung = false;
                }
            }

            try
            {
                Log($"Worker '{Thread.CurrentThread.Name}' is executing task '{workItem.Name}'.");
                RaiseEvent(
                    TaskStarted,
                    $"Task '{workItem.Name}' started.",
                    workerName: Thread.CurrentThread.Name,
                    taskName: workItem.Name);

                workItem.Action();

                Interlocked.Increment(ref _completedTasks);

                Log($"Task '{workItem.Name}' completed.");
                RaiseEvent(
                    TaskCompleted,
                    $"Task '{workItem.Name}' completed.",
                    workerName: Thread.CurrentThread.Name,
                    taskName: workItem.Name);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _failedTasks);

                Log($"Task '{workItem.Name}' failed: {ex.Message}");
                RaiseEvent(
                    TaskFailed,
                    $"Task '{workItem.Name}' failed: {ex.Message}",
                    workerName: Thread.CurrentThread.Name,
                    taskName: workItem.Name,
                    exception: ex);
            }
            finally
            {
                lock (_sync)
                {
                    _busyWorkers--;
                    _pendingTasks--;

                    if (_workerInfos.TryGetValue(Thread.CurrentThread.ManagedThreadId, out var workerInfo))
                    {
                        workerInfo.IsExecutingTask = false;
                        workerInfo.CurrentTaskName = null;
                        workerInfo.CurrentTaskStartedAtUtc = null;
                        workerInfo.IsMarkedHung = false;
                    }

                    if (_pendingTasks == 0)
                    {
                        Monitor.PulseAll(_sync);
                    }
                }

                TryScaleUp();
            }
        }
    }
    catch (Exception ex)
    {
        failedUnexpectedly = true;
        Interlocked.Increment(ref _workerFailures);
        Log($"Worker '{Thread.CurrentThread.Name}' crashed unexpectedly: {ex.GetType().Name} {ex.Message}");
    }
    finally
    {
        lock (_sync)
        {
            _workers.Remove(Thread.CurrentThread);
            _workerInfos.Remove(Thread.CurrentThread.ManagedThreadId);

            if (retiredByIdleTimeout)
            {
                _retiringWorkers--;
            }
        }

        Interlocked.Increment(ref _retiredWorkers);

        Log($"Worker stopped: {Thread.CurrentThread.Name}");
        RaiseEvent(
            WorkerStopped,
            $"Worker stopped: {Thread.CurrentThread.Name}",
            workerName: Thread.CurrentThread.Name);
        
        if (!retiredByIdleTimeout && !stoppedByPoolShutdown && failedUnexpectedly)
        {
            RecoverWorkerIfNeeded();
        }
    }
}

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
            throw new ObjectDisposedException(nameof(DynamicThreadPool));
    }

    private void Log(string message)
    {
        if (!_options.EnableLogging)
            return;

        Console.WriteLine($"[DynamicThreadPool] {DateTime.Now:HH:mm:ss.fff} | {message}");
    }
    
    private bool HasOverdueQueuedTaskUnsafe(DateTime nowUtc)
    {
        if (_queue.Count == 0)
            return false;

        foreach (var item in _queue)
        {
            if (nowUtc - item.EnqueuedAtUtc >= _options.QueueWaitThreshold)
                return true;
        }

        return false;
    }
    
    private void RecoverWorkerIfNeeded()
    {
        bool shouldRecover;

        lock (_sync)
        {
            shouldRecover =
                !_isStopping &&
                _workers.Count < _options.MinThreads;
        }

        if (shouldRecover)
        {
            Interlocked.Increment(ref _recoveredWorkers);
            StartWorker("worker recovery");
        }
    }
    
    private void DetectAndReplaceHungWorkers()
    {
        if (!_options.EnableHungWorkerReplacement)
            return;

        List<string> hungWorkerNames = new();

        lock (_sync)
        {
            var nowUtc = DateTime.UtcNow;

            foreach (var info in _workerInfos.Values)
            {
                if (!info.IsExecutingTask)
                    continue;

                if (info.IsMarkedHung)
                    continue;

                if (!info.CurrentTaskStartedAtUtc.HasValue)
                    continue;

                var executionTime = nowUtc - info.CurrentTaskStartedAtUtc.Value;

                if (executionTime >= _options.HungTaskThreshold)
                {
                    info.IsMarkedHung = true;
                    hungWorkerNames.Add($"{info.Name} (task: {info.CurrentTaskName}, running: {executionTime.TotalMilliseconds:F0} ms)");
                }
            }
        }

        foreach (var workerName in hungWorkerNames)
        {
            Interlocked.Increment(ref _hungWorkersDetected);
            Log($"Hung worker detected: {workerName}");
            RaiseEvent(
                HungWorkerDetected,
                $"Hung worker detected: {workerName}",
                workerName: workerName);

            StartReplacementWorkerForHungWorker();
        }
    }
    
    private bool StartReplacementWorker()
    {
        Thread? thread = null;

        lock (_sync)
        {
            var hungWorkersCount = GetHungWorkersCountUnsafe();
            var effectiveWorkerCount = _workers.Count - hungWorkersCount;

            if (_isStopping || effectiveWorkerCount >= _options.MaxThreads)
                return false;

            var workerNumber = (int)Interlocked.Increment(ref _createdWorkers);

            thread = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = $"DynamicThreadPool-Worker-{workerNumber}"
            };

            _workers.Add(thread);
            _workerInfos[thread.ManagedThreadId] = new WorkerRuntimeInfo
            {
                Thread = thread
            };
        }

        thread.Start();

        Log($"Worker started: {thread.Name} | reason: hung worker replacement");
        RaiseEvent(
            WorkerStarted,
            $"Worker started: {thread.Name} | reason: hung worker replacement",
            workerName: thread.Name);

        return true;
    }
    
    private void StartReplacementWorkerForHungWorker()
    {
        var started = StartReplacementWorker();

        if (started)
        {
            Interlocked.Increment(ref _replacementWorkersStarted);
        }
    }
    
    private int GetHungWorkersCountUnsafe()
    {
        var count = 0;

        foreach (var info in _workerInfos.Values)
        {
            if (info.IsMarkedHung)
                count++;
        }

        return count;
    }
    
    private void RaiseEvent(
        EventHandler<ThreadPoolEventArgs>? handler,
        string message,
        string? workerName = null,
        string? taskName = null,
        ThreadPoolSnapshot? snapshot = null,
        Exception? exception = null)
    {
        handler?.Invoke(
            this,
            new ThreadPoolEventArgs(
                message,
                workerName,
                taskName,
                snapshot,
                exception));
    }
}