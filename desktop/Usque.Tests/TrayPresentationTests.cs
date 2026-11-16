using System.ComponentModel;
using Usque.Core;
using Usque.Desktop;

internal static class TrayPresentationTests
{
    public static (string Name, Func<Task> Run)[] All =>
    [
        ("Tray connection notification waits for the first complete traffic sample", () => Check(() =>
        {
            var presenter = new TrayPresentation(ConnectionState.Offline);
            var connected = Snapshot(ConnectionState.Connected);
            Assert(presenter.Update(connected).Notice == null, "notification used a pre-connection sample");
            Assert(presenter.Update(connected).Notice == null, "a binding refresh sent a premature notification");
            var notice = presenter.Update(connected, trafficSample: true).Notice;
            Assert(notice is { Warning: false } && notice.Body.Contains("2.5 MiB/s") && notice.Body.Contains("750 KiB/s") &&
                notice.Body.Contains(Localization.Instance["tray.notice.instantRates"]), "connection speeds/explanation missing");
            Assert(presenter.Update(connected, trafficSample: true).Notice == null, "rate ticks repeated the connection notification");
        })),
        ("Tray hover tooltip contains connection status and both current speeds", () => Check(() =>
        {
            var tooltip = TrayPresentation.FormatTooltip(Snapshot(ConnectionState.Connected));
            Assert(tooltip.Contains(Localization.Instance["tray.status.connected"]) &&
                tooltip.Contains("↓ 2.5 MiB/s") && tooltip.Contains("↑ 750 KiB/s"), "live hover speeds are missing");
            var newer = TrayPresentation.FormatTooltip(Snapshot(ConnectionState.Connected) with
                { DownloadRate = "10 MiB/s", UploadRate = "3 MiB/s" });
            Assert(newer.Contains("10 MiB/s") && newer.Contains("3 MiB/s") && newer != tooltip, "tooltip retained old rates");
        })),
        ("Tray disconnected and transitional tooltips never show stale speeds", () => Check(() =>
        {
            foreach (var state in new[] { ConnectionState.Offline, ConnectionState.Connecting, ConnectionState.Verifying,
                ConnectionState.Reconnecting, ConnectionState.Disconnecting, ConnectionState.Blocked, ConnectionState.Error })
            {
                var tooltip = TrayPresentation.FormatTooltip(Snapshot(state));
                Assert(!tooltip.Contains("MiB/s") && !tooltip.Contains("KiB/s"), "stale rates shown in " + state);
            }
            Assert(TrayPresentation.FormatTooltip(Snapshot(ConnectionState.Offline))
                .Contains(Localization.Instance["tray.status.offline"]), "offline status not explicit");
        })),
        ("Tray normal disconnect is announced once after cleanup finishes", () => Check(() =>
        {
            var presenter = Connected();
            Assert(presenter.Update(Snapshot(ConnectionState.Disconnecting)).Notice == null, "disconnect announced before shutdown");
            var notice = presenter.Update(Snapshot(ConnectionState.Offline)).Notice;
            Assert(notice is { Warning: false } && notice.Title == Localization.Instance["tray.notice.disconnectedTitle"],
                "normal disconnection notification missing");
            Assert(presenter.Update(Snapshot(ConnectionState.Offline), trafficSample: true).Notice == null,
                "offline ticks repeated the notification");
        })),
        ("Tray tunnel interruption is announced immediately and reconnect gets fresh speeds", () => Check(() =>
        {
            var presenter = Connected();
            var lost = presenter.Update(Snapshot(ConnectionState.Reconnecting)).Notice;
            Assert(lost is { Warning: true } && lost.Body.Contains(Localization.Instance["tray.notice.lostRetry"]),
                "tunnel loss was not announced");
            Assert(presenter.Update(Snapshot(ConnectionState.Reconnecting), trafficSample: true).Notice == null,
                "retry countdown caused notification spam");
            presenter.Update(Snapshot(ConnectionState.Verifying));
            presenter.Update(Snapshot(ConnectionState.Connected));
            var reconnected = presenter.Update(Snapshot(ConnectionState.Connected) with
                { DownloadRate = "9 MiB/s" }, trafficSample: true).Notice;
            Assert(reconnected is { Warning: false } && reconnected.Body.Contains("9 MiB/s"),
                "reconnection reused an old sample or did not notify");
        })),
        ("Tray health warnings do not masquerade as disconnections or repeated connections", () => Check(() =>
        {
            var presenter = Connected();
            var degraded = Snapshot(ConnectionState.Degraded);
            var warning = presenter.Update(degraded);
            Assert(warning.Notice is { Warning: true } &&
                warning.Notice.Title != Localization.Instance["tray.notice.disconnectedTitle"] &&
                warning.Tooltip.Contains("2.5 MiB/s"), "a health warning falsely claimed tunnel loss");
            presenter.Update(Snapshot(ConnectionState.Connected));
            Assert(presenter.Update(Snapshot(ConnectionState.Connected), trafficSample: true).Notice == null,
                "health recovery repeated the connected notification");
        })),
        ("Tray disconnect before the first speed sample cancels pending connected notification", () => Check(() =>
        {
            var presenter = new TrayPresentation(ConnectionState.Offline);
            presenter.Update(Snapshot(ConnectionState.Connected));
            presenter.Update(Snapshot(ConnectionState.Disconnecting));
            var ended = presenter.Update(Snapshot(ConnectionState.Offline), trafficSample: true);
            Assert(ended.Notice?.Title == Localization.Instance["tray.notice.disconnectedTitle"],
                "a delayed connected message appeared after disconnect");
        })),
        ("Tray failed initial connection reports an error rather than a false disconnection", () => Check(() =>
        {
            var presenter = new TrayPresentation(ConnectionState.Offline);
            presenter.Update(Snapshot(ConnectionState.Connecting));
            var failed = presenter.Update(Snapshot(ConnectionState.Error)).Notice;
            Assert(failed is { Warning: true } && failed.Title != Localization.Instance["tray.notice.disconnectedTitle"],
                "a connection which never succeeded was reported as disconnected");
        })),
        ("Tray proxy and missing-adapter speeds are explicitly unavailable", () => Check(() =>
        {
            foreach (var proxy in new[] { false, true })
            {
                var presenter = new TrayPresentation(ConnectionState.Offline);
                var snapshot = Snapshot(ConnectionState.Connected) with { IsProxy = proxy, RatesAvailable = proxy };
                var tooltip = presenter.Update(snapshot).Tooltip;
                var notice = presenter.Update(snapshot, trafficSample: true).Notice;
                Assert(tooltip.Contains("↓ —") && tooltip.Contains("↑ —") &&
                    notice != null && !notice.Body.Contains("2.5 MiB/s") && !notice.Body.Contains("750 KiB/s"),
                    "unavailable traffic was presented as measured speed");
                Assert(notice!.Body.Contains(Localization.Instance[proxy ? "proxy.traffic" : "tray.notice.ratesPending"]),
                    "missing-data explanation not provided");
            }
        })),
        ("Tray attached to an existing session does not emit a new-connection notification", () => Check(() =>
        {
            var presenter = new TrayPresentation(ConnectionState.Connected);
            Assert(presenter.Update(Snapshot(ConnectionState.Connected), trafficSample: true).Notice == null,
                "attaching a tray was treated as a fresh connection");
        })),
        ("Tray fatal connection loss retains the protection warning", () => Check(() =>
        {
            var presenter = Connected();
            var notice = presenter.Update(Snapshot(ConnectionState.Blocked) with
                { Detail = Localization.Instance["detail.blocked"] }).Notice;
            Assert(notice is { Warning: true } && notice.Title == Localization.Instance["tray.notice.disconnectedTitle"] &&
                notice.Body.Contains(Localization.Instance["detail.blocked"]), "kill-switch context was lost");
        })),
        ("Tray text respects Windows limits without dropping either speed or splitting Unicode", () => Check(() =>
        {
            var presenter = new TrayPresentation(ConnectionState.Offline);
            var snapshot = Snapshot(ConnectionState.Connected) with
            {
                DownloadRate = string.Concat(Enumerable.Repeat("🚀", 30)),
                UploadRate = new string('9', 100) + " TiB/s"
            };
            var tooltip = presenter.Update(snapshot).Tooltip;
            var notice = presenter.Update(snapshot, trafficSample: true).Notice!;
            Assert(tooltip.Length <= 63 && tooltip.Contains('↓') && tooltip.Contains('↑') &&
                notice.Title.Length <= 63 && notice.Body.Length <= 255, "Windows text limits exceeded");
            ValidUnicode(tooltip); ValidUnicode(notice.Body);
            var warning = presenter.Update(Snapshot(ConnectionState.Error) with { Detail = new string('x', 254) + "🚀more" }).Notice!;
            Assert(warning.Body.Length <= 255, "error details exceeded balloon limit");
            ValidUnicode(warning.Body);
        })),
        ("Tray labels and connection notifications follow Turkish localization", () =>
        {
            Localization.Instance.SetLanguage(AppLanguage.Turkish);
            try
            {
                var presenter = new TrayPresentation(ConnectionState.Offline);
                var snapshot = Snapshot(ConnectionState.Connected);
                Assert(presenter.Update(snapshot).Tooltip.Contains("Bağlı"), "Turkish connection state missing");
                var notice = presenter.Update(snapshot, trafficSample: true).Notice!;
                Assert(notice.Title.Contains("Bağlandı") && notice.Body.Contains("İndirme:") && notice.Body.Contains("Yükleme:"),
                    "Turkish notification text missing");
                Assert(TrayPresentation.FormatTooltip(Snapshot(ConnectionState.Offline)).Contains("Bağlı değil"),
                    "Turkish offline hover text missing");
            }
            finally { Localization.Instance.SetLanguage(AppLanguage.English); }
            return Task.CompletedTask;
        }),
        ("UI publishes a single coherent tray sample per statistics tick", () => UiTestHost.RunAsync(async () =>
        {
            using var model = new MainViewModel();
            var tick = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var changes = new List<string?>();
            void Changed(object? sender, PropertyChangedEventArgs e)
            {
                changes.Add(e.PropertyName);
                if (e.PropertyName == nameof(MainViewModel.TrayStatus)) tick.TrySetResult();
            }
            model.PropertyChanged += Changed;
            try
            {
                await tick.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Assert(changes.Count(c => c == nameof(MainViewModel.TrayStatus)) == 1 &&
                    changes.IndexOf(nameof(MainViewModel.DownloadRate)) < changes.IndexOf(nameof(MainViewModel.TrayStatus)) &&
                    changes.IndexOf(nameof(MainViewModel.UploadRate)) < changes.IndexOf(nameof(MainViewModel.TrayStatus)),
                    "tray received partial or duplicate statistics updates");
                Assert(model.TrayStatus.State == ConnectionState.Offline && !model.TrayStatus.RatesAvailable,
                    "offline view-model claimed measured active traffic");
            }
            finally { model.PropertyChanged -= Changed; }
        }))
    ];

    private static TraySnapshot Snapshot(ConnectionState state) => new(state, false, "2.5 MiB/s", "750 KiB/s", true, "Status detail");
    private static TrayPresentation Connected()
    {
        var presenter = new TrayPresentation(ConnectionState.Offline);
        presenter.Update(Snapshot(ConnectionState.Connected));
        presenter.Update(Snapshot(ConnectionState.Connected), trafficSample: true);
        return presenter;
    }
    private static Task Check(Action test) { test(); return Task.CompletedTask; }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void ValidUnicode(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (!char.IsSurrogate(value[i])) continue;
            Assert(char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]),
                "text truncation split a Unicode surrogate pair");
            i++;
        }
    }
}
