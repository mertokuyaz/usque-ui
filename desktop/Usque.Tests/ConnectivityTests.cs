using System.IO;
using System.Net;
using System.Threading.Channels;
using Usque.Core;

internal static class ConnectivityTests
{
    private static ConnectionTimings Fast => new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30))
        { RetryDelay = TimeSpan.FromMilliseconds(40), MaximumRetryDelay = TimeSpan.FromMilliseconds(150) };

    public static (string Name, Func<Task> Run)[] All =>
    [
        ("Network changes rebuild routes after joining the old engine without disarming", async () =>
        {
            var f = new Fixture(true, new Healthy(), Fast);
            f.Engine.Attempts.Enqueue(Ready()); f.Engine.Attempts.Enqueue(Ready());
            try
            {
                await f.Controller.ConnectAsync(new(AutoReconnect: true));
                await f.WaitFor(ConnectionState.Connected);
                var connected = f.WaitFor(ConnectionState.Connected, requireNew: true);
                f.Controller.NotifyEnvironmentChanged(EnvironmentChange.Network);
                await connected;
                Assert(f.Engine.Starts == 2 && f.Controller.ReconnectCount == 1, "network change was not recovered");
                Assert(f.Calls.Count(c => c == "prepare") == 2 && !f.Calls.Contains("disarm"), "routes were not rebuilt under protection");
                Assert(f.Calls.IndexOf("engine-stop") < f.Calls.LastIndexOf("prepare"), "new routes preceded engine shutdown");
                Assert(f.Controller.LastSuccessfulCheckUtc != null && f.Controller.TransportEstablished, "fresh connection metadata missing");
            }
            finally { await f.Controller.StopAsync(); }
        }),
        ("Suspend stops the engine and waits for resume while keeping protection", async () =>
        {
            var f = new Fixture(true, timings: Fast);
            f.Engine.Attempts.Enqueue(Ready()); f.Engine.Attempts.Enqueue(Ready());
            try
            {
                await f.Controller.ConnectAsync(new(AutoReconnect: true));
                await f.WaitFor(ConnectionState.Connected);
                var suspended = WaitFor(f.Controller, s => s.Detail == Localization.Instance["recovery.suspended"]);
                f.Controller.NotifyEnvironmentChanged(EnvironmentChange.Suspend);
                await suspended;
                await Task.Delay(120);
                Assert(f.Engine.Starts == 1 && f.Calls.Contains("engine-stop") && f.Kill.Enabled, "suspend started a new engine or released protection");
                var connected = f.WaitFor(ConnectionState.Connected, requireNew: true);
                f.Controller.NotifyEnvironmentChanged(EnvironmentChange.Resume);
                await connected;
                Assert(f.Engine.Starts == 2 && !f.Calls.Contains("disarm"), "resume did not recover safely");
            }
            finally { await f.Controller.StopAsync(); }
        }),
        ("Disconnect cancels a retry countdown and environment events cannot restart it", async () =>
        {
            var f = new Fixture(true, timings: Fast with { RetryDelay = TimeSpan.FromSeconds(2) });
            f.Engine.Attempts.Enqueue(Failed()); f.Engine.Attempts.Enqueue(Ready());
            await f.Controller.ConnectAsync(new(AutoReconnect: true));
            await WaitFor(f.Controller, _ => f.Controller.RetryAtUtc != null);
            await f.Controller.StopAsync();
            f.Controller.NotifyEnvironmentChanged(EnvironmentChange.Resume);
            f.Controller.NotifyEnvironmentChanged(EnvironmentChange.Network);
            await Task.Delay(100);
            Assert(f.Engine.Starts == 1 && f.Controller.Status.State == ConnectionState.Offline &&
                f.Controller.RetryAtUtc == null, "cancelled retry restarted the engine");
        }),
        ("Failed TCP retries stay on TCP rather than looping between protocols", async () =>
        {
            var f = new Fixture(true, timings: Fast);
            f.Engine.Attempts.Enqueue(Failed()); f.Engine.Attempts.Enqueue(Failed()); f.Engine.Attempts.Enqueue(Ready());
            try
            {
                await f.Controller.ConnectAsync(new(AutomaticFallback: true, AutoReconnect: true));
                await f.WaitFor(ConnectionState.Connected);
                Assert(f.Engine.StartedOptions.Select(o => o.Http2).SequenceEqual(new[] { false, true, true }), "retry oscillated protocols");
                Assert(f.Controller.ReconnectCount == 2 && f.Kill.Enabled && !f.Calls.Contains("disarm"), "retry count or protection incorrect");
            }
            finally { await f.Controller.StopAsync(); }
        }),
        ("Cleanup failure terminates automatic recovery without lowering protection", async () =>
        {
            var f = new Fixture(true, timings: Fast);
            f.Engine.Attempts.Enqueue(Ready()); f.Engine.Attempts.Enqueue(Ready());
            try
            {
                await f.Controller.ConnectAsync(new(AutoReconnect: true));
                await f.WaitFor(ConnectionState.Connected);
                f.Network.FailCleanup = true;
                var blocked = f.WaitFor(ConnectionState.Blocked);
                f.Controller.NotifyEnvironmentChanged(EnvironmentChange.Network);
                await blocked;
                Assert(f.Engine.Starts == 1 && f.Kill.Enabled && !f.Calls.Contains("disarm"), "failed cleanup retried or disarmed");
            }
            finally { f.Network.FailCleanup = false; await f.Controller.StopAsync(); }
        }),
        ("Automatic recovery can be disabled for an existing session", async () =>
        {
            var f = new Fixture(true, timings: Fast);
            try
            {
                await f.Controller.ConnectAsync(new(AutoReconnect: false));
                await f.Connect();
                f.Controller.NotifyEnvironmentChanged(EnvironmentChange.Network);
                await Task.Delay(100);
                Assert(f.Engine.Starts == 1 && f.Controller.Status.State == ConnectionState.Connected, "disabled observer restarted a session");
            }
            finally { await f.Controller.StopAsync(); }
        }),
        ("Environment recovery cancels pending health and requires fresh verification", async () =>
        {
            var health = new InterruptedHealth();
            var f = new Fixture(true, health, Fast with { HealthInterval = TimeSpan.FromMilliseconds(20) });
            f.Engine.Attempts.Enqueue(Ready()); f.Engine.Attempts.Enqueue(Ready());
            try
            {
                await f.Controller.ConnectAsync(new(AutoReconnect: true));
                await f.WaitFor(ConnectionState.Connected);
                await health.Started.Task;
                var connected = f.WaitFor(ConnectionState.Connected, requireNew: true);
                f.Controller.NotifyEnvironmentChanged(EnvironmentChange.Network);
                await connected;
                Assert(health.Cancelled.Task.IsCompletedSuccessfully && health.Checks >= 3, "old health was reused or not cancelled");
                Assert(f.Controller.LastHealth?.Healthy == true && f.Kill.Enabled, "new path was not verified under protection");
            }
            finally { await f.Controller.StopAsync(); }
        }),
        ("SOCKS5 proxy has no machine-wide network or firewall mutations", () => VerifyProxy(ConnectionMode.Socks5)),
        ("HTTP proxy has no machine-wide network or firewall mutations", () => VerifyProxy(ConnectionMode.HttpProxy)),
        ("Proxy refuses a pre-existing protection lock without releasing it", async () =>
        {
            var f = new Fixture(true);
            f.Kill.Enabled = true;
            await f.Controller.ConnectAsync(new(Mode: ConnectionMode.Socks5));
            await f.WaitFor(ConnectionState.Blocked);
            Assert(f.Kill.Enabled && f.Engine.Starts == 0 && f.Calls.Count == 0, "proxy silently removed protection");
            await f.Controller.StopAsync();
        }),
        ("Proxy refuses a stale network journal without changing it", async () =>
        {
            var f = new Fixture(true);
            f.Network.HasRecoveryState = true;
            await f.Controller.ConnectAsync(new(Mode: ConnectionMode.HttpProxy));
            await f.WaitFor(ConnectionState.Error);
            Assert(f.Network.HasRecoveryState && f.Calls.Count == 0 && f.Engine.Starts == 0, "proxy modified stale session state");
            await f.Controller.StopAsync();
        }),
        ("Proxy startup arguments are loopback-only and never enable physical DNS", () =>
        {
            using var data = new TestData();
            var engine = new UsqueEngine(data.Paths, _ => { });
            foreach (var mode in new[] { ConnectionMode.Socks5, ConnectionMode.HttpProxy })
            {
                var info = engine.CreateStartInfo(new(Mode: mode, ProxyPort: 43210));
                var args = info.ArgumentList.ToList();
                Assert(args.Contains(mode == ConnectionMode.Socks5 ? "socks" : "http-proxy"), "wrong proxy subcommand");
                Assert(args.Contains("127.0.0.1") && args.Contains("43210") && args.Contains("--status-json"), "unsafe bind or missing status");
                Assert(!args.Contains("--interface-name") && !args.Contains("--local-dns") && !args.Contains("--system-dns"), "proxy enabled machine routing or DNS bypass");
            }
            return Task.CompletedTask;
        }),
        ("Proxy health explicitly uses the selected proxy with no local bypass", () =>
        {
            foreach (var mode in new[] { ConnectionMode.Socks5, ConnectionMode.HttpProxy })
            {
                var options = new ConnectOptions(Mode: mode, ProxyPort: 43210);
                using var handler = WarpHealthCheck.CreateHandler(options);
                var proxy = handler.Proxy as WebProxy;
                Assert(handler.UseProxy && proxy?.Address == options.ProxyUri && !proxy.BypassProxyOnLocal &&
                    !proxy.IsBypassed(new Uri("https://1.1.1.1")), "health could use a direct connection");
            }
            using var tunnel = WarpHealthCheck.CreateHandler(new());
            Assert(!tunnel.UseProxy && tunnel.Proxy == null, "tunnel inherited system proxy settings");
            return Task.CompletedTask;
        }),
        ("Proxy startup errors retain actionable port messages rather than JSON parser errors", () =>
        {
            using var data = new TestData();
            var logs = new List<string>();
            var engine = new UsqueEngine(data.Paths, logs.Add);
            var ready = engine.ParseStatus("{\"state\":\"ready\"}");
            Assert(ready.State == "ready" && ready.Interface == null, "proxy readiness requires an OS adapter");
            const string error = "Failed to start SOCKS proxy: listen tcp 127.0.0.1:1080: address already in use";
            try { engine.ParseStatus(error); throw new Exception("engine error was ignored"); }
            catch (IOException ex) { Assert(ex.Message == error && logs.Single() == error, "actionable error was discarded"); }
            return Task.CompletedTask;
        }),
        ("Connection mode, local proxy port and recovery preference survive restart", () =>
        {
            using var data = new TestData();
            var preferences = new PreferencesStore(data.Paths);
            Assert(preferences.Current.Mode == ConnectionMode.Tunnel && preferences.Current.AutoReconnect, "unexpected defaults");
            preferences.Save(preferences.Current with { Mode = ConnectionMode.HttpProxy, ProxyPort = 43210, AutoReconnect = false });
            var reopened = new PreferencesStore(data.Paths).Current;
            Assert(reopened.Mode == ConnectionMode.HttpProxy && reopened.ProxyPort == 43210 && !reopened.AutoReconnect &&
                reopened.KillSwitch, "proxy settings lost tunnel protection preference");
            var original = File.ReadAllText(data.Paths.Settings);
            foreach (var port in new[] { 0, -1, 65536 })
            {
                try { preferences.Save(reopened with { ProxyPort = port }); throw new Exception("bad port accepted"); }
                catch (InvalidDataException) { }
            }
            try { preferences.Save(reopened with { Mode = (ConnectionMode)999 }); throw new Exception("bad mode accepted"); }
            catch (InvalidDataException) { }
            Assert(File.ReadAllText(data.Paths.Settings) == original, "invalid settings overwrote preferences");
            return Task.CompletedTask;
        }),
        ("Diagnostics offline performs local checks only and gives actionable skipped results", async () =>
        {
            var f = new Fixture(true);
            var validations = 0;
            var diagnostics = new ConnectionDiagnostics(f.Controller, _ => validations++, () => true);
            var result = await diagnostics.RunAsync(ConnectionMode.Socks5, CancellationToken.None);
            Assert(result.Mode == ConnectionMode.Socks5 && validations == 1 && result.Checks.Length == 7, "diagnostic checks missing");
            Assert(result.Checks.Take(2).All(c => c.Outcome == DiagnosticOutcome.Passed) &&
                result.Checks.Skip(2).All(c => c.Outcome == DiagnosticOutcome.Skipped && c.AdviceKey.Length > 0), "offline probes not skipped");
            Assert(f.Calls.Count == 0 && f.Engine.Starts == 0, "diagnostics changed networking");
        }),
        ("Diagnostics separates internet, DNS, WARP and optional IPv6 outcomes", async () =>
        {
            var health = new Healthy();
            var f = new Fixture(true, health);
            try
            {
                await f.Controller.ConnectAsync(new());
                await f.Connect();
                health.Result = new(DateTimeOffset.UtcNow, true, false, false, 25, "DNS failed") { InternetReachable = true };
                var calls = f.Calls.Count;
                var result = await new ConnectionDiagnostics(f.Controller, _ => { }, () => true)
                    .RunAsync(ConnectionMode.Tunnel, CancellationToken.None);
                Assert(result.Checks.Single(c => c.NameKey == "diag.internet").Outcome == DiagnosticOutcome.Passed &&
                    result.Checks.Single(c => c.NameKey == "diag.warp").Outcome == DiagnosticOutcome.Passed &&
                    result.Checks.Single(c => c.NameKey == "diag.dns").Outcome == DiagnosticOutcome.Failed &&
                    result.Checks.Single(c => c.NameKey == "diag.ipv6").Outcome == DiagnosticOutcome.Warning, "diagnostic results conflated failures");
                Assert(f.Calls.Count == calls && f.Kill.Enabled, "diagnostics mutated the network");
            }
            finally { await f.Controller.StopAsync(); }
        }),
        ("Diagnostics reports runtime and local network failures without invasive fixes", async () =>
        {
            var f = new Fixture(true);
            var diagnostics = new ConnectionDiagnostics(f.Controller, _ => throw new FileNotFoundException("private-path"), () => false);
            var result = await diagnostics.RunAsync(ConnectionMode.Tunnel, CancellationToken.None);
            Assert(result.Checks.Take(2).All(c => c.Outcome == DiagnosticOutcome.Failed && c.AdviceKey.Length > 0), "missing repair advice");
            Assert(!System.Text.Json.JsonSerializer.Serialize(result).Contains("private-path") && f.Calls.Count == 0, "private data leaked or networking changed");
        }),
        ("Diagnostic export includes mode, recovery count and safe individual checks", () =>
        {
            var checks = new ConnectionDiagnosticSnapshot(DateTimeOffset.UtcNow, ConnectionMode.Socks5,
                [new("diag.dns", DiagnosticOutcome.Failed, "diag.probe.failed", "diag.fix.dns")]);
            var report = DiagnosticsWriter.Create("test", new(ConnectionState.Connected, "status"), false,
                new Preferences { Mode = ConnectionMode.Socks5 }, false, null, new(), [], new(), 3, checks);
            Assert(report.SchemaVersion == 2 && report.Mode == ConnectionMode.Socks5 && report.ReconnectCount == 3 &&
                report.Checks?.Checks.Length == 1 && !report.KillSwitchRequested, "new report metadata missing or misleading");
            return Task.CompletedTask;
        }),
        ("New feature labels and advice have English and Turkish translations", () =>
        {
            var keys = new[] { "mode.label", "mode.tunnel", "proxy.warning", "proxy.usage", "recovery.hint",
                "recovery.countdown", "diag.run", "diag.internet", "diag.dns", "diag.warp", "diag.fix.dns",
                "diag.outcome.Passed", "diag.outcome.Skipped", "health.failed.dns" };
            try
            {
                foreach (var language in new[] { AppLanguage.English, AppLanguage.Turkish })
                {
                    Localization.Instance.SetLanguage(language);
                    foreach (var key in keys) Assert(Localization.Instance[key] != key, "missing translation " + key);
                }
            }
            finally { Localization.Instance.SetLanguage(AppLanguage.English); }
            return Task.CompletedTask;
        })
    ];

    private static async Task VerifyProxy(ConnectionMode mode)
    {
        var f = new Fixture(true, new Healthy(), Fast);
        // Proxy readiness intentionally has no native interface.
        f.Engine.Attempts.Enqueue(Ready(proxy: true)); f.Engine.Attempts.Enqueue(Ready(proxy: true));
        await f.Controller.ConnectAsync(new(Mode: mode, AutoReconnect: true));
        await f.WaitFor(ConnectionState.Connected);
        Assert(f.Controller.LastHealth?.Healthy == true && !f.Controller.ProtectionActive && !f.Network.HasRecoveryState, "proxy inherited tunnel protection");
        var connected = f.WaitFor(ConnectionState.Connected, requireNew: true);
        f.Controller.NotifyEnvironmentChanged(EnvironmentChange.Network);
        await connected;
        await f.Controller.StopAsync();
        Assert(f.Engine.Starts == 2 && f.Calls.All(c => c is "engine-start" or "engine-stop"), "proxy mutated routes, DNS or WFP");
    }
    private static Channel<TunnelEvent> Ready(bool proxy = false)
    {
        var events = Channel.CreateUnbounded<TunnelEvent>();
        events.Writer.TryWrite(new("ready", proxy ? null : NetworkManager.InterfaceName));
        events.Writer.TryWrite(new("connected"));
        return events;
    }
    private static Channel<TunnelEvent> Failed()
    {
        var events = Channel.CreateUnbounded<TunnelEvent>();
        events.Writer.Complete(new IOException("simulated connection failure"));
        return events;
    }
    private static Task WaitFor(ConnectionController controller, Func<ConnectionStatus, bool> predicate)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(ConnectionStatus status)
        {
            if (!predicate(status)) return;
            controller.StatusChanged -= Handler;
            done.TrySetResult();
        }
        controller.StatusChanged += Handler;
        Handler(controller.Status);
        return done.Task;
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class TestData : IDisposable
    {
        public AppPaths Paths { get; }
        public TestData()
        {
            var directory = Path.Combine(Path.GetTempPath(), "opencode", "usque-connectivity-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            Paths = new(directory);
        }
        public void Dispose() => Directory.Delete(Paths.DataDirectory, true);
    }
    private sealed class Healthy : IConnectionHealth
    {
        public HealthResult Result { get; set; } = new(DateTimeOffset.UtcNow, true, true, true, 10, "Verified");
        public Task<HealthResult> CheckAsync(CancellationToken token) => Task.FromResult(Result);
    }
    private sealed class InterruptedHealth : IConnectionHealth
    {
        public int Checks { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<HealthResult> CheckAsync(CancellationToken token)
        {
            if (++Checks == 2)
            {
                Started.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
            }
            return new(DateTimeOffset.UtcNow, true, true, true, 10, "Fresh verification");
        }
    }
}
