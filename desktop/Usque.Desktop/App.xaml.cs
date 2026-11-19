using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using Usque.Core;

namespace Usque.Desktop;

public partial class App : Application
{
    private Mutex? _instance;
    private NetworkEnvironmentMonitor? _networkMonitor;
    private InstanceActivation? _activation;
    internal bool SuppressStartupForTests { get; init; }
    protected override async void OnStartup(StartupEventArgs e)
    {
        if (SuppressStartupForTests) return;
        base.OnStartup(e);
        bool preview = e.Args.Contains("--preview");
        try
        {
            if (preview)
            {
                new MainWindow(new MainViewModel()).Show();
                return;
            }
            TryLoadStartupLanguage();
            // The manifest requests asInvoker so any launcher (shortcut, terminal,
            // other programs) can start this process even when elevation is not
            // available. If we are not already elevated, relaunch ourselves with
            // ShellExecute's runas verb and exit; VPN, WFP and routing need admin.
            if (!IsCurrentProcessElevated())
            {
                if (!TryRelaunchElevated(e.Args))
                    throw new InvalidOperationException(AppLocalization.Instance["startup.adminRequired"]);
                Shutdown();
                return;
            }
            _instance = new Mutex(true, @"Global\UsqueDesktop-57d78a76", out var first);
            if (!first)
            {
                // Sign-in launches stay quiet; an ordinary launch brings the running
                // window forward (it may be hidden in the notification area).
                // Recovery requests and other sessions still get the explanation.
                if (!e.Args.Contains("--restore-network") && (e.Args.Contains("--startup") || InstanceActivation.TrySignal()))
                {
                    Shutdown();
                    return;
                }
                throw new InvalidOperationException(AppLocalization.Instance["startup.alreadyRunning"]);
            }
            var paths = new AppPaths();
            paths.EnsureSecureDirectory();
            if (e.Args.Contains("--restore-network"))
            {
                // Recovery must work even if preferences or the selected profile
                // are corrupt. Neither is needed to release machine-wide rules.
                RestoreAndExit(new ConnectionController(new ProfileStore(paths), new UsqueEngine(paths, _ => { }),
                    new NetworkManager(paths, _ => { }), new WfpKillSwitch(paths)));
                return;
            }
            var preferences = new PreferencesStore(paths);
            AppLocalization.Instance.SetLanguage(preferences.Current.Language);
            if (e.Args.Contains("--startup") && !preferences.Current.StartWithWindows) { Shutdown(); return; }
            var profile = new ProfileStore(paths);
            MainViewModel? viewModel = null;
            void Log(string line) => viewModel?.AddLog(line);
            var controller = new ConnectionController(profile, new UsqueEngine(paths, Log),
                new NetworkManager(paths, Log), new WfpKillSwitch(paths), new WarpHealthCheck());
            viewModel = new MainViewModel(controller, profile, preferences, paths,
                new StartupRegistration(Environment.ProcessPath ?? throw new InvalidOperationException(AppLocalization.Instance["startup.noExecutable"])));
            var window = new MainWindow(viewModel);
            _activation = InstanceActivation.Listen(() =>
            {
                if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(window.RestoreFromTray);
            });
            _networkMonitor = new NetworkEnvironmentMonitor(controller);
            controller.Initialize();
            window.Show();
            if (e.Args.Contains("--startup")) window.MinimizeWindow();
            await viewModel.StartAutomaticallyAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, AppLocalization.Instance["dlg.appTitle"], MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static void TryLoadStartupLanguage()
    {
        // Best effort only: recovery and elevation must still work when settings
        // are corrupt or not readable before elevation. Never rewrite preferences.
        try
        {
            var paths = new AppPaths();
            if (!File.Exists(paths.Settings)) return;
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(paths.Settings));
            if (document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                document.RootElement.TryGetProperty("Language", out var language) &&
                language.ValueKind == System.Text.Json.JsonValueKind.String &&
                Enum.TryParse<AppLanguage>(language.GetString(), out var parsed) && Enum.IsDefined(parsed))
                AppLocalization.Instance.SetLanguage(parsed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // English remains available even when preferences cannot be loaded.
        }
    }

    private static bool IsCurrentProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity != null && new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    // Relaunch this executable elevated, carrying the command-line arguments.
    // Returns false if the user declined the prompt or elevation is impossible.
    private static bool TryRelaunchElevated(string[] args)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable)) return false;
        var start = CreateElevatedStartInfo(executable, typeof(App).Assembly.Location, args);
        try { Process.Start(start); return true; }
        catch (Win32Exception) { return false; }
    }
    internal static ProcessStartInfo CreateElevatedStartInfo(string executable, string assemblyPath, IEnumerable<string> args)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        // ArgumentList handles empty arguments, embedded quotes, tabs and trailing
        // backslashes correctly. Preserve the entry DLL when launched by dotnet.
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(assemblyPath);
        foreach (var argument in args) start.ArgumentList.Add(argument);
        return start;
    }

    private async void RestoreAndExit(ConnectionController controller)
    {
        try
        {
            await controller.StopAsync();
            MessageBox.Show(AppLocalization.Instance["recovery.restored"], AppLocalization.Instance["dlg.appTitle"]);
            Shutdown();
        }
        catch (Exception ex)
        {
            MessageBox.Show(AppLocalization.Instance.Format("recovery.failed", ex.Message), AppLocalization.Instance["dlg.appTitle"]);
            Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _activation?.Dispose();
        _networkMonitor?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
