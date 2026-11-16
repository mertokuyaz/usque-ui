using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Security.AccessControl;
using System.Text;

namespace Usque.Core;

public interface IStartupRegistration
{
    bool IsEnabled { get; }
    void SetEnabled(bool enabled);
}

public static class StartupPreferences
{
    public static void SetEnabled(PreferencesStore preferences, IStartupRegistration registration, bool enabled)
    {
        var previous = preferences.Current;
        var previouslyEnabled = registration.IsEnabled;
        registration.SetEnabled(enabled);
        try { preferences.Save(previous with { StartWithWindows = enabled }); }
        catch (Exception saveError)
        {
            try { registration.SetEnabled(previouslyEnabled); }
            catch (Exception rollbackError) { throw new AggregateException("Settings could not be saved and the startup task could not be rolled back.", saveError, rollbackError); }
            throw;
        }
    }
}

// Task Scheduler's interactive-token / highest-privilege task avoids storing a
// password or displaying a UAC prompt at every sign-in. Never called on load:
// creation/removal happens only after the user explicitly changes the option.
public sealed class StartupRegistration(string executable) : IStartupRegistration
{
    private static string CurrentSid
    {
        get { using var identity = WindowsIdentity.GetCurrent(); return identity.User?.Value ?? throw new InvalidOperationException("Current user SID unavailable."); }
    }
    private static string TaskName => "UsqueDesktop-57d78a76-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CurrentSid)))[..12];

    public bool IsEnabled
    {
        get
        {
            using var scope = new ComScope();
            dynamic service = scope.CreateService();
            service.Connect();
            dynamic folder = scope.Track(service.GetFolder("\\"));
            try
            {
                dynamic task = scope.Track(folder.GetTask(TaskName));
                dynamic definition = scope.Track(task.Definition);
                dynamic actions = scope.Track(definition.Actions);
                if ((int)actions.Count != 1) return false;
                dynamic action = scope.Track(actions.Item(1));
                var path = Path.GetFullPath((string)action.Path);
                return (bool)task.Enabled && path.StartsWith(InstallRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(path).Equals("Usque.Desktop.exe", StringComparison.OrdinalIgnoreCase)
                    && (string)action.Arguments == "--startup";
            }
            catch (COMException ex) when ((uint)ex.HResult == 0x80070002) { return false; }
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled && (!Path.IsPathFullyQualified(executable) || !File.Exists(executable) ||
            !Path.GetFileName(executable).Equals("Usque.Desktop.exe", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Configure Windows startup from the published Usque.Desktop.exe, not the development dotnet host.");
        // Never schedule an elevated executable from a user-writable Desktop or
        // Downloads folder. Opt-in installs a credential-free, protected copy.
        var installed = enabled ? InstallProtectedCopy() : null;
        using var scope = new ComScope();
        dynamic service = scope.CreateService();
        service.Connect();
        dynamic folder = scope.Track(service.GetFolder("\\"));
        if (!enabled)
        {
            try { folder.DeleteTask(TaskName, 0); }
            catch (COMException ex) when ((uint)ex.HResult == 0x80070002) { }
            return;
        }
        var sid = CurrentSid;
        dynamic definition = scope.Track(service.NewTask(0));
        dynamic info = scope.Track(definition.RegistrationInfo);
        info.Description = "Launch Usque Desktop for this user at sign-in. No password is stored.";
        dynamic principal = scope.Track(definition.Principal);
        principal.UserId = sid;
        principal.LogonType = 3; // TASK_LOGON_INTERACTIVE_TOKEN
        principal.RunLevel = 1; // TASK_RUNLEVEL_HIGHEST
        dynamic settings = scope.Track(definition.Settings);
        settings.Enabled = true;
        settings.StartWhenAvailable = true;
        settings.DisallowStartIfOnBatteries = false;
        settings.StopIfGoingOnBatteries = false;
        settings.ExecutionTimeLimit = "PT0S";
        settings.MultipleInstances = 2; // TASK_INSTANCES_IGNORE_NEW
        dynamic triggers = scope.Track(definition.Triggers);
        dynamic trigger = scope.Track(triggers.Create(9)); // TASK_TRIGGER_LOGON
        trigger.UserId = sid;
        dynamic actions = scope.Track(definition.Actions);
        dynamic action = scope.Track(actions.Create(0));
        action.Path = installed!;
        action.Arguments = "--startup";
        action.WorkingDirectory = Path.GetDirectoryName(installed)!;
        scope.Track(folder.RegisterTaskDefinition(TaskName, definition, 6, sid, null, 3, null)); // CREATE_OR_UPDATE
    }

    private static string InstallRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "UsqueDesktop-57d78a76");
    private string InstallProtectedCopy()
    {
        var source = Path.GetDirectoryName(executable)!;
        const string markerName = ".usque-startup-install";
        const string markerValue = "Usque Desktop 57d78a76 protected startup distribution";
        var root = InstallRoot;
        var marker = Path.Combine(root, markerName);
        if (Directory.Exists(root) && (!File.Exists(marker) || File.ReadAllText(marker) != markerValue))
            throw new IOException("The startup installation directory contains unfamiliar files. It was not overwritten.");
        if (Directory.Exists(root) && (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The startup installation directory must not be a junction or symbolic link.");
        var alreadyInstalled = source.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        var package = alreadyInstalled ? StartupPackage.ReadManifest(source) : StartupPackage.CreateManifest(source, root);
        if (alreadyInstalled && !StartupPackage.IsValid(package, source))
            throw new IOException("The installed startup package is damaged. Enable startup from a fresh published distribution.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        if (!Directory.Exists(root)) new DirectoryInfo(root).Create(security);
        else new DirectoryInfo(root).SetAccessControl(security);
        File.WriteAllText(marker, markerValue);
        if (alreadyInstalled) return executable;
        var target = Path.Combine(root, "v2-" + package.Id);
        var destination = Path.Combine(target, "Usque.Desktop.exe");
        if (Directory.Exists(target))
        {
            if (StartupPackage.IsValid(package, target)) return destination;
            throw new IOException("A damaged startup cache was found. It was not launched or overwritten; repair the protected installation before retrying.");
        }
        var staging = Path.Combine(root, ".staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            StartupPackage.CopyVerified(source, staging, package);
            Directory.Move(staging, target);
            return destination;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }
    internal static bool IsDistributionFile(string relative)
    {
        var normalized = relative.Replace('\\', '/');
        if (Path.IsPathRooted(relative) || normalized.Contains(':') || normalized.StartsWith('/') || normalized.Split('/').Any(p => p is ".." or ".")) return false;
        if (Path.GetExtension(normalized).Equals(".dll", StringComparison.OrdinalIgnoreCase)) return true;
        return normalized is "Usque.Desktop.exe" or "Usque.Desktop.deps.json" or "Usque.Desktop.runtimeconfig.json"
            or "engine/usque.exe" or "Scripts/Network.ps1" or "USQUE-LICENSE.md" or "WINTUN-LICENSE.txt";
    }

    private sealed class ComScope : IDisposable
    {
        private readonly List<object> _objects = [];
        public dynamic Track(object value) { _objects.Add(value); return value; }
        public dynamic CreateService() => Track(Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")
            ?? throw new PlatformNotSupportedException("Windows Task Scheduler is unavailable."))!);
        public void Dispose()
        {
            for (var i = _objects.Count - 1; i >= 0; i--)
                if (Marshal.IsComObject(_objects[i])) Marshal.ReleaseComObject(_objects[i]);
        }
    }
}
