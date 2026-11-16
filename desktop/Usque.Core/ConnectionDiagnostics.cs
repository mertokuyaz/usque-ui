using System.Net.NetworkInformation;

namespace Usque.Core;

public enum DiagnosticOutcome { Passed, Warning, Failed, Skipped }
public sealed record DiagnosticCheck(string NameKey, DiagnosticOutcome Outcome, string DetailKey, string AdviceKey);
public sealed record ConnectionDiagnosticSnapshot(DateTimeOffset CheckedUtc, ConnectionMode Mode, DiagnosticCheck[] Checks);

// Read-only: no firewall changes, driver installation, registration, or direct
// internet probes which could bypass protection. Online probes reuse the exact
// tunnel/proxy health path. Offline checks explain what could not be tested.
public sealed class ConnectionDiagnostics(ConnectionController controller, Action<ConnectionMode> validateInstallation,
    Func<bool>? networkAvailable = null)
{
    public async Task<ConnectionDiagnosticSnapshot> RunAsync(ConnectionMode selectedMode, CancellationToken token)
    {
        var active = controller.Status.State is ConnectionState.Connected or ConnectionState.Degraded;
        var mode = active ? controller.ActiveOptions.Mode : selectedMode;
        var checks = new List<DiagnosticCheck>();
        token.ThrowIfCancellationRequested();
        try
        {
            validateInstallation(mode);
            checks.Add(new("diag.installation", DiagnosticOutcome.Passed,
                mode == ConnectionMode.Tunnel ? "diag.driver.ok" : "diag.driver.proxy", ""));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidDataException)
        {
            checks.Add(new("diag.installation", DiagnosticOutcome.Failed, "diag.driver.failed", "diag.fix.driver"));
        }
        bool available;
        try { available = (networkAvailable ?? HasPhysicalNetwork)(); }
        catch (NetworkInformationException) { available = false; }
        checks.Add(new("diag.network", available ? DiagnosticOutcome.Passed : DiagnosticOutcome.Failed,
            available ? "diag.network.ok" : "diag.network.failed", available ? "" : "diag.fix.network"));

        if (active)
        {
            try { await controller.CheckHealthNowAsync(); }
            catch (OperationCanceledException) { token.ThrowIfCancellationRequested(); }
            catch (InvalidOperationException) { /* Session ended while starting the check. */ }
        }
        token.ThrowIfCancellationRequested();
        var health = controller.LastHealth;
        checks.Add(new("diag.endpoint", controller.TransportEstablished ? DiagnosticOutcome.Passed : DiagnosticOutcome.Skipped,
            controller.TransportEstablished ? "diag.endpoint.ok" : "diag.offline",
            controller.TransportEstablished ? "" : "diag.fix.endpoint"));
        AddHealth("diag.internet", health?.InternetReachable ?? health?.WarpVerified, "diag.fix.internet");
        AddHealth("diag.dns", health?.DnsReachable, "diag.fix.dns");
        AddHealth("diag.warp", health?.WarpVerified, "diag.fix.warp");
        AddHealth("diag.ipv6", health?.IPv6Reachable, "diag.fix.ipv6", optional: true);
        return new(DateTimeOffset.UtcNow, mode, checks.ToArray());

        void AddHealth(string key, bool? success, string advice, bool optional = false)
        {
            checks.Add(new(key, success == null ? DiagnosticOutcome.Skipped : success.Value ? DiagnosticOutcome.Passed :
                optional ? DiagnosticOutcome.Warning : DiagnosticOutcome.Failed,
                success == null ? "diag.offline" : success.Value ? "diag.probe.ok" : "diag.probe.failed",
                success == true ? "" : success == null ? "diag.fix.connect" : advice));
        }
    }

    private static bool HasPhysicalNetwork() => NetworkInterface.GetAllNetworkInterfaces().Any(n =>
        n.Name != NetworkManager.InterfaceName && n.OperationalStatus == OperationalStatus.Up &&
        n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) &&
        n.GetIPProperties().GatewayAddresses.Count > 0);
}
