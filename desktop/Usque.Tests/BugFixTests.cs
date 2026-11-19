using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Usque.Core;
using Usque.Desktop;
using Localization = Usque.Core.Localization;

// Regressions for defects found during the desktop bug-fix review.
internal static class BugFixTests
{
    public static (string Name, Func<Task> Run)[] All =>
    [
        ("UI exit that keeps protection closes the window without lowering it", () => UiTestHost.RunAsync(async () =>
        {
            var f = new Fixture(true);
            var interaction = new FakeDesktopInteraction { Answer = MessageBoxResult.No };
            using var model = new MainViewModel(f.Controller, interaction: interaction);
            var window = QuietWindow(model);
            var closed = false;
            window.Closed += (_, _) => closed = true;
            try
            {
                await f.Controller.ConnectAsync(new());
                await f.Connect();
                window.RequestExit();
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                Assert(closed, "choosing to keep protection did not exit: " + string.Join(" | ", interaction.Messages));
                Assert(interaction.Messages.Count == 1, "keeping protection reported an exit error");
                Assert(f.Kill.Enabled && !f.Calls.Contains("disarm"), "keeping protection lowered the kill switch");
            }
            finally { await f.Controller.StopAsync(); }
        })),
        ("UI exit that restores networking closes even when cleanup completes synchronously", () => UiTestHost.RunAsync(async () =>
        {
            // Offline with a leftover journal: StopAsync has nothing to await.
            var f = new Fixture(true);
            f.Network.HasRecoveryState = true;
            var interaction = new FakeDesktopInteraction { Answer = MessageBoxResult.Yes };
            using var model = new MainViewModel(f.Controller, interaction: interaction);
            var window = QuietWindow(model);
            var closed = false;
            window.Closed += (_, _) => closed = true;
            window.RequestExit();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Assert(closed && interaction.Messages.Count == 1 && !f.Network.HasRecoveryState,
                "restoring before exit failed or reported an error: " + string.Join(" | ", interaction.Messages));
        })),
        ("UI reopening from the tray during exit confirmation does not crash", () => UiTestHost.RunAsync(async () =>
        {
            var f = new Fixture(true);
            var interaction = new FakeDesktopInteraction { Answer = MessageBoxResult.Cancel };
            using var model = new MainViewModel(f.Controller, interaction: interaction);
            var window = QuietWindow(model);
            try
            {
                await f.Controller.ConnectAsync(new(KillSwitch: false));
                await f.Connect();
                Exception? failure = null;
                // The tray "Open" item stays enabled while the modal exit question is shown.
                interaction.OnShow = () => { try { window.RestoreFromTray(); } catch (Exception ex) { failure = ex; } };
                window.RequestExit();
                Assert(failure == null, "reopening during exit confirmation threw: " + failure?.Message);
                Assert(!model.IsExiting && window.IsVisible, "cancelled exit did not leave a usable window");
            }
            finally
            {
                interaction.OnShow = null;
                await f.Controller.StopAsync();
                window.RequestExit();
            }
        })),
        ("Tray retry cycles warn once until the recovery episode ends", () =>
        {
            var presenter = new TrayPresentation(ConnectionState.Offline);
            presenter.Update(Tray(ConnectionState.Connecting));
            Assert(presenter.Update(Tray(ConnectionState.Reconnecting)).Notice is { Warning: true },
                "the first failed attempt was not announced");
            for (var attempt = 0; attempt < 3; attempt++)
            {
                Assert(presenter.Update(Tray(ConnectionState.Connecting)).Notice == null, "a retry attempt was announced");
                Assert(presenter.Update(Tray(ConnectionState.Reconnecting)).Notice == null, "each retry cycle repeated a warning");
            }
            Assert(presenter.Update(Tray(ConnectionState.Blocked)).Notice is { Warning: true },
                "the terminal failure after retries was hidden");
            presenter.Update(Tray(ConnectionState.Connecting));
            Assert(presenter.Update(Tray(ConnectionState.Reconnecting)).Notice is { Warning: true },
                "a new recovery episode after an explicit retry was silent");
            return Task.CompletedTask;
        }),
        ("Tray tunnel loss followed by failing retries announces the loss only once", () =>
        {
            var presenter = new TrayPresentation(ConnectionState.Offline);
            presenter.Update(Tray(ConnectionState.Connected));
            presenter.Update(Tray(ConnectionState.Connected), trafficSample: true);
            Assert(presenter.Update(Tray(ConnectionState.Reconnecting)).Notice is { Warning: true }, "tunnel loss was silent");
            presenter.Update(Tray(ConnectionState.Connecting));
            Assert(presenter.Update(Tray(ConnectionState.Reconnecting)).Notice == null, "a retry repeated the loss warning");
            presenter.Update(Tray(ConnectionState.Verifying));
            presenter.Update(Tray(ConnectionState.Connected));
            Assert(presenter.Update(Tray(ConnectionState.Connected), trafficSample: true).Notice is { Warning: false },
                "recovery did not announce the new connection");
            Assert(presenter.Update(Tray(ConnectionState.Reconnecting)).Notice is { Warning: true },
                "a later, separate tunnel loss was silent");
            return Task.CompletedTask;
        }),
        ("UI built-in status text survives redaction of a profile named like product wording", () => UiTestHost.RunAsync(async () =>
        {
            using var data = new UiTestData();
            var store = new PreferencesStore(data.Paths);
            new ProfileCatalog(data.Paths, store).Create("WARP");
            var f = new Fixture(true, new KeyedHealth());
            using var model = new MainViewModel(f.Controller, preferences: store, paths: data.Paths,
                runDiagnostics: (_, _) => Task.FromResult(new ConnectionDiagnosticSnapshot(DateTimeOffset.UtcNow.AddMinutes(1),
                    ConnectionMode.Tunnel, [new("diag.warp", DiagnosticOutcome.Failed, "health.failed.dns", "diag.fix.warp")])));
            try
            {
                await f.Controller.ConnectAsync(new(KillSwitch: false));
                await f.Connect();
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                var verified = Localization.Instance["health.verified.dual"];
                Assert(model.Detail == verified && model.HealthSummary == verified,
                    "built-in health text was redacted: " + model.Detail);
                await ((AsyncCommand)model.DiagnoseCommand).ExecuteAsync();
                Assert(model.ConnectionHelpSummary == Localization.Instance["health.failed.dns"] &&
                    model.ConnectionHelpAdvice == Localization.Instance["diag.fix.warp"],
                    "built-in help text was redacted: " + model.ConnectionHelpSummary);
                model.AddLog("profile WARP access_token=leaky-log-token");
                Assert(!model.Logs.Contains("leaky-log-token") && !model.Logs.Contains("profile WARP"),
                    "raw log text was no longer redacted");
            }
            finally { await f.Controller.StopAsync(); }
        })),
        ("UI backend fragments inside localized status text remain redacted", () => UiTestHost.RunAsync(() =>
        {
            using var data = new UiTestData();
            var store = new PreferencesStore(data.Paths);
            new ProfileCatalog(data.Paths, store).Create("Secret Home");
            var controller = new ConnectionController(new FakeProfile { Exists = true }, new FakeEngine(new FakeProfile(), []),
                new FakeNetwork([]), new LeakyKillSwitch());
            controller.Initialize();
            using var model = new MainViewModel(controller, preferences: store, paths: data.Paths);
            Assert(controller.Status.DetailKey == "err.protectionUnable", "test did not produce a localized failure");
            Assert(model.Detail.Contains("Windows Filtering Platform") && !model.Detail.Contains("leaky-wfp-token") &&
                !model.Detail.Contains("Secret Home") && !model.TrayStatus.Detail.Contains("leaky-wfp-token"),
                "a backend message argument bypassed redaction: " + model.Detail);
            return Task.CompletedTask;
        })),
    ];

