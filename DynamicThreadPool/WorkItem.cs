namespace DynamicThreadPool;

internal sealed class WorkItem
{
    public WorkItem(Action action, string? name = null)
    {
        Action = action ?? throw new ArgumentNullException(nameof(action));
        Name = string.IsNullOrWhiteSpace(name) ? "UnnamedTask" : name;
        EnqueuedAtUtc = DateTime.UtcNow;
    }

    public Action Action { get; }

    public string Name { get; }

    public DateTime EnqueuedAtUtc { get; }
}