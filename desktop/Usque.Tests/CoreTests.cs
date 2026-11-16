using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Usque.Core;
using Usque.Desktop;

// Dependency-free tests: no real routing, firewall rules, or Cloudflare accounts.
internal static class CoreTests
{
    public static (string Name, Func<Task> Run)[] All =>
    [
        ("First connection registers and secures networking in order", async () =>
        {
            var f = new Fixture();
            await f.Controller.ConnectAsync(new());
            await f.Connect();
            Assert(f.Engine.Registrations == 1, "profile registered once");
            Assert(f.Calls.IndexOf("arm") < f.Calls.IndexOf("prepare"), "lockdown precedes networking");
            Assert(f.Calls.IndexOf("allow-tunnel") < f.Calls.IndexOf("configure"), "actual tunnel is allowed first");
            await f.Controller.StopAsync();
            Assert(f.Calls.LastIndexOf("cleanup") < f.Calls.IndexOf("disarm"), "restore routes before removing protection");
        }),
        ("Existing profiles are reused", async () =>
        {
            var f = new Fixture(true);
            await f.Controller.ConnectAsync(new());
            await f.Connect();
            Assert(f.Engine.Registrations == 0, "profile not overwritten");
            await f.Controller.StopAsync();
        }),
        ("Stale DNS and routes are cleaned before first-run registration", async () =>
        {
            var f = new Fixture();
            f.Network.HasRecoveryState = true;
            f.Engine.BeforeRegister = () => Assert(!f.Network.HasRecoveryState, "old tunnel DNS must be removed before contacting Cloudflare");
            await f.Controller.ConnectAsync(new());
            await f.Connect();
            await f.Controller.StopAsync();
        }),
        ("Cancelling a delayed profile load cannot arm protection or start the engine", async () =>
        {
            var f = new Fixture(true);
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Profiles.LoadGate = loaded.Task;
            await f.Controller.ConnectAsync(new());
            var stop = f.Controller.StopAsync();
            loaded.SetResult(); // Simulate an I/O completion racing with cancellation.
            await stop;
            Assert(!f.Calls.Contains("arm") && !f.Calls.Contains("prepare") && f.Engine.Starts == 0,
                "cancelled sessions must not make new network changes");
        }),
        ("A delayed profile load immediately reports Connecting", async () =>
        {
            var f = new Fixture(true);
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Profiles.LoadGate = loaded.Task;
            await f.Controller.ConnectAsync(new());
            Assert(f.Controller.Status.State == ConnectionState.Connecting, "UI must not offer profile import while loading a running session");
            var stop = f.Controller.StopAsync();
            loaded.SetResult();
            await stop;
        }),
        ("Unavailable firewall status does not crash recovery or allow registration", async () =>
        {
            var f = new Fixture();
            f.Kill.FailRead = true;
            f.Controller.Initialize();
            Assert(f.Controller.Status.State == ConnectionState.Error && f.Controller.ProtectionActive,
                "unknown protection must be reported without claiming that traffic is blocked");
            await f.Controller.ConnectAsync(new());
            Assert(f.Engine.Registrations == 0 && !f.Calls.Contains("prepare"), "do not bootstrap with unknown protection");
            f.Kill.FailRead = false;
            await f.Controller.StopAsync();
        }),
        ("Protection bindings use cached state rather than querying native WFP repeatedly", async () =>
        {
            var f = new Fixture(true);
            f.Controller.Initialize();
            var reads = f.Kill.Reads;
            for (var i = 0; i < 50; i++) _ = f.Controller.ProtectionActive;
            Assert(f.Kill.Reads == reads, "UI property reads must not open 50 native firewall sessions");
            await f.Controller.StopAsync();
        }),
        ("An already-cancelled process command never launches a child", async () =>
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try
            {
                await ProcessRunner.RunAsync(ProcessRunner.StartInfo(@"C:\__usque_cancelled_test_missing__.exe"),
                    TimeSpan.FromSeconds(1), cancelled.Token);
                throw new Exception("cancelled command completed");
            }
            catch (OperationCanceledException) { }
        }),
        ("An already-cancelled VPN iterator never launches the engine", async () =>
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var engine = new UsqueEngine(new AppPaths(), _ => { });
            try
            {
                await foreach (var item in engine.RunAsync(new(), cancelled.Token)) { }
                throw new Exception("cancelled engine completed");
            }
            catch (OperationCanceledException) { }
        }),
        ("Process runner captures output from a real non-networking child", async () =>
        {
            var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            var output = await ProcessRunner.RunAsync(ProcessRunner.StartInfo(executable,
                "-NoProfile", "-NonInteractive", "-Command", "[Console]::Out.Write('usque-process-test')"),
                TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert(output == "usque-process-test", "child stdout was not captured correctly");
        }),
        ("Process timeout stops a real non-networking child and reports TimeoutException", async () =>
        {
            var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            try
            {
                await ProcessRunner.RunAsync(ProcessRunner.StartInfo(executable,
                    "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"),
                    TimeSpan.FromMilliseconds(200), CancellationToken.None);
                throw new Exception("sleeping child should time out");
            }
            catch (TimeoutException) { }
        }),
        ("Windows-encoded profile imports are normalized for the Go engine", async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "opencode", "usque-profile-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var paths = new AppPaths(directory);
                var store = new ProfileStore(paths);
                var source = Path.Combine(directory, "source.json");
                foreach (var encoding in new Encoding[] { new UTF8Encoding(true), Encoding.Unicode })
                {
                    await File.WriteAllTextAsync(source, CreateProfileJson(), encoding);
                    await store.ImportAsync(source);
                    var bytes = await File.ReadAllBytesAsync(paths.Profile);
                    using var json = JsonDocument.Parse(bytes); // Go also requires BOM-free UTF-8.
                    Assert((await store.LoadAsync(false, CancellationToken.None)).Endpoint == "162.159.198.1", "imported profile should remain usable");
                }
            }
            finally { Directory.Delete(directory, true); }
        }),
        ("Failed profile replacement cleans temporary credentials", async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "opencode", "usque-profile-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var paths = new AppPaths(directory);
                Directory.CreateDirectory(paths.Profile); // Force File.Move to fail without touching real profiles.
                var source = Path.Combine(directory, "source.json");
                await File.WriteAllTextAsync(source, CreateProfileJson());
                try { await new ProfileStore(paths).ImportAsync(source); throw new Exception("replacement should fail"); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                Assert(Directory.GetFiles(directory).Length == 1, "failed import must not leave temporary private keys behind");
            }
            finally { Directory.Delete(directory, true); }
        }),
        ("Profile validation rejects noncanonical IPv4 endpoints before lockdown", () =>
        {
            using var json = JsonDocument.Parse(CreateProfileJson("127.1"));
            try { ProfileStore.Validate(json.RootElement, false); throw new Exception("shorthand endpoint accepted"); }
            catch (InvalidDataException) { }
            return Task.CompletedTask;
        }),
        ("UI commands report clipboard/browser errors instead of crashing", () =>
        {
            Exception? reported = null;
            var expected = new IOException("simulated clipboard busy");
            var command = new RelayCommand(() => throw expected, ex => reported = ex);
            command.Execute(null);
            Assert(ReferenceEquals(reported, expected), "command failures must reach the UI error handler");
            return Task.CompletedTask;
        }),
        ("Async UI commands reject duplicate execution and recover after errors", async () =>
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int calls = 0;
            var command = new AsyncCommand(async () => { calls++; await gate.Task; }, () => true, _ => throw new Exception("unexpected error"));
            var running = command.ExecuteAsync();
            await command.ExecuteAsync();
            Assert(calls == 1 && !command.CanExecute(null), "double click must not start a second action");
            gate.SetResult();
            await running;
            Assert(command.CanExecute(null), "command must become available after completion");
            Exception? reported = null;
            var expected = new IOException("simulated preflight failure");
            var failing = new AsyncCommand(() => Task.FromException(expected), () => true, ex => reported = ex);
            await failing.ExecuteAsync();
            Assert(ReferenceEquals(reported, expected) && failing.CanExecute(null), "async failures must be reported and allow retry");
        }),
        ("Tunnel loss keeps protection armed throughout reconnect", async () =>
        {
            var f = new Fixture(true);
            await f.Controller.ConnectAsync(new());
            await f.Connect();
            var wait = f.WaitFor(ConnectionState.Reconnecting);
            f.Engine.Events.Writer.TryWrite(new("disconnected"));
            await wait;
            Assert(f.Kill.IsEnabled && !f.Calls.Contains("disarm"), "no fail-open on loss");
            var reconnected = f.WaitFor(ConnectionState.Connected);
            f.Engine.Events.Writer.TryWrite(new("connected"));
            await reconnected;
            await f.Controller.StopAsync();
        }),
        ("Engine crash remains blocked until explicit recovery", async () =>
        {
            var f = new Fixture(true);
            await f.Controller.ConnectAsync(new());
            await f.Connect();
            var wait = f.WaitFor(ConnectionState.Blocked);
            f.Engine.Events.Writer.Complete(new IOException("simulated crash"));
            await wait;
            Assert(f.Kill.IsEnabled, "crash did not disarm filters");
            await f.Controller.StopAsync();
            Assert(!f.Kill.IsEnabled && f.Controller.Status.State == ConnectionState.Offline, "explicit recovery worked");
        }),
        ("Partial adapter setup fails closed", async () =>
        {
            var f = new Fixture(true);
            f.Network.FailConfigure = true;
            await f.Controller.ConnectAsync(new());
            var wait = f.WaitFor(ConnectionState.Blocked);
            f.Engine.Events.Writer.TryWrite(new("ready", NetworkManager.InterfaceName));
            await wait;
            Assert(f.Kill.IsEnabled, "setup error left protection active");
            await f.Controller.StopAsync();
        }),
        ("Cleanup failure never removes protection", async () =>
        {
            var f = new Fixture(true);
            await f.Controller.ConnectAsync(new());
            await f.Connect();
            f.Network.FailCleanup = true;
            try { await f.Controller.StopAsync(); throw new Exception("cleanup should fail"); }
            catch (IOException) { }
            Assert(f.Kill.IsEnabled && !f.Calls.Contains("disarm"), "failed cleanup remains fail-closed");
            f.Network.FailCleanup = false;
            await f.Controller.StopAsync();
        }),
        ("Cancel registration never starts networking", async () =>
        {
            var f = new Fixture();
            f.Engine.HoldRegistration = true;
            await f.Controller.ConnectAsync(new());
            await f.Controller.StopAsync();
            Assert(!f.Calls.Contains("prepare") && !f.Calls.Contains("arm"), "cancelled before route changes");
        }),
        ("Recovery never silently releases old filters", async () =>
        {
            var f = new Fixture();
            f.Kill.Enabled = true;
            f.Controller.Initialize();
            Assert(f.Controller.Status.State == ConnectionState.Blocked, "startup detects persistent filters");
            var wait = f.WaitFor(ConnectionState.Blocked, requireNew: true);
            await f.Controller.ConnectAsync(new(KillSwitch: false));
            await wait;
            Assert(f.Engine.Registrations == 0 && f.Kill.IsEnabled, "missing profile cannot lower protection to register");
            await f.Controller.StopAsync();
        }),
        ("Connected requires adapter readiness and configured routing", async () =>
        {
            var f = new Fixture(true);
            await f.Controller.ConnectAsync(new());
            f.Engine.Events.Writer.TryWrite(new("connected"));
            await f.Connect();
            Assert(f.Calls.Contains("configure"), "adapter readiness required");
            await f.Controller.StopAsync();
        }),
        ("Firewall blocks both families at ALE and transport layers", () =>
        {
            var rules = KillSwitchPolicy.Build("162.159.198.1", false, "engine.exe", "svchost.exe");
            Assert(rules.Count(r => !r.Permit) == 4, "four unconditional block filters");
            Assert(rules.Where(r => !r.Permit).All(r => r.Endpoint == null && r.Application == null && r.TunnelLuid == null), "blocks are unconditional");
            Assert(rules.Where(r => r.Name == "Endpoint").All(r => !r.Layer.IPv6 && r.Protocol == 17 && r.RemotePort == 443), "exact IPv4 QUIC endpoint only");
            Assert(rules.Single(r => r.Name == "Endpoint" && r.Layer.ApplicationAware).Application == "engine.exe", "ALE endpoint is application-restricted");
            Assert(rules.All(r => r.RemotePort != 53 && r.RemotePort != 853), "no direct DNS exceptions");
            Assert(KillSwitchPolicy.Build("162.159.198.2", true, "engine", "host").Where(r => r.Name == "Endpoint").All(r => r.Protocol == 6), "HTTP/2 permits TCP only");
            return Task.CompletedTask;
        }),
        ("Invalid profiles fail without silent re-registration", () =>
        {
            using var missing = JsonDocument.Parse("{}");
            try { ProfileStore.Validate(missing.RootElement, false); throw new Exception("invalid profile accepted"); }
            catch (InvalidDataException) { }
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var valid = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                private_key = Convert.ToBase64String(key.ExportECPrivateKey()), endpoint_pub_key = key.ExportSubjectPublicKeyInfoPem(),
                endpoint_v4 = "162.159.198.1", ipv4 = "172.16.0.2", ipv6 = "2606:4700::2", id = "device"
            }));
            Assert(ProfileStore.Validate(valid.RootElement, true).Endpoint == "162.159.198.2", "upstream HTTP/2 fallback");
            return Task.CompletedTask;
        }),
        ("WFP native ABI matches 64-bit Windows SDK", () =>
        {
            Assert(IntPtr.Size == 8, "64-bit Windows required");
            Assert(Marshal.SizeOf<WfpKillSwitch.Native.Value>() == 16, "FWP_VALUE0 size");
            Assert(Marshal.SizeOf<WfpKillSwitch.Native.Condition>() == 40, "FWPM_FILTER_CONDITION0 size");
            Assert(Marshal.SizeOf<WfpKillSwitch.Native.Filter>() == 200, "FWPM_FILTER0 size");
            Assert(Marshal.OffsetOf<WfpKillSwitch.Native.Filter>("Conditions").ToInt32() == 120, "condition pointer offset");
            Assert(Marshal.OffsetOf<WfpKillSwitch.Native.Filter>("Context").ToInt32() == 152, "union alignment");
            Assert(Marshal.SizeOf<WfpKillSwitch.Native.SubLayer>() == 72, "FWPM_SUBLAYER0 size");
            return Task.CompletedTask;
        })
    ];

    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static string CreateProfileJson(string endpoint = "162.159.198.1")
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return JsonSerializer.Serialize(new
        {
            private_key = Convert.ToBase64String(key.ExportECPrivateKey()), endpoint_pub_key = key.ExportSubjectPublicKeyInfoPem(),
            endpoint_v4 = endpoint, ipv4 = "172.16.0.2", ipv6 = "2606:4700::2", id = "device"
        });
    }
}
