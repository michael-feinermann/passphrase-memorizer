using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml.Linq;
using KalynaArchiver.Services;
using KalynaArchiver.Signing;
using KeepVaultMac.Packaging;

return await RunAsync(args).ConfigureAwait(false);

static async Task<int> RunAsync(string[] args)
{
    try
    {
        if (args.Length == 0)
        {
            return Usage("A command is required.");
        }

        (Dictionary<string, string> options, List<string> targets) = ParseOptions(args[1..]);
        if (string.Equals(args[0], "release-keygen", StringComparison.Ordinal))
        {
            RequireNoTargets(targets, args[0]);
            RequireOnlyOptions(options, "directory", "reference-library");
            string temporaryKeychainDirectory = PrepareMacTemporaryKeychainDirectory();
            try
            {
                return ReleaseKeyGenerator.Generate(Require(options, "directory"), Require(options, "reference-library"));
            }
            finally
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                RequireDirectoryEmpty(temporaryKeychainDirectory, "key-generation temporary keychain directory");
            }
        }
        if (string.Equals(args[0], "wrap-mldsa-key", StringComparison.OrdinalIgnoreCase))
        {
            RequireNoTargets(targets, args[0]);
            RequireOnlyOptions(
                options,
                "mldsa-private-key",
                "mldsa-private-key-encrypted",
                "mldsa-wrapping-key-keychain-service",
                "mldsa-wrapping-key-keychain-account");
            return WrapMldsaKey(options);
        }

        if (string.Equals(args[0], "wrap-pfx-password", StringComparison.OrdinalIgnoreCase))
        {
            RequireNoTargets(targets, args[0]);
            RequireOnlyOptions(
                options,
                "pfx-password-encrypted",
                "pfx-password-keychain-service",
                "pfx-password-keychain-account",
                "pfx-wrapping-key-keychain-service",
                "pfx-wrapping-key-keychain-account");
            return WrapPfxPassword(options);
        }

        if (targets.Count == 0)
        {
            return Usage("At least one --target is required.");
        }

        if (string.Equals(args[0], "hash", StringComparison.OrdinalIgnoreCase))
        {
            RequireOnlyOptions(options);
            foreach (string targetValue in targets)
            {
                string target = RequireRegularFile(targetValue, "public hashing target");
                (byte[] sha256, byte[] sha3, byte[] skein) = Fingerprint(target);
                try { PrintPublicHashes(target, sha256, sha3, skein); }
                finally
                {
                    CryptographicOperations.ZeroMemory(sha256);
                    CryptographicOperations.ZeroMemory(sha3);
                    CryptographicOperations.ZeroMemory(skein);
                }
            }
            return 0;
        }

        if (string.Equals(args[0], "sign", StringComparison.OrdinalIgnoreCase))
        {
            RequireOnlyOptions(
                options,
                "pfx",
                "pfx-password-encrypted",
                "pfx-wrapping-key-keychain-service",
                "pfx-wrapping-key-keychain-account",
                "pfx-wrapping-key-file",
                "mldsa-private-key-encrypted",
                "mldsa-wrapping-key-keychain-service",
                "mldsa-wrapping-key-keychain-account",
                "mldsa-wrapping-key-file",
                "mldsa-public-key",
                "reference-library",
                "policy",
                "launcher-pins");
            return await SignAsync(options, targets).ConfigureAwait(false);
        }

        if (string.Equals(args[0], "verify", StringComparison.OrdinalIgnoreCase))
        {
            RequireOnlyOptions(
                options,
                "mldsa-public-key",
                "policy",
                "payload-root",
                "signature-root");
            return Verify(options, targets);
        }

        return Usage($"Unknown command: {args[0]}");
    }
    catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException or CryptographicException
        or InvalidOperationException or PlatformNotSupportedException or UnauthorizedAccessException
        or AggregateException)
    {
        await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
        return 2;
    }
}

