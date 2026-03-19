using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Framework;

namespace Runner;

public static class Program
{
    private sealed record DiscoveredTest(
        Type ClassType,
        MethodInfo Method,
        MethodInfo[] SetUps,
        MethodInfo[] TearDowns,
        ISharedContext? SharedContext,
        object?[] Args,
        int Priority,
        int? TimeoutMilliseconds);

    private sealed record ClassRuntime(Type ClassType, ISharedContext? SharedContext);

    private sealed record RunReport(
        string Mode,
        int MaxDegreeOfParallelism,
        int Passed,
        int Failed,
        int Skipped,
        long ElapsedMilliseconds,
        IReadOnlyCollection<string> Results);
    
    private sealed record ComparisonReport(
        double Speedup,
        long TimeSaved,
        double PercentSaved);

    public static async Task<int> Main(string[] args)
    {
        var testAssemblyPath = GetTestAssemblyPath(args);

        if (string.IsNullOrWhiteSpace(testAssemblyPath))
        {
            Console.WriteLine("Usage: Runner <path-to-tests-assembly.dll> [--mdop=4]");
            Console.WriteLine("Tip: Run from Rider without args is supported, but auto-detection failed.");
            return 2;
        }

        testAssemblyPath = Path.GetFullPath(testAssemblyPath);

        if (!File.Exists(testAssemblyPath))
        {
            Console.WriteLine($"Tests assembly not found: {testAssemblyPath}");
            return 2;
        }

        var userMdop = GetMaxDegreeOfParallelism(args);

        Console.WriteLine();
        Console.WriteLine("============================================================");
        Console.WriteLine("The same tests will be run twice:");
        Console.WriteLine("1) sequentially, with MaxDegreeOfParallelism = 1");
        Console.WriteLine($"2) with user value, MaxDegreeOfParallelism = {userMdop}");
        Console.WriteLine("============================================================");
        Console.WriteLine();

        var asm = Assembly.LoadFrom(testAssemblyPath);

        var sequentialReport = await RunSuiteAsync(asm, 1, "SEQUENTIAL");
        RunReport? parallelReport = null;

        if (userMdop != 1)
        {
            parallelReport = await RunSuiteAsync(asm, userMdop, "PARALLEL");
        }

        Console.WriteLine();
        Console.WriteLine("============================================================");
        Console.WriteLine("FINAL COMPARISON");
        Console.WriteLine("============================================================");

        PrintShortReport(sequentialReport);

        if (parallelReport != null)
        {
            PrintShortReport(parallelReport);

            var comparison = BuildComparison(sequentialReport, parallelReport);

            Console.WriteLine();
            Console.WriteLine($"Speedup: {comparison.Speedup:F2}x");
            Console.WriteLine($"Time saved: {comparison.TimeSaved}ms ({comparison.PercentSaved:F1}%)");
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine("User MaxDegreeOfParallelism = 1, so comparison is not applicable.");
        }

        SaveCombinedReport(sequentialReport, parallelReport);

        if (sequentialReport.Failed > 0)
            return 1;

        if (parallelReport is not null && parallelReport.Failed > 0)
            return 1;

        return 0;
    }

