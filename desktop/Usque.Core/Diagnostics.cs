using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;

namespace Usque.Core;

public sealed class DiagnosticRedactor
{
    private readonly HashSet<string> _secrets = new(StringComparer.Ordinal);
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);
    public void AddSecrets(IEnumerable<string> values)
    {
        foreach (var value in values.Where(v => !string.IsNullOrEmpty(v)))
        {
            _secrets.Add(value);
            // Loggers may JSON-escape quotes, backslashes, Unicode and newlines.
            var encoded = JsonSerializer.Serialize(value);
            _secrets.Add(encoded[1..^1]);
        }
    }
    public void RefreshFromProfile(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            if (json.RootElement.ValueKind == JsonValueKind.Object)
                AddSecrets(json.RootElement.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).Select(p => p.Value.GetString()!));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }
    public string Redact(string text)
    {
        try
        {
            text = Regex.Replace(text, @"-----BEGIN (?:\w+ )?PRIVATE KEY-----.*?-----END (?:\w+ )?PRIVATE KEY-----", "[PRIVATE KEY REDACTED]", RegexOptions.Singleline, RegexTimeout);
            text = Regex.Replace(text, @"-----BEGIN (?:\w+ )?PRIVATE KEY-----.*", "[PRIVATE KEY REDACTED]", RegexOptions.Singleline, RegexTimeout);
            text = Regex.Replace(text, @"(?i)\bBearer\s+\S+|\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+", "[TOKEN REDACTED]", RegexOptions.None, RegexTimeout);
            text = Regex.Replace(text, "(?i)([\"']?(?:private_key|access_token|authorization|jwt|token|license|password)[\"']?\\s*[:=]\\s*)(?:\"(?:\\\\.|[^\"\\\\])*\"|'[^']*'|[^\\s,;]+)", "$1\"[REDACTED]\"", RegexOptions.None, RegexTimeout);
            // Preserve full known-value matches before generic IP/path masking
            // can split a sensitive value into fragments.
            foreach (var secret in _secrets.OrderByDescending(s => s.Length)) text = text.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
            text = Regex.Replace(text, @"\b[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}\b", "[ID]", RegexOptions.None, RegexTimeout);
            text = Regex.Replace(text, @"(?i)[A-Z]:\\{1,2}Users\\{1,2}[^\\\r\n]+", @"C:\Users\[USER]", RegexOptions.None, RegexTimeout);
            text = Regex.Replace(text, @"\b(?:\d{1,3}\.){3}\d{1,3}\b", "[IP]", RegexOptions.None, RegexTimeout);
            text = Regex.Replace(text, @"[0-9a-fA-F:]{2,}(?:%\d+)?", match => match.Value.Contains(':') && IPAddress.TryParse(match.Value, out _) ? "[IP]" : match.Value, RegexOptions.None, RegexTimeout);
            return text;
        }
        catch (RegexMatchTimeoutException) { return "[Log entry omitted: redaction limit exceeded]"; }
    }
}

public sealed record DiagnosticReport(int SchemaVersion, DateTimeOffset CreatedUtc, string AppVersion,
    string OperatingSystem, string Architecture, string Runtime, string State, string Transport,
    bool KillSwitchRequested, bool ProtectionMayBeActive, int ProfileCount,
    HealthResult? Health, TrafficSnapshot Traffic, string[] Events)
{
    public ConnectionMode Mode { get; init; }
    public int ReconnectCount { get; init; }
    public ConnectionDiagnosticSnapshot? Checks { get; init; }
}

public static class DiagnosticsWriter
{
    public static DiagnosticReport Create(string version, ConnectionStatus status, bool http2, Preferences preferences,
        bool protection, HealthResult? health, TrafficSnapshot traffic, IEnumerable<string> events, DiagnosticRedactor redactor,
        int reconnectCount = 0, ConnectionDiagnosticSnapshot? checks = null) => new(
            2, DateTimeOffset.UtcNow, version, RuntimeInformation.OSDescription, RuntimeInformation.OSArchitecture.ToString(),
            RuntimeInformation.FrameworkDescription, status.State.ToString(), http2 ? "HTTP/2" : "QUIC",
            preferences.Mode == ConnectionMode.Tunnel && preferences.KillSwitch, protection, preferences.Profiles.Length,
            health == null ? null : health with { Detail = redactor.Redact(health.Detail) }, traffic,
            events.TakeLast(200).Select(redactor.Redact).ToArray())
            {
                Mode = preferences.Mode, ReconnectCount = reconnectCount,
                Checks = checks == null ? null : checks with { Checks = checks.Checks.Select(c => c with
                {
                    NameKey = redactor.Redact(c.NameKey), DetailKey = redactor.Redact(c.DetailKey), AdviceKey = redactor.Redact(c.AdviceKey)
                }).ToArray() }
            };
    public static void Write(string path, DiagnosticReport report) => AtomicJson.Write(path, report);
}
