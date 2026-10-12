using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace PassphraseMemorizer.Runtime;

// ACL updates are serialized across all instances under the current user.
// Wait/release happen on the same worker thread; cancellation never abandons
// an active runner and its temporary permissions.
internal sealed class UserRunnerMutex : IDisposable
{
    private readonly nint handle;
    private bool acquired;
    internal UserRunnerMutex(CancellationToken cancellationToken)
    {
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new IOException("The runner user identity is unavailable.");
        if (!NativeMethods.ConvertStringSecurityDescriptorToSecurityDescriptorW("D:P(A;;GA;;;SY)(A;;GA;;;" + sid + ")", 1, out nint descriptor, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var attributes = new NativeMethods.SecurityAttributes { Length = Marshal.SizeOf<NativeMethods.SecurityAttributes>(), Descriptor = descriptor };
            handle = NativeMethods.CreateMutexExW(ref attributes, @"Global\PassphraseMemorizer.Runner." + sid, 0, 0x00100001);
            if (handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "The runner's per-user isolation lock is unavailable.");
        }
        finally { NativeMethods.LocalFree(descriptor); }
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                uint wait = NativeMethods.WaitForSingleObject(handle, 100);
                if (wait is 0 or 0x80) { acquired = true; break; }
                if (wait != 0x102) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        catch { NativeMethods.CloseHandle(handle); throw; }
    }
    public void Dispose()
    {
        if (acquired) { NativeMethods.ReleaseMutex(handle); acquired = false; }
        NativeMethods.CloseHandle(handle);
    }
}