    private static async Task<RunReport> RunSuiteAsync(Assembly asm, int maxDegreeOfParallelism, string mode)
    {
        Console.WriteLine();
        Console.WriteLine("------------------------------------------------------------");
        Console.WriteLine($"RUN MODE: {mode}");
        Console.WriteLine($"MaxDegreeOfParallelism = {maxDegreeOfParallelism}");
        Console.WriteLine("------------------------------------------------------------");

        var totalStopwatch = Stopwatch.StartNew();

        var results = new ConcurrentQueue<string>();
        var consoleLock = new object();
        var passed = 0;
        var failed = 0;
        var skipped = 0;

        void SafeWriteLine(string message)
        {
            lock (consoleLock)
            {     
                Console.WriteLine(message);
            }
        }

        var discoveredTests = new List<DiscoveredTest>();
        var classRuntimes = new List<ClassRuntime>();

        try
        {
            foreach (var testClass in asm.GetTypes()
                         .Where(t => t.GetCustomAttribute<TestClassAttribute>() != null))
            {
                var useShared = testClass.GetCustomAttribute<UseSharedContextAttribute>();
                ISharedContext? shared = null;

                if (useShared != null)
                {
                    if (!typeof(ISharedContext).IsAssignableFrom(useShared.ContextType))
                        throw new TestDiscoveryException($"{useShared.ContextType.Name} must implement ISharedContext.");

                    shared = (ISharedContext)Activator.CreateInstance(useShared.ContextType)!
                             ?? throw new TestDiscoveryException($"Cannot create shared context {useShared.ContextType.Name}");

                    SafeWriteLine($"[CTX] {testClass.Name}: shared context created: {useShared.ContextType.Name}");
                    await shared.SetUpAsync();
                    SafeWriteLine($"[CTX] {testClass.Name}: shared context SetUpAsync() done");
                }

                classRuntimes.Add(new ClassRuntime(testClass, shared));

                var setupMethods = testClass.GetMethods()
                    .Where(m => m.GetCustomAttribute<SetUpAttribute>() != null)
                    .ToArray();

                var teardownMethods = testClass.GetMethods()
                    .Where(m => m.GetCustomAttribute<TearDownAttribute>() != null)
                    .ToArray();

                var testMethods = testClass.GetMethods()
                    .Where(m => m.GetCustomAttribute<TestAttribute>() != null)
                    .Select(m => new
                    {
                        Method = m,
                        Priority = m.GetCustomAttribute<PriorityAttribute>()?.Value ?? 0,
                        Ignore = m.GetCustomAttribute<IgnoreAttribute>(),
                        TimeoutMilliseconds = m.GetCustomAttribute<TimeoutAttribute>()?.Milliseconds
                    })
                    .OrderByDescending(x => x.Priority)
                    .ToArray();

                foreach (var tm in testMethods)
                {
                    if (tm.Ignore != null)
                    {
                        Interlocked.Increment(ref skipped);
                        results.Enqueue($"SKIP  {testClass.Name}.{tm.Method.Name}  reason='{tm.Ignore.Reason}'");
                        continue;
                    }

                    var paramCount = tm.Method.GetParameters().Length;
                    var cases = tm.Method.GetCustomAttributes<TestCaseAttribute>().ToArray();

                    if (paramCount > 0 && cases.Length == 0)
                        throw new TestDiscoveryException(
                            $"{testClass.Name}.{tm.Method.Name} has parameters but no [TestCase] attributes.");

                    if (cases.Length == 0)
                    {
                        discoveredTests.Add(new DiscoveredTest(
                            testClass,
                            tm.Method,
                            setupMethods,
                            teardownMethods,
                            shared,
                            Array.Empty<object?>(),
                            tm.Priority,
                            tm.TimeoutMilliseconds));
                        continue;
                    }

                    foreach (var c in cases)
                    {
                        if (c.Args.Length != paramCount)
                            throw new TestDiscoveryException(
                                $"{testClass.Name}.{tm.Method.Name}: TestCase params count {c.Args.Length} does not match parameters count {paramCount}.");

                        discoveredTests.Add(new DiscoveredTest(
                            testClass,
                            tm.Method,
                            setupMethods,
                            teardownMethods,
                            shared,
                            c.Args,
                            tm.Priority,
                            tm.TimeoutMilliseconds));
                    }
                }
            }

            var semaphore = new SemaphoreSlim(maxDegreeOfParallelism);

            var tasks = discoveredTests
                .OrderByDescending(t => t.Priority)
                .Select(async test =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        await RunSingleAsync(test);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                })
                .ToArray();

            await Task.WhenAll(tasks);
        }
        finally
        {
            foreach (var runtime in classRuntimes)
            {
                if (runtime.SharedContext == null)
                    continue;

                await runtime.SharedContext.TearDownAsync();
                SafeWriteLine($"[CTX] {runtime.ClassType.Name}: shared context TearDownAsync() done");
                DumpSharedContextInfo(runtime.ClassType, runtime.SharedContext, SafeWriteLine);
            }
        }

        totalStopwatch.Stop();

        foreach (var line in results)
            Console.WriteLine(line);

        Console.WriteLine(new string('-', 60));
        Console.WriteLine($"MODE: {mode}");
        Console.WriteLine($"TOTAL: {passed + failed + skipped}, PASS: {passed}, FAIL: {failed}, SKIP: {skipped}");
        Console.WriteLine($"TOTAL ELAPSED: {totalStopwatch.ElapsedMilliseconds}ms");

        return new RunReport(
            mode,
            maxDegreeOfParallelism,
            passed,
            failed,
            skipped,
            totalStopwatch.ElapsedMilliseconds,
            results.ToArray());

