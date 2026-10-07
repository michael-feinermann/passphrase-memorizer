using System.ComponentModel;
using System.Runtime.InteropServices;
using KalynaArchiver.Services;
using Microsoft.Win32.SafeHandles;

namespace KeepVaultMac.Packaging;

/// <summary>
/// Stages one secret file below a bound directory descriptor and publishes
/// exactly the descriptor that the caller validated. This is macOS-only.
/// </summary>
internal sealed class MacBoundSecretFile : IDisposable
{
    private const int OpenReadOnly = 0x0000;
    private const int OpenDirectory = 0x00100000;
    private const int OpenCloseOnExec = 0x01000000;
    private const int OpenNoFollowAny = 0x20000000;
    private const int AtSymlinkNoFollow = 0x0020;
    private const uint RenameExclusive = 0x00000004;
    private const ushort OwnerReadWrite = 0x0180; // 0600
    private const ushort FileTypeMask = 0xF000;
    private const ushort RegularFile = 0x8000;
    private const ushort Directory = 0x4000;

    private readonly PrivateDirectoryLease? _directoryLease;
    private readonly string _canonicalParent;
    private readonly string _finalName;
    private readonly SafeFileHandle _parentHandle;
    private readonly FileStream _stream;
    private readonly FileIdentity _parentIdentity;
    private readonly FileIdentity _createdIdentity;
    private string? _currentName;
    private bool _published;
    private bool _disposed;

    // Adversarial hooks are internal to the signer/test assembly. They never
    // carry plaintext and are null in production.
    internal static Action<string, string>? TestHookBeforeRename { get; set; }
    internal static Action<string, string>? TestHookAfterRename { get; set; }
    internal static Action? TestHookBeforeDispose { get; set; }

    private MacBoundSecretFile(
        string canonicalParent,
        string finalName,
        SafeFileHandle parentHandle,
        FileStream stream,
        FileIdentity parentIdentity,
        FileIdentity createdIdentity,
        string temporaryName,
        PrivateDirectoryLease? directoryLease)
    {
        _directoryLease = directoryLease;
        _canonicalParent = canonicalParent;
        _finalName = finalName;
        _parentHandle = parentHandle;
        _stream = stream;
        _parentIdentity = parentIdentity;
        _createdIdentity = createdIdentity;
        _currentName = temporaryName;
    }

    internal FileStream Stream => _stream;

    internal static void ValidatePrivateDirectory(string directory)
    {
        using var lease = BindPrivateDirectory(directory);
        lease.Validate();
    }

    internal static PrivateDirectoryLease BindPrivateDirectory(string directory)
        => new(directory);

    // A provisioning operation retains this descriptor through generation,
    // every publication and the final proof. A pathname precheck alone cannot
    // bind several independently created files to one protected directory.
    internal sealed class PrivateDirectoryLease : IDisposable
    {
        private readonly SafeFileHandle _handle;
        private readonly FileIdentity _identity;
        internal string Path { get; }

        internal PrivateDirectoryLease(string directory)
        {
            if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
            Path = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(directory));
            if (!string.Equals(Path, ResolveExistingPath(Path), StringComparison.Ordinal))
                throw new IOException("The key directory must not contain symbolic links.");
            int fd = Open(Path, OpenReadOnly | OpenDirectory | OpenCloseOnExec | OpenNoFollowAny);
            if (fd < 0) throw NativeIOException("The private key directory cannot be opened.");
            _handle = new SafeFileHandle((nint)fd, ownsHandle: true);
            try
            {
                _identity = GetIdentity(_handle);
                Validate();
            }
            catch { _handle.Dispose(); throw; }
        }

