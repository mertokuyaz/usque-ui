using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Usque.Core;

namespace Usque.Desktop;

public sealed partial class MainViewModel : INotifyPropertyChanged, IDataErrorInfo, IDisposable
{
    private static AppLocalization L => AppLocalization.Instance;
    private readonly ConnectionController? _controller;
    private readonly ProfileStore? _profiles;
    private readonly PreferencesStore? _preferences;
    private readonly ProfileCatalog? _catalog;
    private readonly AppPaths? _paths;
    private readonly IStartupRegistration? _startup;
    private readonly IDesktopInteraction _interaction;
    private readonly Func<ConnectionMode, CancellationToken, Task<ConnectionDiagnosticSnapshot>>? _runDiagnostics;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly CancellationTokenSource _lifetime = new();
    private ConnectionDiagnosticSnapshot? _diagnosticSnapshot;
    private readonly DispatcherTimer _timer;
    private readonly TrafficMonitor _traffic = new();
    private readonly Stopwatch _sessionClock = new();
    private readonly DiagnosticRedactor _redactor = new();
    private readonly Queue<string> _logs = new();
    private readonly Action<ConnectionStatus> _statusHandler;
    private readonly Action<HealthResult> _healthHandler;
    private readonly IReadOnlyList<LocalizedChoice<TransportMode>> _transportChoices;
    private readonly IReadOnlyList<LocalizedChoice<ConnectionMode>> _modeChoices;
    private Preferences _currentPreferences = new();
    private string _proxyPortText = "1080";
    private ConnectionStatus _status = new(ConnectionState.Offline, "")
        { DetailKey = "detail.offline" };
    private string _page = "nav.overview", _profileName = "Default";
    private bool _acting, _checkingHealth, _diagnosing, _refreshingProfiles, _disposed, _exiting;
    private int _diagnosticGeneration;
    public event PropertyChangedEventHandler? PropertyChanged;
    public bool IsPreview => _controller == null;
    public FlowDirection WindowFlowDirection => AppLocalization.Instance.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
    public string VersionLabel => AppLocalization.Instance["side.version"] + Assembly.GetExecutingAssembly().GetName().Version?.ToString(3);
    public string this[string key] => AppLocalization.Instance[key];
    public string Reconnects => (_controller?.ReconnectCount ?? 0).ToString();
    public string RetryCountdown => _controller?.RetryAtUtc is { } retry
        ? L.Format("recovery.countdown", Math.Max(0, (int)Math.Ceiling((retry - DateTimeOffset.UtcNow).TotalSeconds))) : "";
    public string LastSuccessfulCheck => _controller?.LastSuccessfulCheckUtc is { } last
        ? L.Format("health.lastSuccess", last.ToLocalTime().ToString("HH:mm:ss")) : L["health.notChecked"];
    public string TrafficNote => IsProxy ? L["proxy.traffic"] : "";
    private bool IsAvailable => !_disposed && !_exiting;
    public bool IsExiting => _exiting;
    public bool CanRestore => IsAvailable && !IsPreview && !_acting && !_startupBusy && State != ConnectionState.Disconnecting;
    public bool ShowOverview => _page == "nav.overview";
    public bool ShowLogs => _page == "nav.log";
    public bool ShowSettings => _page == "nav.settings";
    public string PageName => L[_page];
    public ConnectionState State => _status.State;
    public bool IsSession => State is ConnectionState.Registering or ConnectionState.Connecting or ConnectionState.Verifying or ConnectionState.Connected or ConnectionState.Degraded or ConnectionState.Reconnecting;
    public bool IsBusy => State is ConnectionState.Registering or ConnectionState.Connecting or ConnectionState.Verifying or ConnectionState.Reconnecting or ConnectionState.Disconnecting;
    public bool CanEdit => IsAvailable && !_acting && !_diagnosing && !_startupBusy && !IsSession && State != ConnectionState.Disconnecting && !(_controller?.ProtectionActive ?? false) && !(_controller?.HasRecoveryState ?? false);
    public bool CanPower => IsAvailable && !IsPreview && !_acting && !_startupBusy && State != ConnectionState.Disconnecting &&
        (!_diagnosing || IsSession) && (IsSession || !IsProxy || !HasProxyPortError);
    public bool CanCheckHealth => IsAvailable && !IsPreview && !_acting && !_checkingHealth && !_diagnosing && State is (ConnectionState.Connected or ConnectionState.Degraded);
    public bool ShowRecovery => State is ConnectionState.Blocked or ConnectionState.Error;
    public string Title => L[State switch
    {
        ConnectionState.Registering => "title.registering",
        ConnectionState.Connecting => "title.connecting",
        ConnectionState.Verifying => "title.verifying",
        ConnectionState.Connected => "title.connected",
        ConnectionState.Degraded => "title.degraded",
        ConnectionState.Reconnecting => "title.reconnecting",
        ConnectionState.Blocked => "title.blocked",
        ConnectionState.Error => "title.error",
        ConnectionState.Disconnecting => "title.disconnecting",
        _ => "title.offline"
    }];
    public string Detail => State is ConnectionState.Connected or ConnectionState.Degraded && _controller?.LastHealth != null
        ? HealthSummary : _status.Localize(_redactor.Redact);
    public string PowerHint => IsPreview ? L["hint.preview"] : IsSession ? L["hint.disconnect"] : L["hint.connect"];
    public string Badge => State switch
    {
        ConnectionState.Connected when IsProxy => L["proxy.badge"],
        ConnectionState.Connected when _controller?.LastHealth?.Healthy == true => L["badge.verified"],
        ConnectionState.Connected => L["badge.tunnel"],
        ConnectionState.Degraded => L["badge.health"],
        ConnectionState.Blocked => L["badge.blocked"],
        _ when IsBusy => L["badge.busy"],
        _ => L["badge.off"]
    };
    // Bound six times and re-read on every refresh: share frozen brushes.
    private static readonly Brush ConnectedBrush = FrozenBrush(0x67, 0xE4, 0xBF), WarningBrush = FrozenBrush(0xF2, 0xB6, 0x6E),
        BusyBrush = FrozenBrush(0x9D, 0xBF, 0xF8), IdleBrush = FrozenBrush(0x8B, 0x9D, 0xA8);
    private static Brush FrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
    public Brush StateBrush => State switch
    {
        ConnectionState.Connected => ConnectedBrush,
        ConnectionState.Blocked or ConnectionState.Error or ConnectionState.Degraded => WarningBrush,
        _ when IsBusy => BusyBrush,
        _ => IdleBrush
    };
    public string Transport => IsSession ? L[_controller!.ActiveHttp2 ? "transport.http2" : "transport.quic"] : PreferredTransport switch
    { TransportMode.Automatic => L["transport.automatic"], TransportMode.Http2 => L["transport.http2"], _ => L["transport.quic"] };
    public string SessionTime => _sessionClock.Elapsed.ToString(_sessionClock.Elapsed.TotalDays >= 1 ? @"d\.hh\:mm\:ss" : @"hh\:mm\:ss");
    public string Coverage => IsProxy ? L["proxy.coverage"] : _controller?.LastHealth is { IPv6Reachable: true, Healthy: true } ? L["coverage.verified"] : L["coverage.full"];
    internal static string FormatTrafficRate(bool isProxy, bool isSession, bool available, double rate) =>
        isProxy || isSession && !available ? "—" : TrafficAccumulator.FormatBytes(isSession ? rate : 0) + "/s";
    public string DownloadRate => FormatTrafficRate(IsProxy, IsSession, _traffic.Current.AdapterAvailable, _traffic.Current.DownloadBytesPerSecond);
    public string UploadRate => FormatTrafficRate(IsProxy, IsSession, _traffic.Current.AdapterAvailable, _traffic.Current.UploadBytesPerSecond);
    public string DownloadTotal => IsProxy ? "—" : TrafficAccumulator.FormatBytes(_traffic.Current.DownloadedBytes);
    public string UploadTotal => IsProxy ? "—" : TrafficAccumulator.FormatBytes(_traffic.Current.UploadedBytes);
    internal TraySnapshot TrayStatus => new(State, IsProxy, DownloadRate, UploadRate,
        _traffic.Current.AdapterAvailable && !IsProxy, Detail);
    public ICommand PowerCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand CreateProfileCommand { get; }
    public ICommand RenameProfileCommand { get; }
    public ICommand DeleteProfileCommand { get; }
    public ICommand OverviewCommand { get; }
    public ICommand LogsCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand CopyLogsCommand { get; }
    public ICommand ClearLogsCommand { get; }
    public ICommand TermsCommand { get; }
    public ICommand ExportDiagnosticsCommand { get; }
    public ICommand CheckHealthCommand { get; }
    public ICommand DiagnoseCommand { get; }
    public ICommand CopyProxyCommand { get; }
    public ICommand RetryConnectionCommand { get; }

