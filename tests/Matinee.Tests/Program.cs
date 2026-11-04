using System.Diagnostics;
using System.Runtime.CompilerServices;
using Matinee.Tests;

UnitTests.Register();
AdaptiveTests.Register();
EndToEndTests.Register();
UplinkTests.Register();
return await T.RunAsync(args.Length > 0 ? args[0] : null);

namespace Matinee.Tests
{
    /// <summary>Minimal test runner: no dependencies, no reflection.</summary>
    internal static class T
    {
        private static readonly List<(string Name, Func<Task> Body)> Tests = new();

        public static void Add(string name, Func<Task> body) => Tests.Add((name, body));
        public static void Add(string name, Action body) => Tests.Add((name, () => { body(); return Task.CompletedTask; }));

        public static async Task<int> RunAsync(string? filter)
        {
            int pass = 0, fail = 0;
            var sw = Stopwatch.StartNew();
            foreach (var (name, body) in Tests)
            {
                if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                var t = Stopwatch.StartNew();
                try
                {
                    var task = body();
                    if (await Task.WhenAny(task, Task.Delay(20_000)) != task) throw new TimeoutException("test exceeded 20s");
                    await task;
                    pass++;
                    Console.WriteLine($"  ok   {name} ({t.ElapsedMilliseconds} ms)");
                }
                catch (Exception e)
                {
                    fail++;
                    Console.WriteLine($"  FAIL {name}: {e.GetType().Name}: {e.Message}");
                }
            }
            Console.WriteLine($"{pass} passed, {fail} failed in {sw.Elapsed.TotalSeconds:0.0}s");
            return fail == 0 ? 0 : 1;
        }

        public static void Eq<TV>(TV expected, TV actual, [CallerArgumentExpression(nameof(actual))] string? expr = null)
        {
            if (!EqualityComparer<TV>.Default.Equals(expected, actual))
                throw new Exception($"{expr}: expected <{expected}>, got <{actual}>");
        }

        public static void False(bool cond, [CallerArgumentExpression(nameof(cond))] string? expr = null)
        {
            if (cond) throw new Exception($"expected false: {expr}");
        }

        public static void True(bool cond, [CallerArgumentExpression(nameof(cond))] string? expr = null)
        {
            if (!cond) throw new Exception($"expected true: {expr}");
        }
    }
}
