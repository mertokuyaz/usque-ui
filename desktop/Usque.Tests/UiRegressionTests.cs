using System.IO;
using System.ComponentModel;
using System.Windows.Automation;
using System.Windows.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Usque.Core;
using Usque.Desktop;
using Localization = Usque.Core.Localization;

internal static class UiRegressionTests
{
    public static (string Name, Func<Task> Run)[] All =>
    [
        ("UI attaching to an existing session immediately shows its real state", () => UiTestHost.RunAsync(async () =>
        {
            var f = new Fixture(true);
            try
            {
                await f.Controller.ConnectAsync(new(Http2: true));
                await f.Connect();
                using var model = new MainViewModel(f.Controller);
                Assert(model.State == ConnectionState.Connected && model.IsSession && !model.CanEdit,
                    "a new view-model displayed an active session as offline");
                Assert(model.Transport == Localization.Instance["transport.http2"], "active transport was hidden");
            }
            finally { await f.Controller.StopAsync(); }
        })),
        ("UI attaching to a proxy uses its active mode and port rather than local defaults", () => UiTestHost.RunAsync(async () =>
        {
            var f = new Fixture(true);
            var interaction = new FakeDesktopInteraction();
            try
            {
                await f.Controller.ConnectAsync(new(Mode: ConnectionMode.Socks5, ProxyPort: 43210));
                await f.Connect();
                using var model = new MainViewModel(f.Controller, interaction: interaction);
                Assert(model.IsProxy && model.Coverage == Localization.Instance["proxy.coverage"] &&
                    model.ProxyAddress == "socks5://127.0.0.1:43210" && model.DownloadRate == "—",
                    "active proxy was shown as a default full-device connection");
                model.CopyProxyCommand.Execute(null);
                Assert(interaction.CopiedText == "socks5://127.0.0.1:43210", "copy used the inactive preference port");
            }
            finally { await f.Controller.StopAsync(); }
        })),
        ("UI commands requiring absent profile or startup services stay disabled", () => UiTestHost.RunAsync(() =>
        {
            using var model = new MainViewModel(new Fixture(true).Controller);
            Assert(!model.CreateProfileCommand.CanExecute(null) && !model.RenameProfileCommand.CanExecute(null) &&
                !model.DeleteProfileCommand.CanExecute(null) && !model.ImportCommand.CanExecute(null) && !model.CanConfigureStartup,
                "unavailable UI services were offered as usable commands");
            return Task.CompletedTask;
        })),
        ("UI recovery command stays disabled until an in-progress disconnect finishes", () => UiTestHost.RunAsync(async () =>
        {
            var f = new Fixture(true);
            var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var model = new MainViewModel(f.Controller);
            try
            {
                await f.Controller.ConnectAsync(new());
                await f.Connect();
                f.Network.CleanupGate = cleanup.Task;
                var stop = f.Controller.StopAsync();
                Assert(model.State == ConnectionState.Disconnecting && !model.RestoreCommand.CanExecute(null),
                    "the tray could queue another restore while disconnecting");
                cleanup.TrySetResult();
                await stop;
            }
            finally { cleanup.TrySetResult(); await f.Controller.StopAsync(); }
        })),
        ("UI disposal disables commands and ignores already-queued updates", () => UiTestHost.RunAsync(async () =>
        {
            var f = new Fixture(true);
            var model = new MainViewModel(f.Controller);
            // Keep the dispatcher occupied until the callback is queued.
            Task.Run(() => model.AddLog("queued before disposal")).GetAwaiter().GetResult();
            model.Dispose();
            model.Dispose();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Assert(!model.PowerCommand.CanExecute(null) && !model.RestoreCommand.CanExecute(null) &&
                !model.TermsCommand.CanExecute(null), "disposed commands remained enabled");
            Assert(!model.Logs.Contains("queued before disposal"), "a queued callback updated a disposed view-model");
        })),
        ("UI health text never displays raw token values", () => UiTestHost.RunAsync(async () =>
        {
            var f = new Fixture(true, new SensitiveHealth());
            using var model = new MainViewModel(f.Controller);
            try
            {
                await f.Controller.ConnectAsync(new());
                await f.Connect();
                Assert(!model.HealthSummary.Contains("secret-health-token") && !model.Detail.Contains("secret-health-token"),
                    "health text bypassed redaction");
            }
            finally { await f.Controller.StopAsync(); }
        })),
        ("UI active crash protection is not presented as a disabled preference", () => UiTestHost.RunAsync(() =>
        {
            var f = new Fixture(true);
            f.Kill.Enabled = true;
            f.Controller.Initialize();
            using var data = new UiTestData();
            var preferences = new PreferencesStore(data.Paths);
            preferences.Save(preferences.Current with { KillSwitch = false });
            using var model = new MainViewModel(f.Controller, preferences: preferences);
            Assert(model.ShowRecovery && model.KillSwitchSelection &&
                model.ProtectionDescription != Localization.Instance["protect.off"],
                "an existing protection lock was shown as off");
            return Task.CompletedTask;
        })),
        ("UI language changes retain transport and mode selections", () => UiTestHost.RunAsync(async () =>
        {
            using var model = new MainViewModel();
            var window = new MainWindow(model);
            try
            {
                model.Mode = ConnectionMode.HttpProxy;
                model.PreferredTransport = TransportMode.Http2;
                var root = (FrameworkElement)window.Content;
                root.Measure(new Size(1060, 980));
                root.Arrange(new Rect(0, 0, 1060, 980));
                root.UpdateLayout();
                model.Language = AppLanguage.Turkish;
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                Assert(model.Mode == ConnectionMode.HttpProxy && model.PreferredTransport == TransportMode.Http2 &&
                    model.Language == AppLanguage.Turkish && Localization.Instance.Language == AppLanguage.Turkish,
                    "localization changed a saved selection or disagreed with the selected language");
            }
            finally { window.RequestExit(); Localization.Instance.SetLanguage(AppLanguage.English); }
        })),
        ("UI navigation buttons stretch content to wrap translated labels", () => UiTestHost.RunAsync(() =>
        {
            var button = new Button { Content = "Navigation", Style = (Style)Application.Current.FindResource("NavButton") };
            button.ApplyTemplate();
            Assert(Descendants(button).OfType<ContentPresenter>().Any(p => p.HorizontalAlignment == HorizontalAlignment.Stretch),
                "navigation content cannot stretch to wrap translated labels");
            return Task.CompletedTask;
        })),
        ("UI shows enlarged exit buttons below the logo and in the footer", () => UiTestHost.RunAsync(() =>
        {
            using var model = new MainViewModel();
            var window = new MainWindow(model);
            try
            {
                var root = (FrameworkElement)window.Content;
                root.Measure(new Size(1060, 980));
                root.Arrange(new Rect(0, 0, 1060, 980));
                root.UpdateLayout();
                var buttons = Descendants(root).OfType<Button>()
                    .Where(b => System.Windows.Automation.AutomationProperties.GetName(b) == Localization.Instance["side.exit"])
                    .ToArray();
                Assert(buttons.Length == 2 && buttons.All(b => b.FontSize == 40),
                    "expected two exit buttons at four times the original font size");
            }
            finally { window.RequestExit(); }
            return Task.CompletedTask;
        })),
        ("UI log copy and clear commands are disabled for an empty log", () => UiTestHost.RunAsync(() =>
        {
            using var model = new MainViewModel();
            Assert(!model.CopyLogsCommand.CanExecute(null) && !model.ClearLogsCommand.CanExecute(null),
                "empty-log actions were still offered");
            model.AddLog("test event");
            Assert(model.CopyLogsCommand.CanExecute(null) && model.ClearLogsCommand.CanExecute(null),
                "nonempty-log actions were disabled");
            model.ClearLogsCommand.Execute(null);
            Assert(!model.CopyLogsCommand.CanExecute(null) && !model.ClearLogsCommand.CanExecute(null),
                "clearing the log did not disable empty actions");
            return Task.CompletedTask;
        })),
        ("UI invalid proxy ports are inline errors and cannot start or copy a stale address", () => UiTestHost.RunAsync(() =>
        {
            using var data = new UiTestData();
            var store = new PreferencesStore(data.Paths);
            store.Save(store.Current);
            var f = new Fixture(true);
            var interaction = new FakeDesktopInteraction();
            using var model = new MainViewModel(f.Controller, preferences: store, interaction: interaction);
            model.Mode = ConnectionMode.Socks5;
            var saved = File.ReadAllText(data.Paths.Settings);
            foreach (var text in new[] { "", "0", "-1", "65536", "abc", "+1080", " 1080" })
            {
                model.ProxyPortText = text;
                Assert(model.HasProxyPortError && ((IDataErrorInfo)model)[nameof(model.ProxyPortText)].Length > 0 &&
                    !model.CanPower && !model.CopyProxyCommand.CanExecute(null), "invalid port accepted: " + text);
                Assert(File.ReadAllText(data.Paths.Settings) == saved, "invalid draft changed saved preferences");
            }
            Assert(interaction.Messages.Count == 0, "typing invalid text opened a modal error");
            model.ProxyPortText = "43210";
            Assert(!model.HasProxyPortError && model.CanPower && model.CopyProxyCommand.CanExecute(null) &&
                model.ProxyAddress == "socks5://127.0.0.1:43210" &&
                new PreferencesStore(data.Paths).Current.ProxyPort == 43210, "valid port did not recover/persist");
            model.CopyProxyCommand.Execute(null);
            Assert(interaction.CopiedText == model.ProxyAddress, "copy command used a stale port");
            return Task.CompletedTask;
        })),
        ("UI proxy port binding validates text before a connect click or focus change", () => UiTestHost.RunAsync(() =>
        {
            var f = new Fixture(true);
            using var model = new MainViewModel(f.Controller);
            var window = new MainWindow(model);
            try
            {
                model.Mode = ConnectionMode.HttpProxy;
                model.SettingsCommand.Execute(null);
                var root = (FrameworkElement)window.Content;
                root.Measure(new Size(1060, 980));
                root.Arrange(new Rect(0, 0, 1060, 980));
                root.UpdateLayout();
                var input = Descendants(root).OfType<TextBox>().Single(t =>
                    BindingOperations.GetBinding(t, TextBox.TextProperty)?.Path.Path == nameof(model.ProxyPortText));
                input.Text = "abc";
                input.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
                Assert(model.HasProxyPortError && !model.CanPower && Validation.GetHasError(input),
                    "binding hid invalid text while leaving connect enabled");
            }
            finally { window.RequestExit(); }
            return Task.CompletedTask;
        })),
        ("UI stale diagnostics cannot reappear after a different session starts", () => UiTestHost.RunAsync(async () =>
        {
            var f = new Fixture(true);
            var pending = new TaskCompletionSource<ConnectionDiagnosticSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var model = new MainViewModel(f.Controller, runDiagnostics: (_, _) => pending.Task);
            try
            {
                var diagnosing = ((AsyncCommand)model.DiagnoseCommand).ExecuteAsync();
                Assert(!model.CanPower && !model.CanEdit, "offline diagnostics allowed settings/session races");
                // Simulate a backend session change arriving from outside this UI.
                await f.Controller.ConnectAsync(new(KillSwitch: false));
                await f.Connect();
                Assert(model.CanPower, "diagnostics disabled disconnect for the new active session");
                pending.SetResult(Snapshot());
                await diagnosing;
                Assert(model.DiagnosticChecks.Count == 0, "an old diagnostic snapshot was applied to the new session");
            }
            finally { pending.TrySetResult(Snapshot()); await f.Controller.StopAsync(); }
        })),
        ("UI diagnostic completion is cancelled promptly when the view-model is disposed", () => UiTestHost.RunAsync(async () =>
        {
            var f = new Fixture(true);
            var pending = new TaskCompletionSource<ConnectionDiagnosticSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            var model = new MainViewModel(f.Controller, runDiagnostics: (_, _) => pending.Task);
            var diagnosing = ((AsyncCommand)model.DiagnoseCommand).ExecuteAsync();
            model.Dispose();
            await diagnosing.WaitAsync(TimeSpan.FromSeconds(1));
            pending.SetResult(Snapshot());
            Assert(model.DiagnosticChecks.Count == 0 && !model.DiagnoseCommand.CanExecute(null),
                "disposed diagnostics published results");
        })),
        ("UI selecting another profile clears old diagnostics but no-op settings do not", () => UiTestHost.RunAsync(async () =>
        {
            using var data = new UiTestData();
            var store = new PreferencesStore(data.Paths);
            var catalogue = new ProfileCatalog(data.Paths, store);
            var work = catalogue.Create("Work");
            catalogue.Select("default");
            using var model = new MainViewModel(new Fixture(true).Controller, preferences: store, paths: data.Paths,
                runDiagnostics: (_, _) => Task.FromResult(Snapshot()));
            await ((AsyncCommand)model.DiagnoseCommand).ExecuteAsync();
            Assert(model.DiagnosticChecks.Count > 0, "test diagnostics did not complete");
            model.Mode = model.Mode;
            Assert(model.DiagnosticChecks.Count > 0, "an unchanged selector erased diagnostics");
            model.SelectedProfileId = work.Id;
            Assert(model.DiagnosticChecks.Count == 0 && model.SelectedProfileId == work.Id, "previous profile results remained visible");
        })),
        ("UI minimize keeps the window in the taskbar by default", () => UiTestHost.RunAsync(() =>
        {
            using var model = new MainViewModel();
            var window = new MainWindow(model);
            try
            {
                Assert(!model.MinimizeToTray, "tray hiding was enabled by default");
                window.MinimizeWindow();
                Assert(window.ShowInTaskbar && window.WindowState == WindowState.Minimized,
                    "minimize did not preserve the taskbar window");
            }
            finally { window.RequestExit(); }
            return Task.CompletedTask;
        })),
        ("UI close minimizes to the taskbar without disconnecting or entering exit flow", () => UiTestHost.RunAsync(async () =>
        {
            var f = new Fixture(true);
            var interaction = new FakeDesktopInteraction();
            using var model = new MainViewModel(f.Controller, interaction: interaction);
            var window = new MainWindow(model);
            var closed = false;
            window.Closed += (_, _) => closed = true;
            try
            {
                await f.Controller.ConnectAsync(new(KillSwitch: false));
                await f.Connect();
                interaction.OnShow = () => throw new Exception("ordinary close requested exit confirmation");
                window.Close();
                Assert(!closed && window.ShowInTaskbar && window.WindowState == WindowState.Minimized &&
                    model.IsSession && !model.IsExiting && model.CanPower,
                    "ordinary close exited, hid the taskbar window, or interrupted the session");
            }
            finally
            {
                interaction.OnShow = null;
                await f.Controller.StopAsync();
                window.RequestExit();
            }
            Assert(closed, "explicit exit did not close the window");
        })),
        ("UI exit flow gates tray commands and cancellation restores them", () => UiTestHost.RunAsync(async () =>
        {
            var f = new Fixture(true);
            var interaction = new FakeDesktopInteraction { Answer = MessageBoxResult.Cancel };
            using var model = new MainViewModel(f.Controller, interaction: interaction);
            var window = new MainWindow(model);
            try
            {
                await f.Controller.ConnectAsync(new(KillSwitch: false));
                await f.Connect();
                interaction.OnShow = () =>
                    Assert(model.IsExiting && !model.CanPower && !model.CanRestore && !model.CanEdit,
                        "tray actions remained enabled in the modal exit flow");
                window.RequestExit();
                Assert(!model.IsExiting && model.CanPower && model.CanRestore, "cancelled exit left commands disabled");
            }
            finally
            {
                interaction.OnShow = null;
                await f.Controller.StopAsync();
                window.RequestExit();
            }
        })),
        ("UI failed exit cleanup leaves the app usable and redacts its error dialog", () => UiTestHost.RunAsync(async () =>
        {
            var f = new Fixture(true);
            var interaction = new FakeDesktopInteraction { Answer = MessageBoxResult.Yes };
            using var model = new MainViewModel(f.Controller, interaction: interaction);
            var window = new MainWindow(model);
            try
            {
                await f.Controller.ConnectAsync(new());
                await f.Connect();
                f.Network.FailCleanup = true;
                window.RequestExit();
                await interaction.ErrorShown.Task.WaitAsync(TimeSpan.FromSeconds(2));
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                Assert(!model.IsExiting && model.CanPower && model.CanRestore && window.IsEnabled,
                    "failed cleanup left an unusable exit state");
                Assert(f.Kill.Enabled, "UI exit failure lowered protection");
            }
            finally
            {
                f.Network.FailCleanup = false;
                await f.Controller.StopAsync();
                window.RequestExit();
            }
        })),
        ("UI selectors keep stable identities when labels are translated", () => UiTestHost.RunAsync(() =>
        {
            using var model = new MainViewModel();
            var transports = model.TransportChoices;
            var modes = model.ModeChoices;
            var selected = transports.Single(c => c.Value == TransportMode.Http2);
            try
            {
                model.Language = AppLanguage.Turkish;
                Assert(ReferenceEquals(transports, model.TransportChoices) && ReferenceEquals(modes, model.ModeChoices) &&
                    ReferenceEquals(selected, model.TransportChoices.Single(c => c.Value == TransportMode.Http2)) &&
                    selected.Label == Localization.Instance["choice.http2"], "localization replaced selector identities");
            }
            finally { Localization.Instance.SetLanguage(AppLanguage.English); }
            return Task.CompletedTask;
        })),
        ("UI failed language save leaves both saved and visible language unchanged", () => UiTestHost.RunAsync(() =>
        {
            using var data = new UiTestData();
            var store = new PreferencesStore(data.Paths);
            store.Save(store.Current);
            var interaction = new FakeDesktopInteraction();
            using var model = new MainViewModel(preferences: store, interaction: interaction);
            var original = File.ReadAllText(data.Paths.Settings);
            using (var locked = new FileStream(data.Paths.Settings, FileMode.Open, FileAccess.Read, FileShare.None))
                model.Language = AppLanguage.Turkish;
            Assert(model.Language == AppLanguage.English && Localization.Instance.Language == AppLanguage.English &&
                interaction.Messages.Count == 1 && File.ReadAllText(data.Paths.Settings) == original,
                "failed save left the visible language inconsistent");
            return Task.CompletedTask;
        })),
        ("UI clipboard and browser failures use the redacted error presenter", () => UiTestHost.RunAsync(() =>
        {
            var interaction = new FakeDesktopInteraction { EffectError = new IOException("access_token=private-ui-token") };
            using var model = new MainViewModel(interaction: interaction);
            model.AddLog("enable copying");
            model.CopyLogsCommand.Execute(null);
            model.TermsCommand.Execute(null);
            Assert(interaction.Messages.Count == 2 && interaction.Messages.All(m => !m.Contains("private-ui-token")) &&
                !model.Logs.Contains("private-ui-token"), "UI effect errors leaked secrets or escaped their handler");
            return Task.CompletedTask;
        })),
        ("UI minimum dimensions fit a small high-DPI workspace", () => UiTestHost.RunAsync(() =>
        {
            using var model = new MainViewModel();
            var window = new MainWindow(model);
            try
            {
                window.FitToWorkArea(new Rect(0, 0, 800, 480));
                Assert(window.Width <= 800 && window.MinWidth <= 800 && window.Height <= 480 && window.MinHeight <= 480,
                    "minimum dimensions forced the window outside the workspace");
            }
            finally { window.RequestExit(); }
            return Task.CompletedTask;
        })),
        ("UI settings toggles and selectors expose localized accessibility names", () => UiTestHost.RunAsync(() =>
        {
            using var model = new MainViewModel();
            var window = new MainWindow(model);
            try
            {
                model.Language = AppLanguage.Turkish;
                model.SettingsCommand.Execute(null);
                var root = (FrameworkElement)window.Content;
                root.Measure(new Size(1060, 980));
                root.Arrange(new Rect(0, 0, 1060, 980));
                root.UpdateLayout();
                var controls = Descendants(root).Where(c => c is CheckBox or ComboBox).ToArray();
                Assert(controls.Length >= 9 && controls.All(c => !string.IsNullOrWhiteSpace(AutomationProperties.GetName(c))),
                    "settings controls have no accessible name: " + string.Join(", ",
                        controls.Select(c => c.GetType().Name + "=" + AutomationProperties.GetName(c))));
                Assert(controls.Any(c => AutomationProperties.GetName(c) == Localization.Instance["settings.language"]),
                    "accessibility names did not follow localization");
            }
            finally { window.RequestExit(); Localization.Instance.SetLanguage(AppLanguage.English); }
            return Task.CompletedTask;
        })),
        ("UI elevation preserves exact arguments and dotnet-hosted entry assemblies", () =>
        {
            string[] arguments = ["--startup", "", @"C:\folder with spaces\", "embedded\"quote", "\targument"];
            var native = App.CreateElevatedStartInfo(@"C:\Usque\Usque.Desktop.exe", @"C:\Usque\Usque.Desktop.dll", arguments);
            Assert(native.UseShellExecute && native.Verb == "runas" && native.ArgumentList.SequenceEqual(arguments),
                "native elevation altered argument boundaries");
            var hosted = App.CreateElevatedStartInfo(@"C:\dotnet\dotnet.exe", @"C:\Usque\Usque.Desktop.dll", arguments);
            Assert(hosted.ArgumentList.SequenceEqual(new[] { @"C:\Usque\Usque.Desktop.dll" }.Concat(arguments)),
                "elevation dropped the DLL when launched through dotnet");
            return Task.CompletedTask;
        })
    ];

