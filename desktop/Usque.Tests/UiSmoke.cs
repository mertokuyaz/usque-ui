using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Usque.Core;
using Usque.Desktop;
using Localization = Usque.Core.Localization;

internal static class UiSmoke
{
    // Instantiate and render only an off-screen preview. Do not call Show(),
    // Application.Run(), OnStartup(), create a tray icon, or touch host networking.
    public static Task VerifyAsync() => UiTestHost.RunAsync(() =>
    {
        var trace = new StringWriter();
        using var listener = new TextWriterTraceListener(trace);
        var source = PresentationTraceSources.DataBindingSource;
        var previous = source.Switch.Level;
        using var viewModel = new MainViewModel();
        var window = new MainWindow(viewModel);
        try
        {
            source.Switch.Level = SourceLevels.Error;
            source.Listeners.Add(listener);
            var root = (FrameworkElement)window.Content;
            if (root is System.Windows.Controls.Border border) border.Background = window.Background;
            var directory = Path.Combine(Path.GetTempPath(), "opencode", "usque-ui-smoke-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            Render("overview");
            Render("overview-small", 800, 480);
            viewModel.SettingsCommand.Execute(null);
            Render("settings");
            viewModel.Mode = ConnectionMode.Socks5;
            if (viewModel.CanConfigureKillSwitch || viewModel.Coverage == Localization.Instance["coverage.full"] ||
                !viewModel.ProxyAddress.StartsWith("socks5://127.0.0.1:"))
                throw new Exception("Proxy UI claims full-device coverage or permits the tunnel kill switch.");
            Render("settings-socks5");
            viewModel.ProxyPortText = "abc";
            Render("settings-invalid-port");
            viewModel.ProxyPortText = "1080";
            viewModel.Mode = ConnectionMode.HttpProxy;
            if (!viewModel.ProxyAddress.StartsWith("http://127.0.0.1:") || viewModel.DownloadRate != "—")
                throw new Exception("HTTP proxy address or traffic display is misleading.");
            viewModel.Language = AppLanguage.Turkish;
            Render("settings-http-turkish");
            Render("settings-small-turkish", 800, 480);
            viewModel.DiagnosticChecks.Add(new("DNS", "Uyarı", "Alan adına ulaşılamadı.", "DNS ilkesini kontrol edin."));
            Render("diagnostics-small-turkish", 800, 480, scrollToEnd: true);
            viewModel.Language = AppLanguage.Arabic;
            if (viewModel.WindowFlowDirection != FlowDirection.RightToLeft)
                throw new Exception("Arabic did not switch FlowDirection to RightToLeft.");
            Render("settings-arabic-rtl");
            viewModel.Language = AppLanguage.Chinese;
            Render("settings-chinese");
            foreach (var language in Enum.GetValues<AppLanguage>())
            {
                viewModel.Language = language;
                Render("settings-" + language.ToString().ToLowerInvariant());
                if (root.FlowDirection != viewModel.WindowFlowDirection)
                    throw new Exception($"Content flow direction did not update for {language}.");
            }
            viewModel.Language = AppLanguage.English;
            viewModel.Mode = ConnectionMode.Tunnel;
            viewModel.LogsCommand.Execute(null);
            Render("logs");
            if (viewModel.PowerCommand.CanExecute(null)) throw new Exception("Preview must not enable VPN connection.");
            VerifySessionStateUpdatesSynchronously();
            VerifyHealthDoesNotBlockDisconnect();
            if (!string.IsNullOrWhiteSpace(trace.ToString())) throw new Exception("WPF binding errors: " + trace);
            Console.WriteLine("UI snapshots (off-screen): " + directory);

            void Render(string name, int width = 1060, int height = 980, bool scrollToEnd = false)
            {
                root.Width = width; root.Height = height;
                root.Measure(new Size(width, height));
                root.Arrange(new Rect(0, 0, width, height));
                root.UpdateLayout();
                var settingsScroll = UiRegressionTests.Descendants(root).OfType<System.Windows.Controls.ScrollViewer>()
                    .FirstOrDefault(v => v.Visibility == Visibility.Visible && v.Content is System.Windows.Controls.StackPanel);
                if (scrollToEnd) settingsScroll?.ScrollToEnd(); else settingsScroll?.ScrollToTop();
                root.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                if (TextContent(root).Any(text => text.Contains("LocalizedChoice`") || text.Contains("ProfileEntry {")))
                    throw new Exception("A selector rendered a type name instead of its display label.");
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(root);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(directory, name + ".png"));
                encoder.Save(file);
            }
        }
        finally
        {
            window.RequestExit();
            Localization.Instance.SetLanguage(AppLanguage.English);
            source.Listeners.Remove(listener);
            source.Switch.Level = previous;
        }
        return Task.CompletedTask;
    });

    private static void VerifySessionStateUpdatesSynchronously()
    {
        var fixture = new Fixture(true);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Profiles.LoadGate = gate.Task;
        using var model = new MainViewModel(fixture.Controller);
        try
        {
            Pump(fixture.Controller.ConnectAsync(new()));
            if (model.State != ConnectionState.Connecting || model.CanEdit)
                throw new Exception("Starting a session left profile editing enabled until the next dispatcher turn.");
        }
        finally
        {
            var stop = fixture.Controller.StopAsync();
            gate.TrySetResult();
            Pump(stop);
        }
    }

    private static void VerifyHealthDoesNotBlockDisconnect()
    {
        var health = new PendingHealth();
        var fixture = new Fixture(true, health);
        using var model = new MainViewModel(fixture.Controller);
        Pump(fixture.Controller.ConnectAsync(new()));
        Pump(fixture.Connect());
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        var probe = ((AsyncCommand)model.CheckHealthCommand).ExecuteAsync();
        if (!model.CanPower || !model.RestoreCommand.CanExecute(null) || model.CanCheckHealth)
            throw new Exception("A pending health check disabled disconnect/recovery or allowed duplicate probes.");
        Pump(fixture.Controller.StopAsync());
        Pump(probe);
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        if (model.State != ConnectionState.Offline || fixture.Controller.LastHealth != null)
            throw new Exception("Health cancellation did not leave a clean disconnected state.");
    }

    private static void Pump(Task task)
    {
        if (!task.IsCompleted)
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var frame = new DispatcherFrame();
            var expired = false;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += (_, _) => { expired = true; frame.Continue = false; };
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
            timer.Start();
            try { Dispatcher.PushFrame(frame); }
            finally { timer.Stop(); }
            if (expired) throw new TimeoutException("Off-screen UI operation timed out.");
        }
        task.GetAwaiter().GetResult();
    }

    private sealed class PendingHealth : IConnectionHealth
    {
        private int _checks;
        private readonly TaskCompletionSource<HealthResult> _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<HealthResult> CheckAsync(CancellationToken token) => ++_checks == 1
            ? Task.FromResult(new HealthResult(DateTimeOffset.UtcNow, true, true, true, 1, "Verified fake health"))
            : _pending.Task.WaitAsync(token);
    }

    private static IEnumerable<string> TextContent(DependencyObject node)
    {
        if (node is System.Windows.Controls.TextBlock text) yield return text.Text;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            foreach (var content in TextContent(VisualTreeHelper.GetChild(node, i))) yield return content;
    }

}
