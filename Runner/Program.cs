using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Framework;

namespace Runner;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var testAssemblyPath = args.Length > 0 ? args[0] : TryAutoDetectTestsDll();

        if (string.IsNullOrWhiteSpace(testAssemblyPath))
        {
            Console.WriteLine("Usage: Runner <path-to-tests-assembly.dll>");
            Console.WriteLine("Tip: Run from Rider without args is supported, but auto-detection failed.");
            return 2;
        }
        

        testAssemblyPath = Path.GetFullPath(testAssemblyPath);

        if (!File.Exists(testAssemblyPath))
        {
            Console.WriteLine($"Tests assembly not found: {testAssemblyPath}");
            return 2;
        }

        var asm = Assembly.LoadFrom(testAssemblyPath);

        var results = new List<string>();
        var passed = 0;
        var failed = 0;
        var skipped = 0;

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

                Console.WriteLine($"[CTX] {testClass.Name}: shared context created: {useShared.ContextType.Name}");
                await shared.SetUpAsync();
                Console.WriteLine($"[CTX] {testClass.Name}: shared context SetUpAsync() done");
            }
            
            var setupMethods = testClass.GetMethods()
                .Where(m => m.GetCustomAttribute<SetUpAttribute>() != null).ToArray();

            var teardownMethods = testClass.GetMethods()
                .Where(m => m.GetCustomAttribute<TearDownAttribute>() != null).ToArray();
            
            var testMethods = testClass.GetMethods()
                .Where(m => m.GetCustomAttribute<TestAttribute>() != null)
                .Select(m => new
                {
                    Method = m,
                    Priority = m.GetCustomAttribute<PriorityAttribute>()?.Value ?? 0,
                    Ignore = m.GetCustomAttribute<IgnoreAttribute>()
                })
                .OrderByDescending(x => x.Priority)
                .ToArray();

            try
            {
                foreach (var tm in testMethods)
                {
                    if (tm.Ignore != null)
                    {
                        skipped++;
                        results.Add($"SKIP  {testClass.Name}.{tm.Method.Name}  reason='{tm.Ignore.Reason}'");
                        continue;
                    }

                    var paramCount = tm.Method.GetParameters().Length;
                    var cases = tm.Method.GetCustomAttributes<TestCaseAttribute>().ToArray();
                    
                    if (paramCount > 0 && cases.Length == 0)
                        throw new TestDiscoveryException(
                            $"{testClass.Name}.{tm.Method.Name} has parameters but no [TestCase] attributes.");
                    
                    if (cases.Length == 0)
                    {
                        await RunSingle(testClass, tm.Method, setupMethods, teardownMethods, shared,
                            Array.Empty<object?>());
                        continue;
                    }

                    foreach (var c in cases)
                    {
                        var param = c.Args;
                        if (param.Length != paramCount)
                            throw new TestDiscoveryException(
                                $"{testClass.Name}.{tm.Method.Name}: TestCase params count {param.Length} does not match parameters count {paramCount}.");
                        
                        await RunSingle(testClass, tm.Method, setupMethods, teardownMethods, shared, c.Args);
                    }
                }
            }
            finally
            {
                if (shared != null)
                {
                    await shared.TearDownAsync();
                    Console.WriteLine($"[CTX] {testClass.Name}: shared context TearDownAsync() done");
                    
                    DumpSharedContextInfo(testClass, shared);
                }
            }
        }

        foreach (var line in results) Console.WriteLine(line);
        Console.WriteLine(new string('-', 60));
        Console.WriteLine($"TOTAL: {passed + failed + skipped}, PASS: {passed}, FAIL: {failed}, SKIP: {skipped}");

        var outPath = Path.Combine(Environment.CurrentDirectory, "test-results.txt");
        File.WriteAllLines(outPath, results.Concat(new[]
        {
            "------------------------------------------------------------",
            $"TOTAL: {passed + failed + skipped}, PASS: {passed}, FAIL: {failed}, SKIP: {skipped}"
        }));
        Console.WriteLine($"Results saved: {outPath}");

        return failed == 0 ? 0 : 1;



        async Task RunSingle(
            Type classType,
            MethodInfo method,
            MethodInfo[] setUps,
            MethodInfo[] tearDowns,
            ISharedContext? sharedCtx,
            object?[] methodArgs)
        {
            object instance = CreateTestInstance(classType, sharedCtx);
            var sw = Stopwatch.StartNew();

            try
            {
                foreach (var s in setUps) InvokeMaybeAsync(instance, s);

                var ret = method.Invoke(instance, methodArgs);
                if (ret is Task task) await task;

                passed++;
                results.Add($"PASS  {classType.Name}.{method.Name}({FormatArgs(methodArgs)})  {sw.ElapsedMilliseconds}ms");
            }
            catch (TargetInvocationException tie) when (tie.InnerException is AssertionFailedException aex)
            {
                failed++;
                results.Add($"FAIL  {classType.Name}.{method.Name}({FormatArgs(methodArgs)})  {aex.Message}");
            }
            catch (TargetInvocationException tie) when (tie.InnerException is TestSkippedException skipex)
            {
                skipped++;
                results.Add($"SKIP  {classType.Name}.{method.Name}({FormatArgs(methodArgs)})  {skipex.Message}");
            }
            catch (TargetInvocationException tie)
            {
                failed++;
                var inner = tie.InnerException;
                results.Add(
                    $"FAIL  {classType.Name}.{method.Name}({FormatArgs(methodArgs)})  EX: {inner?.GetType().Name} {inner?.Message}");
                if (!string.IsNullOrWhiteSpace(inner?.StackTrace))
                    results.Add($"      STACK: {TrimStack(inner.StackTrace)}");
            }
            catch (Exception ex)
            {
                failed++;
                results.Add($"FAIL  {classType.Name}.{method.Name}({FormatArgs(methodArgs)})  EX: {ex.GetType().Name} {ex.Message}");
                if (!string.IsNullOrWhiteSpace(ex.StackTrace))
                    results.Add($"      STACK: {TrimStack(ex.StackTrace)}");
            }
            finally
            {
                foreach (var t in tearDowns) InvokeMaybeAsync(instance, t);
            }
        }
        
        void InvokeMaybeAsync(object instance, MethodInfo m)
        {
            var r = m.Invoke(instance, Array.Empty<object?>());
            if (r is Task task) task.GetAwaiter().GetResult();
        }

        void DumpSharedContextInfo(Type classType, ISharedContext ctx)
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

            Console.WriteLine($"[CTX] {classType.Name}: Logs ({lines.Count})");
            foreach (var l in lines.Take(10))
                Console.WriteLine($"[CTX]   {l}");

            if (lines.Count > 10)
                Console.WriteLine($"[CTX]   ... ({lines.Count - 10} more)");
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
                var p = c.GetParameters();
                return p.Length == 1 && p[0].ParameterType.IsInstanceOfType(shared);
            });

        if (ctor != null)
            return ctor.Invoke(new object?[] { shared });

        return Activator.CreateInstance(testClass)
               ?? throw new TestDiscoveryException($"No suitable constructor found for {testClass.Name}");
    }

    private static string FormatArgs(object?[]? args)
    {
        if (args == null || args.Length == 0) return "";

        return string.Join(", ", args.Select(a =>
            a == null ? "null" : (a.ToString() ?? "null")));
    }

    private static string TrimStack(string stack)
    {
        var lines = stack.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" | ", lines.Take(5));
    }
}