    internal static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static ConnectionDiagnosticSnapshot Snapshot() => new(DateTimeOffset.UtcNow, ConnectionMode.Tunnel,
        [new("diag.dns", DiagnosticOutcome.Passed, "diag.probe.ok", "")]);
    private sealed class SensitiveHealth : IConnectionHealth
    {
        public Task<HealthResult> CheckAsync(CancellationToken token) =>
            Task.FromResult(new HealthResult(DateTimeOffset.UtcNow, true, true, true, 1, "access_token=secret-health-token"));
    }
}

internal sealed class FakeDesktopInteraction : IDesktopInteraction
{
    public MessageBoxResult Answer { get; init; } = MessageBoxResult.OK;
    public List<string> Messages { get; } = [];
    public Action? OnShow { get; set; }
    public Exception? EffectError { get; init; }
    public string? CopiedText { get; private set; }
    public TaskCompletionSource ErrorShown { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public MessageBoxResult Show(string text, string title, MessageBoxButton buttons, MessageBoxImage image)
    {
        Messages.Add(text);
        OnShow?.Invoke();
        if (image == MessageBoxImage.Error) ErrorShown.TrySetResult();
        return Answer;
    }
    public string? SelectProfile(string title) => null;
    public string? SelectReport(string title, string fileName) => null;
    public void CopyText(string text) { if (EffectError != null) throw EffectError; CopiedText = text; }
    public void OpenTerms() { if (EffectError != null) throw EffectError; }
}

internal sealed class UiTestData : IDisposable
{
    public AppPaths Paths { get; }
    public UiTestData()
    {
        var directory = Path.Combine(Path.GetTempPath(), "opencode", "usque-ui-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Paths = new(directory);
    }
    public void Dispose() => Directory.Delete(Paths.DataDirectory, true);
}
