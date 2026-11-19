using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Usque.Core;
using Usque.Desktop;

// Desktop usability and efficiency improvements made after the bug-fix pass.
internal static class UsabilityTests
{
    public static (string Name, Func<Task> Run)[] All =>
    [
        ("Activation signal reaches the running instance and stops after disposal", async () =>
        {
            var name = @"Local\UsqueTests-activate-" + Guid.NewGuid().ToString("N");
            Assert(!InstanceActivation.TrySignal(name), "signalled an instance that does not exist");
            var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using (InstanceActivation.Listen(() => activated.TrySetResult(), name))
            {
                Assert(InstanceActivation.TrySignal(name), "a second launch could not reach the running instance");
                await activated.Task.WaitAsync(TimeSpan.FromSeconds(2));
            }
            Assert(!InstanceActivation.TrySignal(name), "a disposed listener still accepted activation");
        }),
        ("UI window behavior stays editable during a session while connection settings stay locked", () => UiTestHost.RunAsync(async () =>
        {
            using var data = new UiTestData();
            var store = new PreferencesStore(data.Paths);
            var f = new Fixture(true);
            using var model = new MainViewModel(f.Controller, preferences: store);
            try
            {
                await f.Controller.ConnectAsync(new(KillSwitch: false));
                await f.Connect();
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                Assert(!model.CanEdit && model.CanChangeWindowBehavior, "window behavior was locked by the session");
                model.MinimizeToTray = true;
                model.KillSwitch = !model.KillSwitch;
                Assert(model.MinimizeToTray && store.Current.MinimizeToTray, "the tray preference was not saved during a session");
                Assert(store.Current.KillSwitch, "a connection setting changed during a session");
            }
            finally { await f.Controller.StopAsync(); }
        })),
        ("UI log view follows new entries but keeps a reader's earlier position", () => UiTestHost.RunAsync(async () =>
        {
            using var model = new MainViewModel();
            var window = new MainWindow(model);
            try
            {
                model.LogsCommand.Execute(null);
                var log = Render(window).OfType<TextBox>().Single(t =>
                    BindingOperations.GetBinding(t, TextBox.TextProperty)?.Path.Path == nameof(MainViewModel.Logs));
                for (var i = 0; i < 250; i++) model.AddLog("event " + i);
                await Settle(window);
                Assert(log.ExtentHeight > log.ViewportHeight && log.VerticalOffset + log.ViewportHeight >= log.ExtentHeight - 1,
                    "the log did not follow its newest entries");
                log.ScrollToVerticalOffset(0);
                await Settle(window);
                model.AddLog("event while reading older entries");
                await Settle(window);
                Assert(log.VerticalOffset < 1, "a new entry moved a reader away from older entries");
            }
            finally { window.RequestExit(); }
        })),
        ("UI busy spinner animates only while a connection is in progress", () => UiTestHost.RunAsync(async () =>
        {
            var f = new Fixture(true);
            using var model = new MainViewModel(f.Controller);
            var window = new MainWindow(model);
            try
            {
                var spinner = Render(window).OfType<Ellipse>().Single(e => e.RenderTransform is RotateTransform);
                Assert(!spinner.RenderTransform.HasAnimatedProperties, "the spinner animated while idle");
                await f.Controller.ConnectAsync(new(KillSwitch: false));
                await Settle(window);
                Assert(model.IsBusy && spinner.RenderTransform.HasAnimatedProperties, "the spinner did not animate while connecting");
                await f.Connect();
                await Settle(window);
                Assert(!model.IsBusy && !spinner.RenderTransform.HasAnimatedProperties, "the spinner kept animating once connected");
            }
            finally { await f.Controller.StopAsync(); window.RequestExit(); }
        })),
        ("UI diagnostic rows hide empty advice instead of leaving gaps", () => UiTestHost.RunAsync(async () =>
        {
            using var model = new MainViewModel(runDiagnostics: (_, _) => Task.FromResult(new ConnectionDiagnosticSnapshot(
                DateTimeOffset.UtcNow, ConnectionMode.Tunnel,
                [new("diag.dns", DiagnosticOutcome.Passed, "diag.probe.ok", ""),
                 new("diag.warp", DiagnosticOutcome.Failed, "diag.probe.failed", "diag.fix.warp")])));
            var window = new MainWindow(model);
            try
            {
                await ((AsyncCommand)model.DiagnoseCommand).ExecuteAsync();
                model.SettingsCommand.Execute(null);
                var advice = Render(window).OfType<TextBlock>().Where(t =>
                    BindingOperations.GetBinding(t, TextBlock.TextProperty)?.Path.Path == nameof(DiagnosticRow.Advice)).ToArray();
                Assert(advice.Length == 2 && advice.Count(t => t.Visibility == Visibility.Collapsed) == 1 &&
                    advice.Single(t => t.Visibility == Visibility.Visible).Text.Length > 0,
                    "empty advice lines were not collapsed");
            }
            finally { window.RequestExit(); }
        })),
    ];

    private static IEnumerable<DependencyObject> Render(Window window) => BugFixTests.Layout(window);
    private static async Task Settle(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            ((FrameworkElement)window.Content).UpdateLayout();
        }
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
}
