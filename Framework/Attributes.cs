using System;

namespace Framework;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class TestClassAttribute : Attribute
{
    public string? Category { get; init; }
}

[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class TestAttribute : Attribute
{
    public string? Description { get; init; }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class TestCaseAttribute : Attribute
{
    public object?[] Args { get; }

    public TestCaseAttribute(params object?[] args) => Args = args ?? new object?[] { null };
}

[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class PriorityAttribute : Attribute
{
    public int Value { get; }
    public PriorityAttribute(int value) => Value = value;
}

[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class IgnoreAttribute : Attribute
{
    public string Reason { get; }
    public IgnoreAttribute(string reason) => Reason = reason;
}

[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class SetUpAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class TearDownAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class UseSharedContextAttribute : Attribute
{
    public Type ContextType { get; }
    public UseSharedContextAttribute(Type contextType) => ContextType = contextType;
}

[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class TimeoutAttribute : Attribute
{
    public int Milliseconds { get; }

    public TimeoutAttribute(int milliseconds)
    {
        if (milliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(milliseconds), "Timeout must be greater than 0.");

        Milliseconds = milliseconds;
    }
}
