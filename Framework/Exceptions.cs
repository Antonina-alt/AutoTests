using System;

namespace Framework;

public class TestException : Exception
{
    public TestException(string message) : base(message) { }
    public TestException(string message, Exception inner) : base(message, inner) { }
}

public sealed class AssertionFailedException : TestException
{
    public AssertionFailedException(string message) : base(message) { }
}

public sealed class TestSkippedException : TestException
{
    public TestSkippedException(string message) : base(message) { }
}

public sealed class TestDiscoveryException : TestException
{
    public TestDiscoveryException(string message) : base(message) { }
}

public sealed class TestInvocationException : TestException
{
    public TestInvocationException(string message, Exception inner) : base(message, inner) { }
}