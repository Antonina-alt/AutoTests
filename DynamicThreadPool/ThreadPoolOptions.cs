namespace DynamicThreadPool;

public sealed class ThreadPoolOptions
{
    public int MinThreads { get; init; } = 1;

    public int MaxThreads { get; init; } = 4;

    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan QueueWaitThreshold { get; init; } = TimeSpan.FromMilliseconds(300);

    public TimeSpan MonitorPeriod { get; init; } = TimeSpan.FromSeconds(1);

    public bool EnableLogging { get; init; } = true;
    
    public TimeSpan HungTaskThreshold { get; init; } = TimeSpan.FromSeconds(3);

    public bool EnableHungWorkerReplacement { get; init; } = true;

    public void Validate()
    {
        if (MinThreads <= 0)
            throw new ArgumentOutOfRangeException(nameof(MinThreads), "MinThreads must be greater than 0.");

        if (MaxThreads <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxThreads), "MaxThreads must be greater than 0.");

        if (MinThreads > MaxThreads)
            throw new ArgumentException("MinThreads cannot be greater than MaxThreads.");

        if (IdleTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(IdleTimeout), "IdleTimeout must be greater than zero.");

        if (QueueWaitThreshold <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(QueueWaitThreshold), "QueueWaitThreshold must be greater than zero.");

        if (MonitorPeriod <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(MonitorPeriod), "MonitorPeriod must be greater than zero.");
    }
}