using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Framework;
using DynamicThreadPool;

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
        int? TimeoutMilliseconds,
        string? Category,
        string? Author);

    private delegate bool TestFilterDelegate(DiscoveredTest test);

    private sealed record ClassRuntime(Type ClassType, ISharedContext? SharedContext);

    private sealed record RunReport(
        int MinThreads,
        int MaxThreads,
        int Passed,
        int Failed,
        int Skipped,
        long ElapsedMilliseconds,
        IReadOnlyCollection<string> Results);

    private sealed record SimulationRunInfo(
        int RunNumber,
        int MinThreads,
        int MaxThreads,
        int Passed,
        int Failed,
        int Skipped,
        long ElapsedMilliseconds,
        string ReportFilePath);

    private sealed record SimulationSummary(
        int TotalRuns,
        int SuccessfulRuns,
        int FailedRuns,
        double AverageTimeMs,
        long MinTimeMs,
        long MaxTimeMs);

    private sealed class RunExecutionState
    {
        public ConcurrentQueue<string> Results { get; } = new();

        public int Passed;
        public int Failed;
        public int Skipped;
    }

    public static async Task<int> Main(string[] args)
    {
        var testAssemblyPath = GetTestAssemblyPath(args);

        if (string.IsNullOrWhiteSpace(testAssemblyPath))
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("Runner <path-to-tests-assembly.dll>");
            return 2;
        }

        testAssemblyPath = Path.GetFullPath(testAssemblyPath);

        if (!File.Exists(testAssemblyPath))
        {
            Console.WriteLine($"Tests assembly not found: {testAssemblyPath}");
            return 2;
        }

        var asm = Assembly.LoadFrom(testAssemblyPath);

        return await RunLoadSimulationAsync(asm);
    }

    private static async Task<int> RunLoadSimulationAsync(Assembly asm)
    {
        const int totalRuns = 50;
        const int minThreads = 2;
        const int maxThreads = 6;

        Console.WriteLine();
        Console.WriteLine("============================================================");
        Console.WriteLine("TEST EXECUTION");
        Console.WriteLine("Total planned runs: 50");
        Console.WriteLine("Uneven load is simulated inside each run");
        Console.WriteLine("============================================================");
        Console.WriteLine();

        var testFilter = BuildTestFilterFromConsole();

        var runs = new List<SimulationRunInfo>();

        for (var runNumber = 1; runNumber <= totalRuns; runNumber++)
        {
            Console.WriteLine();
            Console.WriteLine($"[RUN] Starting run #{runNumber:00}");

            var report = await RunSuiteWithDynamicPoolAsync(asm, minThreads, maxThreads, testFilter);
            var reportPath = SaveRunReport(report, runNumber);

            var runInfo = new SimulationRunInfo(
                RunNumber: runNumber,
                MinThreads: minThreads,
                MaxThreads: maxThreads,
                Passed: report.Passed,
                Failed: report.Failed,
                Skipped: report.Skipped,
                ElapsedMilliseconds: report.ElapsedMilliseconds,
                ReportFilePath: reportPath);

            runs.Add(runInfo);

            Console.WriteLine(
                $"[RUN] Run #{runInfo.RunNumber:00} finished | " +
                $"PASS={runInfo.Passed}, FAIL={runInfo.Failed}, SKIP={runInfo.Skipped}, " +
                $"TIME={runInfo.ElapsedMilliseconds}ms");
        }

        var summary = BuildSimulationSummary(runs);

        PrintSimulationSummary(runs, summary);
        SaveSimulationSummary(runs, summary);

        return runs.Any(r => r.Failed > 0) ? 1 : 0;
    }

    private static async Task<RunReport> RunSuiteWithDynamicPoolAsync(
        Assembly asm,
        int minThreads,
        int maxThreads,
        TestFilterDelegate testFilter)
    {
        var totalStopwatch = Stopwatch.StartNew();
        var consoleLock = new object();
        var state = new RunExecutionState();

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
                        throw new TestDiscoveryException(
                            $"{useShared.ContextType.Name} must implement ISharedContext.");

                    shared = (ISharedContext)Activator.CreateInstance(useShared.ContextType)!
                             ?? throw new TestDiscoveryException(
                                 $"Cannot create shared context {useShared.ContextType.Name}");

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
                        TimeoutMilliseconds = m.GetCustomAttribute<TimeoutAttribute>()?.Milliseconds,
                        Category = m.GetCustomAttribute<TestAttribute>()?.Category,
                        Author = m.GetCustomAttribute<TestAttribute>()?.Author
                    })
                    .OrderByDescending(x => x.Priority)
                    .ToArray();

                foreach (var tm in testMethods)
                {
                    if (tm.Ignore != null)
                    {
                        Interlocked.Increment(ref state.Skipped);
                        state.Results.Enqueue($"SKIP  {testClass.Name}.{tm.Method.Name}  reason='{tm.Ignore.Reason}'");
                        continue;
                    }

                    var testAttribute = tm.Method.GetCustomAttribute<TestAttribute>();
                    var testArguments = GetTestArguments(testClass, tm.Method);

                    foreach (var args in testArguments)
                    {
                        discoveredTests.Add(new DiscoveredTest(
                            testClass,
                            tm.Method,
                            setupMethods,
                            teardownMethods,
                            shared,
                            args,
                            tm.Priority,
                            tm.TimeoutMilliseconds,
                            testAttribute?.Category,
                            testAttribute?.Author));
                    }
                }
            }

            using var pool = new DynamicThreadPool.DynamicThreadPool(new ThreadPoolOptions
            {
                MinThreads = minThreads,
                MaxThreads = maxThreads,
                IdleTimeout = TimeSpan.FromSeconds(2),
                QueueWaitThreshold = TimeSpan.FromMilliseconds(300),
                MonitorPeriod = TimeSpan.FromSeconds(1),
                EnableLogging = true
            });

            SubscribeToThreadPoolEvents(pool);

            var testsToRun = discoveredTests
                .Where(testFilter.Invoke)
                .ToList();

            SafeWriteLine("");
            SafeWriteLine("Test filtering result:");
            SafeWriteLine($"  discovered tests: {discoveredTests.Count}");
            SafeWriteLine($"  selected tests:   {testsToRun.Count}");
            SafeWriteLine("");

            await EnqueueTestsWithUnevenLoadAsync(
                testsToRun,
                pool,
                state,
                SafeWriteLine);

            pool.WaitAll();
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

        foreach (var line in state.Results)
            Console.WriteLine(line);

        Console.WriteLine(new string('-', 60));
        Console.WriteLine(
            $"TOTAL: {state.Passed + state.Failed + state.Skipped}, PASS: {state.Passed}, FAIL: {state.Failed}, SKIP: {state.Skipped}");
        Console.WriteLine($"TOTAL ELAPSED: {totalStopwatch.ElapsedMilliseconds}ms");

        return new RunReport(
            minThreads,
            maxThreads,
            state.Passed,
            state.Failed,
            state.Skipped,
            totalStopwatch.ElapsedMilliseconds,
            state.Results.ToArray());
    }

    private static void SubscribeToThreadPoolEvents(DynamicThreadPool.DynamicThreadPool pool)
    {
        pool.WorkerStarted += (_, e) =>
            PrintThreadPoolEvent("WorkerStarted", e);

        pool.WorkerStopped += (_, e) =>
            PrintThreadPoolEvent("WorkerStopped", e);

        pool.TaskEnqueued += (_, e) =>
            PrintThreadPoolEvent("TaskEnqueued", e);

        pool.TaskStarted += (_, e) =>
            PrintThreadPoolEvent("TaskStarted", e);

        pool.TaskCompleted += (_, e) =>
            PrintThreadPoolEvent("TaskCompleted", e);

        pool.TaskFailed += (_, e) =>
            PrintThreadPoolEvent("TaskFailed", e);

        pool.SnapshotCreated += (_, e) =>
            PrintThreadPoolEvent("SnapshotCreated", e);

        pool.HungWorkerDetected += (_, e) =>
            PrintThreadPoolEvent("HungWorkerDetected", e);

        pool.PoolDisposed += (_, e) =>
            PrintThreadPoolEvent("PoolDisposed", e);
    }

    private static async Task EnqueueTestsWithUnevenLoadAsync(
        IReadOnlyList<DiscoveredTest> tests,
        DynamicThreadPool.DynamicThreadPool threadPool,
        RunExecutionState state,
        Action<string> safeWriteLine)
    {
        if (tests.Count == 0)
            return;

        var ordered = tests
            .OrderByDescending(t => t.Priority)
            .ToList();

        var total = ordered.Count;
        var singleCount = Math.Max(1, total / 5);
        var burstCount = Math.Max(1, total / 3);
        var moderateCount = Math.Max(1, total / 4);

        var index = 0;

        async Task EnqueueOneByOneAsync(int count, TimeSpan delay, string stage)
        {
            for (var i = 0; i < count && index < total; i++, index++)
            {
                var test = ordered[index];
                threadPool.Enqueue(
                    () => RunSingleSync(test, state),
                    $"{test.ClassType.Name}.{test.Method.Name}");

                safeWriteLine($"[LOAD] {stage}: single enqueue -> {test.ClassType.Name}.{test.Method.Name}");
                await Task.Delay(delay);
            }
        }

        async Task EnqueueBurstAsync(int count, string stage)
        {
            for (var i = 0; i < count && index < total; i++, index++)
            {
                var test = ordered[index];
                threadPool.Enqueue(
                    () => RunSingleSync(test, state),
                    $"{test.ClassType.Name}.{test.Method.Name}");

                safeWriteLine($"[LOAD] {stage}: burst enqueue -> {test.ClassType.Name}.{test.Method.Name}");
            }

            await Task.Yield();
        }

        async Task EnqueueModerateAsync(int count, TimeSpan delay, string stage)
        {
            for (var i = 0; i < count && index < total; i++, index++)
            {
                var test = ordered[index];
                threadPool.Enqueue(
                    () => RunSingleSync(test, state),
                    $"{test.ClassType.Name}.{test.Method.Name}");

                safeWriteLine($"[LOAD] {stage}: moderate enqueue -> {test.ClassType.Name}.{test.Method.Name}");
                await Task.Delay(delay);
            }
        }

        await EnqueueOneByOneAsync(singleCount, TimeSpan.FromMilliseconds(150), "Stage 1");
        safeWriteLine("[LOAD] Idle interval after Stage 1");
        await Task.Delay(TimeSpan.FromMilliseconds(250));

        await EnqueueBurstAsync(burstCount, "Stage 2");
        safeWriteLine("[LOAD] Idle interval after Stage 2");
        await Task.Delay(TimeSpan.FromMilliseconds(150));

        await EnqueueModerateAsync(moderateCount, TimeSpan.FromMilliseconds(80), "Stage 3");
        safeWriteLine("[LOAD] Idle interval after Stage 3");
        await Task.Delay(TimeSpan.FromMilliseconds(250));

        while (index < total)
        {
            var test = ordered[index++];
            threadPool.Enqueue(
                () => RunSingleSync(test, state),
                $"{test.ClassType.Name}.{test.Method.Name}");

            safeWriteLine($"[LOAD] Stage 4: tail enqueue -> {test.ClassType.Name}.{test.Method.Name}");
            await Task.Delay(TimeSpan.FromMilliseconds(40));
        }
    }

    private static void RunSingleSync(
        DiscoveredTest test,
        RunExecutionState state)
    {
        var instance = CreateTestInstance(test.ClassType, test.SharedContext);
        var sw = Stopwatch.StartNew();
        var displayName = $"{test.ClassType.Name}.{test.Method.Name}({FormatArgs(test.Args)})";

        try
        {
            if (test.TimeoutMilliseconds is int timeoutMs)
            {
                ExecuteWithTimeout(instance, test, timeoutMs);
            }
            else
            {
                ExecuteTestBodyAsync(instance, test).GetAwaiter().GetResult();
            }

            Interlocked.Increment(ref state.Passed);
            state.Results.Enqueue($"PASS  {displayName}  {sw.ElapsedMilliseconds}ms");
        }
        catch (TimeoutException)
        {
            Interlocked.Increment(ref state.Failed);
            state.Results.Enqueue($"FAIL  {displayName}  TIMEOUT after {test.TimeoutMilliseconds}ms");
        }
        catch (TargetInvocationException tie) when (tie.InnerException is AssertionFailedException aex)
        {
            Interlocked.Increment(ref state.Failed);
            state.Results.Enqueue($"FAIL  {displayName}  {aex.Message}");
        }
        catch (TargetInvocationException tie) when (tie.InnerException is TestSkippedException skipex)
        {
            Interlocked.Increment(ref state.Skipped);
            state.Results.Enqueue($"SKIP  {displayName}  {skipex.Message}");
        }
        catch (TargetInvocationException tie)
        {
            Interlocked.Increment(ref state.Failed);
            var inner = tie.InnerException;
            state.Results.Enqueue($"FAIL  {displayName}  EX: {inner?.GetType().Name} {inner?.Message}");
            if (!string.IsNullOrWhiteSpace(inner?.StackTrace))
                state.Results.Enqueue($"      STACK: {TrimStack(inner.StackTrace)}");
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref state.Failed);
            state.Results.Enqueue($"FAIL  {displayName}  EX: {ex.GetType().Name} {ex.Message}");
            if (!string.IsNullOrWhiteSpace(ex.StackTrace))
                state.Results.Enqueue($"      STACK: {TrimStack(ex.StackTrace)}");
        }
    }

    private static void ExecuteWithTimeout(object instance, DiscoveredTest test, int timeoutMs)
    {
        Exception? capturedException = null;

        var executionThread = new Thread(() =>
        {
            try
            {
                ExecuteTestBodyAsync(instance, test).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                capturedException = ex;
            }
        })
        {
            IsBackground = true,
            Name = $"TestExecution-{test.ClassType.Name}.{test.Method.Name}"
        };

        executionThread.Start();

        var finished = executionThread.Join(timeoutMs);

        if (!finished)
            throw new TimeoutException($"Test exceeded timeout of {timeoutMs}ms.");

        if (capturedException != null)
            ExceptionDispatchInfo.Capture(capturedException).Throw();
    }

    private static TestFilterDelegate BuildTestFilterFromConsole()
    {
        Console.WriteLine("Choose test filter:");
        Console.WriteLine("  1 - Run all tests");
        Console.WriteLine("  2 - Filter by category");
        Console.WriteLine("  3 - Filter by author");
        Console.WriteLine("  4 - Filter by minimum priority");
        Console.WriteLine("  5 - Filter by category and author");
        Console.WriteLine("  6 - Filter by category and minimum priority");
        Console.WriteLine("  7 - Filter by author and minimum priority");
        Console.WriteLine("  8 - Filter by category and author and minimum priority");

        var choice = ReadMenuOption("Enter option number: ", min: 1, max: 8);

        return choice switch
        {
            1 => BuildAllTestsFilter(),
            2 => BuildFilterFromConsole(useCategory: true, useAuthor: false, usePriority: false),
            3 => BuildFilterFromConsole(useCategory: false, useAuthor: true, usePriority: false),
            4 => BuildFilterFromConsole(useCategory: false, useAuthor: false, usePriority: true),
            5 => BuildFilterFromConsole(useCategory: true, useAuthor: true, usePriority: false),
            6 => BuildFilterFromConsole(useCategory: true, useAuthor: false, usePriority: true),
            7 => BuildFilterFromConsole(useCategory: false, useAuthor: true, usePriority: true),
            8 => BuildFilterFromConsole(useCategory: true, useAuthor: true, usePriority: true),
            _ => BuildAllTestsFilter()
        };
    }

    private static int ReadMenuOption(string prompt, int min, int max)
    {
        while (true)
        {
            Console.Write(prompt);

            var input = Console.ReadLine()?.Trim();

            if (int.TryParse(input, out var option) && option >= min && option <= max)
            {
                return option;
            }

            Console.WriteLine($"Invalid option. Please enter a number from {min} to {max}.");
        }
    }

    private static TestFilterDelegate BuildAllTestsFilter()
    {
        Console.WriteLine("Selected filter: all tests");
        Console.WriteLine();

        return _ => true;
    }

    private static TestFilterDelegate BuildFilterFromConsole(
        bool useCategory,
        bool useAuthor,
        bool usePriority)
    {
        string? category = null;
        string? author = null;
        int? minPriority = null;

        if (useCategory)
        {
            Console.Write("Enter category, for example Add, Search, Update: ");
            category = Console.ReadLine()?.Trim();

            if (string.IsNullOrWhiteSpace(category))
            {
                Console.WriteLine("Empty category. Category filter will be ignored.");
                category = null;
            }
        }

        if (useAuthor)
        {
            Console.Write("Enter author, for example Antonina: ");
            author = Console.ReadLine()?.Trim();

            if (string.IsNullOrWhiteSpace(author))
            {
                Console.WriteLine("Empty author. Author filter will be ignored.");
                author = null;
            }
        }

        if (usePriority)
        {
            Console.Write("Enter minimum priority, for example 5: ");
            var priorityInput = Console.ReadLine()?.Trim();

            if (int.TryParse(priorityInput, out var parsedPriority))
            {
                minPriority = parsedPriority;
            }
            else
            {
                Console.WriteLine("Invalid priority. Priority filter will be ignored.");
            }
        }

        Console.WriteLine();
        Console.WriteLine("Selected filter:");
        Console.WriteLine($"  category:     {category ?? "-"}");
        Console.WriteLine($"  author:       {author ?? "-"}");
        Console.WriteLine($"  min priority: {(minPriority.HasValue ? minPriority.Value.ToString() : "-")}");
        Console.WriteLine();

        return test =>
        {
            if (category != null &&
                !string.Equals(test.Category, category, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (author != null &&
                !string.Equals(test.Author, author, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (minPriority.HasValue && test.Priority < minPriority.Value)
            {
                return false;
            }

            return true;
        };
    }

    private static IReadOnlyList<object?[]> GetTestArguments(Type testClass, MethodInfo method)
    {
        var paramCount = method.GetParameters().Length;
        var result = new List<object?[]>();

        var testCases = method.GetCustomAttributes<TestCaseAttribute>().ToArray();

        foreach (var testCase in testCases)
        {
            ValidateArgumentsCount(testClass, method, testCase.Args, paramCount);
            result.Add(testCase.Args);
        }

        var sources = method.GetCustomAttributes<TestCaseSourceAttribute>().ToArray();

        foreach (var source in sources)
        {
            var sourceMethod = testClass.GetMethod(
                source.SourceName,
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            if (sourceMethod == null)
                throw new TestDiscoveryException(
                    $"{testClass.Name}.{method.Name}: test case source method '{source.SourceName}' was not found.");

            var sourceValue = sourceMethod.Invoke(null, Array.Empty<object?>());

            if (sourceValue is not System.Collections.IEnumerable enumerable)
                throw new TestDiscoveryException(
                    $"{testClass.Name}.{method.Name}: test case source '{source.SourceName}' must return IEnumerable.");

            foreach (var item in enumerable)
            {
                var args = ConvertSourceItemToArguments(testClass, method, item, paramCount);

                ValidateArgumentsCount(testClass, method, args, paramCount);
                result.Add(args);
            }
        }

        if (paramCount > 0 && result.Count == 0)
            throw new TestDiscoveryException(
                $"{testClass.Name}.{method.Name} has parameters but no [TestCase] or [TestCaseSource] attributes.");

        if (paramCount == 0 && result.Count == 0)
            result.Add(Array.Empty<object?>());

        return result;
    }

    private static object?[] ConvertSourceItemToArguments(
        Type testClass,
        MethodInfo method,
        object? item,
        int paramCount)
    {
        if (item is object?[] args)
            return args;

        if (paramCount == 1)
            return new[] { item };

        throw new TestDiscoveryException(
            $"{testClass.Name}.{method.Name}: each item from [TestCaseSource] must be object?[] for tests with multiple parameters.");
    }

    private static void ValidateArgumentsCount(
        Type testClass,
        MethodInfo method,
        object?[] args,
        int expectedCount)
    {
        if (args.Length != expectedCount)
            throw new TestDiscoveryException(
                $"{testClass.Name}.{method.Name}: arguments count {args.Length} does not match parameters count {expectedCount}.");
    }

    private static void PrintThreadPoolEvent(string eventName, ThreadPoolEventArgs e)
    {
        Console.WriteLine(
            $"[EVENT] {eventName} | " +
            $"time={e.TimestampUtc:HH:mm:ss.fff} | " +
            $"worker={e.WorkerName ?? "-"} | " +
            $"task={e.TaskName ?? "-"} | " +
            $"message={e.Message}");

        if (e.Snapshot != null)
        {
            Console.WriteLine(
                $"        snapshot: workers={e.Snapshot.WorkerCount}, " +
                $"queue={e.Snapshot.QueueLength}, " +
                $"completed={e.Snapshot.CompletedTasks}, " +
                $"failed={e.Snapshot.FailedTasks}, " +
                $"hung={e.Snapshot.HungWorkersDetected}");
        }

        if (e.Exception != null)
        {
            Console.WriteLine($"        exception: {e.Exception.GetType().Name}: {e.Exception.Message}");
        }
    }

    private static void SaveSimulationSummary(
        IReadOnlyCollection<SimulationRunInfo> runs,
        SimulationSummary summary)
    {
        var lines = new List<string>
        {
            "TEST EXECUTION SUMMARY",
            $"DATE: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            $"TOTAL RUNS: {summary.TotalRuns}",
            $"SUCCESSFUL RUNS: {summary.SuccessfulRuns}",
            $"FAILED RUNS: {summary.FailedRuns}",
            $"AVERAGE TIME: {summary.AverageTimeMs:F2} ms",
            $"MIN TIME: {summary.MinTimeMs} ms",
            $"MAX TIME: {summary.MaxTimeMs} ms",
            "",
            "DETAILS:"
        };

        foreach (var run in runs)
        {
            lines.Add(
                $"Run #{run.RunNumber:00} | " +
                $"Min={run.MinThreads}, Max={run.MaxThreads} | " +
                $"PASS={run.Passed}, FAIL={run.Failed}, SKIP={run.Skipped} | " +
                $"TIME={run.ElapsedMilliseconds}ms | REPORT={run.ReportFilePath}");
        }

        var filePath = Path.Combine(AppContext.BaseDirectory, "test-execution-summary.txt");
        File.WriteAllLines(filePath, lines);

        Console.WriteLine();
        Console.WriteLine($"Execution summary saved to: {filePath}");
    }

    private static string SaveRunReport(RunReport report, int? runNumber)
    {
        var lines = new List<string>
        {
            "RUN REPORT",
            $"DATE: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            $"MIN THREADS: {report.MinThreads}",
            $"MAX THREADS: {report.MaxThreads}",
            $"PASS: {report.Passed}",
            $"FAIL: {report.Failed}",
            $"SKIP: {report.Skipped}",
            $"ELAPSED: {report.ElapsedMilliseconds}ms",
            ""
        };

        lines.AddRange(report.Results);

        var reportsDir = Path.Combine(AppContext.BaseDirectory, "run-reports");
        Directory.CreateDirectory(reportsDir);

        var filePath = Path.Combine(
            reportsDir,
            $"run_{runNumber:00}.txt");

        File.WriteAllLines(filePath, lines);
        return filePath;
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

    private static SimulationSummary BuildSimulationSummary(IReadOnlyCollection<SimulationRunInfo> runs)
    {
        if (runs.Count == 0)
        {
            return new SimulationSummary(
                TotalRuns: 0,
                SuccessfulRuns: 0,
                FailedRuns: 0,
                AverageTimeMs: 0,
                MinTimeMs: 0,
                MaxTimeMs: 0);
        }

        var successfulRuns = runs.Count(r => r.Failed == 0);
        var failedRuns = runs.Count(r => r.Failed > 0);

        return new SimulationSummary(
            TotalRuns: runs.Count,
            SuccessfulRuns: successfulRuns,
            FailedRuns: failedRuns,
            AverageTimeMs: runs.Average(r => r.ElapsedMilliseconds),
            MinTimeMs: runs.Min(r => r.ElapsedMilliseconds),
            MaxTimeMs: runs.Max(r => r.ElapsedMilliseconds));
    }

    private static void PrintSimulationSummary(
        IReadOnlyCollection<SimulationRunInfo> runs,
        SimulationSummary summary)
    {
        Console.WriteLine();
        Console.WriteLine("============================================================");
        Console.WriteLine("LOAD SIMULATION SUMMARY");
        Console.WriteLine("============================================================");
        Console.WriteLine($"Total runs: {summary.TotalRuns}");
        Console.WriteLine($"Successful runs: {summary.SuccessfulRuns}");
        Console.WriteLine($"Failed runs: {summary.FailedRuns}");
        Console.WriteLine($"Average time: {summary.AverageTimeMs:F2} ms");
        Console.WriteLine($"Min time: {summary.MinTimeMs} ms");
        Console.WriteLine($"Max time: {summary.MaxTimeMs} ms");
        Console.WriteLine();

        foreach (var run in runs)
        {
            Console.WriteLine(
                $"Run #{run.RunNumber:00} | " +
                $"Min={run.MinThreads}, Max={run.MaxThreads} | " +
                $"PASS={run.Passed}, FAIL={run.Failed}, SKIP={run.Skipped} | " +
                $"TIME={run.ElapsedMilliseconds}ms");
        }
    }

    private static void DumpSharedContextInfo(Type classType, ISharedContext ctx, Action<string> writeLine)
    {
        var ctxType = ctx.GetType();
        var logsProp =
            ctxType.GetProperty("Logs", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
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