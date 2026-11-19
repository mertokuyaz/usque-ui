namespace Usque.Core;

public enum ConnectionState { Offline, Registering, Connecting, Verifying, Connected, Degraded, Reconnecting, Blocked, Error, Disconnecting }
public sealed record ConnectionStatus(ConnectionState State, string Detail)
{
    // Keep the original text for logs, and resolve built-in messages at display
    // time so switching languages does not require restarting the connection.
    public string? DetailKey { get; init; }
    public object[] DetailArguments { get; init; } = [];
    public string DetailSuffix { get; init; } = "";
    public string? DetailSuffixKey { get; init; }
    public string LocalizedDetail => Localize(static text => text);
    // Built-in templates are product text. Only backend-supplied fragments (raw
    // messages, string arguments, suffixes) go through `external`, so a redactor
    // cannot mangle wording that happens to match a profile name or secret.
    public string Localize(Func<string, string> external) => DetailKey == null ? external(Detail)
        : Localization.Instance.Format(DetailKey, DetailArguments.Select(a => a is string text ? external(text) : a).ToArray()) +
            (DetailSuffixKey == null ? external(DetailSuffix) : " " + Localization.Instance[DetailSuffixKey]);
}
public sealed record TunnelEvent(string State, string? Interface = null);
public sealed record WarpProfile(string Endpoint, string IPv4, string IPv6, string DeviceId);
public enum ConnectionMode { Tunnel, Socks5, HttpProxy }
public enum EnvironmentChange { Network, Suspend, Resume }
public sealed record ConnectOptions(bool KillSwitch = true, bool Http2 = false, bool AutomaticFallback = false,
    ConnectionMode Mode = ConnectionMode.Tunnel, int ProxyPort = 1080, bool AutoReconnect = false)
{
    public bool IsProxy => Mode != ConnectionMode.Tunnel;
    public Uri ProxyUri => new($"{(Mode == ConnectionMode.Socks5 ? "socks5" : "http")}://127.0.0.1:{ProxyPort}");
    public void Validate()
    {
        if (!Enum.IsDefined(Mode) || ProxyPort is < 1 or > 65535)
            throw new ArgumentException("Invalid connection mode or proxy port (1–65535).");
    }
}
public sealed record ConnectionTimings(TimeSpan TransportTimeout, TimeSpan HealthInterval)
{
    public static ConnectionTimings Default { get; } = new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan MaximumRetryDelay { get; init; } = TimeSpan.FromSeconds(30);
}
public sealed record HealthResult(DateTimeOffset CheckedUtc, bool WarpVerified, bool DnsReachable, bool IPv6Reachable,
    double? LatencyMilliseconds, string Detail)
{
    public bool Healthy => WarpVerified && DnsReachable;
    public bool? InternetReachable { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? DetailKey { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string LocalizedDetail => Localize(static text => text);
    public string Localize(Func<string, string> external) => DetailKey == null ? external(Detail) : Localization.Instance[DetailKey];
}
public interface IConnectionHealth
{
    Task<HealthResult> CheckAsync(CancellationToken cancellationToken);
    Task<HealthResult> CheckAsync(ConnectOptions options, CancellationToken cancellationToken) => CheckAsync(cancellationToken);
}

public interface IProfileStore
{
    bool Exists { get; }
    Task<WarpProfile> LoadAsync(bool http2, CancellationToken cancellationToken);
}

public interface IVpnEngine
{
    void ValidateInstallation();
    void ValidateInstallation(ConnectionMode mode) => ValidateInstallation();
    Task RegisterAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<TunnelEvent> RunAsync(ConnectOptions options, CancellationToken cancellationToken);
}

public interface INetworkManager
{
    bool HasRecoveryState { get; }
    Task PrepareAsync(string endpoint, CancellationToken cancellationToken);
    Task<ulong> GetTunnelLuidAsync(string name, CancellationToken cancellationToken);
    Task ConfigureAsync(string name, CancellationToken cancellationToken);
    Task CleanupAsync(CancellationToken cancellationToken);
}

public interface IKillSwitch
{
    bool IsEnabled { get; }
    void Arm(string endpoint, bool http2);
    void AllowTunnel(ulong luid);
    void Disarm();
}
