using System.Diagnostics;
using System.IO;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Usque.Core;
using Usque.Desktop;
using Localization = Usque.Core.Localization;

internal static class UiRecoveryTests
{
    public static (string Name, Func<Task> Run)[] All =>
    [
        ("UI recovery explains engine failure and retries without releasing protection", () => UiTestHost.RunAsync(async () =>
        {
            var f = new Fixture(true);
            var failed = Channel.CreateUnbounded<TunnelEvent>();
            f.Engine.Attempts.Enqueue(failed);
            failed.Writer.TryComplete(new IOException("engine failure access_token=private-recovery-token"));
            using var model = new MainViewModel(f.Controller);
            try
            {
                await f.Controller.ConnectAsync(new());
                await f.WaitFor(ConnectionState.Blocked);
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                Assert(model.ShowConnectionHelp && model.ShowRetryConnection && model.CanRetryConnection &&
                    model.ShowRestoreRecovery && !model.ShowHealthRetry, "failed connection did not offer appropriate actions");
                Assert(model.ConnectionHelpSummary == Localization.Instance["help.failure"] &&
                    model.ConnectionHelpProtection == Localization.Instance["help.protected"] &&
                    !model.Detail.Contains("private-recovery-token"), "recovery explanation was speculative or exposed secrets");
                model.LogsCommand.Execute(null);
                Assert(model.ShowLogs, "connection log action did not navigate");
                await ((AsyncCommand)model.RetryConnectionCommand).ExecuteAsync();
                await f.Connect();
                Assert(model.State == ConnectionState.Connected && !model.ShowConnectionHelp &&
                    !model.RetryConnectionCommand.CanExecute(null) && f.Kill.Enabled && !f.Calls.Contains("disarm"),
                    "retry lowered protection or failed to clear the panel");
                var starts = f.Engine.Starts;
                await ((AsyncCommand)model.RetryConnectionCommand).ExecuteAsync();
                Assert(model.IsSession && f.Engine.Starts == starts, "a stale retry disconnected/restarted an active session");
            }
            finally { await f.Controller.StopAsync(); }
        })),
        ("UI recovery requires explicit restoration before registering behind existing protection", () => UiTestHost.RunAsync(async () =>
        {
            var f = new Fixture();
            f.Kill.Enabled = true;
            f.Controller.Initialize();
            var interaction = new FakeDesktopInteraction { Answer = MessageBoxResult.Cancel };
            using var model = new MainViewModel(f.Controller, interaction: interaction);
            try
            {
                Assert(model.ShowConnectionHelp && !model.ShowRetryConnection && !model.CanRetryConnection &&
                    model.ShowRestoreRecovery, "registration behind a protection lock was offered as a retry");
                await ((AsyncCommand)model.RetryConnectionCommand).ExecuteAsync();
                await ((AsyncCommand)model.RestoreCommand).ExecuteAsync();
                Assert(f.Kill.Enabled && f.Engine.Starts == 0 && f.Engine.Registrations == 0 &&
                    !f.Calls.Contains("disarm") && interaction.Messages.Count == 1,
                    "recovery bypassed confirmation or registered without explicit restoration");
            }
            finally { await f.Controller.StopAsync(); }
        })),
        ("UI recovery exposes stale-network cleanup and clears after explicit restore", () => UiTestHost.RunAsync(async () =>
        {
            var f = new Fixture(true);
            f.Network.HasRecoveryState = true;
            f.Controller.Initialize();
            using var model = new MainViewModel(f.Controller, interaction: new FakeDesktopInteraction());
            Assert(model.ShowConnectionHelp && model.ShowRestoreRecovery && !model.ShowRetryConnection &&
                model.ConnectionHelpSummary == Localization.Instance["detail.needsCleanup"] &&
                model.ConnectionHelpProtection == Localization.Instance["help.unprotected"],
                "stale network state was not explained accurately");
            await ((AsyncCommand)model.RestoreCommand).ExecuteAsync();
            Assert(model.State == ConnectionState.Offline && !model.ShowConnectionHelp && !f.Network.HasRecoveryState,
                "successful restore left stale error guidance visible");
        })),
        ("UI recovery health warnings offer a health recheck rather than a disconnecting retry", () => UiTestHost.RunAsync(async () =>
        {
            var health = new MutableHealth();
            var f = new Fixture(true, health);
            using var model = new MainViewModel(f.Controller);
            try
            {
                await f.Controller.ConnectAsync(new());
                await f.Connect();
                health.Result = new(DateTimeOffset.UtcNow, false, false, false, null, "Unknown probe failure");
                await f.Controller.CheckHealthNowAsync();
                Assert(model.ConnectionHelpSummary == Localization.Instance["help.health"],
                    "an inconclusive probe was presented as verified internet access");
                health.Result = new(DateTimeOffset.UtcNow, true, false, true, 1, "DNS failed") { DetailKey = "health.failed.dns" };
                await f.Controller.CheckHealthNowAsync();
                Assert(model.ShowConnectionHelp && model.ShowHealthRetry && !model.ShowRetryConnection &&
                    model.ConnectionHelpSummary == Localization.Instance["health.failed.dns"] &&
                    model.ConnectionHelpAdvice == Localization.Instance["diag.fix.dns"],
                    "DNS failure did not offer targeted health guidance");
                health.Result = MutableHealth.Good;
                await ((AsyncCommand)model.CheckHealthCommand).ExecuteAsync();
                Assert(!model.ShowConnectionHelp && model.State == ConnectionState.Connected && f.Kill.Enabled &&
                    !f.Calls.Contains("disarm"), "a successful recheck disconnected the session or left stale guidance");
            }
            finally { await f.Controller.StopAsync(); }
        })),
        ("UI recovery never presents unknown protection as a verified traffic block", () => UiTestHost.RunAsync(() =>
        {
            var f = new Fixture(true);
            f.Kill.FailRead = true;
            f.Controller.Initialize();
            using var model = new MainViewModel(f.Controller);
            Assert(model.ShowConnectionHelp && model.ShowRestoreRecovery &&
                model.ConnectionHelpProtection == Localization.Instance["help.unknown"] &&
                model.ConnectionHelpProtection != Localization.Instance["help.protected"],
                "an unreadable kill switch was presented as confirmed protection");
            return Task.CompletedTask;
        })),
        ("UI recovery proxy failures retain proxy-only warnings and require restoration for old locks", () => UiTestHost.RunAsync(async () =>
        {
            using var data = new UiTestData();
            var store = new PreferencesStore(data.Paths);
            store.Save(store.Current with { Mode = ConnectionMode.Socks5 });
            var f = new Fixture(true);
            f.Engine.Events.Writer.TryComplete(new IOException("local proxy could not start"));
            using var model = new MainViewModel(f.Controller, preferences: store);
            try
            {
                await f.Controller.ConnectAsync(new(Mode: ConnectionMode.Socks5));
                await f.WaitFor(ConnectionState.Error);
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                Assert(model.ShowConnectionHelp && model.ShowRetryConnection && !model.ShowRestoreRecovery &&
                    model.ConnectionHelpProtection == Localization.Instance["proxy.warning"] && !f.Calls.Contains("arm"),
                    "proxy failure claimed full-device protection or offered unnecessary restoration");
                f.Kill.Enabled = true;
                f.Controller.Initialize();
                Assert(!model.ShowRetryConnection && !model.CanRetryConnection && model.ShowRestoreRecovery,
                    "retry was offered for a proxy behind an existing machine lock");
                await ((AsyncCommand)model.RetryConnectionCommand).ExecuteAsync();
                Assert(f.Kill.Enabled && !f.Calls.Contains("disarm"), "proxy recovery silently removed existing protection");
            }
            finally { await f.Controller.StopAsync(); }
        })),
        ("UI recovery does not let stale diagnostics override a newer live health failure", () => UiTestHost.RunAsync(async () =>
        {
            var health = new MutableHealth();
            var f = new Fixture(true, health);
            var checkedAt = DateTimeOffset.UtcNow;
            using var model = new MainViewModel(f.Controller, runDiagnostics: (_, _) => Task.FromResult(
                new ConnectionDiagnosticSnapshot(checkedAt, ConnectionMode.Tunnel,
                    [new("diag.installation", DiagnosticOutcome.Failed, "diag.driver.failed", "diag.fix.driver")])));
            try
            {
                await f.Controller.ConnectAsync(new());
                await f.Connect();
                health.Result = new(checkedAt, true, false, true, 1, "DNS failed") { DetailKey = "health.failed.dns" };
                await f.Controller.CheckHealthNowAsync();
                await ((AsyncCommand)model.DiagnoseCommand).ExecuteAsync();
                Assert(model.ConnectionHelpAdvice == Localization.Instance["diag.fix.driver"], "fresh diagnostic advice was ignored");
                health.Result = new(checkedAt.AddSeconds(1), false, false, false, null, "Internet failed")
                    { InternetReachable = false, DetailKey = "health.failed.internet" };
                await f.Controller.CheckHealthNowAsync();
                Assert(model.ConnectionHelpSummary == Localization.Instance["health.failed.internet"] &&
                    model.ConnectionHelpAdvice == Localization.Instance["diag.fix.internet"], "stale diagnostics hid the latest health failure");
            }
            finally { await f.Controller.StopAsync(); }
        })),
        ("UI recovery uses fresh diagnostic advice and updates it when language changes", () => UiTestHost.RunAsync(async () =>
        {
            var f = new Fixture(true);
            f.Engine.Events.Writer.TryComplete(new IOException("unclassified backend failure"));
            using var model = new MainViewModel(f.Controller, runDiagnostics: (_, _) => Task.FromResult(
                new ConnectionDiagnosticSnapshot(DateTimeOffset.UtcNow, ConnectionMode.Tunnel,
                    [new("diag.installation", DiagnosticOutcome.Failed, "diag.driver.failed", "diag.fix.driver")])));
            try
            {
                await f.Controller.ConnectAsync(new(KillSwitch: false));
                await f.WaitFor(ConnectionState.Error);
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                await ((AsyncCommand)model.DiagnoseCommand).ExecuteAsync();
                Assert(model.ConnectionHelpSummary == Localization.Instance["diag.driver.failed"] &&
                    model.ConnectionHelpAdvice == Localization.Instance["diag.fix.driver"], "diagnostics did not refine the recovery advice");
                model.Language = AppLanguage.Turkish;
                Assert(model.ConnectionHelpSummary == Localization.Instance["diag.driver.failed"] &&
                    model.ConnectionHelpAdvice == Localization.Instance["diag.fix.driver"] &&
                    model.ConnectionHelpProtection == Localization.Instance["help.unprotected"], "recovery advice did not relocalize");
                await f.Controller.StopAsync();
                Assert(!model.ShowConnectionHelp && model.DiagnosticChecks.Count == 0, "old failure advice survived a state change");
            }
            finally { await f.Controller.StopAsync(); Localization.Instance.SetLanguage(AppLanguage.English); }
        })),
        ("UI recovery respects pending diagnostics, exit and disposed-command guards", () => UiTestHost.RunAsync(async () =>
        {
            var f = new Fixture(true);
            f.Kill.Enabled = true;
            f.Controller.Initialize();
            var pending = new TaskCompletionSource<ConnectionDiagnosticSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var model = new MainViewModel(f.Controller, runDiagnostics: (_, _) => pending.Task);
            var diagnosing = ((AsyncCommand)model.DiagnoseCommand).ExecuteAsync();
            try
            {
                Assert(!model.RetryConnectionCommand.CanExecute(null), "retry raced pending offline diagnostics");
                model.BeginExit();
                Assert(!model.RetryConnectionCommand.CanExecute(null) && !model.RestoreCommand.CanExecute(null) &&
                    !model.LogsCommand.CanExecute(null) && !model.DiagnoseCommand.CanExecute(null), "exit left recovery actions available");
                model.CancelExit();
                pending.TrySetResult(new(DateTimeOffset.UtcNow, ConnectionMode.Tunnel, []));
                await diagnosing;
                Assert(model.RetryConnectionCommand.CanExecute(null), "completed diagnostics left retry locked");
                model.Dispose();
                Assert(!model.RetryConnectionCommand.CanExecute(null), "disposed recovery command remained enabled");
            }
            finally
            {
                pending.TrySetResult(new(DateTimeOffset.UtcNow, ConnectionMode.Tunnel, []));
                await diagnosing;
                await f.Controller.StopAsync();
            }
        })),
        ("UI recovery renders accessible wrapped actions and safe details on small localized views", () => UiTestHost.RunAsync(() =>
        {
            var f = new Fixture(true);
            f.Kill.Enabled = true;
            f.Controller.Initialize();
            using var model = new MainViewModel(f.Controller, interaction: new FakeDesktopInteraction { Answer = MessageBoxResult.No });
            var window = new MainWindow(model);
            var trace = new StringWriter();
            using var listener = new TextWriterTraceListener(trace);
            var source = PresentationTraceSources.DataBindingSource;
            var previous = source.Switch.Level;
            try
            {
                source.Switch.Level = SourceLevels.Error;
                source.Listeners.Add(listener);
                var root = (FrameworkElement)window.Content;
                if (root is Border border) border.Background = window.Background;
                var directory = Path.Combine(Path.GetTempPath(), "opencode", "usque-recovery-ui-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                foreach (var language in new[] { AppLanguage.English, AppLanguage.Turkish, AppLanguage.Arabic })
                {
                    model.Language = language;
                    root.Width = 800; root.Height = 480;
                    root.Measure(new Size(800, 480));
                    root.Arrange(new Rect(0, 0, 800, 480));
                    root.UpdateLayout();
                    var panel = UiRegressionTests.Descendants(root).OfType<Border>().Single(b =>
                        System.Windows.Automation.AutomationProperties.GetName(b) == Localization.Instance["help.title"]);
                    Assert(panel.Visibility == Visibility.Visible &&
                        System.Windows.Automation.AutomationProperties.GetLiveSetting(panel) == System.Windows.Automation.AutomationLiveSetting.Polite &&
                        UiRegressionTests.Descendants(panel).OfType<WrapPanel>().Any(),
                        "recovery panel lacks accessible status or wrapping actions");
                    Assert(panel.TransformToAncestor(root).Transform(new Point()).Y < 200,
                        "recovery actions were buried below the connection card");
                    var retry = UiRegressionTests.Descendants(panel).OfType<Button>().Single(b => b.Command == model.RetryConnectionCommand);
                    Assert((string)retry.Content == Localization.Instance["help.retry"], "retry label did not translate");
                    var expander = UiRegressionTests.Descendants(panel).OfType<Expander>().Single();
                    expander.IsExpanded = true;
                    root.UpdateLayout();
                    var details = UiRegressionTests.Descendants(expander).OfType<TextBox>().Single();
                    Assert(details.IsReadOnly && details.Text == model.Detail, "full technical details were unavailable");
                    var bitmap = new RenderTargetBitmap(800, 480, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(root);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(directory, language + ".png"));
                    encoder.Save(file);
                }
                Assert(string.IsNullOrWhiteSpace(trace.ToString()), "recovery WPF binding errors: " + trace);
                Console.WriteLine("Recovery UI snapshots (off-screen): " + directory);
            }
            finally
            {
                window.RequestExit();
                Localization.Instance.SetLanguage(AppLanguage.English);
                source.Listeners.Remove(listener);
                source.Switch.Level = previous;
            }
            return Task.CompletedTask;
        }))
    ];

    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class MutableHealth : IConnectionHealth
    {
        public static HealthResult Good => new(DateTimeOffset.UtcNow, true, true, true, 1, "Healthy");
        public HealthResult Result { get; set; } = Good;
        public Task<HealthResult> CheckAsync(CancellationToken token) => Task.FromResult(Result);
    }
}
