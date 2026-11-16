using System.Text.Json;
using System.Text.Json.Serialization;

namespace Usque.Core;

public enum TransportMode { Automatic, Quic, Http2 }
public sealed record ProfileEntry(string Id, string Name);
public sealed record Preferences
{
    public int Version { get; init; } = 1;
    public bool KillSwitch { get; init; } = true;
    public TransportMode Transport { get; init; } = TransportMode.Automatic;
    public ConnectionMode Mode { get; init; } = ConnectionMode.Tunnel;
    public int ProxyPort { get; init; } = 1080;
    public bool AutoReconnect { get; init; } = true;
    public AppLanguage Language { get; init; } = AppLanguage.English;
    public bool MinimizeToTray { get; init; }
    public bool AutoConnect { get; init; }
    public bool StartWithWindows { get; init; }
    public bool TermsAccepted { get; init; }
    public string SelectedProfileId { get; init; } = "default";
    public ProfileEntry[] Profiles { get; init; } = [new("default", "Default")];
}

internal static class AtomicJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true, Converters = { new JsonStringEnumConverter() }
    };
    internal static void Write<T>(string path, T value)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, value, Options);
                file.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public sealed class PreferencesStore
{
    private readonly AppPaths _paths;
    private readonly object _sync = new();
    private Preferences _current;
    public Preferences Current { get { lock (_sync) return Snapshot(_current); } }
    public PreferencesStore(AppPaths paths)
    {
        _paths = paths;
        var loaded = File.Exists(paths.Settings)
            ? JsonSerializer.Deserialize<Preferences>(File.ReadAllText(paths.Settings), AtomicJson.Options)
                ?? throw new InvalidDataException("Settings cannot be null.")
            : new();
        Validate(loaded);
        _current = Snapshot(loaded);
        paths.SelectProfile(_current.SelectedProfileId);
    }
    public void Save(Preferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        lock (_sync)
        {
            var snapshot = Snapshot(preferences);
            Validate(snapshot);
            AtomicJson.Write(_paths.Settings, snapshot);
            _paths.SelectProfile(snapshot.SelectedProfileId);
            _current = snapshot;
        }
    }
    private static Preferences Snapshot(Preferences preferences) => preferences with
    { Profiles = preferences.Profiles == null ? null! : [.. preferences.Profiles] };
    private static void Validate(Preferences preferences)
    {
        if (preferences.Version != 1 || !Enum.IsDefined(preferences.Transport) || !Enum.IsDefined(preferences.Language))
            throw new InvalidDataException("Unsupported settings version, transport mode, or language. Settings were not overwritten.");
        if (!Enum.IsDefined(preferences.Mode) || preferences.ProxyPort is < 1 or > 65535)
            throw new InvalidDataException("Invalid connection mode or proxy port (1–65535). Settings were not overwritten.");
        if (preferences.Profiles is not { Length: > 0 and <= 32 } profiles ||
            profiles.Any(p => p == null || !AppPaths.IsValidProfileId(p.Id) || string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 40 || p.Name != p.Name.Trim() || p.Name.Any(char.IsControl)) ||
            profiles.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != profiles.Length ||
            profiles.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != profiles.Length ||
            !profiles.Any(p => p.Id == preferences.SelectedProfileId))
            throw new InvalidDataException("Invalid profile catalogue in settings. Settings were not overwritten.");
        if (preferences.AutoConnect && !preferences.TermsAccepted)
            throw new InvalidDataException("Automatic connection requires explicit Cloudflare terms consent.");
    }
}

public sealed class ProfileCatalog(AppPaths paths, PreferencesStore preferences, Func<bool>? canChange = null)
{
    internal Action<string> DeleteBackup { get; init; } = File.Delete;
    public IReadOnlyList<ProfileEntry> Entries => preferences.Current.Profiles;
    public void Select(string id)
    {
        Guard();
        if (!Entries.Any(p => p.Id == id)) throw new ArgumentException("Unknown profile.", nameof(id));
        preferences.Save(preferences.Current with { SelectedProfileId = id });
    }
    public ProfileEntry Create(string name)
    {
        Guard();
        var entry = new ProfileEntry(Guid.NewGuid().ToString("N"), name.Trim());
        Directory.CreateDirectory(paths.ProfilesDirectory);
        preferences.Save(preferences.Current with { Profiles = [.. Entries, entry], SelectedProfileId = entry.Id });
        return entry;
    }
    public void Rename(string id, string name)
    {
        Guard();
        if (!Entries.Any(p => p.Id == id)) throw new ArgumentException("Unknown profile.", nameof(id));
        preferences.Save(preferences.Current with { Profiles = Entries.Select(p => p.Id == id ? p with { Name = name.Trim() } : p).ToArray() });
    }
    public void Delete(string id)
    {
        Guard();
        if (Entries.Count <= 1) throw new InvalidOperationException("Keep at least one profile.");
        if (!Entries.Any(p => p.Id == id)) throw new ArgumentException("Unknown profile.", nameof(id));
        var path = paths.ProfilePath(id);
        var backup = path + ".delete-" + Guid.NewGuid().ToString("N");
        var moved = false;
        var previous = preferences.Current;
        var committed = false;
        try
        {
            if (File.Exists(path)) { File.Move(path, backup); moved = true; }
            var remaining = Entries.Where(p => p.Id != id).ToArray();
            preferences.Save(preferences.Current with
            {
                Profiles = remaining,
                SelectedProfileId = preferences.Current.SelectedProfileId == id ? remaining[0].Id : preferences.Current.SelectedProfileId
            });
            committed = true;
            if (moved) DeleteBackup(backup);
        }
        catch (Exception error)
        {
            var errors = new List<Exception> { error };
            if (moved && File.Exists(backup))
            {
                try { File.Move(backup, path); }
                catch (Exception restoreError) { errors.Add(restoreError); }
            }
            if (committed)
            {
                try { preferences.Save(previous); }
                catch (Exception restoreError) { errors.Add(restoreError); }
            }
            if (errors.Count > 1) throw new AggregateException("Profile deletion failed and recovery was incomplete. Saved credentials were not intentionally discarded.", errors);
            throw;
        }
    }
    private void Guard()
    {
        if (canChange?.Invoke() == false) throw new InvalidOperationException("Disconnect and restore networking before changing profiles.");
    }
}