static async Task<int> SignAsync(Dictionary<string, string> options, IReadOnlyList<string> targets)
{
    string pfxPath = Require(options, "pfx");
    string mldsaEnvelopePath = Require(options, "mldsa-private-key-encrypted");
    string pfxEnvelopePath = Require(options, "pfx-password-encrypted");
    string publicKeyPath = Require(options, "mldsa-public-key");
    string referencePath = Require(options, "reference-library");
    string policyPath = Require(options, "policy");
    string launcherPinsPath = Require(options, "launcher-pins");

    RequireDistinctWrappingKeyIdentities(options);

    LockedSensitiveBuffer? mldsaWrappingKey = null;
    LockedSensitiveBuffer? pfxWrappingKey = null;
    LockedSensitiveBuffer? privateKey = null;
    LockedSensitiveBuffer? pfxPasswordCharacters = null;
    LockedSensitiveBuffer? pfxBytes = null;
    byte[]? publicKey = null;
    string? macTemporaryKeychainDirectory = null;
    X509Certificate2? certificate = null;
    Exception? operationFailure = null;
    try
    {
        // Each half uses a distinct role-specific Keychain item or an
        // explicitly selected private file on the offline signing volume.
        // Equality is rejected as a final invariant in case two independently
        // named items were accidentally populated with the same bytes.
        mldsaWrappingKey = ReadMldsaWrappingKey(options);
        pfxWrappingKey = ReadPfxWrappingKey(options);
        if (CryptographicOperations.FixedTimeEquals(
                mldsaWrappingKey.Bytes,
                pfxWrappingKey.Bytes))
        {
            throw new CryptographicException(
                "The RSA and ML-DSA wrapping keys are equal; independent role keys are required.");
        }

        privateKey = HybridKeyEnvelope.ReadMldsaPrivateKey(
            mldsaEnvelopePath,
            mldsaWrappingKey.Bytes);
        pfxPasswordCharacters = ReadPfxPasswordCharacters(
            pfxEnvelopePath,
            pfxWrappingKey.Bytes);
        publicKey = ReadExactFile(publicKeyPath, Mldsa87.PublicKeyBytes, "ML-DSA-87 public key");
        macTemporaryKeychainDirectory = OperatingSystem.IsMacOS()
            ? PrepareMacTemporaryKeychainDirectory()
            : null;
        X509KeyStorageFlags storageFlags = OperatingSystem.IsMacOS()
            ? X509KeyStorageFlags.DefaultKeySet
            : X509KeyStorageFlags.EphemeralKeySet;
        const int maximumRsaPfxBytes = 1024 * 1024;
        pfxBytes = MacBoundSecretFile.ReadPrivateBytes(
            pfxPath,
            minimumBytes: 1,
            maximumBytes: maximumRsaPfxBytes,
            description: "RSA PFX");
        certificate = X509CertificateLoader.LoadPkcs12(
            pfxBytes.Bytes,
            MemoryMarshal.Cast<byte, char>(pfxPasswordCharacters.Bytes.AsSpan()),
            storageFlags,
            Pkcs12LoaderLimits.Defaults);
        pfxBytes.Dispose();
        pfxBytes = null;
        pfxPasswordCharacters.Dispose();
        pfxPasswordCharacters = null;
        HybridSignaturePolicy policy = LoadPolicy(policyPath, publicKey);
        if (!policy.MatchesRsaCertificate(certificate, out string pinError))
        {
            throw new CryptographicException(pinError);
        }

        byte[] derivedPublicKey = Mldsa87.DerivePublicKey(privateKey.Bytes);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(derivedPublicKey, publicKey))
            {
                throw new CryptographicException("The ML-DSA-87 private key does not match the pinned public key.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derivedPublicKey);
        }

        using var reference = new Mldsa87Reference(referencePath);
        CrossCheckMldsa(reference, privateKey.Bytes, publicKey);

        foreach (string targetValue in targets)
        {
            string target = RequireRegularFile(targetValue, "hybrid-signing target");
            (byte[] sha256, byte[] sha3, byte[] skein) = Fingerprint(target);
            try
            {
                WriteTextAtomically(target + ".sha3", Convert.ToHexString(sha3) + "\n");
                WriteTextAtomically(target + ".skein", Convert.ToHexString(skein) + "\n");
                PrintPublicHashes(target, sha256, sha3, skein);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(sha256);
                CryptographicOperations.ZeroMemory(sha3);
                CryptographicOperations.ZeroMemory(skein);
            }

            foreach (string signedPath in new[] { target, target + ".sha3", target + ".skein" })
            {
                using HybridSignatureCreationResult creation = await HybridSignatureService.CreateAsync(
                    signedPath,
                    signedPath + HybridSignatureService.SidecarExtension,
                    certificate,
                    privateKey.Bytes,
                    publicKey).ConfigureAwait(false);
                if (!reference.Verify(creation.Payload, creation.MldsaSignature, publicKey))
                {
                    throw new CryptographicException($"The pinned ML-DSA reference implementation rejected {Path.GetFileName(signedPath)}.");
                }
                HybridSignatureVerificationResult verification = HybridSignatureService.VerifyFile(
                    signedPath,
                    signedPath + HybridSignatureService.SidecarExtension,
                    policy);
                if (!verification.IsTrusted)
                {
                    throw new CryptographicException($"Hybrid signature verification failed for {Path.GetFileName(signedPath)}: {verification.Message}");
                }
            }

            // Signing always writes the sidecars adjacent to the payload; the
            // relocation into Contents/Resources happens afterwards in the
            // packaging step.
            VerifyManifests(target, target);
            Console.WriteLine($"hybrid_signed={target}");
        }

        WriteLauncherPins(launcherPinsPath, certificate.RawData, publicKey);
        Console.WriteLine($"launcher_pins={Path.GetFullPath(launcherPinsPath)}");
    }
    catch (Exception ex)
    {
        operationFailure = ex;
    }

    LockedBufferTransfer.CompleteVoid(
        operationFailure,
        "Hybrid signing failed during secure cleanup.",
        () =>
        {
            if (publicKey is not null)
            {
                CryptographicOperations.ZeroMemory(publicKey);
            }
            SecureMemory.ZeroAndDisposeAll(
                privateKey,
                pfxPasswordCharacters,
                pfxBytes,
                mldsaWrappingKey,
                pfxWrappingKey);
        },
        () => certificate?.Dispose(),
        () =>
        {
            if (macTemporaryKeychainDirectory is not null)
            {
                // Apple private keys require a temporary on-disk keychain.
                // Collect after the explicit certificate dispose, then verify
                // that .NET removed every Security.framework residue.
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        },
        () =>
        {
            if (macTemporaryKeychainDirectory is not null)
            {
                RequireDirectoryEmpty(
                    macTemporaryKeychainDirectory,
                    "temporary macOS keychain directory");
            }
        });
    return 0;
}

static string PrepareMacTemporaryKeychainDirectory()
{
    string configured = Environment.GetEnvironmentVariable("KEEPVAULT_KEYCHAIN_TEMP_ROOT")
        ?? throw new CryptographicException(
            "KEEPVAULT_KEYCHAIN_TEMP_ROOT is required for isolated macOS PFX loading. Use the release build script.");
    string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured));
    var directory = new DirectoryInfo(fullPath);
    if (!directory.Exists
        || directory.LinkTarget is not null
        || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
    {
        throw new CryptographicException("The temporary macOS keychain directory is missing or is a symbolic link.");
    }

    UnixFileMode mode = File.GetUnixFileMode(fullPath);
    UnixFileMode forbidden = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
    if ((mode & forbidden) != 0)
    {
        throw new CryptographicException("The temporary macOS keychain directory must be private to the release user.");
    }

    string runtimeTemporaryPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
    if (!string.Equals(runtimeTemporaryPath, fullPath, StringComparison.Ordinal))
    {
        throw new CryptographicException("TMPDIR does not resolve to KEEPVAULT_KEYCHAIN_TEMP_ROOT.");
    }

    RequireDirectoryEmpty(fullPath, "temporary macOS keychain directory");
    return fullPath;
}

