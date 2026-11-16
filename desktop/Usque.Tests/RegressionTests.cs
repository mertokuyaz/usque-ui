using System.IO;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Usque.Core;

internal static class RegressionTests
{
    public static (string Name, Func<Task> Run)[] All =>
    [
        ("Profile IDs cannot alias credential files through Windows filename casing", () =>
        {
            using var data = new TestData();
            var store = new PreferencesStore(data.Paths);
            const string id = "abcdefabcdefabcdefabcdefabcdefab";
            Reject(() => store.Save(store.Current with
            {
                Profiles = [new(id, "First"), new(id.ToUpperInvariant(), "Second")], SelectedProfileId = id
            }));
            try { _ = data.Paths.ProfilePath(id.ToUpperInvariant()); throw new Exception("noncanonical ID accepted"); }
            catch (ArgumentException) { }
            return Task.CompletedTask;
        }),
        ("Saved preferences cannot be mutated through shared profile arrays", () =>
        {
            using var data = new TestData();
            var store = new PreferencesStore(data.Paths);
            var candidate = store.Current;
            store.Save(candidate);
            candidate.Profiles[0] = new("default", "Tampered input");
            Assert(store.Current.Profiles[0].Name == "Default", "caller retained mutable ownership of saved preferences");
            var snapshot = store.Current;
            snapshot.Profiles[0] = new("default", "Tampered snapshot");
            Assert(store.Current.Profiles[0].Name == "Default", "reading preferences exposes mutable internal state");
            return Task.CompletedTask;
        }),
        ("Known secrets cannot disable structural token redaction", () =>
        {
            var redactor = new DiagnosticRedactor();
            redactor.AddSecrets(["token", "private_key"]); // Valid profile names can collide with field labels.
            var safe = redactor.Redact("access_token=unrecognised-secret private_key=unrecognised-key");
            Assert(!safe.Contains("unrecognised-secret") && !safe.Contains("unrecognised-key"), "known-secret substitution hid a sensitive field name before masking its value");
            return Task.CompletedTask;
        }),
        ("Known secrets are masked in JSON-escaped log messages", () =>
        {
            var redactor = new DiagnosticRedactor();
            const string secret = "p@ss\"quote\\slash\nline";
            redactor.AddSecrets([secret]);
            var safe = redactor.Redact(JsonSerializer.Serialize(new { message = secret }));
            Assert(!safe.Contains("p@ss") && !safe.Contains("quote"), "JSON escaping bypassed known-secret redaction");
            return Task.CompletedTask;
        }),
        ("Known sensitive phrases and escaped Windows user paths remain masked", () =>
        {
            var redactor = new DiagnosticRedactor();
            const string secret = "sensitive-name 203.0.113.7 private-suffix";
            redactor.AddSecrets([secret]);
            Assert(!redactor.Redact(secret).Contains("sensitive-name"), "generic IP masking prevented a full known-secret match");
            var safe = redactor.Redact(JsonSerializer.Serialize(new { path = @"C:\Users\OtherPrivateUser\profile.json" }));
            Assert(!safe.Contains("OtherPrivateUser"), "JSON-escaped user path leaked an unknown username");
            return Task.CompletedTask;
        }),
        ("Contradictory or duplicate WARP flags are not verified", () =>
        {
            Assert(!WarpHealthCheck.IsWarpTrace("warp=off\nwarp=on\n") && !WarpHealthCheck.IsWarpTrace("warp=on\nwarp=plus\n"), "ambiguous trace was treated as verified");
            return Task.CompletedTask;
        }),
        ("Failed credential removal rolls back both catalogue and profile path", () =>
        {
            using var data = new TestData();
            var store = new PreferencesStore(data.Paths);
            var catalogue = new ProfileCatalog(data.Paths, store)
            {
                DeleteBackup = _ => throw new IOException("simulated antivirus lock")
            };
            var profile = catalogue.Create("Work");
            var path = data.Paths.Profile;
            File.WriteAllText(path, "credentials to preserve");
            try { catalogue.Delete(profile.Id); throw new Exception("deletion should fail"); } catch (IOException) { }
            Assert(store.Current.Profiles.Length == 2 && store.Current.SelectedProfileId == profile.Id &&
                data.Paths.Profile == path && File.ReadAllText(path) == "credentials to preserve", "post-commit failure left the wrong profile selected or lost credentials");
            Assert(!Directory.EnumerateFiles(data.Paths.ProfilesDirectory).Any(p => p.Contains(".delete-")), "rollback left a private backup behind");
            return Task.CompletedTask;
        }),
        ("Disconnect clears stale verified health", async () =>
        {
            var f = new Fixture(true, new GoodHealth());
            await f.Controller.ConnectAsync(new());
            await f.Connect();
            await f.Controller.StopAsync();
            Assert(f.Controller.LastHealth == null, "Offline retained a verified connection result");
        }),
        ("Engine failure clears stale verified health", async () =>
        {
            var f = new Fixture(true, new GoodHealth());
            await f.Controller.ConnectAsync(new());
            await f.Connect();
            var failed = f.WaitFor(ConnectionState.Blocked);
            f.Engine.Events.Writer.Complete(new IOException("simulated process crash"));
            await failed;
            Assert(f.Controller.LastHealth == null, "Blocked retained a verified connection result");
            await f.Controller.StopAsync();
        }),
        ("Initial health verification cannot bypass the transport deadline", async () =>
        {
            var health = new DelayedHealth(delayFirst: true);
            var f = new Fixture(true, health, new(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(30)));
            try
            {
                await f.Controller.ConnectAsync(new());
                f.Engine.Events.Writer.TryWrite(new("ready", NetworkManager.InterfaceName));
                f.Engine.Events.Writer.TryWrite(new("connected"));
                await health.Started.Task;
                await f.WaitFor(ConnectionState.Blocked).WaitAsync(TimeSpan.FromSeconds(2));
                Assert(f.Controller.LastHealth == null && f.Kill.Enabled, "verification timeout must fail closed");
            }
            finally { await f.Controller.StopAsync(); }
        }),
        ("A verification timeout cancels QUIC health before automatic TCP fallback", async () =>
        {
            var health = new DelayedHealth(delayFirst: true);
            var f = new Fixture(true, health, new(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(30)));
            for (var i = 0; i < 2; i++)
            {
                var events = Channel.CreateUnbounded<TunnelEvent>();
                events.Writer.TryWrite(new("ready", NetworkManager.InterfaceName));
                events.Writer.TryWrite(new("connected"));
                f.Engine.Attempts.Enqueue(events);
            }
            try
            {
                await f.Controller.ConnectAsync(new(AutomaticFallback: true));
                await f.WaitFor(ConnectionState.Connected).WaitAsync(TimeSpan.FromSeconds(2));
                Assert(f.Controller.ActiveHttp2 && health.Checks == 2 && f.Engine.Starts == 2, "verification timeout did not fall back to a fresh TCP attempt");
                Assert(!f.Calls.Contains("disarm") && f.Calls.IndexOf("engine-stop") < f.Calls.LastIndexOf("arm"), "fallback overlapped engines or released protection");
            }
            finally { await f.Controller.StopAsync(); }
        }),
        ("Tunnel loss interrupts initial health verification", async () =>
        {
            var health = new DelayedHealth(delayFirst: true);
            var f = new Fixture(true, health);
            try
            {
                await f.Controller.ConnectAsync(new());
                f.Engine.Events.Writer.TryWrite(new("ready", NetworkManager.InterfaceName));
                f.Engine.Events.Writer.TryWrite(new("connected"));
                await health.Started.Task;
                f.Engine.Events.Writer.TryWrite(new("disconnected"));
                await f.WaitFor(ConnectionState.Reconnecting).WaitAsync(TimeSpan.FromSeconds(2));
                Assert(f.Controller.LastHealth == null && f.Kill.Enabled, "lost tunnel retained verified health or released protection");
                f.Engine.Events.Writer.TryWrite(new("connected"));
                await f.WaitFor(ConnectionState.Connected).WaitAsync(TimeSpan.FromSeconds(2));
                Assert(health.Checks == 2, "reconnection did not run a fresh verification");
            }
            finally { await f.Controller.StopAsync(); }
        }),
        ("Periodic health checks do not delay engine failure handling", async () =>
        {
            var health = new DelayedHealth(delayFirst: false);
            var f = new Fixture(true, health, new(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(20)));
            try
            {
                await f.Controller.ConnectAsync(new());
                await f.Connect();
                await health.Started.Task;
                f.Engine.Events.Writer.Complete(new IOException("simulated crash during health check"));
                await f.WaitFor(ConnectionState.Blocked).WaitAsync(TimeSpan.FromSeconds(2));
                Assert(f.Controller.LastHealth == null && f.Kill.Enabled, "engine failure did not clear stale verification and stay protected");
            }
            finally { await f.Controller.StopAsync(); }
        }),
        ("Queued manual health checks are discarded after tunnel loss", async () =>
        {
            var health = new GatedHealth();
            var f = new Fixture(true, health);
            try
            {
                await f.Controller.ConnectAsync(new());
                await f.Connect();
                var first = f.Controller.CheckHealthNowAsync();
                await health.Started.Task;
                var queued = f.Controller.CheckHealthNowAsync();
                f.Engine.Events.Writer.TryWrite(new("disconnected"));
                await f.WaitFor(ConnectionState.Reconnecting).WaitAsync(TimeSpan.FromSeconds(2));
                health.Release.TrySetResult();
                await Task.WhenAll(first, queued);
                Assert(health.Checks == 2 && f.Controller.LastHealth == null, "a stale queued check probed or verified the disconnected tunnel");
            }
            finally { health.Release.TrySetResult(); await f.Controller.StopAsync(); }
        }),
        ("Reconnect waits for a cancelled periodic probe without applying its result", async () =>
        {
            var health = new GatedHealth();
            var f = new Fixture(true, health, new(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(20)));
            try
            {
                await f.Controller.ConnectAsync(new());
                await f.Connect();
                await health.Started.Task;
                f.Engine.Events.Writer.TryWrite(new("disconnected"));
                await f.WaitFor(ConnectionState.Reconnecting).WaitAsync(TimeSpan.FromSeconds(2));
                f.Engine.Events.Writer.TryWrite(new("connected"));
                await f.WaitFor(ConnectionState.Verifying).WaitAsync(TimeSpan.FromSeconds(2));
                Assert(f.Controller.LastHealth == null, "reconnect reused verification from the lost tunnel");
                health.Release.TrySetResult();
                await f.WaitFor(ConnectionState.Connected).WaitAsync(TimeSpan.FromSeconds(2));
                Assert(health.Checks >= 3 && f.Controller.LastHealth?.Healthy == true, "reconnection did not verify the new tunnel");
            }
            finally { health.Release.TrySetResult(); await f.Controller.StopAsync(); }
        }),
        ("Tunnel addresses must be IP literals understood by the Go engine", () =>
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            foreach (var (ipv4, ipv6) in new[] { ("172.16.2", "2606:4700::2"), ("172.16.0.2", "2606:4700::2%3"), ("172.16.0.2", "[2606:4700::2]") })
            {
                using var json = JsonDocument.Parse(JsonSerializer.Serialize(new
                {
                    private_key = Convert.ToBase64String(key.ExportECPrivateKey()), endpoint_pub_key = key.ExportSubjectPublicKeyInfoPem(),
                    endpoint_v4 = "162.159.198.1", ipv4, ipv6, id = "device"
                }));
                Reject(() => ProfileStore.Validate(json.RootElement, false));
            }
            return Task.CompletedTask;
        }),
        ("Startup rollback restores the actual task state, not stale preferences", () =>
        {
            using var data = new TestData();
            var store = new PreferencesStore(data.Paths);
            store.Save(store.Current); // Saved preference is false, but an owned task already exists.
            var task = new ExistingStartup();
            using (var locked = new FileStream(data.Paths.Settings, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                try { StartupPreferences.SetEnabled(store, task, true); throw new Exception("locked settings unexpectedly saved"); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            Assert(task.IsEnabled, "failed preference save disabled a pre-existing startup task");
            return Task.CompletedTask;
        }),
        ("Startup package identity includes every runtime DLL and its path", () =>
        {
            using var data = new TestData();
            var source = CreateBundle(data.Paths.DataDirectory);
            var runtime = Path.Combine(source, "runtime.dll");
            File.WriteAllText(runtime, "runtime version one");
            var first = StartupPackage.CreateManifest(source);
            File.WriteAllText(runtime, "runtime version two");
            var second = StartupPackage.CreateManifest(source);
            Assert(first.Id != second.Id, "a changed framework DLL reused an old startup cache");
            File.Move(runtime, Path.Combine(source, "renamed-runtime.dll"));
            Assert(second.Id != StartupPackage.CreateManifest(source).Id, "package identity ignored file names");
            return Task.CompletedTask;
        }),
        ("Startup cache rejects missing, corrupted and unexpected payload files", () =>
        {
            using var data = new TestData();
            var source = CreateBundle(data.Paths.DataDirectory);
            File.WriteAllText(Path.Combine(source, "config.json"), "private credentials must not be copied");
            var manifest = StartupPackage.CreateManifest(source);
            var target = Path.Combine(data.Paths.DataDirectory, "target");
            Directory.CreateDirectory(target);
            StartupPackage.CopyVerified(source, target, manifest);
            Assert(StartupPackage.IsValid(manifest, target) && !File.Exists(Path.Combine(target, "config.json")), "verified copy failed or copied private data");
            File.WriteAllText(Path.Combine(target, "extra.dll"), "unexpected payload");
            Assert(!StartupPackage.IsValid(manifest, target), "unexpected runtime was accepted");
            File.Delete(Path.Combine(target, "extra.dll"));
            File.WriteAllText(Path.Combine(target, "Usque.Core.dll"), "corrupted runtime");
            Assert(!StartupPackage.IsValid(manifest, target), "corrupted runtime was accepted");
            File.Delete(Path.Combine(target, "Usque.Core.dll"));
            Assert(!StartupPackage.IsValid(manifest, target), "incomplete cache was accepted");
            return Task.CompletedTask;
        }),
        ("Source changes during startup staging are rejected before promotion", () =>
        {
            using var data = new TestData();
            var source = CreateBundle(data.Paths.DataDirectory);
            var manifest = StartupPackage.CreateManifest(source);
            File.WriteAllText(Path.Combine(source, "Usque.Core.dll"), "changed after hashing");
            var target = Path.Combine(data.Paths.DataDirectory, "staging");
            Directory.CreateDirectory(target);
            try { StartupPackage.CopyVerified(source, target, manifest); throw new Exception("changed source was promoted"); }
            catch (IOException) { }
            Assert(!StartupPackage.IsValid(manifest, target), "staging change was not detected");
            return Task.CompletedTask;
        }),
        ("Incomplete startup distributions fail before installation", () =>
        {
            using var data = new TestData();
            var source = CreateBundle(data.Paths.DataDirectory);
            File.Delete(Path.Combine(source, "Scripts", "Network.ps1"));
            Reject(() => StartupPackage.CreateManifest(source));
            return Task.CompletedTask;
        }),
        ("Driver architecture is validated before a VPN session can arm protection", () =>
        {
            using var data = new TestData();
            var path = Path.Combine(data.Paths.DataDirectory, "wintun.dll");
            var bytes = new byte[70];
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, 0x5A4D);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(60), 64);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(64), 0x00004550);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(68), PortableExecutable.Arm64);
            File.WriteAllBytes(path, bytes);
            PortableExecutable.Validate(path, Architecture.Arm64);
            Reject(() => PortableExecutable.Validate(path, Architecture.X64));
            File.WriteAllBytes(path, [1, 2, 3]);
            Reject(() => PortableExecutable.ReadMachine(path));
            return Task.CompletedTask;
        })
    ];

    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action) { try { action(); throw new Exception("invalid preferences accepted"); } catch (InvalidDataException) { } }
    private static string CreateBundle(string directory)
    {
        var source = Path.Combine(directory, "source");
        foreach (var file in new[] { "Usque.Desktop.exe", "Usque.Desktop.dll", "Usque.Core.dll", "Usque.Desktop.deps.json",
            "Usque.Desktop.runtimeconfig.json", "engine/usque.exe", "engine/wintun.dll", "Scripts/Network.ps1" })
        {
            var path = Path.Combine(source, file.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "test payload: " + file);
        }
        return source;
    }
    private sealed class GoodHealth : IConnectionHealth
    {
        public Task<HealthResult> CheckAsync(CancellationToken token) => Task.FromResult(new HealthResult(DateTimeOffset.UtcNow, true, true, true, 1, "Verified test result"));
    }
    private sealed class DelayedHealth(bool delayFirst) : IConnectionHealth
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Checks { get; private set; }
        public async Task<HealthResult> CheckAsync(CancellationToken token)
        {
            if (++Checks == (delayFirst ? 1 : 2))
            {
                Started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return await new GoodHealth().CheckAsync(token);
        }
    }
    private sealed class GatedHealth : IConnectionHealth
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Checks { get; private set; }
        public async Task<HealthResult> CheckAsync(CancellationToken token)
        {
            if (++Checks == 2)
            {
                Started.TrySetResult();
                // Model an I/O completion racing with cancellation.
                await Release.Task;
            }
            return await new GoodHealth().CheckAsync(token);
        }
    }
    private sealed class ExistingStartup : IStartupRegistration
    {
        public bool IsEnabled { get; private set; } = true;
        public void SetEnabled(bool enabled) => IsEnabled = enabled;
    }
    private sealed class TestData : IDisposable
    {
        public AppPaths Paths { get; }
        public TestData()
        {
            var directory = Path.Combine(Path.GetTempPath(), "opencode", "usque-regressions-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            Paths = new(directory);
        }
        public void Dispose() => Directory.Delete(Paths.DataDirectory, true);
    }
}
