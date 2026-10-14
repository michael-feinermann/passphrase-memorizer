using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PassphraseMemorizer.Runtime;

internal sealed class SandboxProcess : IDisposable
{
    internal readonly FileStream Input, Output, Error;
    private readonly nint process, job, cancellationEvent;
    private readonly object lifetime = new();
    private bool disposed;
    private SandboxProcess(nint process, nint job, nint cancellationEvent, SafeFileHandle input, SafeFileHandle output, SafeFileHandle error)
    {
        this.process = process; this.job = job; this.cancellationEvent = cancellationEvent;
        Input = new FileStream(input, FileAccess.Write, 4096, false);
        Output = new FileStream(output, FileAccess.Read, 4096, false);
        Error = new FileStream(error, FileAccess.Read, 4096, false);
    }
    internal static SandboxProcess Start(string executable, string[] arguments, nint sid)
    {
        var attributes = new NativeMethods.SecurityAttributes { Length = Marshal.SizeOf<NativeMethods.SecurityAttributes>(), Inherit = true };
        SafeFileHandle? childInput = null, parentInput = null, parentOutput = null, childOutput = null, parentError = null, childError = null;
        nint list = 0, job = 0, process = 0, thread = 0, cancellationEvent = 0, childCancellationEvent = 0;
        bool listInitialized = false;
        List<nint> allocations = [];
        nint Allocate<T>(T value) where T : struct { nint pointer = Marshal.AllocHGlobal(Marshal.SizeOf<T>()); allocations.Add(pointer); Marshal.StructureToPtr(value, pointer, false); return pointer; }
        try
        {
            Require(NativeMethods.CreatePipe(out childInput, out parentInput, ref attributes, 0));
            Require(NativeMethods.CreatePipe(out parentOutput, out childOutput, ref attributes, 0));
            Require(NativeMethods.CreatePipe(out parentError, out childError, ref attributes, 0));
            Require(NativeMethods.SetHandleInformation(parentInput, 1, 0));
            Require(NativeMethods.SetHandleInformation(parentOutput, 1, 0));
            Require(NativeMethods.SetHandleInformation(parentError, 1, 0));
            var eventSecurity = new NativeMethods.SecurityAttributes { Length = Marshal.SizeOf<NativeMethods.SecurityAttributes>() };
            cancellationEvent = NativeMethods.CreateEventExW(ref eventSecurity, null, 1, 0x00100002); Require(cancellationEvent != 0);
            Require(NativeMethods.DuplicateHandle(NativeMethods.GetCurrentProcess(), cancellationEvent, NativeMethods.GetCurrentProcess(), out childCancellationEvent, 0x00100000, true, 0));
            nuint size = 0;
            NativeMethods.InitializeProcThreadAttributeList(0, 4, 0, ref size);
            list = Marshal.AllocHGlobal(checked((nint)size));
            Require(NativeMethods.InitializeProcThreadAttributeList(list, 4, 0, ref size)); listInitialized = true;
            void Attribute(nuint key, nint value, nuint count) => Require(NativeMethods.UpdateProcThreadAttribute(list, 0, key, value, count, 0, 0));
            // The sole capability permits Windows Winsock's registry reads.
            // It grants no network, COM, private-file or registry write access.
            var registryRead = new SecurityIdentifier(AppContainerProfile.RegistryReadCapability);
            byte[] capabilityBytes = new byte[registryRead.BinaryLength]; registryRead.GetBinaryForm(capabilityBytes, 0);
            nint capabilitySid = Marshal.AllocHGlobal(capabilityBytes.Length); allocations.Add(capabilitySid); Marshal.Copy(capabilityBytes, 0, capabilitySid, capabilityBytes.Length);
            nint capability = Marshal.AllocHGlobal(2 * IntPtr.Size); allocations.Add(capability);
            Marshal.WriteIntPtr(capability, capabilitySid); Marshal.WriteInt32(capability, IntPtr.Size, 4);
            if (IntPtr.Size == 8) Marshal.WriteInt32(capability, 12, 0);
            var capabilities = new NativeMethods.SecurityCapabilities { Sid = sid, Capabilities = capability, Count = 1 };
            Attribute(0x20009, Allocate(capabilities), (nuint)Marshal.SizeOf<NativeMethods.SecurityCapabilities>());
            nint handles = Marshal.AllocHGlobal(4 * IntPtr.Size); allocations.Add(handles);
            Marshal.WriteIntPtr(handles, childInput.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, IntPtr.Size, childOutput.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, 2 * IntPtr.Size, childError.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, 3 * IntPtr.Size, childCancellationEvent);
            Attribute(0x20002, handles, (nuint)(4 * IntPtr.Size));
            Attribute(0x2000f, Allocate(1u), sizeof(uint)); // LPAC: opt out of ALL APPLICATION PACKAGES access.
            // The active-process-limit job below denies children before the
            // suspended process runs. The additional child-policy attribute
            // prevents Windows DLL initialization on the tested Windows host.
            // CUDA under WDDM needs Win32k graphics system calls. The CPU build
            // disabled them (bit 28); retain every other mitigation, the LPAC
            // token, the single registryRead capability and the mandatory live
            // network/filesystem/registry/child-process denial probes.
            ulong mitigations = 1UL | (1UL << 8) | (1UL << 12) | (1UL << 24) | (1UL << 32) | (1UL << 36);
            Attribute(0x20007, Allocate(mitigations), sizeof(ulong));
            var startup = new NativeMethods.StartupInfoEx { Attributes = list };
            startup.Startup.Size = Marshal.SizeOf<NativeMethods.StartupInfoEx>();
            startup.Startup.Flags = 0x100;
            startup.Startup.Input = childInput.DangerousGetHandle(); startup.Startup.Output = childOutput.DangerousGetHandle(); startup.Startup.Error = childError.DangerousGetHandle();
            // Windows rewrites these three public system/profile paths for a
            // lowbox. Build an allowlisted environment; inherit no variables.
            string minimalEnvironment = "SystemRoot=" + Environment.GetFolderPath(Environment.SpecialFolder.Windows) + "\0USERPROFILE=" + Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\0LOCALAPPDATA=" + Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\0\0";
            nint environment = Marshal.StringToHGlobalUni(minimalEnvironment); allocations.Add(environment);
            string eventArgument = childCancellationEvent.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
            var command = new StringBuilder(Quote(executable) + " " + string.Join(" ", arguments.Append(eventArgument).Select(Quote)));
            Require(NativeMethods.CreateProcessW(executable, command, 0, 0, true, 0x00080000 | 0x00000400 | 0x08000000 | 0x4,
                environment, Path.GetDirectoryName(executable)!, ref startup, out var created));
            process = created.Process; thread = created.Thread;
            var actual = new StringBuilder(32768); uint actualSize = (uint)actual.Capacity;
            Require(NativeMethods.QueryFullProcessImageNameW(process, 0, actual, ref actualSize));
            if (!Path.GetFullPath(actual.ToString()).Equals(Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase)) throw new IOException("The launched runner path differs from the verified inventory.");
            job = NativeMethods.CreateJobObjectW(0, null); Require(job != 0);
            var limits = new NativeMethods.JobLimits { Basic = new NativeMethods.JobBasicLimits { Flags = 0x2000 | 0x8, ActiveProcesses = 1 } };
            Require(NativeMethods.SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<NativeMethods.JobLimits>()));
            Require(NativeMethods.AssignProcessToJobObject(job, process));
            if (NativeMethods.ResumeThread(thread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
            NativeMethods.CloseHandle(thread); thread = 0;
            var result = new SandboxProcess(process, job, cancellationEvent, parentInput, parentOutput, parentError);
            process = job = cancellationEvent = 0; parentInput = parentOutput = parentError = null;
            return result;
        }
        finally
        {
            childInput?.Dispose(); childOutput?.Dispose(); childError?.Dispose();
            if (childCancellationEvent != 0) NativeMethods.CloseHandle(childCancellationEvent);
            if (cancellationEvent != 0) NativeMethods.CloseHandle(cancellationEvent);
            parentInput?.Dispose(); parentOutput?.Dispose(); parentError?.Dispose();
            if (process != 0) { NativeMethods.TerminateProcess(process, 75); NativeMethods.WaitForSingleObject(process, 10000); NativeMethods.CloseHandle(process); }
            if (thread != 0) NativeMethods.CloseHandle(thread);
            if (job != 0) NativeMethods.CloseHandle(job);
            if (listInitialized) NativeMethods.DeleteProcThreadAttributeList(list);
            if (list != 0) Marshal.FreeHGlobal(list);
            foreach (nint allocation in allocations) Marshal.FreeHGlobal(allocation);
        }
    }
    private static string Quote(string argument)
    {
        if (argument.Contains('"') || argument.Contains('\0') || argument.EndsWith('\\')) throw new ArgumentException("Invalid runner path.");
        return "\"" + argument + "\"";
    }
    private static void Require(bool success) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not enforce the required runner isolation."); }
    internal void Cancel()
    {
        lock (lifetime) { if (disposed) return; Require(NativeMethods.SetEvent(cancellationEvent)); }
        // Callbacks allow the model loader/compute loop to unwind and wipe.
        // A hung or compromised worker is forcibly stopped after two seconds.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            lock (lifetime) { if (disposed) return; }
            Thread.Sleep(2000);
            lock (lifetime) { if (!disposed && NativeMethods.WaitForSingleObject(process, 0) != 0) NativeMethods.TerminateJobObject(job, 75); }
        });
    }
    internal uint Wait()
    {
        if (NativeMethods.WaitForSingleObject(process, 10000) != 0) throw new IOException("The isolated runner did not terminate.");
        Require(NativeMethods.GetExitCodeProcess(process, out uint result)); return result;
    }
    public void Dispose()
    {
        lock (lifetime) { if (disposed) return; NativeMethods.SetEvent(cancellationEvent); }
        Input.Dispose();
        if (NativeMethods.WaitForSingleObject(process, 2000) != 0) NativeMethods.TerminateJobObject(job, 75);
        NativeMethods.WaitForSingleObject(process, 10000);
        lock (lifetime) { disposed = true; }
        Input.Dispose(); Output.Dispose(); Error.Dispose();
        lock (lifetime) { NativeMethods.CloseHandle(cancellationEvent); NativeMethods.CloseHandle(job); NativeMethods.CloseHandle(process); }
    }
}