    public MainViewModel(ConnectionController? controller = null, ProfileStore? profiles = null,
        PreferencesStore? preferences = null, AppPaths? paths = null, IStartupRegistration? startup = null,
        IDesktopInteraction? interaction = null,
        Func<ConnectionMode, CancellationToken, Task<ConnectionDiagnosticSnapshot>>? runDiagnostics = null)
    {
        _controller = controller; _profiles = profiles; _preferences = preferences; _paths = paths; _startup = startup;
        _interaction = interaction ?? new DesktopInteraction();
        ReadPreferences();
        _proxyPortText = ProxyPort.ToString(CultureInfo.InvariantCulture);
        _catalog = paths != null && preferences != null ? new(paths, preferences, () => CanEdit) : null;
        _transportChoices = BuildTransportChoices();
        _modeChoices = BuildModeChoices();
        _runDiagnostics = runDiagnostics;
        if (_runDiagnostics == null && controller != null && paths != null)
            _runDiagnostics = new ConnectionDiagnostics(controller, new UsqueEngine(paths, _ => { }).ValidateInstallation).RunAsync;
        _redactor.AddSecrets(new[] { Environment.MachineName, Environment.UserName }.Where(s => s.Length >= 3));
        _statusHandler = status => Dispatch(() => Update(status));
        _healthHandler = _ => Dispatch(Refresh);
        if (controller != null) { controller.StatusChanged += _statusHandler; controller.HealthChanged += _healthHandler; }
        AppLocalization.Instance.PropertyChanged += OnLanguageChanged;
        PowerCommand = new AsyncCommand(PowerAsync, () => CanPower, ReportError);
        RetryConnectionCommand = new AsyncCommand(RetryConnectionAsync, () => CanRetryConnection, ReportError);
        RestoreCommand = new AsyncCommand(RestoreAsync, () => CanRestore, ReportError);
        ImportCommand = new AsyncCommand(ImportAsync, () => _profiles != null && CanEdit, ReportError);
        CreateProfileCommand = new RelayCommand(() => ChangeProfiles(() => _catalog!.Create(ProfileName)), ReportError,
            () => _catalog != null && CanEdit && Profiles.Count < 32 && CreateProfileNameError.Length == 0);
        RenameProfileCommand = new RelayCommand(() => ChangeProfiles(() => _catalog!.Rename(SelectedProfileId, ProfileName)), ReportError,
            () => _catalog != null && CanEdit && ProfileNameError.Length == 0);
        DeleteProfileCommand = new RelayCommand(DeleteProfile, ReportError, () => _catalog != null && CanEdit && Profiles.Count > 1);
        OverviewCommand = new RelayCommand(() => Navigate("nav.overview"), ReportError, () => IsAvailable);
        LogsCommand = new RelayCommand(() => Navigate("nav.log"), ReportError, () => IsAvailable);
        SettingsCommand = new RelayCommand(() => Navigate("nav.settings"), ReportError, () => IsAvailable);
        CopyLogsCommand = new RelayCommand(() => _interaction.CopyText(Logs), ReportError, () => IsAvailable && _logs.Count > 0);
        ClearLogsCommand = new RelayCommand(() => { _logs.Clear(); Changed(nameof(Logs)); CommandManager.InvalidateRequerySuggested(); }, ReportError, () => IsAvailable && _logs.Count > 0);
        TermsCommand = new RelayCommand(_interaction.OpenTerms, ReportError, () => IsAvailable);
        ExportDiagnosticsCommand = new RelayCommand(ExportDiagnostics, ReportError, () => IsAvailable);
        CheckHealthCommand = new AsyncCommand(CheckHealthAsync, () => CanCheckHealth, ReportError);
        DiagnoseCommand = new AsyncCommand(DiagnoseAsync, () => CanDiagnose, ReportError);
        CopyProxyCommand = new RelayCommand(() => _interaction.CopyText(ProxyAddress), ReportError,
            () => IsAvailable && IsProxy && (IsSession || !HasProxyPortError));
        RefreshProfiles();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTimerTick;
        _timer.Start();
        if (preferences != null) AddLog(L["log.preferencesLoaded"]);
        if (controller != null) Update(controller.Status);
        _ = RefreshStartupAsync();
    }

