using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace KalynaArchiver.Services;

internal static partial class SecureMemory
{
    private const long MinimumWorkingSetMargin = 64L * 1024 * 1024;
    private const long MaximumWorkingSetMargin = 256L * 1024 * 1024;
    private static readonly object WorkingSetGate = new();
    private static long _lockedBytes;
    private static long _lockedAllocations;
    private static long _reservedWorkingSetBytes;
    private static nuint _originalMinimumWorkingSet;
    private static nuint _originalMaximumWorkingSet;
    private static bool _originalWorkingSetAvailable;
    private static readonly Dictionary<nuint, int> MacLockedPages = [];
    private static readonly Dictionary<nuint, int> WinLockedPages = [];
    private static readonly List<LockedArray> RetainedFailedLockRollbacks = [];

    internal static Func<nint, nuint, int>? MacMemoryLockOverrideForTests { get; set; }
    internal static Func<nint, nuint, int>? MacMemoryUnlockOverrideForTests { get; set; }
    internal static Func<nint, nuint, bool>? WindowsMemoryLockOverrideForTests { get; set; }
    internal static Func<nint, nuint, bool>? WindowsMemoryUnlockOverrideForTests { get; set; }
    internal static Action? SensitiveBufferBeforeUnlockForTests { get; set; }

    internal static long LockedBytesForTests
    {
        get
        {
            lock (WorkingSetGate)
            {
                return _lockedBytes;
            }
        }
    }

    /// <summary>
    /// How many locked buffers are alive right now.
    /// </summary>
    /// <remarks>
    /// The leak counter that <see cref="LockedBytesForTests"/> cannot be. A
    /// locked buffer is charged the pages it spans, and where the collector
    /// pins a buffer decides whether it spans one page or two: a 64-byte
    /// entropy pool straddles a page boundary in about one allocation in
    /// eighty. So a test that replaces a pool and compares byte totals is
    /// comparing where the collector happened to put things, and fails on a
    /// tree with nothing wrong with it. The count answers the question such a
    /// test is actually asking - whether every buffer taken was given back -
    /// and answers it exactly.
    /// </remarks>
    internal static long LockedAllocationsForTests
    {
        get
        {
            lock (WorkingSetGate)
            {
                return _lockedAllocations;
            }
        }
    }

    internal static long ReservedWorkingSetBytesForTests
    {
        get
        {
            lock (WorkingSetGate)
            {
                return _reservedWorkingSetBytes;
            }
        }
    }

    internal static int RetainedFailedLockRollbacksForTests
    {
        get
        {
            lock (WorkingSetGate)
            {
                return RetainedFailedLockRollbacks.Count;
            }
        }
    }

    public static IDisposable TryLock(byte[]? buffer)
    {
        if (buffer is null || buffer.Length == 0)
        {
            return NoopDisposable.Instance;
        }

        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("Secure memory locking requires a reviewed operating-system adapter.");
        }