static void RequireDirectoryEmpty(string path, string description)
{
    string? unexpected = Directory.EnumerateFileSystemEntries(path).FirstOrDefault();
    if (unexpected is not null)
    {
        throw new CryptographicException(
            $"The {description} contains a residual object: {Path.GetFileName(unexpected)}");
    }
}

static int Verify(Dictionary<string, string> options, IReadOnlyList<string> targets)
{
    string publicKeyPath = Require(options, "mldsa-public-key");
    byte[] publicKey = ReadExactFile(publicKeyPath, Mldsa87.PublicKeyBytes, "ML-DSA-87 public key");
    try
    {
        HybridSignaturePolicy policy = LoadPolicy(Require(options, "policy"), publicKey);
        options.TryGetValue("payload-root", out string? payloadRoot);
        options.TryGetValue("signature-root", out string? signatureRoot);
        if (payloadRoot is null != (signatureRoot is null))
        {
            throw new ArgumentException("--payload-root and --signature-root must be supplied together.");
        }

        foreach (string targetValue in targets)
        {
            string target = RequireRegularFile(targetValue, "hybrid-verification target");
            string sidecarBase = ResolveSidecarBase(target, payloadRoot, signatureRoot);
            VerifyManifests(target, sidecarBase);
            (string Payload, string Sidecar)[] signedPairs =
            [
                (target, sidecarBase + HybridSignatureService.SidecarExtension),
                (sidecarBase + ".sha3", sidecarBase + ".sha3" + HybridSignatureService.SidecarExtension),
                (sidecarBase + ".skein", sidecarBase + ".skein" + HybridSignatureService.SidecarExtension),
            ];
            foreach ((string signedPath, string sidecarPath) in signedPairs)
            {
                HybridSignatureVerificationResult result = HybridSignatureService.VerifyFile(
                    signedPath,
                    sidecarPath,
                    policy);
                if (!result.IsTrusted)
                {
                    throw new CryptographicException($"Hybrid signature verification failed for {Path.GetFileName(signedPath)}: {result.Message}");
                }
            }
            Console.WriteLine($"hybrid_verified={target}");
        }
        return 0;
    }
    finally
    {
        CryptographicOperations.ZeroMemory(publicKey);
    }
}

