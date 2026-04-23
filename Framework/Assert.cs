using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;

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

    public static void That(Expression<Func<bool>> expression, string? message = null)
    {
        if (expression is null)
            throw new ArgumentNullException(nameof(expression));

        bool result;

        try
        {
            result = expression.Compile().Invoke();
        }
        catch (Exception ex)
        {
            throw new AssertionFailedException(
                message ?? $"Expression threw an exception: {ex.GetType().Name}: {ex.Message}");
        }

        if (result)
            return;

        var details = new StringBuilder();

        details.AppendLine(message ?? "Expression assertion failed.");
        details.AppendLine($"Expression: {expression.Body}");
        details.AppendLine("Expression tree details:");
        AppendExpressionDetails(expression.Body, details, indent: 0);

        throw new AssertionFailedException(details.ToString());
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
            throw new AssertionFailedException(message ??
                                               $"Expected {typeof(TException).Name}, but got {ex.GetType().Name}.");
        }

        throw new AssertionFailedException(message ??
                                           $"Expected exception {typeof(TException).Name}, but no exception was thrown.");
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
            throw new AssertionFailedException(message ??
                                               $"Expected {typeof(TException).Name}, but got {ex.GetType().Name}.");
        }

        throw new AssertionFailedException(message ??
                                           $"Expected exception {typeof(TException).Name}, but no exception was thrown.");
    }

    private static void AppendExpressionDetails(Expression expression, StringBuilder builder, int indent)
    {
        var padding = new string(' ', indent * 2);

        switch (expression)
        {
            case BinaryExpression binary:
                builder.AppendLine($"{padding}Binary expression:");
                builder.AppendLine($"{padding}  Operator: {binary.NodeType}");
                builder.AppendLine($"{padding}  Left:");
                AppendExpressionDetails(binary.Left, builder, indent + 2);
                builder.AppendLine($"{padding}  Right:");
                AppendExpressionDetails(binary.Right, builder, indent + 2);

                TryAppendBinaryValues(binary, builder, padding);
                break;

            case MemberExpression member:
                builder.AppendLine($"{padding}Member expression:");
                builder.AppendLine($"{padding}  Member: {member.Member.Name}");
                builder.AppendLine($"{padding}  Value: {FormatValue(EvaluateExpression(member))}");
                break;

            case ConstantExpression constant:
                builder.AppendLine($"{padding}Constant expression:");
                builder.AppendLine($"{padding}  Value: {FormatValue(constant.Value)}");
                break;

            case MethodCallExpression methodCall:
                builder.AppendLine($"{padding}Method call expression:");
                builder.AppendLine($"{padding}  Method: {methodCall.Method.Name}");
                builder.AppendLine($"{padding}  Value: {FormatValue(EvaluateExpression(methodCall))}");

                if (methodCall.Object != null)
                {
                    builder.AppendLine($"{padding}  Object:");
                    AppendExpressionDetails(methodCall.Object, builder, indent + 2);
                }

                if (methodCall.Arguments.Count > 0)
                {
                    builder.AppendLine($"{padding}  Arguments:");

                    foreach (var argument in methodCall.Arguments)
                        AppendExpressionDetails(argument, builder, indent + 2);
                }

                break;

            case UnaryExpression unary:
                builder.AppendLine($"{padding}Unary expression:");
                builder.AppendLine($"{padding}  Operator: {unary.NodeType}");
                builder.AppendLine($"{padding}  Operand:");
                AppendExpressionDetails(unary.Operand, builder, indent + 2);
                builder.AppendLine($"{padding}  Value: {FormatValue(EvaluateExpression(unary))}");
                break;

            default:
                builder.AppendLine($"{padding}{expression.NodeType} expression:");
                builder.AppendLine($"{padding}  Expression: {expression}");
                builder.AppendLine($"{padding}  Value: {FormatValue(EvaluateExpression(expression))}");
                break;
        }
    }
    
    private static void TryAppendBinaryValues(BinaryExpression binary, StringBuilder builder, string padding)
    {
        try
        {
            var leftValue = EvaluateExpression(binary.Left);
            var rightValue = EvaluateExpression(binary.Right);
            var resultValue = EvaluateExpression(binary);

            builder.AppendLine($"{padding}  Values:");
            builder.AppendLine($"{padding}    Left value: {FormatValue(leftValue)}");
            builder.AppendLine($"{padding}    Right value: {FormatValue(rightValue)}");
            builder.AppendLine($"{padding}    Result: {FormatValue(resultValue)}");
        }
        catch (Exception ex)
        {
            builder.AppendLine($"{padding}  Values: cannot evaluate expression part: {ex.Message}");
        }
    }
    
    private static object? EvaluateExpression(Expression expression)
    {
        if (expression is ConstantExpression constant)
            return constant.Value;

        var converted = Expression.Convert(expression, typeof(object));
        var lambda = Expression.Lambda<Func<object?>>(converted);

        return lambda.Compile().Invoke();
    }
    
    private static string FormatValue(object? value)
    {
        return value switch
        {
            null => "null",
            string text => $"\"{text}\"",
            char ch => $"'{ch}'",
            _ => value.ToString() ?? string.Empty
        };
    }
}