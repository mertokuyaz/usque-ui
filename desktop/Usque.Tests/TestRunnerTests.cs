using System.IO;

internal static class TestRunnerTests
{
    public static (string Name, Func<Task> Run)[] All =>
    [
        ("Runner lists matching tests without invoking delegates", async () =>
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var runner = new TestRunner(output, error);
            var code = await runner.RunAsync(["--list", "--filter", "HEALTH"],
                [("health probe", () => throw new Exception("listing ran a test")), ("routing", () => throw new Exception("filter ignored"))]);
            Assert(code == 0 && output.ToString().Contains("health probe") && !output.ToString().Contains("routing") && error.ToString() == "", "listing or case-insensitive filtering failed");
        }),
        ("Runner executes only matching tests", async () =>
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var calls = 0;
            var code = await new TestRunner(output, error).RunAsync(["--filter", "health"],
                [("health probe", () => { calls++; return Task.CompletedTask; }), ("routing", () => throw new Exception("filter ignored"))]);
            Assert(code == 0 && calls == 1 && output.ToString().Contains("1/1 tests passed"), "filter did not isolate the selected test");
        }),
        ("Runner rejects invalid arguments before executing any tests", async () =>
        {
            foreach (var args in new string[][] { ["--unknown"], ["--filter"], ["--filter", ""], ["--filter", "--list"], ["--filter", "a", "--filter", "b"] })
            {
                using var output = new StringWriter();
                using var error = new StringWriter();
                var calls = 0;
                var code = await new TestRunner(output, error).RunAsync(args, [("test", () => { calls++; return Task.CompletedTask; })]);
                Assert(code == 2 && calls == 0 && error.ToString().Contains("Usage:"), "invalid arguments silently ran tests or succeeded");
            }
        }),
        ("Runner reports unmatched filters as an error", async () =>
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var code = await new TestRunner(output, error).RunAsync(["--filter", "missing"], [("test", () => Task.CompletedTask)]);
            Assert(code == 2 && error.ToString().Contains("No tests match"), "an empty selection silently passed");
        }),
        ("Runner help never executes tests", async () =>
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var code = await new TestRunner(output, error).RunAsync(["--help"], [("test", () => throw new Exception("help ran a test"))]);
            Assert(code == 0 && output.ToString().Contains("Usage:") && error.ToString() == "", "help failed");
        }),
        ("Runner reports failures and still runs subsequent tests", async () =>
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var calls = 0;
            var code = await new TestRunner(output, error).RunAsync([], [
                ("synchronous failure", () => throw new IOException("sync failure")),
                ("asynchronous failure", () => Task.FromException(new IOException("async failure"))),
                ("success", () => { calls++; return Task.CompletedTask; })]);
            Assert(code == 1 && calls == 1 && output.ToString().Contains("1/3 tests passed") &&
                error.ToString().Contains("sync failure") && error.ToString().Contains("async failure"), "failure reporting or exit code was incorrect");
        }),
        ("Runner stops after a timeout rather than overlapping tests", async () =>
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            try
            {
                var code = await new TestRunner(output, error, TimeSpan.FromMilliseconds(50)).RunAsync([], [
                    ("pending", () => gate.Task), ("must not run", () => { calls++; return Task.CompletedTask; })]);
                Assert(code == 1 && calls == 0 && error.ToString().Contains("timed-out test may still be running") &&
                    output.ToString().Contains("1 tests not run"), "timeout allowed concurrent work or returned success");
            }
            finally { gate.TrySetResult(); }
        })
    ];

    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
}
