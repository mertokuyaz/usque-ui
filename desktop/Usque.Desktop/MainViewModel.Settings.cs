using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using Usque.Core;

namespace Usque.Desktop;

public sealed partial class MainViewModel
{
    // The store returns defensive catalogue copies. Keep one private snapshot
    // rather than copying up to 32 profiles on every WPF binding evaluation.
    private Preferences Preferences => _currentPreferences;
    public bool KillSwitch { get => Preferences.KillSwitch; set => ChangePreference(p => p with { KillSwitch = value }); }
    public bool KillSwitchSelection
    {
        get => _controller?.ProtectionActive == true || !IsProxy && KillSwitch;
        set { if (!IsProxy) KillSwitch = value; }
    }
    public TransportMode PreferredTransport { get => Preferences.Transport; set => ChangePreference(p => p with { Transport = value }); }
    public ConnectionMode Mode { get => Preferences.Mode; set => ChangePreference(p => p with { Mode = value }); }
    public int ProxyPort { get => Preferences.ProxyPort; set => ProxyPortText = value.ToString(CultureInfo.InvariantCulture); }
    public string ProxyPortText
    {
        get => _proxyPortText;
        set
        {
            if (!CanEdit || value == _proxyPortText) return;
            _proxyPortText = value;
            if (TryGetProxyPort(out var port) && !ChangePreference(p => p with { ProxyPort = port }))
                _proxyPortText = ProxyPort.ToString(CultureInfo.InvariantCulture);
            Refresh();
        }
    }
    private bool TryGetProxyPort(out int port) => int.TryParse(_proxyPortText, NumberStyles.None,
        CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535;
    public bool HasProxyPortError => !TryGetProxyPort(out _);
    public string ProxyPortError => HasProxyPortError ? L["proxy.portError"] : "";
    string IDataErrorInfo.Error => string.Join(Environment.NewLine, new[] { ProxyPortError, ProfileNameError }.Where(s => s.Length > 0));
    string IDataErrorInfo.this[string columnName] => columnName switch
    { nameof(ProxyPortText) => ProxyPortError, nameof(ProfileName) => ProfileNameError, _ => "" };
    public bool AutoReconnect { get => Preferences.AutoReconnect; set => ChangePreference(p => p with { AutoReconnect = value }); }
    public bool IsProxy => (IsSession ? _controller!.ActiveOptions.Mode : Mode) != ConnectionMode.Tunnel;
    public bool CanConfigureKillSwitch => CanEdit && !IsProxy;
    public string ProxyAddress => IsSession ? _controller!.ActiveOptions.ProxyUri.AbsoluteUri.TrimEnd('/')
        : HasProxyPortError ? L["proxy.invalidAddress"] : new ConnectOptions(Mode: Mode, ProxyPort: ProxyPort).ProxyUri.AbsoluteUri.TrimEnd('/');
    public string ModeDescription => IsProxy ? L["proxy.warning"] : L["mode.tunnel.hint"];
    public AppLanguage Language
    {
        get => Preferences.Language;
        set
        {
            if (!IsAvailable || value == Language) return;
            var previous = Preferences.Language;
            try
            {
                Save(Preferences with { Language = value });
                AppLocalization.Instance.SetLanguage(value);
            }
            catch (Exception ex)
            {
                try { AppLocalization.Instance.SetLanguage(previous); } catch { }
                ReportError(ex);
            }
            Refresh();
        }
    }
    public bool MinimizeToTray { get => Preferences.MinimizeToTray; set => ChangeWindowPreference(p => p with { MinimizeToTray = value }); }
    // Window behavior never affects networking, so unlike connection settings it
    // stays editable during a session (the main time people want to change it).
    public bool CanChangeWindowBehavior => IsAvailable;
    public bool AutoConnect
    {
        get => Preferences.AutoConnect;
        set
        {
            if (!CanEdit || value == AutoConnect) return;
            if (value && !Preferences.TermsAccepted && !ConfirmTerms()) return;
            ChangePreference(p => p with { AutoConnect = value, TermsAccepted = p.TermsAccepted || value });
        }
    }
    public bool StartWithWindows
    {
        get => _startupEnabled == true;
        set
        {
            if (!CanConfigureStartup || IsPreview || value == StartWithWindows) return;
            if (value && _interaction.Show(L["dlg.startup.body"], L["dlg.startup.title"], MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK)
            {
                Refresh();
                return;
            }
            _startupBusy = true;
            Refresh();
            StartupOperation = SetStartupEnabledAsync(value);
        }
    }
    public bool CanConfigureStartup => _startup != null && _preferences != null && _startupEnabled.HasValue && !_refreshingStartup && CanEdit;
    public string ProfileDescription => _controller?.ProfileExists == true ? L["profile.active"] : L["profile.pending"];
    public string ProtectionDescription => _controller?.ProtectionActive == true ? L["protect.active"]
        : IsProxy ? L["proxy.noKillSwitch"] : KillSwitch ? L["protect.on"] : L["protect.off"];
    public ObservableCollection<ProfileEntry> Profiles { get; } = [];
    public List<LanguageOption> LanguageOptions { get; } = Languages.Options();
    public IReadOnlyList<LocalizedChoice<TransportMode>> TransportChoices => _transportChoices;
    public IReadOnlyList<LocalizedChoice<ConnectionMode>> ModeChoices => _modeChoices;
    public string SelectedProfileId
    {
        get => Preferences.SelectedProfileId;
        set
        {
            if (_refreshingProfiles || string.IsNullOrEmpty(value) || value == SelectedProfileId || !CanEdit) return;
            try { _catalog?.Select(value); ClearDiagnostics(); RefreshProfiles(); Refresh(); }
            catch (Exception ex) { ReportError(ex); Changed(); }
        }
    }
    public string ProfileName
    {
        get => _profileName;
        set { if (!CanEdit) return; _profileName = value; Refresh(); }
    }
    public string ProfileNameError => ValidateProfileName(excludeSelected: true);
    public string CreateProfileNameError => ValidateProfileName(excludeSelected: false);
    private string ValidateProfileName(bool excludeSelected)
    {
        var name = ProfileName.Trim();
        if (name.Length is < 1 or > 40 || name.Any(char.IsControl)) return L["profile.nameError"];
        return Profiles.Any(p => (!excludeSelected || p.Id != SelectedProfileId) &&
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) ? L["profile.nameDuplicate"] : "";
    }

    private static IReadOnlyList<LocalizedChoice<TransportMode>> BuildTransportChoices() =>
        Array.AsReadOnly<LocalizedChoice<TransportMode>>([new(TransportMode.Automatic, "choice.automatic"),
            new(TransportMode.Quic, "choice.quic"), new(TransportMode.Http2, "choice.http2")]);
    private static IReadOnlyList<LocalizedChoice<ConnectionMode>> BuildModeChoices() =>
        Array.AsReadOnly<LocalizedChoice<ConnectionMode>>([new(ConnectionMode.Tunnel, "mode.tunnel"),
            new(ConnectionMode.Socks5, "mode.socks5"), new(ConnectionMode.HttpProxy, "mode.http")]);
    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e) => Dispatch(() =>
    {
        foreach (var choice in _transportChoices) choice.Refresh();
        foreach (var choice in _modeChoices) choice.Refresh();
        RefreshDiagnosticRows();
        Refresh();
    });
    private void ReadPreferences() { if (_preferences != null) _currentPreferences = _preferences.Current; }
    private void Save(Preferences preferences)
    {
        if (_preferences != null) { _preferences.Save(preferences); ReadPreferences(); }
        else _currentPreferences = preferences;
    }
    private bool ChangePreference(Func<Preferences, Preferences> change)
    {
        if (!CanEdit) return false;
        try
        {
            var next = change(Preferences);
            if (next == Preferences) return true;
            Save(next);
            ClearDiagnostics();
            Refresh();
            return true;
        }
        catch (Exception ex) { ReportError(ex); Refresh(); return false; }
    }
    private void ChangeWindowPreference(Func<Preferences, Preferences> change)
    {
        if (!CanChangeWindowBehavior) return;
        try
        {
            var next = change(Preferences);
            if (next != Preferences) Save(next);
        }
        catch (Exception ex) { ReportError(ex); }
        Refresh();
    }
    private void RefreshProfiles(bool preserveDraft = false)
    {
        ReadPreferences();
        _refreshingProfiles = true;
        try
        {
            Profiles.Clear();
            foreach (var entry in Preferences.Profiles) Profiles.Add(entry);
            if (!preserveDraft) _profileName = Profiles.First(p => p.Id == SelectedProfileId).Name;
            _redactor.AddSecrets(Profiles.Select(p => p.Name));
            if (_paths != null) _redactor.RefreshFromProfile(_paths.Profile);
        }
        finally { _refreshingProfiles = false; }
    }
    private void ChangeProfiles(Action action)
    {
        if (!CanEdit) return;
        try { action(); }
        catch
        {
            // Reload any recovered catalogue state without losing the user's
            // draft if a disk/permission error interrupted the operation.
            RefreshProfiles(preserveDraft: true);
            Refresh();
            throw;
        }
        ClearDiagnostics(); RefreshProfiles(); Refresh();
    }
    private void DeleteProfile()
    {
        if (_interaction.Show(L["dlg.delete.body"], L["dlg.delete.title"], MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK)
            ChangeProfiles(() => _catalog!.Delete(SelectedProfileId));
    }
    private bool ConfirmTerms() => _interaction.Show(L["dlg.terms.body"], L["dlg.terms.title"], MessageBoxButton.OKCancel, MessageBoxImage.Information) == MessageBoxResult.OK;
    private async Task ImportAsync()
    {
        if (_profiles == null) return;
        var source = _interaction.SelectProfile(L["dlg.import.title"]);
        if (source == null) return;
        if (_profiles.Exists && _interaction.Show(L["dlg.import.body"], L["dlg.import.title"], MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        await ActAsync(async () =>
        {
            await _profiles.ImportAsync(source);
            ClearDiagnostics();
            RefreshProfiles();
            Refresh();
            AddLog(L["log.imported"]);
        });
    }
}
