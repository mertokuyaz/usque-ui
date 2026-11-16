using System.IO;

internal sealed class TestRunner(TextWriter output, TextWriter error, TimeSpan? testTimeout = null)
{
    private readonly TimeSpan _testTimeout = testTimeout ?? TimeSpan.FromSeconds(10);
    private const string Usage = "Usage: Usque.Tests [--list] [--filter <text>] [--help]";

    public async Task<int> RunAsync(string[] args, (string Name, Func<Task> Run)[] tests)
    {
        bool list = false, help = false;
        string? filter = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--list": list = true; break;
                case "--help": help = true; break;
                case "--filter" when filter == null && i + 1 < args.Length &&
                    !args[i + 1].StartsWith("--", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(args[i + 1]):
                    filter = args[++i];
                    break;
                default:
                    error.WriteLine($"Invalid or incomplete argument: {args[i]}");
                    error.WriteLine(Usage);
                    return 2;
            }
        }
        if (help)
        {
            output.WriteLine(Usage);
            output.WriteLine("--list           List matching tests without running them.");
            output.WriteLine("--filter <text>  Run tests whose names contain text (case-insensitive).");
            return 0;
        }

        var selected = tests.Where(test => filter == null || test.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (selected.Length == 0)
        {
            error.WriteLine(filter == null ? "No tests are registered." : $"No tests match filter: {filter}");
            return 2;
        }
        if (list)
        {
            foreach (var test in selected) output.WriteLine(test.Name);
            output.WriteLine($"{selected.Length} tests listed. No tests were run.");
            return 0;
        }

        int failed = 0, completed = 0;
        foreach (var test in selected)
        {
            using var deadline = new CancellationTokenSource(_testTimeout);
            try
            {
                await test.Run().WaitAsync(deadline.Token);
                output.WriteLine($"PASS  {test.Name}");
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                failed++;
                completed++;
                error.WriteLine($"FAIL  {test.Name}: exceeded {_testTimeout.TotalSeconds:0.###} seconds.");
                // WaitAsync cannot cancel a test delegate. Do not let the next
                // test race unfinished work, shared WPF state or cleanup.
                error.WriteLine("Stopping the suite because the timed-out test may still be running.");
                break;
            }
            catch (Exception ex)
            {
                failed++;
                error.WriteLine($"FAIL  {test.Name}: {ex}");
            }
            completed++;
        }
        output.WriteLine($"\n{completed - failed}/{selected.Length} tests passed. No real networking was modified.");
        if (completed < selected.Length) output.WriteLine($"{selected.Length - completed} tests not run.");
        return failed == 0 ? 0 : 1;
    }
}
