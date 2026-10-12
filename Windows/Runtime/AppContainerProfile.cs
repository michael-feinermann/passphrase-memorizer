using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using PassphraseMemorizer.Crypto;

namespace PassphraseMemorizer.Runtime;

// Windows needs a registered profile to launch a lowbox. It contains only fresh
// public metadata, is made read-only before launch, and is deleted after exit.
internal sealed class AppContainerProfile : IDisposable
{
    internal const string RegistryReadCapability = "S-1-15-3-1024-1065365936-1281604716-3511738428-1654721687-432734479-3232135806-4053264122-3456934681";
    private const string RegistryBase = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppContainer\Storage\";
    private readonly string name = "PassphraseMemorizer.Runner." + Guid.NewGuid().ToString("N");
    internal nint Sid { get; private set; }
    internal string WriteProbe { get; private set; } = "";
    internal string StorageProbe { get; private set; } = "";
    internal string PrivateRegistryProbe { get; private set; } = "";
    private readonly string privateRegistryKey = @"Software\PassphraseMemorizer.PublicProbe." + Guid.NewGuid().ToString("N");
    private bool created;

    internal AppContainerProfile()
    {
        try
        {
            Marshal.ThrowExceptionForHR(NativeMethods.CreateAppContainerProfile(name, name, "Temporary public isolation metadata", 0, 0, out nint sid));
            Sid = sid; created = true;
            var package = new SecurityIdentifier(sid);
            var user = WindowsIdentity.GetCurrent().User ?? throw new IOException("The runner user identity is unavailable.");
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var ownerRights = new SecurityIdentifier("S-1-3-4");
            Marshal.ThrowExceptionForHR(NativeMethods.GetAppContainerFolderPath(package.Value, out nint folder));
            string containerFolder;
            try { containerFolder = Marshal.PtrToStringUni(folder) ?? throw new IOException("The runner profile is unavailable."); }
            finally { Marshal.FreeCoTaskMem(folder); }
            string profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", name);
            if (!SafeFiles.CanonicalPath(containerFolder).Equals(SafeFiles.CanonicalPath(Path.Combine(profile, "AC")), StringComparison.OrdinalIgnoreCase))
                throw new IOException("The runner profile resolved to an unexpected location.");
            SafeFiles.RejectReparsePath(profile);
            var deny = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
            var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            // Set each existing folder explicitly so protected child DACLs cannot
            // retain writes. Only this newly created, checked profile is touched.
            foreach (string directory in Directory.EnumerateDirectories(profile, "*", SearchOption.AllDirectories).Prepend(profile))
            {
                SafeFiles.RejectReparsePath(directory);
                var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
                security.AddAccessRule(new FileSystemAccessRule(package, deny, inheritance, PropagationFlags.None, AccessControlType.Deny));
                security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
                security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
                security.AddAccessRule(new FileSystemAccessRule(ownerRights, FileSystemRights.ReadPermissions, inheritance, PropagationFlags.None, AccessControlType.Allow));
                security.AddAccessRule(new FileSystemAccessRule(package, FileSystemRights.ReadAndExecute, inheritance, PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(directory).SetAccessControl(security);
            }
            WriteProbe = Path.Combine(profile, "AC", "forbidden-public-write-canary.txt");
            string storage = RegistryBase + name.ToLowerInvariant();
            using (var key = Registry.CurrentUser.CreateSubKey(storage, true))
            {
                var security = new RegistrySecurity(); security.SetAccessRuleProtection(true, false);
                security.AddAccessRule(new RegistryAccessRule(package, RegistryRights.WriteKey | RegistryRights.Delete | RegistryRights.ChangePermissions | RegistryRights.TakeOwnership, InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Deny));
                security.AddAccessRule(new RegistryAccessRule(system, RegistryRights.FullControl, InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
                security.AddAccessRule(new RegistryAccessRule(user, RegistryRights.FullControl, InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
                security.AddAccessRule(new RegistryAccessRule(ownerRights, RegistryRights.ReadPermissions, InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
                security.AddAccessRule(new RegistryAccessRule(package, RegistryRights.ReadKey, InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
                key.SetAccessControl(security);
            }
            StorageProbe = user.Value + "\\" + storage;
            using (var key = Registry.CurrentUser.CreateSubKey(privateRegistryKey, true))
            {
                key.SetValue("PublicCanary", "This is public test data, never a passphrase.");
                var security = new RegistrySecurity(); security.SetAccessRuleProtection(true, false);
                security.AddAccessRule(new RegistryAccessRule(system, RegistryRights.FullControl, AccessControlType.Allow));
                security.AddAccessRule(new RegistryAccessRule(user, RegistryRights.FullControl, AccessControlType.Allow));
                security.AddAccessRule(new RegistryAccessRule(ownerRights, RegistryRights.ReadPermissions, AccessControlType.Allow));
                key.SetAccessControl(security);
            }
            PrivateRegistryProbe = user.Value + "\\" + privateRegistryKey;
        }
        catch { Dispose(); throw; }
    }

    public void Dispose()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(privateRegistryKey, false); }
        finally
        {
            try { if (created) { created = false; Marshal.ThrowExceptionForHR(NativeMethods.DeleteAppContainerProfile(name)); } }
            finally { if (Sid != 0) { NativeMethods.FreeSid(Sid); Sid = 0; } }
        }
    }
}