static void CrossCheckMldsa(Mldsa87Reference reference, ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> publicKey)
{
    byte[] message = SHA512.HashData("Keep Vault macOS hybrid signing reference cross-check v1"u8);
    byte[]? managedSignature = null;
    byte[]? referenceSignature = null;
    try
    {
        managedSignature = Mldsa87.Sign(message, privateKey);
        referenceSignature = reference.Sign(message, privateKey);
        if (!reference.Verify(message, managedSignature, publicKey)
            || !Mldsa87.Verify(message, referenceSignature, publicKey))
        {
            throw new CryptographicException("Managed and pinned reference ML-DSA-87 implementations do not agree.");
        }
    }
    finally
    {
        CryptographicOperations.ZeroMemory(message);
        if (managedSignature is not null) CryptographicOperations.ZeroMemory(managedSignature);
        if (referenceSignature is not null) CryptographicOperations.ZeroMemory(referenceSignature);
    }
}

static (byte[] Sha256, byte[] Sha3, byte[] Skein) Fingerprint(string path)
{
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
    return HybridSignatureService.Fingerprint(stream);
}

static void PrintPublicHashes(string target, byte[] sha256, byte[] sha3, byte[] skein)
{
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
    {
        ["file"] = Path.GetFileName(target),
        ["sha256"] = Convert.ToHexString(sha256).ToLowerInvariant(),
        ["sha3-512"] = Convert.ToHexString(sha3).ToLowerInvariant(),
        ["skein-1024-1024"] = Convert.ToHexString(skein).ToLowerInvariant(),
    }));
}

