using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using Usque.Core;

internal static class FeatureTests
{
    public static (string Name, Func<Task> Run)[] All =>
    [
        ("Preferences survive restart without enabling automatic startup by default", () =>
        {
            using var data = new TestData();
            var store = new PreferencesStore(data.Paths);
            Assert(store.Current.KillSwitch && !store.Current.AutoConnect && !store.Current.StartWithWindows &&
                !store.Current.MinimizeToTray, "safe defaults");
            store.Save(store.Current with { KillSwitch = false, Transport = TransportMode.Http2, MinimizeToTray = true });
            var reopened = new PreferencesStore(data.Paths);
            Assert(!reopened.Current.KillSwitch && reopened.Current.Transport == TransportMode.Http2 && reopened.Current.MinimizeToTray, "saved preferences were lost");
            return Task.CompletedTask;
        }),
        ("Invalid settings and automatic connection without consent are rejected", () =>
        {
            using var data = new TestData();
            var store = new PreferencesStore(data.Paths);
            store.Save(store.Current);
            var original = File.ReadAllText(data.Paths.Settings);
            Reject(() => store.Save(store.Current with { AutoConnect = true }));
            Reject(() => store.Save(store.Current with { Profiles = [new("../../escape", "Bad")], SelectedProfileId = "../../escape" }));
            Reject(() => store.Save(store.Current with { Transport = (TransportMode)999 }));
            Assert(File.ReadAllText(data.Paths.Settings) == original, "failed validation overwrote settings");
            File.WriteAllText(data.Paths.Settings, "{broken");
            try { _ = new PreferencesStore(data.Paths); throw new Exception("corrupt settings were accepted"); } catch (JsonException) { }
            Assert(File.ReadAllText(data.Paths.Settings) == "{broken", "corrupt settings were silently replaced");
            return Task.CompletedTask;
        }),
        ("Profile creation, selection, rename and deletion preserve unrelated credentials", () =>
        {
            using var data = new TestData();
            var store = new PreferencesStore(data.Paths);
            var catalogue = new ProfileCatalog(data.Paths, store);
            var originalPath = data.Paths.Profile;
            File.WriteAllText(originalPath, "original credentials");
            var created = catalogue.Create("Work");
            Assert(!File.Exists(data.Paths.Profile) && data.Paths.Profile != originalPath, "new profiles should be empty and isolated");
            File.WriteAllText(data.Paths.Profile, "work credentials");
            catalogue.Rename(created.Id, "Office");
            catalogue.Select("default");
            Assert(File.ReadAllText(data.Paths.Profile) == "original credentials", "switching changed the original profile");
            catalogue.Select(created.Id);
            catalogue.Delete(created.Id);
            Assert(store.Current.SelectedProfileId == "default" && File.ReadAllText(originalPath) == "original credentials", "delete did not preserve default profile");
            Assert(!File.Exists(data.Paths.ProfilePath(created.Id)), "deleted managed credentials remain");
            try { catalogue.Delete("default"); throw new Exception("last profile was deleted"); } catch (InvalidOperationException) { }
            return Task.CompletedTask;
        }),
        ("Profile changes are blocked while the session guard is active", () =>
        {
            using var data = new TestData();
            var store = new PreferencesStore(data.Paths);
            var catalogue = new ProfileCatalog(data.Paths, store, () => false);
            try { catalogue.Create("Denied"); throw new Exception("session guard ignored"); } catch (InvalidOperationException) { }
            Assert(store.Current.Profiles.Length == 1 && !Directory.Exists(data.Paths.ProfilesDirectory), "guard ran after filesystem mutation");
            return Task.CompletedTask;
        }),
        ("Failed catalogue save restores credentials moved for deletion", () =>
        {
            using var data = new TestData();
            var store = new PreferencesStore(data.Paths);
            var catalogue = new ProfileCatalog(data.Paths, store);
            var created = catalogue.Create("Work");
            var profilePath = data.Paths.Profile;
            File.WriteAllText(profilePath, "keep this key");
            using (var locked = new FileStream(data.Paths.Settings, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                try { catalogue.Delete(created.Id); throw new Exception("locked settings should fail"); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            Assert(File.ReadAllText(profilePath) == "keep this key" && store.Current.Profiles.Length == 2, "failed deletion lost credentials");
            return Task.CompletedTask;
        }),
        ("Redaction masks tokens, private keys, IDs, IPs and user paths", () =>
        {
            var redactor = new DiagnosticRedactor();
            redactor.AddSecrets(["unlabelled-account-secret"]);
            var text = "Authorization: Bearer bearer-secret\n{\"access_token\":\"json-secret\",\"private_key\":\"key-secret\"}\n" +
                "unlabelled-account-secret 12345678-abcd-1234-abcd-123456789abc 203.0.113.7 2606:4700::1234 C:\\Users\\PrivateName\\profile.json\n" +
                "-----BEGIN EC PRIVATE KEY-----\nprivate-key-fragment";
            var safe = redactor.Redact(text);
            foreach (var secret in new[] { "bearer-secret", "json-secret", "key-secret", "unlabelled-account-secret", "12345678-abcd", "203.0.113.7", "2606:4700::1234", "PrivateName", "private-key-fragment" })
                Assert(!safe.Contains(secret), "redaction leaked " + secret);
            return Task.CompletedTask;
        }),
        ("Diagnostic export is valid JSON and excludes profile metadata and credentials", () =>
        {
            using var data = new TestData();
            var redactor = new DiagnosticRedactor();
            redactor.AddSecrets(["raw-account-secret"]);
            var preferences = new Preferences { Profiles = [new("default", "PrivateProfileName")] };
            var report = DiagnosticsWriter.Create("1.1.0", new(ConnectionState.Error, "error"), false, preferences, true,
                null, new(), ["access_token=raw-account-secret", "203.0.113.9"], redactor);
            var path = Path.Combine(data.Paths.DataDirectory, "report.json");
            DiagnosticsWriter.Write(path, report);
            var text = File.ReadAllText(path);
            using var json = JsonDocument.Parse(text);
            Assert(!text.Contains("raw-account-secret") && !text.Contains("PrivateProfileName") && !text.Contains("203.0.113.9"), "diagnostics leaked private data");
            Assert(json.RootElement.GetProperty("ProfileCount").GetInt32() == 1, "whitelisted diagnostic metadata missing");
            return Task.CompletedTask;
        }),
        ("Traffic statistics handle adapter replacement, reset and disappearance", () =>
        {
            var traffic = new TrafficAccumulator();
            traffic.Begin("first", 1000, 2000, TimeSpan.Zero);
            var sample = traffic.Sample("first", 2024, 2512, TimeSpan.FromSeconds(1));
            Assert(sample.DownloadBytesPerSecond == 1024 && sample.UploadBytesPerSecond == 512, "rate calculation");
            sample = traffic.Sample("second", 100, 50, TimeSpan.FromSeconds(2));
            Assert(sample.DownloadedBytes == 1124 && sample.UploadedBytes == 562, "fallback reset session totals");
            sample = traffic.Sample("second", 50, 10, TimeSpan.FromSeconds(3));
            Assert(sample.DownloadedBytes == 1174 && sample.UploadedBytes == 572, "counter reset produced negative traffic");
            sample = traffic.Sample(null, 0, 0, TimeSpan.FromSeconds(4));
            Assert(!sample.AdapterAvailable && sample.DownloadBytesPerSecond == 0 && sample.DownloadedBytes == 1174, "adapter loss discarded totals");
            sample = traffic.Sample("second", 100, 40, TimeSpan.FromSeconds(5));
            Assert(sample.DownloadedBytes == 1274 && sample.UploadedBytes == 612, "a recreated adapter with the same ID was mistaken for old counters");
            Assert(TrafficAccumulator.FormatBytes(1024) == "1 KiB", "human-readable units");
            return Task.CompletedTask;
        }),
        ("Health parser requires an actual WARP trace flag", () =>
        {
            Assert(WarpHealthCheck.IsWarpTrace("ip=ignored\nwarp=plus\n"), "WARP+ not recognized");
            Assert(!WarpHealthCheck.IsWarpTrace("<html>warp=on</html>") && !WarpHealthCheck.IsWarpTrace("warp=off"), "false WARP verification");
            Assert(!new HealthResult(DateTimeOffset.UtcNow, true, false, true, 10, "DNS failed").Healthy, "DNS failure was ignored");
            return Task.CompletedTask;
        }),
        ("Connected waits for end-to-end health verification", async () =>
        {
            var gate = new TaskCompletionSource<HealthResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var health = new FakeHealth { Handler = _ => gate.Task };
            var f = new Fixture(true, health);
            await f.Controller.ConnectAsync(new());
            var verifying = f.WaitFor(ConnectionState.Verifying);
            f.Engine.Events.Writer.TryWrite(new("ready", NetworkManager.InterfaceName));
            f.Engine.Events.Writer.TryWrite(new("connected"));
            await verifying;
            Assert(f.Controller.Status.State != ConnectionState.Connected, "handshake alone reported verified connection");
            var connected = f.WaitFor(ConnectionState.Connected);
            gate.SetResult(FakeHealth.Good);
            await connected;
            await f.Controller.StopAsync();
        }),
        ("Automatic fallback joins the old engine and never disarms protection", async () =>
        {
            var health = new FakeHealth();
            health.Results.Enqueue(FakeHealth.Bad);
            health.Results.Enqueue(FakeHealth.Good);
            var f = new Fixture(true, health);
            f.Engine.Attempts.Enqueue(Ready()); f.Engine.Attempts.Enqueue(Ready());
            var arms = 0;
            f.Kill.BeforeArm = () => { if (++arms > 1) Assert(f.Kill.Enabled, "fallback lowered protection"); };
            await f.Controller.ConnectAsync(new(AutomaticFallback: true));
            await f.WaitFor(ConnectionState.Connected);
            Assert(f.Engine.Starts == 2 && !f.Engine.StartedOptions[0].Http2 && f.Engine.StartedOptions[1].Http2, "QUIC did not fall back to TCP");
            Assert(!f.Calls.Contains("disarm") && f.Calls.IndexOf("engine-stop") < f.Calls.LastIndexOf("arm"), "engine overlap or unprotected fallback");
            Assert(f.Controller.ActiveHttp2 && f.Controller.LastHealth?.Healthy == true, "actual transport/verification not reported");
            await f.Controller.StopAsync();
        }),
        ("Silent QUIC transport times out into TCP automatically", async () =>
        {
            var f = new Fixture(true, timings: new(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(30)));
            f.Engine.Attempts.Enqueue(Channel.CreateUnbounded<TunnelEvent>()); f.Engine.Attempts.Enqueue(Ready());
            await f.Controller.ConnectAsync(new(AutomaticFallback: true));
            await f.WaitFor(ConnectionState.Connected);
            Assert(f.Controller.ActiveHttp2 && f.Engine.Starts == 2 && !f.Calls.Contains("disarm"), "silent QUIC did not fail closed into TCP");
            await f.Controller.StopAsync();
        }),
        ("Failed TCP fallback remains blocked without an endless transport loop", async () =>
        {
            var f = new Fixture(true);
            f.Engine.Attempts.Enqueue(Failed()); f.Engine.Attempts.Enqueue(Failed());
            await f.Controller.ConnectAsync(new(AutomaticFallback: true));
            await f.WaitFor(ConnectionState.Blocked);
            Assert(f.Engine.Starts == 2 && f.Kill.Enabled && !f.Calls.Contains("disarm"), "TCP failure did not remain protected");
            await f.Controller.StopAsync();
        }),
        ("Cancelling fallback cannot start another transport", async () =>
        {
            var f = new Fixture(true);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Network.CleanupGate = gate.Task;
            f.Engine.Attempts.Enqueue(Failed()); f.Engine.Attempts.Enqueue(Ready());
            await f.Controller.ConnectAsync(new(AutomaticFallback: true));
            await f.WaitFor(ConnectionState.Reconnecting);
            var stopping = f.Controller.StopAsync();
            gate.SetResult();
            await stopping;
            Assert(f.Engine.Starts == 1, "cancelled fallback started TCP");
        }),
        ("Periodic health failure is reported and a manual check can recover", async () =>
        {
            var health = new FakeHealth { Default = FakeHealth.Bad };
            health.Results.Enqueue(FakeHealth.Good);
            var f = new Fixture(true, health, new(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(20)));
            await f.Controller.ConnectAsync(new());
            await f.Connect();
            await f.WaitFor(ConnectionState.Degraded);
            health.Default = FakeHealth.Good;
            await f.Controller.CheckHealthNowAsync();
            Assert(f.Controller.Status.State == ConnectionState.Connected && f.Kill.Enabled, "manual health recovery failed");
            await f.Controller.StopAsync();
        }),
        ("Startup changes are opt-in and roll back when saving preferences fails", () =>
        {
            using var data = new TestData();
            var store = new PreferencesStore(data.Paths);
            store.Save(store.Current);
            var startup = new FakeStartup();
            Assert(startup.Changes.Count == 0 && !store.Current.StartWithWindows, "startup was registered implicitly");
            using (var locked = new FileStream(data.Paths.Settings, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                try { StartupPreferences.SetEnabled(store, startup, true); throw new Exception("locked settings should fail"); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            Assert(startup.Changes.SequenceEqual(new[] { true, false }) && !startup.IsEnabled, "failed save left startup enabled");
            StartupPreferences.SetEnabled(store, startup, true);
            Assert(startup.IsEnabled && new PreferencesStore(data.Paths).Current.StartWithWindows, "startup preference was not persisted");
            return Task.CompletedTask;
        }),
        ("A stale manual health result cannot override a tunnel-loss event", async () =>
        {
            var gate = new TaskCompletionSource<HealthResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var checks = 0;
            var health = new FakeHealth { Handler = _ => ++checks == 1 ? Task.FromResult(FakeHealth.Good) : gate.Task };
            var f = new Fixture(true, health);
            await f.Controller.ConnectAsync(new());
            await f.Connect();
            var probe = f.Controller.CheckHealthNowAsync();
            var lost = f.WaitFor(ConnectionState.Reconnecting);
            f.Engine.Events.Writer.TryWrite(new("disconnected"));
            await lost;
            gate.SetResult(FakeHealth.Good);
            await probe;
            Assert(f.Controller.Status.State == ConnectionState.Reconnecting && f.Controller.LastHealth == null, "stale health result falsely reported Connected");
            await f.Controller.StopAsync();
        }),
        ("Startup copies only distribution files, never profile credentials or journals", () =>
        {
            Assert(StartupRegistration.IsDistributionFile("engine/wintun.dll") && StartupRegistration.IsDistributionFile("Scripts/Network.ps1"), "runtime files were excluded");
            foreach (var file in new[] { "config.json", "settings.json", "network-session.json", "profiles/secret.json", "../evil.dll", @"C:\evil.dll" })
                Assert(!StartupRegistration.IsDistributionFile(file), "startup installer would copy " + file);
            return Task.CompletedTask;
        }),
        ("Language catalogue lists the ten most spoken languages plus Turkish", () =>
        {
            var ids = Languages.All.Select(l => l.Language).ToList();
            Assert(ids.Count == 11, "expected 11 languages");
            foreach (var expected in new[] { AppLanguage.English, AppLanguage.Chinese, AppLanguage.Hindi, AppLanguage.Spanish,
                AppLanguage.French, AppLanguage.Arabic, AppLanguage.Bengali, AppLanguage.Russian, AppLanguage.Portuguese,
                AppLanguage.Urdu, AppLanguage.Turkish })
                Assert(ids.Contains(expected), "missing " + expected);
            var options = Languages.Options();
            Assert(options.Count == 11 && options[0].Display == "English" && options[10].Display == "Türkçe · Turkish", "option display names invalid");
            return Task.CompletedTask;
        }),
        ("Language preference persists and switches the UI to Turkish", () =>
        {
            using var data = new TestData();
            var store = new PreferencesStore(data.Paths);
            store.Save(store.Current with { Language = AppLanguage.Turkish });
            if (new PreferencesStore(data.Paths).Current.Language != AppLanguage.Turkish) throw new Exception("language preference did not persist");
            Localization.Instance.SetLanguage(store.Current.Language);
            try
            {
                Assert(Localization.Instance["nav.overview"] == "Genel Bakış", "Turkish nav string missing");
                Assert(Localization.Instance["tray.connect"] == "Bağlan", "Turkish tray string missing");
                Assert(Localization.Instance.Format("health.checkedAt", "12:34").Contains("Son kontrol"), "Turkish format template missing");
            }
            finally { Localization.Instance.SetLanguage(AppLanguage.English); }
            return Task.CompletedTask;
        }),
        ("All supported languages translate every UI key with matching format placeholders", () =>
        {
            var keys = Localization.Keys;
            try
            {
                foreach (var language in Enum.GetValues<AppLanguage>())
                {
                    Localization.Instance.SetLanguage(language);
                    foreach (var key in keys)
                    {
                        Assert(Localization.HasTranslation(language, key), $"missing translation for {language}: {key}");
                        var value = Localization.Instance[key];
                        Assert(!string.IsNullOrWhiteSpace(value), $"empty translation for {language}: {key}");
                        if (key.StartsWith("file."))
                            Assert(value.EndsWith(" (*.json)|*.json"), $"invalid file filter for {language}: {key}");
                        var english = Localization.Raw(AppLanguage.English, key);
                        foreach (var placeholder in new[] { "{0}", "{1}" })
                            Assert(english.Contains(placeholder) == value.Contains(placeholder),
                                $"placeholder {placeholder} mismatch for {language}: {key}");
                        if (english.Contains("{1}")) _ = Localization.Instance.Format(key, "10 MiB/s", "2 MiB/s");
                        else if (english.Contains("{0}")) _ = Localization.Instance.Format(key, "12:34");
                    }
                    if (language != AppLanguage.English)
                    {
                        foreach (var key in new[] { "nav.overview", "nav.log", "nav.settings", "title.connected", "title.offline", "tray.connect", "tray.disconnect", "diag.run" })
                            Assert(Localization.Instance[key] != Localization.Raw(AppLanguage.English, key),
                                $"expected translated string for {language}: {key}");
                    }
                }
                Assert(Localization.Instance["unknown.localization.key"] == "unknown.localization.key",
                    "unknown localization key did not return key name");
                Assert(!Localization.Instance.IsRightToLeft, "non-RTL language reported RTL");
                Localization.Instance.SetLanguage(AppLanguage.Arabic);
                Assert(Localization.Instance.IsRightToLeft, "Arabic should be RTL");
                Localization.Instance.SetLanguage(AppLanguage.Urdu);
                Assert(Localization.Instance.IsRightToLeft, "Urdu should be RTL");
            }
            finally { Localization.Instance.SetLanguage(AppLanguage.English); }
            return Task.CompletedTask;
        }),
        ("Invalid language values are rejected without overwriting settings", () =>
        {
            using var data = new TestData();
            var store = new PreferencesStore(data.Paths);
            store.Save(store.Current);
            var original = File.ReadAllText(data.Paths.Settings);
            Reject(() => store.Save(store.Current with { Language = (AppLanguage)999 }));
            Assert(File.ReadAllText(data.Paths.Settings) == original, "failed validation overwrote settings");
            return Task.CompletedTask;
        }),
        ("Overview, settings and log views render without binding errors or desktop interaction", UiSmoke.VerifyAsync)
    ];

    private static Channel<TunnelEvent> Ready()
    {
        var channel = Channel.CreateUnbounded<TunnelEvent>();
        channel.Writer.TryWrite(new("ready", NetworkManager.InterfaceName));
        channel.Writer.TryWrite(new("connected"));
        return channel;
    }
    private static Channel<TunnelEvent> Failed()
    {
        var channel = Channel.CreateUnbounded<TunnelEvent>();
        channel.Writer.Complete(new IOException("simulated transport failure"));
        return channel;
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action) { try { action(); throw new Exception("invalid settings accepted"); } catch (InvalidDataException) { } }
    private sealed class TestData : IDisposable
    {
        public AppPaths Paths { get; }
        public TestData()
        {
            var directory = Path.Combine(Path.GetTempPath(), "opencode", "usque-features-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            Paths = new(directory);
        }
        public void Dispose() => Directory.Delete(Paths.DataDirectory, true);
    }
    private sealed class FakeHealth : IConnectionHealth
    {
        public static HealthResult Good => new(DateTimeOffset.UtcNow, true, true, true, 10, "Verified test connection");
        public static HealthResult Bad => new(DateTimeOffset.UtcNow, false, false, false, null, "Simulated verification failure");
        public Queue<HealthResult> Results { get; } = new();
        public HealthResult Default { get; set; } = Good;
        public Func<CancellationToken, Task<HealthResult>>? Handler { get; set; }
        public Task<HealthResult> CheckAsync(CancellationToken token) => Handler?.Invoke(token) ?? Task.FromResult(Results.TryDequeue(out var result) ? result : Default);
    }
    private sealed class FakeStartup : IStartupRegistration
    {
        public bool IsEnabled { get; private set; }
        public List<bool> Changes { get; } = [];
        public void SetEnabled(bool enabled) { Changes.Add(enabled); IsEnabled = enabled; }
    }
}
