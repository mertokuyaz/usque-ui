using System.Security.AccessControl;
using System.Security.Principal;

namespace Usque.Core;

public sealed class AppPaths
{
    public AppPaths() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "UsqueDesktop")) { }
    internal AppPaths(string dataDirectory) => DataDirectory = Path.GetFullPath(dataDirectory);
    public string DataDirectory { get; }
    public string ActiveProfileId { get; private set; } = "default";
    public string ProfilesDirectory => Path.Combine(DataDirectory, "profiles");
    public string Settings => Path.Combine(DataDirectory, "settings.json");
    public string Profile => ProfilePath(ActiveProfileId);
    public string ProfilePath(string id)
    {
        if (id == "default") return Path.Combine(DataDirectory, "config.json");
        if (!IsValidProfileId(id)) throw new ArgumentException("Invalid managed profile identifier.", nameof(id));
        return Path.Combine(ProfilesDirectory, id + ".json");
    }
    public void SelectProfile(string id) { _ = ProfilePath(id); ActiveProfileId = id; }
    internal static bool IsValidProfileId(string? id) => id == "default" ||
        Guid.TryParseExact(id, "N", out var guid) && guid.ToString("N") == id;
    public string Journal => Path.Combine(DataDirectory, "network-session.json");
    public string Engine => Path.Combine(AppContext.BaseDirectory, "engine", "usque.exe");
    public string NetworkScript => Path.Combine(AppContext.BaseDirectory, "Scripts", "Network.ps1");

    public void EnsureSecureDirectory()
    {
        // The profile contains private keys. Do not inherit ProgramData's broad
        // read permissions. Also protects recovery instructions from tampering.
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null),
                FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        if (!Directory.Exists(DataDirectory)) new DirectoryInfo(DataDirectory).Create(security);
        else new DirectoryInfo(DataDirectory).SetAccessControl(security);
    }
}