/// <summary>
/// Maps a payload file to the base path of its detached sidecars. Inside a
/// signed app bundle the sidecars are relocated out of Contents/MacOS, which
/// Apple reserves for Mach-O executables, into Contents/Resources.
/// </summary>
static string ResolveSidecarBase(string target, string? payloadRoot, string? signatureRoot)
{
    if (payloadRoot is null || signatureRoot is null)
    {
        return target;
    }

    string fullPayloadRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(payloadRoot));
    string fullTarget = Path.GetFullPath(target);
    if (!fullTarget.StartsWith(fullPayloadRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
    {
        throw new CryptographicException(
            $"Hybrid-verification target lies outside the payload root: {target}");
    }

    string relative = fullTarget[(fullPayloadRoot.Length + 1)..];
    return Path.Combine(Path.GetFullPath(signatureRoot), relative);
}

static void VerifyManifests(string target, string sidecarBase)
{
    (byte[] sha256, byte[] sha3, byte[] skein) = Fingerprint(target);
    try
    {
        string expectedSha3 = ReadHexManifest(sidecarBase + ".sha3", 128);
        string expectedSkein = ReadHexManifest(sidecarBase + ".skein", 256);
        byte[] expectedSha3Bytes = Convert.FromHexString(expectedSha3);
        byte[] expectedSkeinBytes = Convert.FromHexString(expectedSkein);
        try
        {
            if (!(CryptographicOperations.FixedTimeEquals(sha3, expectedSha3Bytes)
                & CryptographicOperations.FixedTimeEquals(skein, expectedSkeinBytes)))
            {
                throw new CryptographicException($"Dual manifest mismatch: {Path.GetFileName(target)}");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedSha3Bytes);
            CryptographicOperations.ZeroMemory(expectedSkeinBytes);
        }
    }
    finally
    {
        CryptographicOperations.ZeroMemory(sha256);
        CryptographicOperations.ZeroMemory(sha3);
        CryptographicOperations.ZeroMemory(skein);
    }
}

static string ReadHexManifest(string path, int expectedCharacters)
{
    string text = File.ReadAllText(RequireRegularFile(path, "integrity manifest"), Encoding.ASCII);
    string normalized = new(text.Where(character => !char.IsWhiteSpace(character)).ToArray());
    if (normalized.Length != expectedCharacters || normalized.Any(character => !Uri.IsHexDigit(character)))
    {
        throw new InvalidDataException($"Invalid integrity manifest: {Path.GetFileName(path)}");
    }
    return normalized;
}

static HybridSignaturePolicy LoadPolicy(string path, byte[] publicKey)
{
    XDocument document = XDocument.Load(RequireRegularFile(path, "compiled signing policy"), LoadOptions.None);
    string Property(string name) => document.Descendants(name).Select(element => element.Value.Trim()).FirstOrDefault()
        ?? throw new InvalidDataException($"Signing policy property is missing: {name}");
    return new HybridSignaturePolicy(
        Property("KalynaExpectedSignerSha256"),
        Property("KalynaExpectedSignerSha3_512"),
        Property("KalynaExpectedSignerSkein1024"),
        Property("KalynaExpectedMldsa87Sha256"),
        Property("KalynaExpectedMldsa87Sha3_512"),
        Property("KalynaExpectedMldsa87Skein1024"),
        publicKey);
}

/// <summary>
/// Wraps a plaintext ML-DSA-87 private key into the encrypted envelope that
/// <c>sign</c> reads, and proves the round trip before writing anything.
/// </summary>
/// <remarks>
/// Encrypting lives in the same file as decrypting on purpose. A wrapping tool
/// that reimplements the format is a tool that can drift from it, and the way
/// that failure shows up is an envelope nobody can open -- with the plaintext
/// already deleted.
///
/// The plaintext key is never touched. Removing it stays a separate, deliberate
/// step for whoever knows whether a backup exists.
/// </remarks>
static int WrapMldsaKey(Dictionary<string, string> options)
{
    string envelopePath = Require(options, "mldsa-private-key-encrypted");
    string privateKeyPath = Require(options, "mldsa-private-key");
    LockedSensitiveBuffer? privateKey = null;
    LockedSensitiveBuffer? wrappingKey = null;
    Exception? operationFailure = null;
    try
    {
        privateKey = MacBoundSecretFile.ReadPrivateBytes(
            privateKeyPath,
            Mldsa87.PrivateKeyBytes,
            Mldsa87.PrivateKeyBytes,
            "ML-DSA-87 private key");
        wrappingKey = ReadMldsaWrappingKey(options);
        HybridKeyEnvelope.WriteMldsaPrivateKey(
            envelopePath,
            privateKey.Bytes,
            wrappingKey.Bytes);
    }
    catch (Exception ex)
    {
        operationFailure = ex;
    }

    LockedBufferTransfer.CompleteVoid(
        operationFailure,
        "ML-DSA-87 key wrapping failed during secure cleanup.",
        () => SecureMemory.ZeroAndDisposeAll(privateKey, wrappingKey));
    Console.WriteLine($"envelope={envelopePath}");
    Console.WriteLine("roundtrip_verified=yes");
    Console.WriteLine("source_left_in_place=yes");
    return 0;
}

/// <summary>
/// Wraps the RSA PFX password from its provisioning Keychain item into its own
/// purpose-specific v12 envelope under an RSA-only wrapping key.
/// </summary>
/// <remarks>
/// The password is read here, inside this process, straight from the existing
/// Keychain item and written encrypted. It is never passed on a command line,
/// where any process could read it out of the process list, and never printed.
/// </remarks>
static int WrapPfxPassword(Dictionary<string, string> options)
{
    string envelopePath = Require(options, "pfx-password-encrypted");
    string sourceService = Require(options, "pfx-password-keychain-service");
    string sourceAccount = Require(options, "pfx-password-keychain-account");
    LockedSensitiveBuffer? encoded = null;
    LockedSensitiveBuffer? wrappingKey = null;
    Exception? operationFailure = null;
    try
    {
        (encoded, int encodedLength) = ReadKeychainOutput(
            sourceService,
            sourceAccount,
            "RSA PFX provisioning password",
            HybridKeyEnvelope.MaximumPfxPasswordBytes);
        if (encodedLength == 0)
        {
            throw new CryptographicException("The PFX password in the Keychain is empty.");
        }
        try
        {
            var strictUtf8 = new UTF8Encoding(false, true);
            _ = strictUtf8.GetCharCount(encoded.Bytes.AsSpan(0, encodedLength));
            if (encoded.Bytes.AsSpan(0, encodedLength).IndexOf((byte)0) >= 0)
            {
                throw new CryptographicException("The PFX provisioning password contains a NUL character.");
            }
        }
        catch (DecoderFallbackException ex)
        {
            throw new CryptographicException("The PFX provisioning password is not valid UTF-8.", ex);
        }

        wrappingKey = ReadPfxWrappingKey(options);
        HybridKeyEnvelope.WritePfxPassword(
            envelopePath,
            encoded.Bytes.AsSpan(0, encodedLength),
            wrappingKey.Bytes);
    }
    catch (Exception ex)
    {
        operationFailure = ex;
    }

    LockedBufferTransfer.CompleteVoid(
        operationFailure,
        "RSA PFX-password wrapping failed during secure cleanup.",
        () => SecureMemory.ZeroAndDisposeAll(encoded, wrappingKey));
    Console.WriteLine($"envelope={envelopePath}");
    Console.WriteLine("roundtrip_verified=yes");
    Console.WriteLine("source_left_in_place=yes");
    return 0;
}

static LockedSensitiveBuffer ReadMldsaWrappingKey(Dictionary<string, string> options) =>
    options.TryGetValue("mldsa-wrapping-key-file", out string? path)
    ? UsbWrappingKey.Read(path)
    : ReadWrappingKey(
        options,
        "mldsa-wrapping-key-keychain-service",
        "mldsa-wrapping-key-keychain-account",
        "ML-DSA-87 wrapping key");

static LockedSensitiveBuffer ReadPfxWrappingKey(Dictionary<string, string> options) =>
    options.TryGetValue("pfx-wrapping-key-file", out string? path)
    ? UsbWrappingKey.Read(path)
    : ReadWrappingKey(
        options,
        "pfx-wrapping-key-keychain-service",
        "pfx-wrapping-key-keychain-account",
        "RSA PFX-password wrapping key");

static LockedSensitiveBuffer ReadWrappingKey(
    Dictionary<string, string> options,
    string serviceOption,
    string accountOption,
    string description)
{
    LockedSensitiveBuffer? encoded = null;
    LockedSensitiveBuffer? key = null;
    Exception? operationFailure = null;
    try
    {
        (encoded, int encodedLength) = ReadKeychainOutput(
            Require(options, serviceOption),
            Require(options, accountOption),
            description,
            maximumBytes: 128);
        key = LockedSensitiveBuffer.Create(HybridKeyEnvelope.WrappingKeyBytes);
        OperationStatus status = Base64.DecodeFromUtf8(
            encoded.Bytes.AsSpan(0, encodedLength),
            key.Bytes,
            out int consumed,
            out int written);
        if (status != OperationStatus.Done
            || consumed != encodedLength
            || written != HybridKeyEnvelope.WrappingKeyBytes)
        {
            throw new CryptographicException(
                $"The {description} in the Keychain is not one canonical 32-byte base64 value.");
        }

    }
    catch (Exception ex)
    {
        operationFailure = ex;
    }

    return LockedBufferTransfer.Complete(
        key,
        operationFailure,
        $"The {description} operation failed during secure cleanup.",
        [encoded],
        []);
}

static void RequireDistinctWrappingKeyIdentities(Dictionary<string, string> options)
{
    bool mldsaFile = options.ContainsKey("mldsa-wrapping-key-file");
    bool pfxFile = options.ContainsKey("pfx-wrapping-key-file");
    if (mldsaFile || pfxFile)
    {
        if (!mldsaFile || !pfxFile
            || options.ContainsKey("mldsa-wrapping-key-keychain-service")
            || options.ContainsKey("mldsa-wrapping-key-keychain-account")
            || options.ContainsKey("pfx-wrapping-key-keychain-service")
            || options.ContainsKey("pfx-wrapping-key-keychain-account")
            || string.Equals(
                Path.GetFullPath(Require(options, "mldsa-wrapping-key-file")),
                Path.GetFullPath(Require(options, "pfx-wrapping-key-file")),
                StringComparison.Ordinal))
        {
            throw new CryptographicException(
                "USB signing requires two distinct role-specific key files and no Keychain options.");
        }
        // SignAsync additionally rejects equal decoded keys with FixedTimeEquals.
        return;
    }

    string mldsaService = Require(options, "mldsa-wrapping-key-keychain-service");
    string mldsaAccount = Require(options, "mldsa-wrapping-key-keychain-account");
    string pfxService = Require(options, "pfx-wrapping-key-keychain-service");
    string pfxAccount = Require(options, "pfx-wrapping-key-keychain-account");
    if (string.Equals(mldsaService, pfxService, StringComparison.Ordinal)
        || string.Equals(mldsaAccount, pfxAccount, StringComparison.Ordinal))
    {
        throw new CryptographicException(
            "RSA and ML-DSA wrapping keys require different Keychain services and different accounts.");
    }
}

/// <summary>
/// Reads a bounded Keychain secret directly from the pipe into mlocked memory.
/// Service and account are ordinary identifiers; secret stdout never becomes a
/// managed string. The external Apple <c>security</c> process remains an opaque
/// platform boundary and is therefore invoked only by its absolute system path.
/// </summary>
static (LockedSensitiveBuffer Buffer, int Length) ReadKeychainOutput(
    string service,
    string account,
    string description,
    int maximumBytes)
{
    if (!OperatingSystem.IsMacOS())
    {
        throw new CryptographicException($"The {description} can only be read from the macOS Keychain.");
    }
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);

    var start = new ProcessStartInfo("/usr/bin/security")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    start.ArgumentList.Add("find-generic-password");
    start.ArgumentList.Add("-s");
    start.ArgumentList.Add(service);
    start.ArgumentList.Add("-a");
    start.ArgumentList.Add(account);
    start.ArgumentList.Add("-w");
    Process? process = null;
    LockedSensitiveBuffer? output = null;
    Exception? operationFailure = null;
    int outputLength = 0;
    try
    {
        output = LockedSensitiveBuffer.Create(checked(maximumBytes + 2));
        process = Process.Start(start)
            ?? throw new InvalidOperationException("Unable to start macOS Keychain lookup.");
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        Stream standardOutput = process.StandardOutput.BaseStream;
        int count = 0;
        while (count < output.Bytes.Length)
        {
            int read = standardOutput.Read(
                output.Bytes,
                count,
                output.Bytes.Length - count);
            if (read == 0) break;
            count += read;
        }

        int overflow = standardOutput.ReadByte();
        if (overflow >= 0 && !process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }
        process.WaitForExit();
        string error = errorTask.GetAwaiter().GetResult();
        while (count > 0 && output.Bytes[count - 1] is (byte)'\r' or (byte)'\n')
        {
            count--;
        }
        if (overflow >= 0 || count > maximumBytes)
        {
            throw new CryptographicException(
                $"The {description} returned by the Keychain exceeds its canonical size bound.");
        }
        if (process.ExitCode != 0 || count == 0)
        {
            throw new CryptographicException(
                $"macOS Keychain did not provide the {description}: {error.Trim()}");
        }
        outputLength = count;
    }
    catch (Exception ex)
    {
        operationFailure = ex;
        if (process is not null && !process.HasExited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
            catch (Exception killFailure)
            {
                operationFailure = new AggregateException(
                    "The failed Keychain lookup could not be terminated cleanly.",
                    operationFailure,
                    killFailure);
            }
        }
    }

    LockedSensitiveBuffer completed = LockedBufferTransfer.Complete(
        output,
        operationFailure,
        $"The {description} lookup failed during secure cleanup.",
        [],
        [process]);
    return (completed, outputLength);
}

