using System;
using System.Collections.Generic;
using System.Linq;

namespace Framework;

public static class Assert
{
    public static void IsTrue(bool condition, string? message = null)
    {
        if (!condition) throw new AssertionFailedException(message ?? "Expected condition to be true.");
    }

    public static void IsFalse(bool condition, string? message = null)
    {
        if (condition) throw new AssertionFailedException(message ?? "Expected condition to be false.");
    }

    public static void AreEqual<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertionFailedException(message ?? $"Expected: {expected}, Actual: {actual}");
    }

    public static void AreNotEqual<T>(T notExpected, T actual, string? message = null)
    {
        if (EqualityComparer<T>.Default.Equals(notExpected, actual))
            throw new AssertionFailedException(message ?? $"Did not expect: {notExpected}");
    }

    public static void IsNull(object? value, string? message = null)
    {
        if (value is not null) throw new AssertionFailedException(message ?? "Expected null.");
    }

    public static void IsNotNull(object? value, string? message = null)
    {
        if (value is null) throw new AssertionFailedException(message ?? "Expected not null.");
    }
    
    public static void IsEmpty(string? value, string? message = null)
    {
        if (!string.IsNullOrEmpty(value))
            throw new AssertionFailedException(message ?? "Expected string to be empty.");
    }

    public static void IsNotEmpty(string? value, string? message = null)
    {
        if (string.IsNullOrEmpty(value))
            throw new AssertionFailedException(message ?? "Expected string to be not empty.");
    }
    
    public static void IsEmpty<T>(IEnumerable<T> items, string? message = null)
    {
        if (items is null) throw new AssertionFailedException(message ?? "Expected collection, got null.");
        if (items.Any())
            throw new AssertionFailedException(message ?? "Expected collection to be empty.");
    }

    public static void IsNotEmpty<T>(IEnumerable<T> items, string? message = null)
    {
        if (items is null) throw new AssertionFailedException(message ?? "Expected collection, got null.");
        if (!items.Any())
            throw new AssertionFailedException(message ?? "Expected collection to be not empty.");
    }

    public static void ContainsKey<TKey, TValue>(IDictionary<TKey, TValue> dict, TKey key, string? message = null)
    {
        if (dict is null) throw new AssertionFailedException(message ?? "Expected dictionary, got null.");
        if (!dict.ContainsKey(key))
            throw new AssertionFailedException(message ?? $"Expected dictionary to contain key '{key}'.");
    }

    public static void Contains<T>(IEnumerable<T> items, T value, string? message = null)
    {
        if (!items.Contains(value))
            throw new AssertionFailedException(message ?? $"Expected collection to contain {value}.");
    }

    public static void DoesNotContain<T>(IEnumerable<T> items, T value, string? message = null)
    {
        if (items.Contains(value))
            throw new AssertionFailedException(message ?? $"Expected collection NOT to contain {value}.");
    }

    public static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string? message = null)
    {
        if (!expected.SequenceEqual(actual))
            throw new AssertionFailedException(message ?? "Sequences are not equal.");
    }

    public static void Greater<T>(T left, T right, string? message = null) where T : IComparable<T>
    {
        if (left.CompareTo(right) <= 0)
            throw new AssertionFailedException(message ?? $"Expected {left} > {right}.");
    }

    public static void InRange<T>(T value, T min, T max, string? message = null) where T : IComparable<T>
    {
        if (value.CompareTo(min) < 0 || value.CompareTo(max) > 0)
            throw new AssertionFailedException(message ?? $"Expected {value} in range [{min}, {max}].");
    }

    public static void Throws<TException>(Action action, string? message = null) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        catch (Exception ex)
        {
            throw new AssertionFailedException(message ?? $"Expected {typeof(TException).Name}, but got {ex.GetType().Name}.");
        }

        throw new AssertionFailedException(message ?? $"Expected exception {typeof(TException).Name}, but no exception was thrown.");
    }
    
    public static async Task ThrowsAsync<TException>(Func<Task> action, string? message = null)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }
        catch (Exception ex)
        {
            throw new AssertionFailedException(message ?? $"Expected {typeof(TException).Name}, but got {ex.GetType().Name}.");
        }

        throw new AssertionFailedException(message ?? $"Expected exception {typeof(TException).Name}, but no exception was thrown.");
    }
}
