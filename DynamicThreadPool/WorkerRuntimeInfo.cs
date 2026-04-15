namespace DynamicThreadPool;

internal sealed class WorkerRuntimeInfo
{
    public required Thread Thread { get; init; }

    public string Name => Thread.Name ?? $"ManagedThread-{Thread.ManagedThreadId}";

    public bool IsExecutingTask { get; set; }

    public string? CurrentTaskName { get; set; }

    public DateTime? CurrentTaskStartedAtUtc { get; set; }

    public bool IsMarkedHung { get; set; }
}