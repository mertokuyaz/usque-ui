using System.Net.NetworkInformation;
using System.Windows.Threading;
using Microsoft.Win32;
using Usque.Core;

namespace Usque.Desktop;

// Observe physical addressing, not our own TUN/route changes. Debounce noisy
// Windows notifications and unsubscribe everything when the window exits.
internal sealed class NetworkEnvironmentMonitor : IDisposable
{
    private readonly ConnectionController _controller;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _debounce;
    private string _fingerprint;
    private bool _disposed;

    public NetworkEnvironmentMonitor(ConnectionController controller)
    {
        _controller = controller;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _fingerprint = Fingerprint();
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _debounce.Tick += Check;
        NetworkChange.NetworkAddressChanged += AddressChanged;
        NetworkChange.NetworkAvailabilityChanged += AvailabilityChanged;
        SystemEvents.PowerModeChanged += PowerChanged;
    }

    private void AddressChanged(object? sender, EventArgs e) => QueueCheck();
    private void AvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => QueueCheck();
    private void QueueCheck() => Post(() => { _debounce.Stop(); _debounce.Start(); });
    private void Post(Action action)
    {
        if (_dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(() => { if (!_disposed) action(); });
    }
    private void Check(object? sender, EventArgs e)
    {
        _debounce.Stop();
        var next = Fingerprint();
        if (next == _fingerprint) return;
        _fingerprint = next;
        _controller.NotifyEnvironmentChanged(EnvironmentChange.Network);
    }
    private void PowerChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) Post(() => _controller.NotifyEnvironmentChanged(EnvironmentChange.Suspend));
        else if (e.Mode == PowerModes.Resume) Post(() =>
        {
            _fingerprint = Fingerprint();
            _controller.NotifyEnvironmentChanged(EnvironmentChange.Resume);
            QueueCheck();
        });
    }

    internal static string Fingerprint()
    {
        try
        {
            return string.Join("|", NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.Name != NetworkManager.InterfaceName &&
                    n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .OrderBy(n => n.Id, StringComparer.Ordinal).Select(n =>
                {
                    var properties = n.GetIPProperties();
                    return $"{n.Id}:{n.OperationalStatus}:" +
                        string.Join(",", properties.UnicastAddresses.Select(a => a.Address.ToString()).Order(StringComparer.Ordinal)) + ":" +
                        string.Join(",", properties.GatewayAddresses.Select(a => a.Address.ToString()).Order(StringComparer.Ordinal));
                }));
        }
        catch (NetworkInformationException) { return "unavailable"; }
    }

    public void Dispose()
    {
        _disposed = true;
        _debounce.Stop();
        NetworkChange.NetworkAddressChanged -= AddressChanged;
        NetworkChange.NetworkAvailabilityChanged -= AvailabilityChanged;
        SystemEvents.PowerModeChanged -= PowerChanged;
    }
}
