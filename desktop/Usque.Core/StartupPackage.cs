using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Usque.Core;

internal sealed record StartupPackageFile(string Path, long Length, string Sha256);
internal sealed record StartupPackageManifest(string Id, StartupPackageFile[] Files);

// Pure filesystem operations, separate from privileged ACL and Task Scheduler
// changes so package identity, integrity and completeness can be regression-tested.
internal static class StartupPackage
{
    internal const string ManifestName = ".usque-package.json";
    private static readonly string[] Required = ["Usque.Desktop.exe", "Usque.Desktop.dll", "Usque.Core.dll",
        "Usque.Desktop.deps.json", "Usque.Desktop.runtimeconfig.json", "engine/usque.exe", "engine/wintun.dll", "Scripts/Network.ps1"];

    internal static StartupPackageManifest CreateManifest(string source, string? excludedDirectory = null)
    {
        source = Path.GetFullPath(source);
        var exclude = excludedDirectory == null ? null : Path.GetFullPath(excludedDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (exclude != null && (source + Path.DirectorySeparatorChar).StartsWith(exclude, StringComparison.OrdinalIgnoreCase)) exclude = null;
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The startup package must not be a junction or symbolic link.");
        var files = new List<StartupPackageFile>();
        foreach (var path in Directory.EnumerateFiles(source, "*", new EnumerationOptions
            { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            if (exclude != null && path.StartsWith(exclude, StringComparison.OrdinalIgnoreCase)) continue;
            var relative = Path.GetRelativePath(source, path).Replace('\\', '/');
            if (!StartupRegistration.IsDistributionFile(relative)) continue;
            using var stream = File.OpenRead(path);
            files.Add(new(relative, stream.Length, Convert.ToHexString(SHA256.HashData(stream))));
        }
        var ordered = files.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
        if (ordered.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != ordered.Length ||
            Required.Any(required => !ordered.Any(f => f.Path == required)))
            throw new InvalidDataException("The desktop distribution is incomplete or contains ambiguous file names. Rebuild it before enabling startup.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in ordered)
            hash.AppendData(Encoding.UTF8.GetBytes($"{file.Path}\0{file.Length}\0{file.Sha256}\0"));
        return new(Convert.ToHexString(hash.GetHashAndReset()), ordered);
    }

    internal static StartupPackageManifest ReadManifest(string directory)
    {
        var path = Path.Combine(directory, ManifestName);
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidDataException("Startup package manifest is too large.");
        return JsonSerializer.Deserialize<StartupPackageManifest>(File.ReadAllText(path), AtomicJson.Options)
            ?? throw new InvalidDataException("Startup package manifest is empty.");
    }

    internal static bool IsValid(StartupPackageManifest expected, string directory)
    {
        try
        {
            var recorded = ReadManifest(directory);
            var actual = CreateManifest(directory);
            return recorded.Id == expected.Id && actual.Id == expected.Id &&
                recorded.Files != null && recorded.Files.SequenceEqual(expected.Files) && actual.Files.SequenceEqual(expected.Files);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException) { return false; }
    }

    internal static void CopyVerified(string source, string destination, StartupPackageManifest manifest)
    {
        foreach (var file in manifest.Files)
        {
            if (!StartupRegistration.IsDistributionFile(file.Path)) throw new InvalidDataException("Invalid startup package path.");
            var relative = file.Path.Replace('/', Path.DirectorySeparatorChar);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(source, relative), target);
        }
        AtomicJson.Write(Path.Combine(destination, ManifestName), manifest);
        if (!IsValid(manifest, destination)) throw new IOException("The startup package changed while being copied. No task was registered.");
    }
}
