namespace DynamicThreadPool;

public sealed record ThreadPoolSnapshot(
    int WorkerCount,
    int BusyWorkers,
    int IdleWorkers,
    int QueueLength,
    int PendingTasks,
    long EnqueuedTasks,
    long CompletedTasks,
    long FailedTasks,
    long CreatedWorkers,
    long RetiredWorkers,
    long WorkerFailures,
    long RecoveredWorkers,
    long HungWorkersDetected,
    long ReplacementWorkersStarted,
    DateTime TimestampUtc);