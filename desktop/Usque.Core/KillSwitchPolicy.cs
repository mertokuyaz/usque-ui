using System.Net;
using System.Net.Sockets;

namespace Usque.Core;

public sealed record FirewallLayer(Guid Key, bool IPv6, bool ApplicationAware);
public sealed record FirewallRule(string Name, FirewallLayer Layer, bool Permit,
    ulong? TunnelLuid = null, string? Endpoint = null, byte? Protocol = null,
    ushort? LocalPort = null, ushort? RemotePort = null, string? Application = null,
    bool Loopback = false);

public static class KillSwitchPolicy
{
    public static readonly FirewallLayer[] Layers =
    [
        new(new("c38d57d1-05a7-4c33-904f-7fbceee60e82"), false, true),
        new(new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4"), true, true),
        new(new("09e61aea-d214-46e2-9b21-b26b0b2f28c8"), false, false),
        new(new("e1735bde-013f-4655-b351-a49e15762df0"), true, false)
    ];
    public static readonly string[] RuleNames = ["Block", "Loopback", "Tunnel", "Endpoint", "Dhcp", "Neighbor133", "Neighbor135", "Neighbor136"];

    public static IReadOnlyList<FirewallRule> Build(string endpoint, bool http2, string engine, string systemHost)
    {
        if (!IPAddress.TryParse(endpoint, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("Only a literal IPv4 endpoint is supported.", nameof(endpoint));
        var rules = new List<FirewallRule>();
        foreach (var layer in Layers)
        {
            rules.Add(new("Block", layer, false));
            rules.Add(new("Loopback", layer, true, Loopback: true));
            if (!layer.IPv6)
                rules.Add(new("Endpoint", layer, true, Endpoint: endpoint,
                    Protocol: http2 ? (byte)6 : (byte)17, RemotePort: 443,
                    Application: layer.ApplicationAware ? engine : null));
            rules.Add(new("Dhcp", layer, true, Protocol: 17,
                LocalPort: layer.IPv6 ? (ushort)546 : (ushort)68,
                RemotePort: layer.IPv6 ? (ushort)547 : (ushort)67,
                Application: layer.ApplicationAware ? systemHost : null));
            if (layer.IPv6)
                foreach (ushort type in new ushort[] { 133, 135, 136 })
                    rules.Add(new($"Neighbor{type}", layer, true, Protocol: 58, LocalPort: type, RemotePort: 0));
        }
        return rules;
    }
}
