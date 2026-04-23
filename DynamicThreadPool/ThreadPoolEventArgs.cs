namespace DynamicThreadPool;

public sealed class ThreadPoolEventArgs : EventArgs
{
    public ThreadPoolEventArgs(
        string message,
        string? workerName = null,
        string? taskName = null,
        ThreadPoolSnapshot? snapshot = null,
        Exception? exception = null)
    {
        Message = message;
        WorkerName = workerName;
        TaskName = taskName;
        Snapshot = snapshot;
        Exception = exception;
        TimestampUtc = DateTime.UtcNow;
    }

    public string Message { get; }

    public string? WorkerName { get; }

    public string? TaskName { get; }

    public ThreadPoolSnapshot? Snapshot { get; }

    public Exception? Exception { get; }

    public DateTime TimestampUtc { get; }
}