        RetryRetainedFailedLockRollbacks();
        return new LockedArray(buffer);
    }

    internal static void RetryRetainedFailedLockRollbacksForTests() =>
        RetryRetainedFailedLockRollbacks();

    private static void RetryRetainedFailedLockRollbacks()
    {
        lock (WorkingSetGate)
        {
            for (int index = RetainedFailedLockRollbacks.Count - 1; index >= 0; index--)
            {
                try
                {
                    RetainedFailedLockRollbacks[index].Dispose();
                }
                catch (CryptographicException)
                {
                    // The buffer was erased before retention. Keep its pin,
                    // pages and accounting intact and retry on a later lock.
                }
            }
        }
    }

    /// <summary>
    /// Attempts every cleanup even when an earlier resource reports an unlock
    /// failure. Failed secure-memory locks retain their own retry registration.
    /// </summary>
    internal static void DisposeAll(params IDisposable?[] resources)
    {
        List<Exception>? failures = null;
        foreach (IDisposable? resource in resources)
        {
            if (resource is null)
            {
                continue;
            }

            try
            {
                resource.Dispose();
            }
            catch (Exception failure)
            {
                (failures ??= []).Add(failure);
            }
        }

        if (failures is not null)
        {
            throw new AggregateException(
                "One or more sensitive-memory resources could not be released; failed locks remain pinned, accounted and retryable.",
                failures);
        }
    }

    /// <summary>
    /// Erases every member of a composite secret before the first member is
    /// unlocked, then attempts every unlock independently.
    /// </summary>
    internal static void ZeroAndDisposeAll(params LockedSensitiveBuffer?[] buffers)
    {
        foreach (LockedSensitiveBuffer? buffer in buffers)
        {
            buffer?.ZeroForDisposal();
        }

        DisposeAll(buffers);
    }

    /// <summary>
    /// Erases and releases a composite secret without losing the exception that
    /// caused cleanup to begin. Every member is erased before the first unlock.
    /// </summary>
    internal static void ZeroAndDisposeAllPreservingFailure(
        Exception? operationFailure,
        string message,
        params LockedSensitiveBuffer?[] buffers)
    {
        try
        {
            ZeroAndDisposeAll(buffers);
        }
        catch (Exception cleanupFailure)
        {
            if (operationFailure is null)
            {
                ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
            }

            throw new AggregateException(message, operationFailure, cleanupFailure);
        }
    }

    private static long RoundedPageBytes(int length)
    {
        long pageSize = Environment.SystemPageSize;
        return checked(((long)length + pageSize - 1) / pageSize * pageSize);
    }

    private static void ReserveWorkingSet(long additionalBytes)
    {
        if (OperatingSystem.IsMacOS())
        {
            _lockedBytes = checked(_lockedBytes + additionalBytes);
            return;
        }

        CaptureOriginalWorkingSetIfNeeded();
        long newTotal = checked(_lockedBytes + additionalBytes);
        SetWorkingSetOrThrow(checked(newTotal + _reservedWorkingSetBytes));
        _lockedBytes = newTotal;
    }

    internal static IDisposable ReserveWorkingSetCapacity(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);

        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("Working-set reservations require a reviewed operating-system adapter.");
        }

        RetryRetainedFailedLockRollbacks();
        var reservation = new WorkingSetReservation(bytes);
        lock (WorkingSetGate)
        {
            if (OperatingSystem.IsMacOS())
            {
                EnsureMacMemoryLockLimit(checked(_lockedBytes + _reservedWorkingSetBytes + bytes));
                _reservedWorkingSetBytes = checked(_reservedWorkingSetBytes + bytes);
                return reservation;
            }

            CaptureOriginalWorkingSetIfNeeded();
            long newReservedBytes = checked(_reservedWorkingSetBytes + bytes);
            SetWorkingSetOrThrow(checked(_lockedBytes + newReservedBytes));
            _reservedWorkingSetBytes = newReservedBytes;
        }

        return reservation;
    }

    private static void CaptureOriginalWorkingSetIfNeeded()
    {
        if (_lockedBytes != 0 || _reservedWorkingSetBytes != 0)
        {
            return;
        }

        _originalWorkingSetAvailable = GetProcessWorkingSetSize(
            GetCurrentProcess(),
            out _originalMinimumWorkingSet,
            out _originalMaximumWorkingSet);
    }

    private static void SetWorkingSetOrThrow(long reservedBytes)
    {
        long minimum = checked(reservedBytes + MinimumWorkingSetMargin);
        long maximum = checked(reservedBytes + MaximumWorkingSetMargin);
        if (!SetProcessWorkingSetSize(GetCurrentProcess(), checked((nuint)minimum), checked((nuint)maximum)))
        {
            throw new CryptographicException(
                "Windows could not reserve the working-set quota required to lock sensitive memory.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }
    }

    private static void ReleaseWorkingSet(long releasedBytes)
    {
        _lockedBytes = Math.Max(0, _lockedBytes - releasedBytes);
        ResizeWorkingSetAfterRelease();
    }

    private static void ReleaseWorkingSetCapacity(long releasedBytes)
    {
        lock (WorkingSetGate)
        {
            _reservedWorkingSetBytes = Math.Max(0, _reservedWorkingSetBytes - releasedBytes);
            ResizeWorkingSetAfterRelease();
        }
    }

    private static void ResizeWorkingSetAfterRelease()
    {
        if (OperatingSystem.IsMacOS())
        {
            return;
        }

        long totalReservedBytes = checked(_lockedBytes + _reservedWorkingSetBytes);
        if (totalReservedBytes == 0 && _originalWorkingSetAvailable)
        {
            _ = SetProcessWorkingSetSize(
                GetCurrentProcess(),
                _originalMinimumWorkingSet,
                _originalMaximumWorkingSet);
            _originalWorkingSetAvailable = false;
            return;
        }

        if (totalReservedBytes == 0)
        {
            _ = SetProcessWorkingSetSize(GetCurrentProcess(), nuint.MaxValue, nuint.MaxValue);
            return;
        }

        long minimum = checked(totalReservedBytes + MinimumWorkingSetMargin);
        long maximum = checked(totalReservedBytes + MaximumWorkingSetMargin);
        _ = SetProcessWorkingSetSize(GetCurrentProcess(), checked((nuint)minimum), checked((nuint)maximum));
    }

    private sealed class WorkingSetReservation(long bytes) : IDisposable
    {
        private long _bytes = bytes;

        public void Dispose()
        {
            long releasedBytes = Interlocked.Exchange(ref _bytes, 0);
            if (releasedBytes != 0)
            {
                ReleaseWorkingSetCapacity(releasedBytes);
            }
        }
    }

    private sealed class LockedArray : IDisposable
    {
        private readonly int _length;
        private long _reservedBytes;
        private nuint[] _lockedPages = [];
        private GCHandle _handle;
        private bool _locked;
        private bool _retainedForRetry;

        public LockedArray(byte[] buffer)
        {
            _length = buffer.Length;
            _handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            nint address = _handle.AddrOfPinnedObject();
            nuint pageSize = checked((nuint)Environment.SystemPageSize);
            nuint first = checked((nuint)address) / pageSize * pageSize;
            nuint lastExclusive = checked((checked((nuint)address) + checked((nuint)buffer.Length) + pageSize - 1) / pageSize * pageSize);
            _reservedBytes = checked((long)(lastExclusive - first));
            try
            {
                lock (WorkingSetGate)
                {
                    ReserveWorkingSet(_reservedBytes);
                    bool lockSucceeded;
                    if (OperatingSystem.IsWindows())
                    {
                        lockSucceeded = TryLockWinPages(
                            _handle.AddrOfPinnedObject(),
                            checked((nuint)buffer.Length),
                            out _lockedPages,
                            out int winError);
                        if (!lockSucceeded)
                        {
                            Marshal.SetLastPInvokeError(winError);
                        }
                    }
                    else
                    {
                        lockSucceeded = TryLockMacPages(
                            _handle.AddrOfPinnedObject(),
                            checked((nuint)buffer.Length),
                            out _lockedPages,
                            out int macError);
                        if (!lockSucceeded)
                        {
                            Marshal.SetLastPInvokeError(macError);
                        }
                    }

                    if (!lockSucceeded)
                    {
                        int error = Marshal.GetLastPInvokeError();
                        long stillLockedBytes = checked(
                            _lockedPages.LongLength * Environment.SystemPageSize);
                        long releasedBytes = checked(_reservedBytes - stillLockedBytes);
                        if (releasedBytes > 0)
                        {
                            ReleaseWorkingSet(releasedBytes);
                            _reservedBytes = stillLockedBytes;
                        }

                        if (_lockedPages.Length > 0)
                        {
                            // The failed lock attempt could not completely undo
                            // the pages it had already acquired. Erase the
                            // caller's bytes immediately, retain the pin and keep
                            // the pages charged in the accounting. Freeing the
                            // GCHandle here would let the array move while the OS
                            // still has its old pages locked.
                            CryptographicOperations.ZeroMemory(buffer);
                            _locked = true;
                            _lockedAllocations = checked(_lockedAllocations + 1);
                            RetainForRetryLocked();
                        }

                        throw new CryptographicException(
                            _lockedPages.Length == 0
                                ? "The operating system could not lock sensitive memory; the operation was stopped to prevent swap exposure."
                                : "The operating system could not lock sensitive memory and could not completely release the partial lock. The bytes were erased and remain pinned and accounted.",
                            new Win32Exception(error));
                    }

                    _locked = true;
                    _lockedAllocations = checked(_lockedAllocations + 1);
                }
            }
            catch
            {
                if (_lockedPages.Length == 0)
                {
                    _handle.Free();
                }
                throw;
            }
        }

        public void Dispose()
        {
            if (!_handle.IsAllocated)
            {
                return;
            }

            Exception? unlockFailure = null;
            lock (WorkingSetGate)
            {
                if (_locked)
                {
                    PageUnlockResult result;
                    if (OperatingSystem.IsWindows())
                    {
                        result = UnlockWinPages(_lockedPages);
                    }
                    else
                    {
                        result = UnlockMacPages(_lockedPages);
                    }

                    _lockedPages = result.RemainingPages;
                    if (result.ReleasedBytes > 0)
                    {
                        _reservedBytes = checked(_reservedBytes - result.ReleasedBytes);
                        ReleaseWorkingSet(result.ReleasedBytes);
                    }

                    if (_lockedPages.Length == 0)
                    {
                        _locked = false;
                        _lockedAllocations = Math.Max(0, _lockedAllocations - 1);
                    }
                    else
                    {
                        RetainForRetryLocked();
                        unlockFailure = new CryptographicException(
                            "The operating system could not unlock every sensitive-memory page. The buffer remains pinned and the unreleased pages remain accounted.",
                            new Win32Exception(result.Error == 0 ? 5 : result.Error));
                    }
                }
            }

            if (unlockFailure is not null)
            {
                throw unlockFailure;
            }

            lock (WorkingSetGate)
            {
                RemoveRetainedRetryLocked();
            }
            _handle.Free();
        }

        private void RetainForRetryLocked()
        {
            if (_retainedForRetry)
            {
                return;
            }

            RetainedFailedLockRollbacks.Add(this);
            _retainedForRetry = true;
        }

        private void RemoveRetainedRetryLocked()
        {
            if (!_retainedForRetry)
            {
                return;
            }

            _ = RetainedFailedLockRollbacks.Remove(this);
            _retainedForRetry = false;
        }
    }

    private static void EnsureMacMemoryLockLimit(long requestedBytes)
    {
        if (GetResourceLimit(ResourceLimitMemoryLock, out ResourceLimit limit) != 0)
        {
            throw new CryptographicException(
                "macOS could not report the memory-lock limit.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        ulong requested = checked((ulong)requestedBytes);
        if (limit.Current != ulong.MaxValue && requested > limit.Current)
        {
            throw new CryptographicException(
                $"macOS permits only {limit.Current} locked bytes, but {requested} bytes are required.");
        }
    }

    private static bool TryLockMacPages(
        nint address,
        nuint length,
        out nuint[] pages,
        out int error)
    {
        nuint pageSize = checked((nuint)Environment.SystemPageSize);
        nuint first = checked((nuint)address) / pageSize * pageSize;
        nuint lastExclusive = checked((checked((nuint)address) + length + pageSize - 1) / pageSize * pageSize);
        var acquired = new List<nuint>(checked((int)((lastExclusive - first) / pageSize)));
        error = 0;
        for (nuint page = first; page < lastExclusive; page += pageSize)
        {
            if (MacLockedPages.TryGetValue(page, out int leases))
            {
                MacLockedPages[page] = checked(leases + 1);
                acquired.Add(page);
                continue;
            }

            if (InvokeMacMemoryLock(checked((nint)page), pageSize) != 0)
            {
                error = Marshal.GetLastPInvokeError();
                PageUnlockResult rollback = UnlockMacPages([.. acquired]);
                pages = rollback.RemainingPages;
                if (rollback.Error != 0)
                {
                    error = rollback.Error;
                }
                return false;
            }

            MacLockedPages.Add(page, 1);
            acquired.Add(page);
        }

        pages = [.. acquired];
        return true;
    }

    private static PageUnlockResult UnlockMacPages(nuint[] pages)
    {
        nuint pageSize = checked((nuint)Environment.SystemPageSize);
        var remaining = new List<nuint>();
        long releasedBytes = 0;
        int firstError = 0;
        foreach (nuint page in pages)
        {
            if (!MacLockedPages.TryGetValue(page, out int leases))
            {
                remaining.Add(page);
                firstError = firstError == 0 ? 22 : firstError;
                continue;
            }

            if (leases > 1)
            {
                MacLockedPages[page] = leases - 1;
                releasedBytes = checked(releasedBytes + (long)pageSize);
            }
            else
            {
                int result = InvokeMacMemoryUnlock(checked((nint)page), pageSize);
                if (result == 0)
                {
                    MacLockedPages.Remove(page);
                    releasedBytes = checked(releasedBytes + (long)pageSize);
                }
                else
                {
                    remaining.Add(page);
                    int error = Marshal.GetLastPInvokeError();
                    firstError = firstError == 0 ? (error == 0 ? 5 : error) : firstError;
                }
            }
        }

        return new PageUnlockResult([.. remaining], releasedBytes, firstError);
    }

    private static bool TryLockWinPages(
        nint address,
        nuint length,
        out nuint[] pages,
        out int error)
    {
        nuint pageSize = checked((nuint)Environment.SystemPageSize);
        nuint first = checked((nuint)address) / pageSize * pageSize;
        nuint lastExclusive = checked((checked((nuint)address) + length + pageSize - 1) / pageSize * pageSize);
        var acquired = new List<nuint>(checked((int)((lastExclusive - first) / pageSize)));
        error = 0;
        for (nuint page = first; page < lastExclusive; page += pageSize)
        {
            if (WinLockedPages.TryGetValue(page, out int leases))
            {
                WinLockedPages[page] = checked(leases + 1);
                acquired.Add(page);
                continue;
            }

            if (!InvokeWindowsMemoryLock(checked((nint)page), pageSize))
            {
                error = Marshal.GetLastPInvokeError();
                PageUnlockResult rollback = UnlockWinPages([.. acquired]);
                pages = rollback.RemainingPages;
                if (rollback.Error != 0)
                {
                    error = rollback.Error;
                }
                return false;
            }

            WinLockedPages.Add(page, 1);
            acquired.Add(page);
        }

        pages = [.. acquired];
        return true;
    }

    private static PageUnlockResult UnlockWinPages(nuint[] pages)
    {
        nuint pageSize = checked((nuint)Environment.SystemPageSize);
        var remaining = new List<nuint>();
        long releasedBytes = 0;
        int firstError = 0;
        foreach (nuint page in pages)
        {
            if (!WinLockedPages.TryGetValue(page, out int leases))
            {
                remaining.Add(page);
                firstError = firstError == 0 ? 487 : firstError;
                continue;
            }

            if (leases > 1)
            {
                WinLockedPages[page] = leases - 1;
                releasedBytes = checked(releasedBytes + (long)pageSize);
            }
            else
            {
                if (InvokeWindowsMemoryUnlock(checked((nint)page), pageSize))
                {
                    WinLockedPages.Remove(page);
                    releasedBytes = checked(releasedBytes + (long)pageSize);
                }
                else
                {
                    remaining.Add(page);
                    int error = Marshal.GetLastPInvokeError();
                    firstError = firstError == 0 ? (error == 0 ? 487 : error) : firstError;
                }
            }
        }

        return new PageUnlockResult([.. remaining], releasedBytes, firstError);
    }

    private static int InvokeMacMemoryLock(nint address, nuint size)
    {
        Func<nint, nuint, int>? testOverride = MacMemoryLockOverrideForTests;
        return testOverride is null
            ? MacMemoryLock(address, size)
            : testOverride(address, size);
    }

    private static int InvokeMacMemoryUnlock(nint address, nuint size)
    {
        Func<nint, nuint, int>? testOverride = MacMemoryUnlockOverrideForTests;
        return testOverride is null
            ? MacMemoryUnlock(address, size)
            : testOverride(address, size);
    }

    private static bool InvokeWindowsMemoryLock(nint address, nuint size)
    {
        Func<nint, nuint, bool>? testOverride = WindowsMemoryLockOverrideForTests;
        return testOverride is null
            ? VirtualLock(address, size)
            : testOverride(address, size);
    }

    private static bool InvokeWindowsMemoryUnlock(nint address, nuint size)
    {
        Func<nint, nuint, bool>? testOverride = WindowsMemoryUnlockOverrideForTests;
        return testOverride is null
            ? VirtualUnlock(address, size)
            : testOverride(address, size);
    }

    private readonly record struct PageUnlockResult(
        nuint[] RemainingPages,
        long ReleasedBytes,
        int Error);

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();

        public void Dispose()
        {
        }
    }

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessWorkingSetSize(
        nint process,
        out nuint minimumWorkingSetSize,
        out nuint maximumWorkingSetSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessWorkingSetSize(
        nint process,
        nuint minimumWorkingSetSize,
        nuint maximumWorkingSetSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualLock(nint address, nuint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualUnlock(nint address, nuint size);

    private const int ResourceLimitMemoryLock = 6;

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct ResourceLimit
    {
        public readonly ulong Current;
        public readonly ulong Maximum;
    }

    [LibraryImport("libSystem.B.dylib", EntryPoint = "getrlimit", SetLastError = true)]
    private static partial int GetResourceLimit(int resource, out ResourceLimit limit);

    [LibraryImport("libSystem.B.dylib", EntryPoint = "mlock", SetLastError = true)]
    private static partial int MacMemoryLock(nint address, nuint size);

    [LibraryImport("libSystem.B.dylib", EntryPoint = "munlock", SetLastError = true)]
    private static partial int MacMemoryUnlock(nint address, nuint size);
}

internal sealed class LockedSensitiveBuffer : IDisposable
{
    private readonly object _gate = new();
    private byte[]? _bytes;
    private IDisposable? _memoryLock;

    internal LockedSensitiveBuffer(byte[] bytes, IDisposable memoryLock)
    {
        _bytes = bytes;
        _memoryLock = memoryLock;
    }

    public byte[] Bytes
    {
        get
        {
            lock (_gate)
            {
                return _bytes ?? throw new ObjectDisposedException(nameof(LockedSensitiveBuffer));
            }
        }
    }

    public static LockedSensitiveBuffer Create(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        byte[] bytes = new byte[length];
        IDisposable? memoryLock = null;
        try
        {
            memoryLock = SecureMemory.TryLock(bytes);
            return new LockedSensitiveBuffer(bytes, memoryLock);
        }
        catch (Exception operationFailure)
        {
            CryptographicOperations.ZeroMemory(bytes);
            try
            {
                SecureMemory.DisposeAll(memoryLock);
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException(
                    "Sensitive-buffer construction failed and its acquired memory lock could not be released.",
                    operationFailure,
                    cleanupFailure);
            }

            ExceptionDispatchInfo.Capture(operationFailure).Throw();
            throw new UnreachableException();
        }
    }

    public static LockedSensitiveBuffer Encode(string value, Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(encoding);
        LockedSensitiveBuffer buffer = Create(encoding.GetByteCount(value));
        try
        {
            int written = encoding.GetBytes(value.AsSpan(), buffer.Bytes);
            if (written != buffer.Bytes.Length)
            {
                throw new CryptographicException("Sensitive text was not encoded completely.");
            }

            return buffer;
        }
        catch (Exception operationFailure)
        {
            try
            {
                SecureMemory.ZeroAndDisposeAll(buffer);
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException(
                    "Sensitive-text encoding failed and its locked buffer could not be released.",
                    operationFailure,
                    cleanupFailure);
            }

            ExceptionDispatchInfo.Capture(operationFailure).Throw();
            throw new UnreachableException();
        }
    }

    internal void ZeroForDisposal()
    {
        lock (_gate)
        {
            if (_bytes is not null)
            {
                CryptographicOperations.ZeroMemory(_bytes);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_bytes is null)
            {
                return;
            }

            CryptographicOperations.ZeroMemory(_bytes);
            SecureMemory.SensitiveBufferBeforeUnlockForTests?.Invoke();
            _memoryLock?.Dispose();
            _memoryLock = null;
            _bytes = null;
        }
    }
}