static LockedSensitiveBuffer ReadPfxPasswordCharacters(
    string envelopePath,
    ReadOnlySpan<byte> wrappingKey)
{
    LockedSensitiveBuffer? encoded = null;
    LockedSensitiveBuffer? characters = null;
    Exception? operationFailure = null;
    try
    {
        encoded = HybridKeyEnvelope.ReadPfxPassword(
            envelopePath,
            wrappingKey);
        var strictUtf8 = new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);
        int characterCount = strictUtf8.GetCharCount(encoded.Bytes);
        characters = LockedSensitiveBuffer.Create(checked(characterCount * sizeof(char)));
        Span<char> characterSpan = MemoryMarshal.Cast<byte, char>(characters.Bytes.AsSpan());
        int written = strictUtf8.GetChars(encoded.Bytes, characterSpan);
        if (written != characterCount
            || characterCount == 0
            || characterSpan.IndexOf('\0') >= 0)
        {
            throw new CryptographicException("The RSA PFX password envelope contains an invalid password.");
        }

    }
    catch (DecoderFallbackException ex)
    {
        operationFailure = new CryptographicException(
            "The RSA PFX password envelope is not valid UTF-8.",
            ex);
    }
    catch (Exception ex)
    {
        operationFailure = ex;
    }

    return LockedBufferTransfer.Complete(
        characters,
        operationFailure,
        "The RSA PFX password operation failed during secure cleanup.",
        [encoded],
        []);
}

