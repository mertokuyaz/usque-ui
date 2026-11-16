using Usque.Core;

namespace Usque.Desktop;

internal sealed record TraySnapshot(ConnectionState State, bool IsProxy, string DownloadRate, string UploadRate,
    bool RatesAvailable, string Detail);
internal sealed record TrayNotice(string Title, string Body, bool Warning = false);
internal sealed record TrayUpdate(string Tooltip, TrayNotice? Notice);

// Pure presentation/state-transition policy. No timers, network requests or
// native notification icons; tests can exercise notifications without showing UI.
internal sealed class TrayPresentation
{
    private static AppLocalization L => AppLocalization.Instance;
    private ConnectionState _lastState;
    private bool _connectionEstablished, _connectedNoticeSent, _pendingConnected;

    public TrayPresentation(ConnectionState initialState)
    {
        _lastState = initialState;
        _connectionEstablished = initialState is ConnectionState.Connected or ConnectionState.Degraded;
        // Attaching a tray to an existing session is not a new connection.
        _connectedNoticeSent = _connectionEstablished;
    }

    public TrayUpdate Update(TraySnapshot snapshot, bool trafficSample = false)
    {
        TrayNotice? notice = null;
        if (snapshot.State != _lastState)
        {
            if (snapshot.State is ConnectionState.Connected or ConnectionState.Degraded)
            {
                _connectionEstablished = true;
                _pendingConnected = snapshot.State == ConnectionState.Connected && !_connectedNoticeSent;
                if (snapshot.State == ConnectionState.Degraded) notice = StateWarning(snapshot);
            }
            else
            {
                _pendingConnected = false;
                // Normal disconnect is announced when cleanup finishes, not
                // while the engine may still be shutting down.
                if (snapshot.State != ConnectionState.Disconnecting)
                {
                    if (_connectionEstablished)
                    {
                        var body = L[snapshot.State == ConnectionState.Reconnecting
                            ? "tray.notice.lostRetry" : "tray.notice.disconnectedBody"];
                        notice = new(L["tray.notice.disconnectedTitle"], Limit(body + "\n" + snapshot.Detail, 255),
                            snapshot.State != ConnectionState.Offline);
                    }
                    else if (snapshot.State is ConnectionState.Reconnecting or ConnectionState.Blocked or ConnectionState.Error)
                        notice = StateWarning(snapshot);
                    _connectionEstablished = false;
                    _connectedNoticeSent = false;
                }
            }
            _lastState = snapshot.State;
        }

        if (_pendingConnected && snapshot.State == ConnectionState.Connected && trafficSample)
        {
            _pendingConnected = false;
            _connectedNoticeSent = true;
            var rates = L.Format("tray.notice.rates", Download(snapshot), Upload(snapshot));
            var explanation = snapshot.IsProxy ? L["proxy.traffic"]
                : snapshot.RatesAvailable ? L["tray.notice.instantRates"] : L["tray.notice.ratesPending"];
            notice = new(L[snapshot.IsProxy ? "tray.notice.proxyTitle" : "tray.notice.connectedTitle"],
                Limit(rates + "\n" + explanation, 255));
        }
        return new(FormatTooltip(snapshot), notice);
    }

    public static string FormatTooltip(TraySnapshot snapshot)
    {
        var key = snapshot.State switch
        {
            ConnectionState.Connected => snapshot.IsProxy ? "tray.status.proxy" : "tray.status.connected",
            ConnectionState.Degraded => snapshot.IsProxy ? "tray.status.proxyWarning" : "tray.status.degraded",
            ConnectionState.Registering => "tray.status.registering",
            ConnectionState.Connecting or ConnectionState.Verifying => "tray.status.connecting",
            ConnectionState.Reconnecting => "tray.status.reconnecting",
            ConnectionState.Disconnecting => "tray.status.disconnecting",
            ConnectionState.Blocked => "tray.status.blocked",
            ConnectionState.Error => "tray.status.error",
            _ => "tray.status.offline"
        };
        var header = Limit("Usque · " + L[key], 30);
        // NotifyIcon's conservative Windows-compatible tooltip limit is 63 UTF-16
        // characters. Bound each rate so neither direction gets cut off.
        return snapshot.State is ConnectionState.Connected or ConnectionState.Degraded
            ? $"{header}\n↓ {Limit(Download(snapshot), 13)}\n↑ {Limit(Upload(snapshot), 13)}"
            : header;
    }

    private static string Download(TraySnapshot snapshot) =>
        !snapshot.IsProxy && snapshot.RatesAvailable ? snapshot.DownloadRate : "—";
    private static string Upload(TraySnapshot snapshot) =>
        !snapshot.IsProxy && snapshot.RatesAvailable ? snapshot.UploadRate : "—";

    private static TrayNotice StateWarning(TraySnapshot snapshot)
    {
        var key = snapshot.State switch
        {
            ConnectionState.Degraded => "title.degraded", ConnectionState.Reconnecting => "title.reconnecting",
            ConnectionState.Blocked => "title.blocked", _ => "title.error"
        };
        return new(Limit("Usque: " + L[key], 63), Limit(snapshot.Detail, 255), true);
    }

    private static string Limit(string text, int maximum)
    {
        if (text.Length <= maximum) return text;
        var length = maximum - 1;
        if (char.IsHighSurrogate(text[length - 1])) length--;
        return text[..length] + "…";
    }
}
