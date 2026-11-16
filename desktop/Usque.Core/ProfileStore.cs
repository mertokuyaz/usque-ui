using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace Usque.Core;

public sealed class ProfileStore(AppPaths paths) : IProfileStore
{
    public bool Exists => File.Exists(paths.Profile);

    public async Task<WarpProfile> LoadAsync(bool http2, CancellationToken cancellationToken)
    {
        using var stream = File.OpenRead(paths.Profile);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return Validate(json.RootElement, http2);
    }

    public static WarpProfile Validate(JsonElement root, bool http2)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The WARP profile must contain a JSON object.");
        string Required(string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()!
            : throw new InvalidDataException($"The saved WARP profile is missing {key}. Import a valid profile; it will not be overwritten.");
        using var privateKey = ECDsa.Create();
        var encodedKey = Convert.FromBase64String(Required("private_key"));
        privateKey.ImportECPrivateKey(encodedKey, out var bytesRead);
        using var publicKey = ECDsa.Create();
        publicKey.ImportFromPem(Required("endpoint_pub_key"));
        if (bytesRead != encodedKey.Length || privateKey.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7"
            || publicKey.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
            throw new InvalidDataException("The saved profile must contain P-256 MASQUE keys.");
        var endpoint = http2 ? "162.159.198.2" : Required("endpoint_v4");
        if (http2 && root.TryGetProperty("endpoint_h2_v4", out var h2) && h2.ValueKind != JsonValueKind.Null)
        {
            if (h2.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("The saved profile has an invalid endpoint_h2_v4 field.");
            if (!string.IsNullOrWhiteSpace(h2.GetString())) endpoint = h2.GetString()!;
        }
        // IPAddress.TryParse also accepts shorthand and hexadecimal forms that
        // the Go engine does not treat as IP literals. Reject them before WFP is armed.
        if (!IPAddress.TryParse(endpoint, out var address) || address.AddressFamily != AddressFamily.InterNetwork
            || address.ToString() != endpoint)
            throw new InvalidDataException("This desktop client requires a dotted-decimal IPv4 WARP endpoint.");
        var ipv4 = Required("ipv4");
        var ipv6 = Required("ipv6");
        // .NET also accepts shorthand IPv4, bracketed IPv6 and scope IDs;
        // these are not tunnel IP literals understood by the Go engine.
        if (!IPAddress.TryParse(ipv4, out var v4) || v4.AddressFamily != AddressFamily.InterNetwork || v4.ToString() != ipv4 ||
            !IPAddress.TryParse(ipv6, out var v6) || v6.AddressFamily != AddressFamily.InterNetworkV6 || ipv6.Any(c => c is '%' or '[' or ']'))
            throw new InvalidDataException("The saved profile has invalid tunnel addresses.");
        return new(endpoint, ipv4, ipv6, Required("id"));
    }

    public async Task ImportAsync(string source)
    {
        // ReadAllText recognizes Windows PowerShell's UTF-8/UTF-16 BOMs. Always
        // write BOM-free UTF-8, which is required by the Go JSON decoder.
        var text = await File.ReadAllTextAsync(source);
        using var json = JsonDocument.Parse(text);
        _ = Validate(json.RootElement, false);
        var temporary = paths.Profile + ".import-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(json.RootElement));
            File.Move(temporary, paths.Profile, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