    private void Navigate(string page) { _page = page; Refresh(); }
    private ConnectOptions Options => new(KillSwitch && !IsProxy, PreferredTransport == TransportMode.Http2,
        PreferredTransport == TransportMode.Automatic, Mode, ProxyPort, AutoReconnect);
    private async Task PowerAsync()
    {
        if (_controller == null) return;
        if (IsSession) { await RestoreAsync(); return; }
        await ConnectAsync();
    }
    private async Task ConnectAsync()
    {
        if (_controller == null) return;
        if (!_controller.ProfileExists && !Preferences.TermsAccepted)
        {
            if (_interaction.Show(L["dlg.firstConnect.body"], L["dlg.firstConnect.title"], MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
            Save(Preferences with { TermsAccepted = true });
        }
        await ActAsync(() => _controller.ConnectAsync(Options));
    }
    public async Task StartAutomaticallyAsync()
    {
        if (!IsAvailable || _controller == null || !Preferences.AutoConnect || !Preferences.TermsAccepted || IsProxy && HasProxyPortError) return;
        try { await _controller.ConnectAsync(Options); }
        catch (Exception ex) { AddLog(ex.Message); }
    }
    private async Task RestoreAsync()
    {
        if (_controller == null) return;
        if (_controller.ProtectionActive && _interaction.Show(L["dlg.restore.body"], L["dlg.restore.title"], MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        await ActAsync(_controller.StopAsync);
    }
    public async Task DisconnectForExitAsync() { if (_controller != null) await _controller.StopAsync(); }
    public bool NeedsExitCleanup => IsSession || (_controller?.HasRecoveryState ?? false) || (_controller?.ProtectionActive ?? false);
    private async Task ActAsync(Func<Task> action)
    {
        _acting = true; Refresh();
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ReportError(ex); }
        finally { _acting = false; Refresh(); }
    }
    private void ReportError(Exception error)
    {
        if (_disposed) return;
        AddLog(error.Message);
        _interaction.Show(_redactor.Redact(error.Message), L["dlg.appTitle"], MessageBoxButton.OK, MessageBoxImage.Error);
    }
    private void Update(ConnectionStatus status)
    {
        if (_paths != null) _redactor.RefreshFromProfile(_paths.Profile);
        if (status.State != State && status.State is not (ConnectionState.Connected or ConnectionState.Degraded))
            ClearDiagnostics();
        _status = status with { Detail = _redactor.Redact(status.Detail) };
        if (status.State is ConnectionState.Offline or ConnectionState.Blocked or ConnectionState.Error)
        {
            if (_sessionClock.IsRunning) { if (!IsProxy) _traffic.Poll(); _sessionClock.Stop(); }
        }
        else if (IsSession && !_sessionClock.IsRunning) { if (!IsProxy) _traffic.Begin(); _sessionClock.Restart(); }
        AddLog($"{status.State}: {status.Detail}");
        Refresh();
    }
    private void Dispatch(Action action)
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(() => { if (!_disposed) action(); });
    }
    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (_sessionClock.IsRunning && !IsProxy) _traffic.Poll();
        Changed(nameof(SessionTime)); Changed(nameof(DownloadRate)); Changed(nameof(UploadRate));
        Changed(nameof(DownloadTotal)); Changed(nameof(UploadTotal)); Changed(nameof(RetryCountdown)); Changed(nameof(Reconnects));
        // A single coherent tray update after both counters have been sampled.
        Changed(nameof(TrayStatus));
    }
    internal void BeginExit() { _exiting = true; Refresh(); }
    internal void CancelExit() { _exiting = false; Refresh(); }
    internal MessageBoxResult ConfirmExit() => _interaction.Show(L["dlg.exit.body"], L["dlg.exit.title"], MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
    internal void ReportExitError(Exception error) => ReportError(new InvalidOperationException(L.Format("err.restoreFailed", error.Message)));
    private void Refresh() { if (_disposed) return; Changed(string.Empty); CommandManager.InvalidateRequerySuggested(); }
    private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        _sessionClock.Stop();
        _lifetime.Cancel();
        _lifetime.Dispose();
        AppLocalization.Instance.PropertyChanged -= OnLanguageChanged;
        if (_controller != null) { _controller.StatusChanged -= _statusHandler; _controller.HealthChanged -= _healthHandler; }
        CommandManager.InvalidateRequerySuggested();
    }
}
