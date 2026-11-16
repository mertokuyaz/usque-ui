using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Usque.Core;

// Health traffic is explicitly bound to the tunnel's source address and bypasses
// HTTP proxy settings. It must not turn a direct physical connection into a
// false-positive WARP result when the kill switch is disabled.
public sealed class WarpHealthCheck : IConnectionHealth
{
    public Task<HealthResult> CheckAsync(CancellationToken cancellationToken) => CheckAsync(new ConnectOptions(), cancellationToken);

    public async Task<HealthResult> CheckAsync(ConnectOptions options, CancellationToken cancellationToken)
    {
        var ipv4 = ProbeAsync(new("https://1.1.1.1/cdn-cgi/trace"), AddressFamily.InterNetwork, options, cancellationToken);
        var dns = ProbeAsync(new("https://www.cloudflare.com/cdn-cgi/trace"), AddressFamily.InterNetwork, options, cancellationToken);
        var ipv6 = ProbeAsync(new("https://[2606:4700:4700::1111]/cdn-cgi/trace"), AddressFamily.InterNetworkV6, options, cancellationToken);
        await Task.WhenAll(ipv4, dns, ipv6);
        var v4 = await ipv4;
        var dnsResult = await dns;
        var v6 = await ipv6;
        var warp = v4.Warp;
        var dnsOk = dnsResult.Warp;
        var ipv6Ok = v6.Warp;
        cancellationToken.ThrowIfCancellationRequested();
        return new(DateTimeOffset.UtcNow, warp, dnsOk, ipv6Ok, v4.Latency,
            warp && dnsOk ? (ipv6Ok ? Localization.Instance["health.verified.dual"] : Localization.Instance["health.verified.dns"])
                : Localization.Instance[!v4.Reachable ? "health.failed.internet" : !warp ? "health.failed.warp" : "health.failed.dns"])
            { InternetReachable = v4.Reachable };
    }

    private sealed record ProbeResult(bool Reachable = false, bool Warp = false, double? Latency = null);

    private static async Task<ProbeResult> ProbeAsync(Uri uri, AddressFamily family, ConnectOptions options, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            using var handler = CreateHandler(options);
            if (!options.IsProxy)
            {
                var adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(a => a.Name == NetworkManager.InterfaceName);
                var local = adapter?.GetIPProperties().UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == family && !a.Address.IsIPv6LinkLocal)?.Address;
                if (local == null) return new();
                handler.ConnectCallback = async (context, cancellation) =>
                {
                    var addresses = IPAddress.TryParse(context.DnsEndPoint.Host, out var literal)
                        ? new[] { literal } : await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellation);
                    foreach (var address in addresses.Where(a => a.AddressFamily == family))
                    {
                        var socket = new Socket(family, SocketType.Stream, ProtocolType.Tcp);
                        try
                        {
                            socket.Bind(new IPEndPoint(local, 0));
                            await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellation);
                            return new NetworkStream(socket, ownsSocket: true);
                        }
                        catch { socket.Dispose(); if (cancellation.IsCancellationRequested) throw; }
                    }
                    throw new IOException("No reachable tunnel destination.");
                };
            }
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 16 * 1024 };
            var trace = await client.GetStringAsync(uri, token);
            return new(true, IsWarpTrace(trace), watch.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException or NetworkInformationException or OperationCanceledException)
        {
            token.ThrowIfCancellationRequested();
            return new();
        }
    }

    internal static SocketsHttpHandler CreateHandler(ConnectOptions options) => new()
    {
        UseProxy = options.IsProxy,
        Proxy = options.IsProxy ? new WebProxy(options.ProxyUri) { BypassProxyOnLocal = false } : null,
        AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(6)
    };

    public static bool IsWarpTrace(string trace)
    {
        string? flag = null;
        foreach (var line in trace.Split('\n'))
        {
            var value = line.Trim();
            if (!value.StartsWith("warp=", StringComparison.Ordinal)) continue;
            if (flag != null) return false; // Contradictory/duplicate flags are not verification.
            flag = value[5..];
        }
        return flag is "on" or "plus";
    }
}
