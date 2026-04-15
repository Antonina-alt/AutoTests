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
        int? TimeoutMilliseconds);

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
        string BlockName,
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
    
    private sealed record BlockSummary(
        string BlockName,
        int MinThreads,
        int MaxThreads,
        int RunsCount,
        double AverageTimeMs,
        long MinTimeMs,
        long MaxTimeMs,
        double DeltaVsBaselineMs,
        double DeltaVsBaselinePercent);

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
        Console.WriteLine();
        Console.WriteLine("============================================================");
        Console.WriteLine("LOAD SIMULATION AND TEST EXECUTION");
        Console.WriteLine("Uneven load: single runs, bursts, pauses");
        Console.WriteLine("Total planned runs: 50");
        Console.WriteLine("============================================================");
        Console.WriteLine();

        var runs = new List<SimulationRunInfo>();
        var runNumber = 0;

        async Task RunBlockAsync(
            string blockName,
            int count,
            int minThreads,
            int maxThreads,
            TimeSpan delayBetweenRuns)
        {
            Console.WriteLine();
            Console.WriteLine("------------------------------------------------------------");
            Console.WriteLine($"BLOCK: {blockName}");
            Console.WriteLine($"Runs: {count}");
            Console.WriteLine($"MinThreads: {minThreads}, MaxThreads: {maxThreads}");
            Console.WriteLine($"Delay between runs: {delayBetweenRuns.TotalMilliseconds} ms");
            Console.WriteLine("------------------------------------------------------------");

            for (int i = 0; i < count; i++)
            {
                runNumber++;

                Console.WriteLine();
                Console.WriteLine($"[SIM] Starting run #{runNumber:00} ({blockName})");

                var report = await RunSuiteWithDynamicPoolAsync(asm, minThreads, maxThreads);
                var reportPath = SaveRunReport(report, runNumber, blockName);

                var runInfo = new SimulationRunInfo(
                    RunNumber: runNumber,
                    BlockName: blockName,
                    MinThreads: minThreads,
                    MaxThreads: maxThreads,
                    Passed: report.Passed,
                    Failed: report.Failed,
                    Skipped: report.Skipped,
                    ElapsedMilliseconds: report.ElapsedMilliseconds,
                    ReportFilePath: reportPath);

                runs.Add(runInfo);

                Console.WriteLine(
                    $"[SIM] Run #{runInfo.RunNumber:00} finished | " +
                    $"PASS={runInfo.Passed}, FAIL={runInfo.Failed}, SKIP={runInfo.Skipped}, " +
                    $"TIME={runInfo.ElapsedMilliseconds}ms");

                if (delayBetweenRuns > TimeSpan.Zero)
                    await Task.Delay(delayBetweenRuns);
            }
        }

        async Task PauseAsync(string title, TimeSpan duration)
        {
            Console.WriteLine();
            Console.WriteLine($"*** {title}: {duration.TotalSeconds:0.#} sec ***");
            await Task.Delay(duration);
        }
        
        await RunBlockAsync(
            blockName: "Baseline",
            count: 5,
            minThreads: 1,
            maxThreads: 1,
            delayBetweenRuns: TimeSpan.FromMilliseconds(300));

        await PauseAsync("Idle interval", TimeSpan.FromMilliseconds(500));

        await RunBlockAsync(
            blockName: "PeakBurst",
            count: 20,
            minThreads: 2,
            maxThreads: 6,
            delayBetweenRuns: TimeSpan.FromMilliseconds(100));

        await PauseAsync("Idle interval", TimeSpan.FromMilliseconds(400));

        await RunBlockAsync(
            blockName: "SparseRuns",
            count: 5,
            minThreads: 1,
            maxThreads: 3,
            delayBetweenRuns: TimeSpan.FromMilliseconds(400));

        await PauseAsync("Idle interval", TimeSpan.FromMilliseconds(500));

        await RunBlockAsync(
            blockName: "ModerateLoad",
            count: 10,
            minThreads: 2,
            maxThreads: 4,
            delayBetweenRuns: TimeSpan.FromMilliseconds(150));

        await PauseAsync("Idle interval", TimeSpan.FromMilliseconds(400));

        await RunBlockAsync(
            blockName: "FinalBurst",
            count: 10,
            minThreads: 2,
            maxThreads: 6,
            delayBetweenRuns: TimeSpan.FromMilliseconds(80));

        var summary = BuildSimulationSummary(runs);
        var blockSummaries = BuildBlockSummaries(runs);

        PrintSimulationSummary(runs, summary);
        PrintBlockComparisonTable(blockSummaries);
        SaveSimulationSummary(runs, summary);

        return runs.Any(r => r.Failed > 0) ? 1 : 0;
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
                $"Run #{run.RunNumber:00} | Block={run.BlockName} | " +
                $"Min={run.MinThreads}, Max={run.MaxThreads} | " +
                $"PASS={run.Passed}, FAIL={run.Failed}, SKIP={run.Skipped} | " +
                $"TIME={run.ElapsedMilliseconds}ms");
        }
    }

    private static async Task<RunReport> RunSuiteWithDynamicPoolAsync(
        Assembly asm,
        int minThreads,
        int maxThreads)
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
                        TimeoutMilliseconds = m.GetCustomAttribute<TimeoutAttribute>()?.Milliseconds
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

            using var pool = new DynamicThreadPool.DynamicThreadPool(new ThreadPoolOptions
            {
                MinThreads = minThreads,
                MaxThreads = maxThreads,
                IdleTimeout = TimeSpan.FromSeconds(2),
                QueueWaitThreshold = TimeSpan.FromMilliseconds(300),
                MonitorPeriod = TimeSpan.FromSeconds(1),
                EnableLogging = true
            });

            await EnqueueTestsWithUnevenLoadAsync(
                discoveredTests,
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
    
    private static IReadOnlyList<BlockSummary> BuildBlockSummaries(
        IReadOnlyCollection<SimulationRunInfo> runs)
    {
        var grouped = runs
            .GroupBy(r => new { r.BlockName, r.MinThreads, r.MaxThreads })
            .Select(g => new
            {
                g.Key.BlockName,
                g.Key.MinThreads,
                g.Key.MaxThreads,
                RunsCount = g.Count(),
                AverageTimeMs = g.Average(x => x.ElapsedMilliseconds),
                MinTimeMs = g.Min(x => x.ElapsedMilliseconds),
                MaxTimeMs = g.Max(x => x.ElapsedMilliseconds)
            })
            .OrderBy(x => x.BlockName)
            .ToList();

        var baseline = grouped.FirstOrDefault(x => x.MinThreads == 1 && x.MaxThreads == 1);
        var baselineAverage = baseline?.AverageTimeMs ?? 0;

        return grouped
            .Select(x =>
            {
                var delta = baseline is null ? 0 : x.AverageTimeMs - baselineAverage;
                var percent = baseline is null || baselineAverage == 0
                    ? 0
                    : delta / baselineAverage * 100.0;

                return new BlockSummary(
                    BlockName: x.BlockName,
                    MinThreads: x.MinThreads,
                    MaxThreads: x.MaxThreads,
                    RunsCount: x.RunsCount,
                    AverageTimeMs: x.AverageTimeMs,
                    MinTimeMs: x.MinTimeMs,
                    MaxTimeMs: x.MaxTimeMs,
                    DeltaVsBaselineMs: delta,
                    DeltaVsBaselinePercent: percent);
            })
            .ToList();
    }
    
    private static void PrintBlockComparisonTable(IReadOnlyList<BlockSummary> summaries)
    {
        Console.WriteLine();
        Console.WriteLine("============================================================");
        Console.WriteLine("BLOCK COMPARISON TABLE");
        Console.WriteLine("============================================================");
        Console.WriteLine(
            $"{"Block",-16} {"Min",4} {"Max",4} {"Runs",4} {"Avg(ms)",10} {"Min(ms)",10} {"Max(ms)",10} {"Delta(ms)",12} {"Delta(%)",10}");

        foreach (var s in summaries)
        {
            Console.WriteLine(
                $"{s.BlockName,-16} " +
                $"{s.MinThreads,4} " +
                $"{s.MaxThreads,4} " +
                $"{s.RunsCount,4} " +
                $"{s.AverageTimeMs,10:F2} " +
                $"{s.MinTimeMs,10} " +
                $"{s.MaxTimeMs,10} " +
                $"{s.DeltaVsBaselineMs,12:F2} " +
                $"{s.DeltaVsBaselinePercent,10:F2}");
        }
    }

    private static void SaveSimulationSummary(
        IReadOnlyCollection<SimulationRunInfo> runs,
        SimulationSummary summary)
    {
        var lines = new List<string>
        {
            "LOAD SIMULATION SUMMARY",
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
        
        lines.Add("");
        lines.Add("BLOCK COMPARISON TABLE:");

        var blockSummaries = BuildBlockSummaries(runs);

        lines.Add(
            $"{"Block",-16} {"Min",4} {"Max",4} {"Runs",4} {"Avg(ms)",10} {"Min(ms)",10} {"Max(ms)",10} {"DeltaVsBaseline",16} {"DeltaPercent",14}");

        foreach (var s in blockSummaries)
        {
            lines.Add(
                $"{s.BlockName,-16} " +
                $"{s.MinThreads,4} " +
                $"{s.MaxThreads,4} " +
                $"{s.RunsCount,4} " +
                $"{s.AverageTimeMs,10:F2} " +
                $"{s.MinTimeMs,10} " +
                $"{s.MaxTimeMs,10} " +
                $"{s.DeltaVsBaselineMs,16:F2} " +
                $"{s.DeltaVsBaselinePercent,14:F2}");
        }

        foreach (var run in runs)
        {
            lines.Add(
                $"Run #{run.RunNumber:00} | Block={run.BlockName} | " +
                $"Min={run.MinThreads}, Max={run.MaxThreads} | " +
                $"PASS={run.Passed}, FAIL={run.Failed}, SKIP={run.Skipped} | " +
                $"TIME={run.ElapsedMilliseconds}ms | REPORT={run.ReportFilePath}");
        }

        var filePath = Path.Combine(AppContext.BaseDirectory, "load-simulation-summary.txt");
        File.WriteAllLines(filePath, lines);

        Console.WriteLine();
        Console.WriteLine($"Simulation summary saved to: {filePath}");
    }
    
    private static string SaveRunReport(RunReport report, int? runNumber, string? blockName)
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

        string filePath;

        if (runNumber.HasValue && !string.IsNullOrWhiteSpace(blockName))
        {
            var reportsDir = Path.Combine(AppContext.BaseDirectory, "simulation-reports");
            Directory.CreateDirectory(reportsDir);

            filePath = Path.Combine(
                reportsDir,
                $"run_{runNumber.Value:00}_{blockName}.txt");
        }
        else
        {
            filePath = Path.Combine(AppContext.BaseDirectory, "run-report.txt");
        }

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