static void RequireNoTargets(IReadOnlyList<string> targets, string command)
{
    if (targets.Count != 0)
    {
        throw new ArgumentException($"{command} does not accept --target.");
    }
}

static void RequireOnlyOptions(
    IReadOnlyDictionary<string, string> options,
    params string[] allowedNames)
{
    var allowed = new HashSet<string>(allowedNames, StringComparer.OrdinalIgnoreCase);
    string? unexpected = options.Keys.FirstOrDefault(name => !allowed.Contains(name));
    if (unexpected is not null)
    {
        throw new ArgumentException($"Unexpected option: --{unexpected}");
    }
}

static byte[] ReadExactFile(string path, int expectedBytes, string description)
{
    string fullPath = RequireRegularFile(path, description);
    using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
    if (stream.Length != expectedBytes)
    {
        throw new InvalidDataException($"{description} must contain exactly {expectedBytes} bytes.");
    }
    byte[] result = new byte[expectedBytes];
    stream.ReadExactly(result);
    return result;
}

static string RequireRegularFile(string path, string description)
{
    string fullPath = Path.GetFullPath(path);
    var info = new FileInfo(fullPath);
    if (!info.Exists || info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
    {
        throw new FileNotFoundException($"{description} is missing, not regular, or a symbolic link.", fullPath);
    }
    return fullPath;
}

static void WriteTextAtomically(string path, string contents)
{
    WriteBytesAtomically(path, Encoding.ASCII.GetBytes(contents));
}

static void WriteLauncherPins(string path, byte[] certificate, byte[] publicKey)
{
    byte[] certificateSha256 = SHA256.HashData(certificate);
    try
    {
        string source = "// Generated by KeepVaultMac.HybridSigner. Do not edit.\n"
            + "enum KeepVaultHybridPins {\n"
            + "    static let rsaCertificateSha256: [UInt8] = [\n"
            + FormatSwiftBytes(certificateSha256, 8)
            + "    ]\n"
            + "    static let mldsaPublicKey: [UInt8] = [\n"
            + FormatSwiftBytes(publicKey, 8)
            + "    ]\n"
            + "}\n";
        WriteBytesAtomically(path, Encoding.UTF8.GetBytes(source));
    }
    finally
    {
        CryptographicOperations.ZeroMemory(certificateSha256);
    }
}

static string FormatSwiftBytes(ReadOnlySpan<byte> bytes, int indentation)
{
    var builder = new StringBuilder();
    string prefix = new(' ', indentation);
    for (int offset = 0; offset < bytes.Length; offset += 16)
    {
        builder.Append(prefix);
        int count = Math.Min(16, bytes.Length - offset);
        for (int index = 0; index < count; index++)
        {
            if (index != 0) builder.Append(' ');
            builder.Append("0x");
            builder.Append(bytes[offset + index].ToString("X2"));
            builder.Append(',');
        }
        builder.AppendLine();
    }
    return builder.ToString();
}

static void WriteBytesAtomically(string path, byte[] contents)
{
    string fullPath = Path.GetFullPath(path);
    string directory = Path.GetDirectoryName(fullPath)
        ?? throw new InvalidOperationException("Output path has no parent directory.");
    Directory.CreateDirectory(directory);
    string temporary = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
    try
    {
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(contents);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, fullPath, overwrite: true);
    }
    finally
    {
        File.Delete(temporary);
        CryptographicOperations.ZeroMemory(contents);
    }
}

static (Dictionary<string, string> Options, List<string> Targets) ParseOptions(string[] args)
{
    var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var targets = new List<string>();
    for (int index = 0; index < args.Length; index += 2)
    {
        if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Invalid option near '{args[index]}'.");
        }
        string name = args[index][2..];
        if (string.Equals(name, "target", StringComparison.OrdinalIgnoreCase))
        {
            targets.Add(args[index + 1]);
        }
        else if (!options.TryAdd(name, args[index + 1]))
        {
            throw new ArgumentException($"Duplicate option: --{name}");
        }
    }
    return (options, targets);
}

static string Require(Dictionary<string, string> options, string name) =>
    options.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new ArgumentException($"Missing required option --{name}.");

static int Usage(string error)
{
    Console.Error.WriteLine(error);
    Console.Error.WriteLine("Usage: sign|verify --target FILE [...] --mldsa-public-key FILE --policy Directory.Build.props");
    Console.Error.WriteLine("       hash --target FILE [...]");
    Console.Error.WriteLine("       release-keygen --directory PRIVATE_DIRECTORY --reference-library FILE");
    return 64;
}