        internal void Validate()
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            RequireOwnershipEnforced(_handle);
            RequireNoExtendedAcl(_handle);
            FileIdentity held = GetIdentity(_handle);
            FileIdentity named = GetPathIdentity(Path);
            if (!_identity.SameObject(held) || !_identity.SameObject(named)
                || (held.Mode & FileTypeMask) != Directory || (held.Mode & 0x01FF) != 0x01C0
                || held.Uid != GetEffectiveUserId() || held.Mode != named.Mode || held.Uid != named.Uid)
                throw new IOException("The private mode-0700 key directory changed its object, owner or permissions.");
        }

        internal void RequireSameDirectory(SafeFileHandle handle, string path)
        {
            Validate();
            if (!string.Equals(path, Path, StringComparison.Ordinal)
                || !_identity.SameObject(GetIdentity(handle)))
                throw new IOException("A keyset write was redirected outside its bound directory.");
        }

        public void Dispose() => _handle.Dispose();
    }

    // Invoked only by adversarial tests after the pathname and descriptor have
    // first been proven to identify the same private file. Production leaves
    // this null.
    internal static Action<string>? TestHookAfterReadValidation { get; set; }

    /// <summary>
    /// Reads one private file through an O_NOFOLLOW_ANY descriptor. Allocation
    /// is bounded by the descriptor's size, never by a path-based precheck, and
    /// the held object plus its namespace identity are revalidated after the
    /// same-descriptor read.
    /// </summary>
    internal static LockedSensitiveBuffer ReadPrivateBytes(
        string sourcePath,
        int minimumBytes,
        int maximumBytes,
        string description)
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("Bound private-file reads require macOS.");
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumBytes);
        if (maximumBytes < minimumBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumBytes),
                "The maximum private-file size is smaller than its minimum size.");
        }

        string fullPath = Path.GetFullPath(sourcePath);
        int descriptor = Open(fullPath, OpenReadOnly | OpenCloseOnExec | OpenNoFollowAny);
        if (descriptor < 0)
        {
            throw NativeIOException($"The {description} could not be opened without symbolic links.");
        }

        SafeFileHandle? handle = new((nint)descriptor, ownsHandle: true);
        FileStream? stream = null;
        LockedSensitiveBuffer? result = null;
        Exception? operationFailure = null;
        try
        {
            RequireOwnershipEnforced(handle);
            RequireNoExtendedAcl(handle);
            FileIdentity before = RequirePrivateReadIdentity(GetIdentity(handle), description);
            FileIdentity pathBefore = GetPathIdentity(fullPath);
            if (!before.SameReadSnapshot(pathBefore))
            {
                throw new IOException($"The {description} path does not identify its opened descriptor.");
            }
            if (before.Size < minimumBytes || before.Size > maximumBytes)
            {
                throw new InvalidDataException(
                    $"The {description} must contain {minimumBytes} to {maximumBytes} bytes.");
            }

            TestHookAfterReadValidation?.Invoke(fullPath);
            result = LockedSensitiveBuffer.Create(checked((int)before.Size));
            stream = new FileStream(
                handle,
                FileAccess.Read,
                // A buffered FileStream would retain short plaintext inputs
                // (including USB wrapping keys) in its ordinary managed array.
                // Read directly into the locked, explicitly erased result.
                bufferSize: 1,
                isAsync: false);
            handle = null;
            stream.ReadExactly(result.Bytes);
            if (stream.ReadByte() != -1)
            {
                throw new IOException($"The {description} grew during its bounded read.");
            }

            FileIdentity after = RequirePrivateReadIdentity(
                GetIdentity(stream.SafeFileHandle),
                description);
            FileIdentity pathAfter = GetPathIdentity(fullPath);
            RequireOwnershipEnforced(stream.SafeFileHandle);
            RequireNoExtendedAcl(stream.SafeFileHandle);
            if (!before.SameReadSnapshot(after)
                || !after.SameReadSnapshot(pathAfter))
            {
                throw new IOException($"The {description} changed during its descriptor-bound read.");
            }

        }
        catch (Exception ex)
        {
            operationFailure = ex;
        }

        return LockedBufferTransfer.Complete(
            result,
            operationFailure,
            $"The {description} read failed during secure cleanup.",
            [],
            [stream, handle]);
    }

    internal static MacBoundSecretFile Create(string destinationPath, PrivateDirectoryLease? directoryLease = null)
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("Bound secret-file creation requires macOS.");
        }

        directoryLease?.Validate();
        string fullPath = Path.GetFullPath(destinationPath);
        string requestedParent = Path.GetDirectoryName(fullPath)
            ?? throw new IOException("The secret-file destination has no parent directory.");
        string finalName = Path.GetFileName(fullPath);
        if (string.IsNullOrWhiteSpace(finalName)
            || !string.Equals(finalName, Path.GetFileName(finalName), StringComparison.Ordinal))
        {
            throw new IOException("The secret-file destination name is invalid.");
        }

        string canonicalParent = ResolveExistingPath(requestedParent);
        SafeFileHandle? parentHandle = null;
        SafeFileHandle? createdHandle = null;
        FileStream? stream = null;
        string? temporaryName = null;
        try
        {
            int parentDescriptor = Open(
                canonicalParent,
                OpenReadOnly | OpenDirectory | OpenCloseOnExec | OpenNoFollowAny);
            if (parentDescriptor < 0)
            {
                throw NativeIOException("The canonical secret-file parent could not be opened without symlinks.");
            }
            parentHandle = new SafeFileHandle((nint)parentDescriptor, ownsHandle: true);
            directoryLease?.RequireSameDirectory(parentHandle, canonicalParent);
            RequireOwnershipEnforced(parentHandle);
            RequireNoExtendedAcl(parentHandle);
            FileIdentity parentIdentity = GetIdentity(parentHandle);
            if ((parentIdentity.Mode & FileTypeMask) != Directory
                || !parentIdentity.SameObject(GetPathIdentity(canonicalParent)))
            {
                throw new IOException("The secret-file parent path is not the bound directory.");
            }

            byte[] templateBytes = System.Text.Encoding.ASCII.GetBytes(
                ".keep-vault-hybrid.XXXXXXXXXXXXXXXXXXXXXXXX\0");
            nint template = Marshal.AllocHGlobal(templateBytes.Length);
            int createdDescriptor = -1;
            try
            {
                Marshal.Copy(templateBytes, 0, template, templateBytes.Length);
                // mkostempsat_np is fixed-arity on Apple arm64 and internally
                // uses O_CREAT|O_EXCL with mode 0600. A variadic open/openat
                // P/Invoke passes mode in the wrong ABI location.
                createdDescriptor = MkOStempsAtNp(
                    parentDescriptor,
                    template,
                    suffixLength: 0,
                    OpenCloseOnExec);
                if (createdDescriptor >= 0)
                {
                    Marshal.Copy(template, templateBytes, 0, templateBytes.Length);
                    int terminator = Array.IndexOf(templateBytes, (byte)0);
                    if (terminator > 0)
                    {
                        temporaryName = System.Text.Encoding.ASCII.GetString(
                            templateBytes,
                            0,
                            terminator);
                    }
                }
            }
            finally
            {
                byte[] zeros = new byte[templateBytes.Length];
                Marshal.Copy(zeros, 0, template, zeros.Length);
                Marshal.FreeHGlobal(template);
                Array.Clear(templateBytes);
            }
            if (createdDescriptor < 0)
            {
                throw NativeIOException("The private secret staging file could not be created exclusively.");
            }
            if (temporaryName is null)
            {
                Close(createdDescriptor);
                throw new IOException("macOS did not return a private staging name.");
            }

            createdHandle = new SafeFileHandle((nint)createdDescriptor, ownsHandle: true);
            if (FChmod(createdDescriptor, OwnerReadWrite) != 0)
            {
                throw NativeIOException("The secret staging descriptor could not be fixed at mode 0600.");
            }
            RequireOwnershipEnforced(createdHandle);
            RequireNoExtendedAcl(createdHandle);
            FileIdentity createdIdentity = RequirePrivateSingleLink(GetIdentity(createdHandle));
            FileIdentity temporaryIdentity = GetIdentityAt(parentHandle, temporaryName);
            if (!createdIdentity.SameObject(temporaryIdentity)
                || temporaryIdentity.LinkCount != 1)
            {
                throw new IOException("The private staging name does not identify its created descriptor.");
            }

            stream = new FileStream(
                createdHandle,
                FileAccess.ReadWrite,
                // Wrapping-key provisioning writes sensitive bytes directly
                // from a locked buffer. Do not retain a second buffered copy.
                bufferSize: 1,
                isAsync: false);
            createdHandle = null;
            var result = new MacBoundSecretFile(
                canonicalParent,
                finalName,
                parentHandle,
                stream,
                parentIdentity,
                createdIdentity,
                temporaryName,
                directoryLease);
            parentHandle = null;
            stream = null;
            return result;
        }
        catch
        {
            if (createdHandle is not null
                && parentHandle is not null
                && temporaryName is not null)
            {
                try
                {
                    FileIdentity handleIdentity = GetIdentity(createdHandle);
                    if (TryGetIdentityAt(parentHandle, temporaryName, out FileIdentity entryIdentity)
                        && handleIdentity.SameObject(entryIdentity)
                        && handleIdentity.LinkCount == 1
                        && entryIdentity.LinkCount == 1)
                    {
                        UnlinkAt(parentHandle, temporaryName);
                    }
                }
                catch
                {
                    // Preserve the construction failure and never unlink an
                    // entry whose identity cannot be proved.
                }
            }
            stream?.Dispose();
            createdHandle?.Dispose();
            parentHandle?.Dispose();
            throw;
        }
    }

    internal void Publish()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_published || _currentName is null)
        {
            throw new InvalidOperationException("The secret staging file has already been published or abandoned.");
        }

        TestHookBeforeRename?.Invoke(_canonicalParent, _currentName);
        RequireParentStillBound();
        RequireOwnershipEnforced(_stream.SafeFileHandle);
        RequireNoExtendedAcl(_stream.SafeFileHandle);
        FileIdentity handleIdentity = RequirePrivateSingleLink(GetIdentity(_stream.SafeFileHandle));
        FileIdentity temporaryIdentity = GetIdentityAt(_parentHandle, _currentName);
        if (!handleIdentity.SameObject(temporaryIdentity)
            || temporaryIdentity.LinkCount != 1)
        {
            throw new IOException("The secret staging name no longer identifies the validated descriptor.");
        }

        RenameAtExclusive(_parentHandle, _currentName, _finalName);
        _currentName = _finalName;
        TestHookAfterRename?.Invoke(_canonicalParent, _finalName);

        // This post-check is mandatory: success is reported only if the final
        // namespace entry still names the exact descriptor that was validated.
        RequireParentStillBound();
        RequireOwnershipEnforced(_stream.SafeFileHandle);
        RequireNoExtendedAcl(_stream.SafeFileHandle);
        FileIdentity installedHandle = RequirePrivateSingleLink(GetIdentity(_stream.SafeFileHandle));
        FileIdentity installedEntry = GetIdentityAt(_parentHandle, _finalName);
        if (!installedHandle.SameObject(installedEntry)
            || installedEntry.LinkCount != 1)
        {
            throw new IOException("The published secret path does not identify the validated descriptor.");
        }
        _published = true;
    }

    internal bool RemoveCurrentNameIfStillOwned()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_currentName is null) return false;

        if (!TryGetIdentityAt(_parentHandle, _currentName, out FileIdentity entryIdentity))
        {
            return false;
        }
        if (!_createdIdentity.SameObject(entryIdentity)
            || entryIdentity.LinkCount != 1)
        {
            // A same-UID process replaced or moved the name. Never unlink the
            // foreign entry; the caller still wipes the held original FD.
            return false;
        }
        if (!_stream.SafeFileHandle.IsClosed
            && !_stream.SafeFileHandle.IsInvalid)
        {
            FileIdentity handleIdentity = GetIdentity(_stream.SafeFileHandle);
            if (!_createdIdentity.SameObject(handleIdentity)
                || handleIdentity.LinkCount != 1)
            {
                return false;
            }
        }

        UnlinkAt(_parentHandle, _currentName);
        _currentName = null;
        _published = false;
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        TestHookBeforeDispose?.Invoke();
        _stream.Dispose();
        _parentHandle.Dispose();
        _disposed = true;
    }

    private void RequireParentStillBound()
    {
        _directoryLease?.RequireSameDirectory(_parentHandle, _canonicalParent);
        RequireOwnershipEnforced(_parentHandle);
        RequireNoExtendedAcl(_parentHandle);
        FileIdentity descriptorIdentity = GetIdentity(_parentHandle);
        FileIdentity pathIdentity = GetPathIdentity(_canonicalParent);
        if (!_parentIdentity.SameObject(descriptorIdentity)
            || !_parentIdentity.SameObject(pathIdentity)
            || (pathIdentity.Mode & FileTypeMask) != Directory)
        {
            throw new IOException("The secret-file parent path changed during publication.");
        }
    }

    private static FileIdentity RequirePrivateSingleLink(FileIdentity identity)
    {
        if ((identity.Mode & FileTypeMask) != RegularFile
            || (identity.Mode & 0x01FF) != OwnerReadWrite
            || identity.LinkCount != 1
            || identity.Uid != GetEffectiveUserId())
        {
            throw new IOException("The secret staging descriptor is not a single-link mode-0600 regular file.");
        }
        return identity;
    }

    private static FileIdentity RequirePrivateReadIdentity(
        FileIdentity identity,
        string description)
    {
        if ((identity.Mode & FileTypeMask) != RegularFile
            || (identity.Mode & 0x01FF) != OwnerReadWrite
            || identity.LinkCount != 1
            || identity.Uid != GetEffectiveUserId())
        {
            throw new IOException(
                $"The {description} descriptor is not a current-user-owned, single-link mode-0600 regular file.");
        }
        return identity;
    }

    internal static void ValidatePrivateVolumeFlags(uint flags)
    {
        const uint local = 0x00001000;
        const uint ignoreOwnership = 0x00200000;
        if ((flags & local) == 0 || (flags & ignoreOwnership) != 0)
            throw new IOException("Signing secrets require a local volume with ownership enforcement enabled.");
    }

    private static void RequireNoExtendedAcl(SafeFileHandle handle)
    {
        // Darwin ACL_TYPE_EXTENDED from the current SDK. Reject any attached
        // ACL rather than treating mode 0600/0700 as proof of exclusivity.
        bool added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            nint acl = AclGetFdNp(checked((int)handle.DangerousGetHandle()), 0x100);
            if (acl != 0)
            {
                _ = AclFree(acl);
                throw new IOException("Signing secrets and their private directory must have no extended ACL.");
            }
            int error = Marshal.GetLastPInvokeError();
            // Darwin returns ENOENT for an absent ACL; ENOATTR is also an
            // explicit absence result. All unsupported/permission errors fail.
            if (error is not (2 or 93))
                throw new IOException("The private signing object's ACL could not be inspected.", new Win32Exception(error));
        }
        finally { if (added) handle.DangerousRelease(); }
    }

    private static void RequireOwnershipEnforced(SafeFileHandle handle)
    {
        // Darwin's statfs64 ABI: verified against the platform SDK in the
        // native regression fixture. Query the open object, never a mount path.
        bool added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            int descriptor = checked((int)handle.DangerousGetHandle());
            DarwinStatFs status;
            int result = RuntimeInformation.ProcessArchitecture == Architecture.X64
                ? FStatFsX64(descriptor, out status)
                : FStatFs(descriptor, out status);
            if (result != 0)
                throw NativeIOException("The signing-secret volume could not be inspected.");
            ValidatePrivateVolumeFlags(status.Flags);
        }
        finally
        {
            if (added) handle.DangerousRelease();
        }
    }

    private static string ResolveExistingPath(string path)
    {
        nint result = RealPath(Path.GetFullPath(path), 0);
        if (result == 0)
        {
            throw NativeIOException("The existing secret-file parent could not be canonicalized.");
        }
        try
        {
            return Marshal.PtrToStringUTF8(result)
                ?? throw new IOException("macOS returned an invalid canonical parent path.");
        }
        finally
        {
            Free(result);
        }
    }

    private static FileIdentity GetIdentity(SafeFileHandle handle)
    {
        bool added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            int descriptor = checked((int)handle.DangerousGetHandle());
            if (FStat(descriptor, out DarwinStat status) != 0)
            {
                throw NativeIOException("The bound secret object could not be inspected.");
            }
            return FileIdentity.From(status);
        }
        finally
        {
            if (added) handle.DangerousRelease();
        }
    }

    private static FileIdentity GetPathIdentity(string path)
    {
        if (LStat(path, out DarwinStat status) != 0)
        {
            throw NativeIOException("The canonical secret parent path could not be revalidated.");
        }
        return FileIdentity.From(status);
    }

    private static FileIdentity GetIdentityAt(SafeFileHandle parent, string name)
    {
        if (!TryGetIdentityAt(parent, name, out FileIdentity identity))
        {
            throw NativeIOException("The bound secret namespace entry is missing.");
        }
        return identity;
    }

    private static bool TryGetIdentityAt(
        SafeFileHandle parent,
        string name,
        out FileIdentity identity)
    {
        bool added = false;
        try
        {
            parent.DangerousAddRef(ref added);
            int descriptor = checked((int)parent.DangerousGetHandle());
            if (FStatAt(descriptor, name, out DarwinStat status, AtSymlinkNoFollow) != 0)
            {
                int error = Marshal.GetLastPInvokeError();
                if (error == 2) // ENOENT
                {
                    identity = default;
                    return false;
                }
                throw new IOException(
                    "The bound secret namespace entry could not be inspected.",
                    new Win32Exception(error));
            }
            identity = FileIdentity.From(status);
            return true;
        }
        finally
        {
            if (added) parent.DangerousRelease();
        }
    }

    private static void RenameAtExclusive(
        SafeFileHandle parent,
        string source,
        string destination)
    {
        bool added = false;
        try
        {
            parent.DangerousAddRef(ref added);
            int descriptor = checked((int)parent.DangerousGetHandle());
            if (RenameAtXNp(
                    descriptor,
                    source,
                    descriptor,
                    destination,
                    RenameExclusive) != 0)
            {
                throw NativeIOException("The secret envelope could not be published with no-replace semantics.");
            }
        }
        finally
        {
            if (added) parent.DangerousRelease();
        }
    }

    private static void UnlinkAt(SafeFileHandle parent, string name)
    {
        bool added = false;
        try
        {
            parent.DangerousAddRef(ref added);
            int descriptor = checked((int)parent.DangerousGetHandle());
            if (UnlinkAtNative(descriptor, name, 0) != 0)
            {
                throw NativeIOException("The identity-bound secret staging entry could not be unlinked.");
            }
        }
        finally
        {
            if (added) parent.DangerousRelease();
        }
    }

    private static IOException NativeIOException(string message) =>
        new(message, new Win32Exception(Marshal.GetLastPInvokeError()));

    [StructLayout(LayoutKind.Sequential)]
    private struct DarwinTimespec
    {
        internal long Seconds;
        internal long Nanoseconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DarwinStat
    {
        internal int Device;
        internal ushort Mode;
        internal ushort LinkCount;
        internal ulong Inode;
        internal uint Uid;
        internal uint Gid;
        internal int Rdev;
        internal DarwinTimespec AccessTime;
        internal DarwinTimespec ModificationTime;
        internal DarwinTimespec ChangeTime;
        internal DarwinTimespec BirthTime;
        internal long Size;
        internal long Blocks;
        internal int BlockSize;
        internal uint Flags;
        internal uint Generation;
        internal int Spare;
        internal long Reserved0;
        internal long Reserved1;
    }

    [StructLayout(LayoutKind.Explicit, Size = 2168)]
    private struct DarwinStatFs
    {
        [FieldOffset(64)] internal uint Flags;
    }

    private readonly record struct FileIdentity(
        int Device,
        ulong Inode,
        ushort LinkCount,
        ushort Mode,
        uint Uid,
        long Size,
        long ModificationSeconds,
        long ModificationNanoseconds,
        long ChangeSeconds,
        long ChangeNanoseconds,
        uint Generation)
    {
        internal static FileIdentity From(DarwinStat status) =>
            new(
                status.Device,
                status.Inode,
                status.LinkCount,
                status.Mode,
                status.Uid,
                status.Size,
                status.ModificationTime.Seconds,
                status.ModificationTime.Nanoseconds,
                status.ChangeTime.Seconds,
                status.ChangeTime.Nanoseconds,
                status.Generation);

        internal bool SameObject(FileIdentity other) =>
            Device == other.Device && Inode == other.Inode;

        internal bool SameReadSnapshot(FileIdentity other) =>
            SameObject(other)
            && LinkCount == other.LinkCount
            && Mode == other.Mode
            && Uid == other.Uid
            && Size == other.Size
            && ModificationSeconds == other.ModificationSeconds
            && ModificationNanoseconds == other.ModificationNanoseconds
            && ChangeSeconds == other.ChangeSeconds
            && ChangeNanoseconds == other.ChangeNanoseconds
            && Generation == other.Generation;
    }

    [DllImport("libSystem.B.dylib", EntryPoint = "acl_get_fd_np", SetLastError = true)]
    private static extern nint AclGetFdNp(int descriptor, int type);

    [DllImport("libSystem.B.dylib", EntryPoint = "acl_free", SetLastError = true)]
    private static extern int AclFree(nint acl);

    [DllImport("libSystem.B.dylib", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string path, int flags);

    [DllImport("libSystem.B.dylib", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    [DllImport("libSystem.B.dylib", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int descriptor);

    [DllImport("libSystem.B.dylib", EntryPoint = "mkostempsat_np", SetLastError = true)]
    private static extern int MkOStempsAtNp(
        int directoryDescriptor,
        nint template,
        int suffixLength,
        int openFlags);

    [DllImport("libSystem.B.dylib", EntryPoint = "fstat", SetLastError = true)]
    private static extern int FStat(int descriptor, out DarwinStat status);

    [DllImport("libSystem.B.dylib", EntryPoint = "fstatfs", SetLastError = true)]
    private static extern int FStatFs(int descriptor, out DarwinStatFs status);

    [DllImport("libSystem.B.dylib", EntryPoint = "fstatfs$INODE64", SetLastError = true)]
    private static extern int FStatFsX64(int descriptor, out DarwinStatFs status);

    [DllImport("libSystem.B.dylib", EntryPoint = "fstatat", SetLastError = true)]
    private static extern int FStatAt(
        int directoryDescriptor,
        string path,
        out DarwinStat status,
        int flags);

    [DllImport("libSystem.B.dylib", EntryPoint = "lstat", SetLastError = true)]
    private static extern int LStat(string path, out DarwinStat status);

    [DllImport("libSystem.B.dylib", EntryPoint = "fchmod", SetLastError = true)]
    private static extern int FChmod(int descriptor, uint mode);

    [DllImport("libSystem.B.dylib", EntryPoint = "renameatx_np", SetLastError = true)]
    private static extern int RenameAtXNp(
        int sourceDirectoryDescriptor,
        string source,
        int destinationDirectoryDescriptor,
        string destination,
        uint flags);

    [DllImport("libSystem.B.dylib", EntryPoint = "unlinkat", SetLastError = true)]
    private static extern int UnlinkAtNative(int directoryDescriptor, string path, int flags);

    [DllImport("libSystem.B.dylib", EntryPoint = "realpath", SetLastError = true)]
    private static extern nint RealPath(string path, nint resolvedPath);

    [DllImport("libSystem.B.dylib", EntryPoint = "free")]
    private static extern void Free(nint pointer);
}
