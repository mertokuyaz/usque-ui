using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Usque.Core;

public sealed class NetworkManager(AppPaths paths, Action<string> log) : INetworkManager
{
    public const string InterfaceName = "UsqueWARP";
    public bool HasRecoveryState => File.Exists(paths.Journal);

    public Task PrepareAsync(string endpoint, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(endpoint, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("A literal IPv4 endpoint is required.", nameof(endpoint));
        return ExecuteAsync("Prepare", cancellationToken, "-Endpoint", endpoint);
    }

    public async Task<ulong> GetTunnelLuidAsync(string name, CancellationToken cancellationToken)
    {
        if (name != InterfaceName) throw new InvalidDataException("Unexpected tunnel adapter name.");
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name == name);
            if (adapter != null)
            {
                var index = adapter.GetIPProperties().GetIPv4Properties().Index;
                var result = ConvertInterfaceIndexToLuid((uint)index, out var luid);
                if (result != 0) throw new System.ComponentModel.Win32Exception((int)result);
                return luid;
            }
            await Task.Delay(250, cancellationToken);
        }
        throw new TimeoutException("The WARP adapter did not appear.");
    }

    public Task ConfigureAsync(string name, CancellationToken cancellationToken)
    {
        if (name != InterfaceName) throw new InvalidDataException("Unexpected tunnel adapter name.");
        return ExecuteAsync("Configure", cancellationToken);
    }
    public Task CleanupAsync(CancellationToken cancellationToken) => ExecuteAsync("Cleanup", cancellationToken);

    private async Task ExecuteAsync(string action, CancellationToken token, params string[] extra)
    {
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var info = ProcessRunner.StartInfo(powershell, "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
            "-File", paths.NetworkScript, "-Action", action, "-StatePath", paths.Journal);
        foreach (var argument in extra) info.ArgumentList.Add(argument);
        var output = await ProcessRunner.RunAsync(info, TimeSpan.FromSeconds(45), token, log);
        if (!string.IsNullOrWhiteSpace(output)) log(output.Trim());
    }

    [DllImport("iphlpapi.dll")] private static extern uint ConvertInterfaceIndexToLuid(uint index, out ulong luid);
}
