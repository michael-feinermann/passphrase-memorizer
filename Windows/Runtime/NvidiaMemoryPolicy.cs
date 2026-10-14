using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PassphraseMemorizer.Crypto;

namespace PassphraseMemorizer.Runtime;

// Runs in the trusted parent, before launching CUDA or sending a prompt.
// Only the verified runner's exact executable association is configured;
// no global profile or unrelated NVIDIA setting is changed.
public static unsafe class NvidiaMemoryPolicy
{
    // The SDK does not publish this hidden setting. Its mapping comes from the
    // original Profile Inspector implementation, independently checked
    // against NVIDIA App's own CudaSysmemFallbackPolicyValue enum (1=disallow).
    // https://github.com/Orbmu2k/nvidiaProfileInspector/blob/master/nvidiaProfileInspector/CustomSettingNames.xml
    // https://nvidia.custhelp.com/app/answers/detail/a_id/5490
    private const uint SysmemFallbackSetting = 0x10ECECC9;
    private const uint PreferNoSysmemFallback = 1;
    private const int SettingNotFound = -160, ProfileNotFound = -163, ExecutableNotFound = -166;
    private static readonly object Gate = new();

    public static void EnsureForExecutable(string executable)
    {
        lock (Gate)
        {
            string path = Path.GetFullPath(executable);
            SafeFiles.RejectReparsePath(path);
            if (!File.Exists(path) || !Path.GetFileName(path).Equals("WindowsMnemonicRunner.exe", StringComparison.OrdinalIgnoreCase))
                throw new IOException("The GPU memory policy requires the verified native runner.");
            string profileName = "Passphrase Memorizer GPU " + Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(path.ToUpperInvariant())))[..16];
            using var api = new DrsApi();
            using (var session = api.Session())
            {
                nint profile = FindExactApplication(api, session.Handle, path);
                if (profile == 0)
                {
                    int found = api.FindProfile(session.Handle, profileName, out profile);
                    if (found == ProfileNotFound)
                    {
                        DrsProfile description = default;
                        description.Version = Version<DrsProfile>(1);
                        PutString(description.Name, profileName);
                        Require(api.CreateProfile(session.Handle, ref description, out profile));
                    }
                    else Require(found);
                    // Never attach a runner to a profile containing another
                    // program: even a profile-name collision fails closed.
                    DrsProfile existing = default; existing.Version = Version<DrsProfile>(1);
                    Require(api.GetProfileInfo(session.Handle, profile, ref existing));
                    if (existing.IsPredefined != 0 || existing.Applications != 0)
                        throw new IOException("The dedicated GPU driver profile is already associated with another program.");
                    DrsApplication application = default; application.Version = Version<DrsApplication>(1);
                    PutString(application.Name, path);
                    PutString(application.FriendlyName, "Passphrase Memorizer GPU runner");
                    Require(api.CreateApplication(session.Handle, profile, ref application));
                }
                // A matched executable must have a dedicated, user-defined
                // profile. Changing a shared profile could affect other apps.
                DrsProfile info = default; info.Version = Version<DrsProfile>(1);
                Require(api.GetProfileInfo(session.Handle, profile, ref info));
                if (info.IsPredefined != 0 || info.Applications != 1)
                    throw new IOException("The GPU runner does not have a dedicated driver profile.");
                var setting = NewSetting();
                int read = api.ReadSetting(session.Handle, profile, ref setting);
                if (read != 0 && read != SettingNotFound) Require(read);
                bool alreadySet = read == 0 && SettingMatches(setting);
                if (!alreadySet)
                {
                    setting = NewSetting(); setting.Id = SysmemFallbackSetting;
                    setting.Current[0] = (byte)PreferNoSysmemFallback;
                    Require(api.SetSetting(session.Handle, profile, ref setting, 0, 0));
                    Require(api.SaveSettings(session.Handle));
                }
            }
            // Read a fresh driver session after saving, not an in-memory DRS
            // snapshot. Missing/rejected/changed policy stops GPU execution.
            using var verify = api.Session();
            nint verifiedProfile = FindExactApplication(api, verify.Handle, path);
            if (verifiedProfile == 0) throw new IOException("The GPU driver did not retain the runner's memory policy.");
            var confirmed = NewSetting();
            Require(api.ReadSetting(verify.Handle, verifiedProfile, ref confirmed));
            if (!SettingMatches(confirmed)) throw new IOException("The GPU driver did not confirm Prefer No Sysmem Fallback.");
        }
    }

    private static nint FindExactApplication(DrsApi api, nint session, string path)
    {
        DrsApplication application = default; application.Version = Version<DrsApplication>(1);
        int result = api.FindApplication(session, path, out nint profile, ref application);
        if (result == ExecutableNotFound) return 0;
        Require(result);
        string stored = new(application.Name);
        if (!Path.IsPathFullyQualified(stored) || !Path.GetFullPath(stored).Equals(path, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The GPU driver's executable association is not the exact runner path.");
        return profile;
    }
    private static DrsSetting NewSetting() => new() { Version = Version<DrsSetting>(1) };
    private static bool SettingMatches(DrsSetting setting) => setting.Id == SysmemFallbackSetting && setting.Type == 0
        && setting.Location == 0 && setting.CurrentIsPredefined == 0 && setting.Current[0] == PreferNoSysmemFallback
        && setting.Current[1] == 0 && setting.Current[2] == 0 && setting.Current[3] == 0;
    private static uint Version<T>(uint version) where T : unmanaged => checked((uint)sizeof(T)) | (version << 16);
    private static void PutString(char* target, string value)
    {
        if (value.Length >= 2048 || value.Contains('\0')) throw new IOException("The GPU profile path is too long.");
        for (int i = 0; i < value.Length; i++) target[i] = value[i]; target[value.Length] = '\0';
    }
    private static void Require(int status)
    {
        if (status != 0) throw new IOException("NVIDIA could not enforce the required per-program GPU memory policy (status " + status.ToString(System.Globalization.CultureInfo.InvariantCulture) + ").");
    }

    // ABI and public interface IDs: NVIDIA/nvapi nvapi.h/nvapi_interface.h.
    // The extended GetSetting/SetSetting interfaces are required for hidden
    // settings, from Profile Inspector's own NvapiDrsWrapper.cs. No public ABI
    // fallback is called with the extended arguments. Driver incompatibility
    // fails closed, rather than enabling inference with an unverified policy.
    [StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Unicode)]
    private struct DrsSetting
    {
        internal uint Version;
        internal fixed char Name[2048];
        internal uint Id, Type, Location, CurrentIsPredefined, PredefinedIsValid;
        internal fixed byte Predefined[4100], Current[4100];
    }
    [StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Unicode)]
    private struct DrsApplication
    {
        internal uint Version, IsPredefined;
        internal fixed char Name[2048], FriendlyName[2048], Launcher[2048];
    }
    [StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Unicode)]
    private struct DrsProfile
    {
        internal uint Version;
        internal fixed char Name[2048];
        internal uint GpuSupport, IsPredefined, Applications, Settings;
    }
    private sealed class DrsApi : IDisposable
    {
        private nint module;
        private bool initialized;
        private readonly FileStream driverLease;
        private readonly NoArguments unload;
        private readonly CreateSession createSession;
        private readonly SessionAction destroySession, loadSettings;
        internal readonly SessionAction SaveSettings;
        internal readonly FindProfileByName FindProfile;
        internal readonly ProfileCreate CreateProfile;
        internal readonly ProfileInfo GetProfileInfo;
        internal readonly ApplicationFind FindApplication;
        internal readonly ApplicationCreate CreateApplication;
        private readonly SettingGet getSetting;
        internal readonly SettingSet SetSetting;
        internal DrsApi()
        {
            if (sizeof(DrsSetting) != 12320 || sizeof(DrsApplication) != 12296 || sizeof(DrsProfile) != 4116)
                throw new IOException("Unsupported NVIDIA driver profile ABI.");
            string driver = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvapi64.dll");
            driverLease = SafeFiles.OpenReadLease(driver);
            try
            {
                VerifyDriverSignature(driver);
                module = LoadLibraryExW(driver, 0, 0x800); // Absolute verified System32 path only.
                if (module == 0) throw new IOException("The NVIDIA driver profile API is unavailable.");
                var loadedPath = new StringBuilder(32768);
                uint pathLength = GetModuleFileNameW(module, loadedPath, (uint)loadedPath.Capacity);
                if (pathLength == 0 || pathLength >= loadedPath.Capacity ||
                    !SafeFiles.CanonicalPath(loadedPath.ToString()).Equals(SafeFiles.CanonicalPath(driver), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The loaded NVIDIA driver profile API differs from the verified System32 module.");
                var query = Marshal.GetDelegateForFunctionPointer<QueryInterface>(NativeLibrary.GetExport(module, "nvapi_QueryInterface"));
                T Get<T>(uint id) where T : Delegate
                {
                    nint pointer = query(id); if (pointer == 0) throw new IOException("The required NVIDIA driver profile API is unavailable.");
                    return Marshal.GetDelegateForFunctionPointer<T>(pointer);
                }
                unload = Get<NoArguments>(0xD22BDD7E);
                Require(Get<NoArguments>(0x0150E828)()); initialized = true;
                createSession = Get<CreateSession>(0x0694D52E);
                destroySession = Get<SessionAction>(0xDAD9CFF8); loadSettings = Get<SessionAction>(0x375DBD6B);
                SaveSettings = Get<SessionAction>(0xFCBC7E14);
                FindProfile = Get<FindProfileByName>(0x7E4A9A0B); CreateProfile = Get<ProfileCreate>(0xCC176068);
                GetProfileInfo = Get<ProfileInfo>(0x61CD6FD6);
                FindApplication = Get<ApplicationFind>(0xEEE566B2); CreateApplication = Get<ApplicationCreate>(0x4347A9DE);
                getSetting = Get<SettingGet>(0xEA99498D); SetSetting = Get<SettingSet>(0x8A2CF5F5);
            }
            catch { Dispose(); throw; }
        }
        internal DrsSession Session()
        {
            Require(createSession(out nint handle));
            try { Require(loadSettings(handle)); return new(this, handle); }
            catch { destroySession(handle); throw; }
        }
        internal int ReadSetting(nint session, nint profile, ref DrsSetting setting)
        {
            uint auxiliary = 0;
            return getSetting(session, profile, SysmemFallbackSetting, ref setting, ref auxiliary);
        }
        public void Dispose()
        {
            if (initialized) { initialized = false; unload(); }
            if (module != 0) { FreeLibrary(module); module = 0; }
            driverLease.Dispose();
        }
        internal sealed class DrsSession(DrsApi api, nint handle) : IDisposable
        {
            internal nint Handle { get; private set; } = handle;
            public void Dispose() { if (Handle != 0) { api.destroySession(Handle); Handle = 0; } }
        }
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint QueryInterface(uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NoArguments();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CreateSession(out nint session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SessionAction(nint session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)] private delegate int FindProfileByName(nint session, string name, out nint profile);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ProfileCreate(nint session, ref DrsProfile info, out nint profile);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ProfileInfo(nint session, nint profile, ref DrsProfile info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)] private delegate int ApplicationFind(nint session, string executable, out nint profile, ref DrsApplication application);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ApplicationCreate(nint session, nint profile, ref DrsApplication application);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SettingGet(nint session, nint profile, uint id, ref DrsSetting setting, ref uint auxiliary);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SettingSet(nint session, nint profile, ref DrsSetting setting, uint flags, uint reserved);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint LoadLibraryExW(string file, nint reserved, uint flags);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetModuleFileNameW(nint module, StringBuilder path, uint capacity);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FreeLibrary(nint module);

    private static void VerifyDriverSignature(string path)
    {
        nint name = Marshal.StringToHGlobalUni(path);
        nint file = 0;
        try
        {
            var information = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = name };
            file = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>()); Marshal.StructureToPtr(information, file, false);
            var trust = new TrustData
            {
                Size = (uint)Marshal.SizeOf<TrustData>(), UserInterface = 2,
                UnionChoice = 1, File = file, StateAction = 1,
                ProviderFlags = 0x1000 // Cached trust only; never fetch URLs.
            };
            Guid action = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
            int status;
            try { status = WinVerifyTrust(-1, ref action, ref trust); }
            finally { trust.StateAction = 2; WinVerifyTrust(-1, ref action, ref trust); }
            if (status != 0) throw new IOException("The System32 NVIDIA driver profile API does not have a valid Authenticode trust chain.");
            // WinVerifyTrust may use Microsoft's WHQL catalog. Independently
            // require the embedded leaf's exact NVIDIA publisher identity.
            // X509CertificateLoader accepts certificates, not signed PE files.
            // Use the legacy extraction API only on the already authenticated,
            // leased System32 PE, then parse its DER with the modern loader.
#pragma warning disable SYSLIB0057
            using var embedded = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            using var signer = X509CertificateLoader.LoadCertificate(embedded.Export(X509ContentType.Cert));
            if (!signer.GetNameInfo(X509NameType.SimpleName, false).Equals("NVIDIA Corporation", StringComparison.Ordinal))
                throw new IOException("The System32 driver profile API was not embedded-signed by NVIDIA Corporation.");
        }
        finally { if (file != 0) Marshal.FreeHGlobal(file); Marshal.FreeHGlobal(name); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct TrustFile { internal uint Size; internal nint Path, Handle, Subject; }
    [StructLayout(LayoutKind.Sequential)] private struct TrustData
    {
        internal uint Size;
        internal nint Callback, Client;
        internal uint UserInterface, RevocationChecks, UnionChoice;
        internal nint File;
        internal uint StateAction;
        internal nint StateData, Url;
        internal uint ProviderFlags, UserInterfaceContext;
        internal nint SignatureSettings;
    }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern int WinVerifyTrust(nint window, ref Guid action, ref TrustData data);
}