    // WPF enforces its closing rules only for a real HWND. Show the window
    // minimized, unactivated, outside the taskbar and without a tray icon.
    private static MainWindow QuietWindow(MainViewModel model)
    {
        var window = new MainWindow(model, notificationArea: false)
            { ShowActivated = false, ShowInTaskbar = false, WindowState = WindowState.Minimized };
        window.Show();
        return window;
    }

    internal static IEnumerable<DependencyObject> Layout(Window window)
    {
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(1060, 980));
        root.Arrange(new Rect(0, 0, 1060, 980));
        root.UpdateLayout();
        return UiRegressionTests.Descendants(root);
    }
    private static TraySnapshot Tray(ConnectionState state) => new(state, false, "1 KiB/s", "2 KiB/s", true, "detail");
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }

    private sealed class KeyedHealth : IConnectionHealth
    {
        public Task<HealthResult> CheckAsync(CancellationToken token) => Task.FromResult(
            new HealthResult(DateTimeOffset.UtcNow, true, true, true, 5, Localization.Instance["health.verified.dual"])
                { DetailKey = "health.verified.dual", InternetReachable = true });
    }
    private sealed class LeakyKillSwitch : IKillSwitch
    {
        public bool IsEnabled => throw new IOException("Secret Home access_token=leaky-wfp-token");
        public void Arm(string endpoint, bool http2) { }
        public void AllowTunnel(ulong luid) { }
        public void Disarm() { }
    }
}