        async Task RunSingleAsync(DiscoveredTest test)
        {
            var instance = CreateTestInstance(test.ClassType, test.SharedContext);
            var sw = Stopwatch.StartNew();
            var displayName = $"{test.ClassType.Name}.{test.Method.Name}({FormatArgs(test.Args)})";

            try
            {
                var executionTask = ExecuteTestBodyAsync(instance, test);

                if (test.TimeoutMilliseconds is int timeoutMs)
                {
                    await executionTask.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
                }
                else
                {
                    await executionTask;
                }

                Interlocked.Increment(ref passed);
                results.Enqueue($"PASS  {displayName}  {sw.ElapsedMilliseconds}ms");
            }
            catch (TimeoutException)
            {
                Interlocked.Increment(ref failed);
                results.Enqueue($"FAIL  {displayName}  TIMEOUT after {test.TimeoutMilliseconds}ms");
            }
            catch (TargetInvocationException tie) when (tie.InnerException is AssertionFailedException aex)
            {
                Interlocked.Increment(ref failed);
                results.Enqueue($"FAIL  {displayName}  {aex.Message}");
            }
            catch (TargetInvocationException tie) when (tie.InnerException is TestSkippedException skipex)
            {
                Interlocked.Increment(ref skipped);
                results.Enqueue($"SKIP  {displayName}  {skipex.Message}");
            }
            catch (TargetInvocationException tie)
            {
                Interlocked.Increment(ref failed);
                var inner = tie.InnerException;
                results.Enqueue($"FAIL  {displayName}  EX: {inner?.GetType().Name} {inner?.Message}");
                if (!string.IsNullOrWhiteSpace(inner?.StackTrace))
                    results.Enqueue($"      STACK: {TrimStack(inner.StackTrace)}");
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failed);
                results.Enqueue($"FAIL  {displayName}  EX: {ex.GetType().Name} {ex.Message}");
                if (!string.IsNullOrWhiteSpace(ex.StackTrace))
                    results.Enqueue($"      STACK: {TrimStack(ex.StackTrace)}");
            }
        }
    }

    private static void PrintShortReport(RunReport report)
    {
        Console.WriteLine();
        Console.WriteLine(
            $"{report.Mode}: MAX DEGREE OF PARALLELISM={report.MaxDegreeOfParallelism}, " +
            $"PASS={report.Passed}, FAIL={report.Failed}, SKIP={report.Skipped}, " +
            $"TIME={report.ElapsedMilliseconds}ms");
    }

    private static void SaveCombinedReport(RunReport sequential, RunReport? parallel)
    {
        var lines = new List<string>();

        AppendRunReport(lines, "==================== SEQUENTIAL RUN ====================", sequential);

        if (parallel != null)
        {
            AppendRunReport(lines, "==================== USER RUN ====================", parallel);

            var comparison = BuildComparison(sequential, parallel);
            AppendComparison(lines, comparison);
        }

        var outPath = Path.Combine(Environment.CurrentDirectory, "test-results.txt");
        File.WriteAllLines(outPath, lines);
        Console.WriteLine();
        Console.WriteLine($"Results saved: {outPath}");
    }
    
    private static ComparisonReport BuildComparison(RunReport sequential, RunReport parallel)
    {
        var speedup = sequential.ElapsedMilliseconds / (double)parallel.ElapsedMilliseconds;
        var saved = sequential.ElapsedMilliseconds - parallel.ElapsedMilliseconds;
        var percent = sequential.ElapsedMilliseconds == 0
            ? 0
            : saved * 100.0 / sequential.ElapsedMilliseconds;

        return new ComparisonReport(speedup, saved, percent);
    }
    
    private static void AppendRunReport(List<string> lines, string title, RunReport report)
    {
        lines.Add(title);
        lines.Add($"Mode: {report.Mode}");
        lines.Add($"MAX DEGREE OF PARALLELISM: {report.MaxDegreeOfParallelism}");
        lines.Add($"TOTAL: {report.Passed + report.Failed + report.Skipped}, PASS: {report.Passed}, FAIL: {report.Failed}, SKIP: {report.Skipped}");
        lines.Add($"TOTAL ELAPSED: {report.ElapsedMilliseconds}ms");
        lines.Add("");
        lines.AddRange(report.Results);
        lines.Add("");
    }
    
    private static void AppendComparison(List<string> lines, ComparisonReport comparison)
    {
        lines.Add("==================== COMPARISON ====================");
        lines.Add($"Speedup: {comparison.Speedup:F2}x");
        lines.Add($"Time saved: {comparison.TimeSaved}ms ({comparison.PercentSaved:F1}%)");
    }

    private static async Task InvokeMaybeAsync(object instance, MethodInfo method)
    {
        var result = method.Invoke(instance, Array.Empty<object?>());
        if (result is Task task)
            await task;
    }

    private static async Task ExecuteTestBodyAsync(object instance, DiscoveredTest test)
    {
        foreach (var setup in test.SetUps)
            await InvokeMaybeAsync(instance, setup);

        try
        {
            var result = test.Method.Invoke(instance, test.Args);
            if (result is Task task)
                await task;
        }
        finally
        {
            foreach (var tearDown in test.TearDowns)
                await InvokeMaybeAsync(instance, tearDown);
        }
    }

    private static void DumpSharedContextInfo(Type classType, ISharedContext ctx, Action<string> writeLine)
    {
        var ctxType = ctx.GetType();
        var logsProp = ctxType.GetProperty("Logs", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (logsProp == null) return;

        var val = logsProp.GetValue(ctx);
        if (val is not System.Collections.IEnumerable enumerable) return;

        var lines = enumerable.Cast<object?>()
            .Select(x => x?.ToString())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        if (lines.Count == 0) return;

        writeLine($"[CTX] {classType.Name}: Logs ({lines.Count})");
        foreach (var line in lines.Take(10))
            writeLine($"[CTX]   {line}");

        if (lines.Count > 10)
            writeLine($"[CTX]   ... ({lines.Count - 10} more)");
    }

    private static string? GetTestAssemblyPath(string[] args)
    {
        var positional = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        return positional ?? TryAutoDetectTestsDll();
    }

    private static int GetMaxDegreeOfParallelism(string[] args)
    {
        var fromArgs = TryParseMaxDegreeOfParallelism(args);
        if (fromArgs.HasValue)
            return fromArgs.Value;

        return ReadMaxDegreeOfParallelismFromConsole();
    }

    private static int? TryParseMaxDegreeOfParallelism(string[] args)
    {
        var mdopArg = args.FirstOrDefault(a =>
            a.StartsWith("--mdop=", StringComparison.OrdinalIgnoreCase));

        if (mdopArg != null &&
            int.TryParse(mdopArg[7..], out var parsed) &&
            parsed > 0)
        {
            return parsed;
        }

        return null;
    }

    private static int ReadMaxDegreeOfParallelismFromConsole()
    {
        var defaultValue = Environment.ProcessorCount;

        while (true)
        {
            Console.Write($"Enter MaxDegreeOfParallelism (default {defaultValue}): ");
            var input = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(input))
                return defaultValue;

            if (int.TryParse(input, out var value) && value > 0)
                return value;

            Console.WriteLine("Please enter a positive integer.");
        }
    }

    private static string? TryAutoDetectTestsDll()
    {
        var start = new DirectoryInfo(AppContext.BaseDirectory);

        for (var i = 0; i < 10 && start != null; i++)
        {
            var testsDir = Path.Combine(start.FullName, "Tests", "bin");
            if (Directory.Exists(testsDir))
            {
                var candidates = Directory.EnumerateFiles(testsDir, "Tests.dll", SearchOption.AllDirectories)
                    .Select(p => new FileInfo(p))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .ToList();

                if (candidates.Count > 0)
                    return candidates[0].FullName;
            }

            var anyTests = Directory.EnumerateFiles(start.FullName, "*.Tests.dll", SearchOption.AllDirectories)
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();

            if (anyTests != null)
                return anyTests.FullName;

            start = start.Parent;
        }

        return null;
    }

    private static object CreateTestInstance(Type testClass, ISharedContext? shared)
    {
        if (shared is null)
            return Activator.CreateInstance(testClass)
                   ?? throw new TestDiscoveryException($"Cannot create instance of {testClass.Name}");

        var ctor = testClass.GetConstructors()
            .FirstOrDefault(c =>
            {
                var parameters = c.GetParameters();
                return parameters.Length == 1 && parameters[0].ParameterType.IsInstanceOfType(shared);
            });

        if (ctor != null)
            return ctor.Invoke(new object?[] { shared });

        return Activator.CreateInstance(testClass)
               ?? throw new TestDiscoveryException($"No suitable constructor found for {testClass.Name}");
    }

    private static string FormatArgs(object?[]? args)
    {
        if (args == null || args.Length == 0) return string.Empty;

        return string.Join(", ", args.Select(a => a == null ? "null" : (a.ToString() ?? "null")));
    }

    private static string TrimStack(string stack)
    {
        var lines = stack.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" | ", lines.Take(5));
    }
}