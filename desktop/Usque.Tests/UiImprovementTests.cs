using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Usque.Core;
using Usque.Desktop;
using Localization = Usque.Core.Localization;

internal static class UiImprovementTests
{
    public static (string Name, Func<Task> Run)[] All =>
    [
        ("UI startup toggle reflects the real scheduled task without rewriting preferences", () => UiTestHost.RunAsync(async () =>
        {
            using var data = new UiTestData();
            var store = new PreferencesStore(data.Paths);
            store.Save(store.Current with { StartWithWindows = true });
            var startup = new TestStartup();
            using var model = new MainViewModel(new Fixture(true).Controller, preferences: store, startup: startup);
            await model.StartupOperation;
            Assert(!model.StartWithWindows && model.CanConfigureStartup && store.Current.StartWithWindows &&
                startup.Changes.Count == 0, "loading trusted the saved flag or silently changed the startup task/settings");
            startup.Enabled = true;
            await model.RefreshStartupAsync();
            Assert(model.StartWithWindows, "an externally enabled task was not detected");
            startup.Enabled = false;
            await model.RefreshStartupAsync();
            Assert(!model.StartWithWindows, "an externally removed task was not detected");
        })),
        ("UI startup installation does not block the dispatcher or overwrite a concurrent language change", () => UiTestHost.RunAsync(async () =>
        {
            using var data = new UiTestData();
            var store = new PreferencesStore(data.Paths);
            var startup = new TestStartup();
            var interaction = new FakeDesktopInteraction();
            using var release = new ManualResetEventSlim();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var model = new MainViewModel(new Fixture(true).Controller, preferences: store, startup: startup, interaction: interaction);
            await model.StartupOperation;
            var uiThread = Environment.CurrentManagedThreadId;
            startup.OnSet = _ =>
            {
                started.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("dispatcher did not release installation");
            };
            try
            {
                model.StartWithWindows = true;
                await started.Task;
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                Assert(!model.CanEdit && !model.CanPower && !model.CanExit && !model.CanConfigureStartup,
                    "startup installation allowed conflicting edits or process exit");
                model.Language = AppLanguage.Turkish;
                release.Set();
                await model.StartupOperation;
                Assert(model.StartWithWindows && model.CanExit && model.CanEdit &&
                    startup.SetThread != uiThread && startup.ReadThread != uiThread &&
                    store.Current.StartWithWindows && store.Current.Language == AppLanguage.Turkish &&
                    interaction.Messages.Count == 1, "installation blocked the UI, lost preferences, or reported an error");
            }
            finally
            {
                release.Set();
                await model.StartupOperation;
                Localization.Instance.SetLanguage(AppLanguage.English);
            }
        })),
        ("UI startup save failure rolls back the task and restores usable controls", () => UiTestHost.RunAsync(async () =>
        {
            using var data = new UiTestData();
            var store = new PreferencesStore(data.Paths);
            store.Save(store.Current);
            var startup = new TestStartup();
            var interaction = new FakeDesktopInteraction();
            using var model = new MainViewModel(new Fixture(true).Controller, preferences: store, startup: startup, interaction: interaction);
            await model.StartupOperation;
            using (var locked = new FileStream(data.Paths.Settings, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                model.StartWithWindows = true;
                await model.StartupOperation;
            }
            Assert(startup.Changes.SequenceEqual(new[] { true, false }) && !startup.Enabled && !model.StartWithWindows &&
                !store.Current.StartWithWindows && model.CanConfigureStartup && model.CanExit &&
                interaction.Messages.Count == 2, "failed persistence left startup enabled or controls locked");
        })),
        ("UI startup query failures are redacted and recover on the next refresh", () => UiTestHost.RunAsync(async () =>
        {
            using var data = new UiTestData();
            var startup = new TestStartup { ReadError = new IOException("access_token=private-startup-token") };
            using var model = new MainViewModel(new Fixture(true).Controller,
                preferences: new PreferencesStore(data.Paths), startup: startup);
            await model.StartupOperation;
            Assert(!model.CanConfigureStartup && !model.StartWithWindows && !model.Logs.Contains("private-startup-token"),
                "unavailable scheduler state was editable or its error leaked secrets");
            startup.ReadError = null;
            startup.Enabled = true;
            await model.RefreshStartupAsync();
            Assert(model.CanConfigureStartup && model.StartWithWindows, "a successful retry did not restore startup state");
        })),
        ("UI profile names validate inline and disable invalid create and rename actions", () => UiTestHost.RunAsync(() =>
        {
            using var data = new UiTestData();
            var store = new PreferencesStore(data.Paths);
            var catalog = new ProfileCatalog(data.Paths, store);
            catalog.Create("Work");
            catalog.Select("default");
            var interaction = new FakeDesktopInteraction();
            using var model = new MainViewModel(new Fixture(true).Controller, preferences: store, paths: data.Paths, interaction: interaction);
            var saved = File.ReadAllText(data.Paths.Settings);
            foreach (var name in new[] { "", "   ", "woRK", "invalid\tname", new string('x', 41) })
            {
                model.ProfileName = name;
                Assert(model.ProfileNameError.Length > 0 && ((IDataErrorInfo)model)[nameof(model.ProfileName)].Length > 0 &&
                    !model.CreateProfileCommand.CanExecute(null) && !model.RenameProfileCommand.CanExecute(null),
                    "invalid name accepted: " + name);
            }
            Assert(interaction.Messages.Count == 0 && File.ReadAllText(data.Paths.Settings) == saved,
                "validation opened a dialog or changed saved settings");
            model.ProfileName = "Default";
            Assert(!model.CreateProfileCommand.CanExecute(null) && model.CreateProfileNameError.Length > 0,
                "creation allowed the selected profile's existing name");
            model.ProfileName = "Personal";
            Assert(model.ProfileNameError.Length == 0 && model.CreateProfileCommand.CanExecute(null) &&
                model.RenameProfileCommand.CanExecute(null), "valid name did not enable profile actions");
            model.CreateProfileCommand.Execute(null);
            Assert(model.Profiles.Count == 3 && model.ProfileName == "Personal", "valid creation failed");
            return Task.CompletedTask;
        })),
        ("UI failed create and rename keep the typed profile name for retry", () => UiTestHost.RunAsync(() =>
        {
            using var data = new UiTestData();
            var store = new PreferencesStore(data.Paths);
            store.Save(store.Current);
            var interaction = new FakeDesktopInteraction();
            using var model = new MainViewModel(new Fixture(true).Controller, preferences: store, paths: data.Paths, interaction: interaction);
            model.ProfileName = "Retry this name";
            using (var locked = new FileStream(data.Paths.Settings, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                model.CreateProfileCommand.Execute(null);
                Assert(model.ProfileName == "Retry this name" && model.Profiles.Count == 1, "failed create discarded the draft");
                model.RenameProfileCommand.Execute(null);
                Assert(model.ProfileName == "Retry this name" && model.Profiles[0].Name == "Default", "failed rename discarded the draft");
            }
            Assert(interaction.Messages.Count == 2, "I/O failures were not presented");
            model.RenameProfileCommand.Execute(null);
            Assert(model.Profiles[0].Name == "Retry this name", "retry did not use the preserved draft");
            return Task.CompletedTask;
        })),
        ("UI unavailable traffic rates differ from measured idle rates and offline rates", () =>
        {
            Assert(MainViewModel.FormatTrafficRate(false, true, false, 1234) == "—", "missing counters fabricated a rate");
            Assert(MainViewModel.FormatTrafficRate(false, true, true, 0) == "0 B/s", "measured idle traffic was unavailable");
            Assert(MainViewModel.FormatTrafficRate(false, true, true, 1024) == "1 KiB/s", "available traffic was not formatted");
            Assert(MainViewModel.FormatTrafficRate(false, false, false, 1234) == "0 B/s", "offline rates retained stale traffic");
            Assert(MainViewModel.FormatTrafficRate(true, true, true, 1234) == "—", "proxy mode claimed device traffic");
            return Task.CompletedTask;
        }),
        ("UI existing connection details relocalize without restarting the session", () => UiTestHost.RunAsync(async () =>
        {
            var f = new Fixture(true);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Profiles.LoadGate = release.Task;
            using var model = new MainViewModel(f.Controller);
            try
            {
                await f.Controller.ConnectAsync(new());
                var original = model.Detail;
                model.Language = AppLanguage.Turkish;
                Assert(model.Detail == Localization.Instance["detail.preparing"] && model.Detail != original &&
                    model.State == ConnectionState.Connecting, "pending status retained its old language");
                release.TrySetResult();
                await f.Connect();
                Assert(model.Detail == Localization.Instance["detail.connected.nohealth"], "connected status has no localization metadata");
                model.Language = AppLanguage.English;
                Assert(model.Detail == Localization.Instance["detail.connected.nohealth"], "connected detail did not relocalize");
                await f.Controller.StopAsync();
                model.Language = AppLanguage.Turkish;
                Assert(model.Detail == Localization.Instance["detail.offline"], "offline detail did not relocalize");
            }
            finally
            {
                release.TrySetResult();
                await f.Controller.StopAsync();
                Localization.Instance.SetLanguage(AppLanguage.English);
            }
        })),
        ("UI existing health summaries relocalize without another network check", () => UiTestHost.RunAsync(async () =>
        {
            var health = new KeyedHealth();
            var f = new Fixture(true, health);
            using var model = new MainViewModel(f.Controller);
            try
            {
                await f.Controller.ConnectAsync(new());
                await f.Connect();
                var original = model.HealthSummary;
                var checks = health.Checks;
                model.Language = AppLanguage.Turkish;
                Assert(model.HealthSummary == Localization.Instance["health.verified.dual"] && model.HealthSummary != original &&
                    model.Detail == model.HealthSummary && health.Checks == checks,
                    "health text remained stale or changing language sent a probe");
            }
            finally { await f.Controller.StopAsync(); Localization.Instance.SetLanguage(AppLanguage.English); }
        })),
        ("UI failed initial health verification relocalizes its blocked-state explanation", () => UiTestHost.RunAsync(async () =>
        {
            var f = new Fixture(true, new KeyedHealth(healthy: false));
            using var model = new MainViewModel(f.Controller);
            try
            {
                await f.Controller.ConnectAsync(new());
                var blocked = f.WaitFor(ConnectionState.Blocked);
                f.Engine.Events.Writer.TryWrite(new("ready", NetworkManager.InterfaceName));
                f.Engine.Events.Writer.TryWrite(new("connected"));
                await blocked;
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                var original = model.Detail;
                model.Language = AppLanguage.Turkish;
                Assert(model.ShowRecovery && model.Detail == Localization.Instance["health.failed.dns"] && model.Detail != original,
                    "failed verification lost its localizable health explanation");
            }
            finally { await f.Controller.StopAsync(); Localization.Instance.SetLanguage(AppLanguage.English); }
        })),
        ("UI localized error arguments remain redacted after a language switch", () => UiTestHost.RunAsync(() =>
        {
            var f = new Fixture(true);
            var controller = new ConnectionController(f.Profiles, f.Engine, f.Network, new UnreadableKillSwitch());
            controller.Initialize();
            using var model = new MainViewModel(controller);
            try
            {
                Assert(!model.Detail.Contains("private-status-token"), "initial error arguments leaked");
                model.Language = AppLanguage.Turkish;
                Assert(!model.Detail.Contains("private-status-token") && model.Detail.Contains("[REDACTED]"),
                    "relocalization bypassed redaction");
            }
            finally { Localization.Instance.SetLanguage(AppLanguage.English); }
            return Task.CompletedTask;
        })),
        ("UI localized status arguments and backend detail survive language changes", () =>
        {
            var status = new ConnectionStatus(ConnectionState.Error, "original")
                { DetailKey = "err.protectionUnable", DetailArguments = ["backend explanation"], DetailSuffix = " extra context" };
            var raw = new ConnectionStatus(ConnectionState.Error, "access_token=raw-backend-error");
            var retry = new ConnectionStatus(ConnectionState.Reconnecting, "original")
                { DetailKey = "recovery.wait.protected", DetailSuffixKey = "health.failed.dns" };
            try
            {
                Localization.Instance.SetLanguage(AppLanguage.Turkish);
                Assert(status.LocalizedDetail == Localization.Instance.Format("err.protectionUnable", "backend explanation") + " extra context" &&
                    raw.LocalizedDetail == raw.Detail, "localization lost error context or translated arbitrary backend text");
                Assert(retry.LocalizedDetail == Localization.Instance["recovery.wait.protected"] + " " +
                    Localization.Instance["health.failed.dns"], "retry reason retained its old language");
            }
            finally { Localization.Instance.SetLanguage(AppLanguage.English); }
            return Task.CompletedTask;
        }),
        ("UI disabled power button is visibly distinct and recovers when enabled", () => UiTestHost.RunAsync(() =>
        {
            using var model = new MainViewModel(new Fixture(true).Controller);
            var window = new MainWindow(model);
            try
            {
                var root = (FrameworkElement)window.Content;
                root.Measure(new Size(1060, 980));
                root.Arrange(new Rect(0, 0, 1060, 980));
                root.UpdateLayout();
                var button = UiRegressionTests.Descendants(root).OfType<Button>().Single(b => b.Command == model.PowerCommand);
                var surface = (Border)button.Template.FindName("PowerSurface", button);
                Assert(button.IsEnabled && surface.Opacity == 1, "enabled power button was dimmed");
                button.IsEnabled = false;
                Assert(surface.Opacity == 0.35, "disabled power button looked enabled");
                button.ClearValue(UIElement.IsEnabledProperty);
                Assert(button.IsEnabled && surface.Opacity == 1, "power button did not recover after re-enabling");
            }
            finally { window.RequestExit(); }
            return Task.CompletedTask;
        })),
        ("UI window minimums recover when moving from a small to a larger display", () => UiTestHost.RunAsync(() =>
        {
            using var model = new MainViewModel();
            var window = new MainWindow(model);
            try
            {
                window.FitToWorkArea(new Rect(0, 0, 800, 480));
                Assert(window.MinWidth == 800 && window.MinHeight == 480, "small display minimums did not fit");
                window.FitToWorkArea(new Rect(0, 0, 1920, 1080));
                Assert(window.MinWidth == 900 && window.MinHeight == 600, "minimums stayed permanently reduced");
                var area = new Rect(-1920, -200, 1920, 1040);
                var moved = MainWindow.ConstrainToWorkArea(new Rect(-2300, 700, 900, 600), area);
                Assert(moved.X == -1920 && moved.Y == 240 && area.Contains(moved),
                    "clamping failed for a monitor with a negative origin");
                var unchanged = new Rect(20, 40, 900, 600);
                Assert(MainWindow.ConstrainToWorkArea(unchanged, new Rect(0, 0, 1920, 1080)) == unchanged,
                    "a fully visible window was moved");
            }
            finally { window.RequestExit(); }
            return Task.CompletedTask;
        }))
    ];

    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }

    private sealed class TestStartup : IStartupRegistration
    {
        public bool Enabled { get; set; }
        public Exception? ReadError { get; set; }
        public Action<bool>? OnSet { get; set; }
        public List<bool> Changes { get; } = [];
        public int ReadThread { get; private set; }
        public int SetThread { get; private set; }
        public bool IsEnabled
        {
            get
            {
                ReadThread = Environment.CurrentManagedThreadId;
                if (ReadError != null) throw ReadError;
                return Enabled;
            }
        }
        public void SetEnabled(bool enabled)
        {
            SetThread = Environment.CurrentManagedThreadId;
            OnSet?.Invoke(enabled);
            Changes.Add(enabled);
            Enabled = enabled;
        }
    }

    private sealed class KeyedHealth(bool healthy = true) : IConnectionHealth
    {
        public int Checks { get; private set; }
        public Task<HealthResult> CheckAsync(CancellationToken token)
        {
            Checks++;
            var key = healthy ? "health.verified.dual" : "health.failed.dns";
            return Task.FromResult(new HealthResult(DateTimeOffset.UtcNow, true, healthy, true, 1,
                Localization.Instance[key]) { DetailKey = key });
        }
    }

    private sealed class UnreadableKillSwitch : IKillSwitch
    {
        public bool IsEnabled => throw new IOException("access_token=private-status-token");
        public void Arm(string endpoint, bool http2) => throw new NotSupportedException();
        public void AllowTunnel(ulong luid) => throw new NotSupportedException();
        public void Disarm() => throw new NotSupportedException();
    }
}
