using System.Security.AccessControl;
using System.Security.Principal;

namespace PassphraseMemorizer.Runtime;

// A fresh SID per invocation has no accumulated model grants. Its public profile
// is separately made read-only. Only exact file read/execute ACEs are added. They are
// removed after job termination without overwriting concurrent unrelated ACLs.
internal sealed class ReadAccessLease : IDisposable
{
    private readonly List<(string Path, bool Directory, FileSystemAccessRule Rule)> changes = [];
    private readonly SecurityIdentifier sid;
    internal ReadAccessLease(nint nativeSid) { sid = new SecurityIdentifier(nativeSid); }
    internal void Grant(string file, bool execute)
    {
        string full = Path.GetFullPath(file);
        Add(full, false, execute ? FileSystemRights.ReadAndExecute : FileSystemRights.Read);
        // The AppContainer token retains SeChangeNotifyPrivilege, permitting
        // traversal of ancestors without directory enumeration. No folder or
        // drive-root ACL is modified; permissions cover these exact files only.
    }
    private void Add(string path, bool directory, FileSystemRights rights)
    {
        if (changes.Any(change => change.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) return;
        var rule = new FileSystemAccessRule(sid, rights, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow);
        if (directory)
        {
            var info = new DirectoryInfo(path); var security = info.GetAccessControl(AccessControlSections.Access);
            security.AddAccessRule(rule); info.SetAccessControl(security);
        }
        else
        {
            var info = new FileInfo(path); var security = info.GetAccessControl(AccessControlSections.Access);
            security.AddAccessRule(rule); info.SetAccessControl(security);
        }
        changes.Add((path, directory, rule));
    }
    public void Dispose()
    {
        List<Exception> failures = [];
        foreach (var change in changes.AsEnumerable().Reverse())
        {
            try
            {
                if (change.Directory)
                {
                    var info = new DirectoryInfo(change.Path); var security = info.GetAccessControl(AccessControlSections.Access);
                    security.RemoveAccessRuleSpecific(change.Rule); info.SetAccessControl(security);
                }
                else
                {
                    var info = new FileInfo(change.Path); var security = info.GetAccessControl(AccessControlSections.Access);
                    security.RemoveAccessRuleSpecific(change.Rule); info.SetAccessControl(security);
                }
            }
            catch (Exception exception) { failures.Add(exception); }
        }
        changes.Clear();
        if (failures.Count != 0) throw new IOException("The runner's temporary read permissions could not be removed.", new AggregateException(failures));
    }
}
