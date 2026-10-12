using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using KalynaArchiver.Signing;
using PassphraseMemorizer.Crypto;

namespace PassphraseMemorizer.WindowsSigning;

internal static class WindowsKeyStore
{
    internal const string RequiredDirectory = @"A:\Passphrase Memorizer Keys";
    internal const string RsaEnvelope = "rsa4096-private.dpapi";
    internal const string MldsaEnvelope = "mldsa87-private.dpapi";

    internal static string ValidateDirectory(string directory)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows CurrentUser DPAPI is required.");
        string full = SafeFiles.CanonicalPath(directory);
        if (!full.Equals(RequiredDirectory, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Private signing keys may only reside in A:\\Passphrase Memorizer Keys.");
        SafeFiles.RejectReparsePath(full);
        return full;
    }

    internal static byte[] ReadPrivate(string directory, string role, string filename)
    {
        directory = ValidateDirectory(directory);
        using var stream = SafeFiles.OpenReadLease(Path.Combine(directory, filename));
        if (stream.Length < 32 || stream.Length > 64 * 1024) throw new CryptographicException("Invalid DPAPI key envelope size.");
        byte[] encrypted = new byte[checked((int)stream.Length)];
        try { stream.ReadExactly(encrypted); return Dpapi.Unprotect(encrypted, role); }
        finally { CryptographicOperations.ZeroMemory(encrypted); }
    }

    internal static void Generate(string directory, string publicDirectory)
    {
        directory = ValidateDirectory(directory);
        if (Directory.EnumerateFileSystemEntries(directory).Any())
            throw new IOException("Key directory must be empty. Existing signing keys are never overwritten.");
        string publicRoot = SafeFiles.CanonicalPath(publicDirectory);
        Directory.CreateDirectory(publicRoot);
        SafeFiles.RejectReparsePath(publicRoot);
        string[] publicNames = ["rsa4096-spki.der", "mldsa87-public.bin", "rsa4096-certificate.cer", "trust.json"];
        if (publicNames.Any(name => File.Exists(Path.Combine(publicRoot, name)))) throw new IOException("Existing public release trust is never overwritten by key generation.");
        using var rsa = RSA.Create(4096);
        var (mldsaPublic, mldsaPrivate) = Mldsa87.GenerateKeyPair();
        byte[] rsaPrivate = rsa.ExportPkcs8PrivateKey();
        byte[] rsaPublic = rsa.ExportSubjectPublicKeyInfo();
        try
        {
            byte[] rsaEncrypted = Dpapi.Protect(rsaPrivate, "RSA-4096");
            byte[] mldsaEncrypted = Dpapi.Protect(mldsaPrivate, "ML-DSA-87");
            try
            {
                // Validate both envelopes before persisting any key. DPAPI holds the wrapping secret in the user's protected profile.
                byte[] rsaRoundtrip = Dpapi.Unprotect(rsaEncrypted, "RSA-4096");
                byte[] mldsaRoundtrip = Dpapi.Unprotect(mldsaEncrypted, "ML-DSA-87");
                try
                {
                    if (!CryptographicOperations.FixedTimeEquals(rsaPrivate, rsaRoundtrip)
                        || !CryptographicOperations.FixedTimeEquals(mldsaPrivate, mldsaRoundtrip)) throw new CryptographicException("DPAPI roundtrip failed.");
                }
                finally { CryptographicOperations.ZeroMemory(rsaRoundtrip); CryptographicOperations.ZeroMemory(mldsaRoundtrip); }
                WriteNew(Path.Combine(directory, RsaEnvelope), rsaEncrypted);
                WriteNew(Path.Combine(directory, MldsaEnvelope), mldsaEncrypted);
            }
            finally { CryptographicOperations.ZeroMemory(rsaEncrypted); CryptographicOperations.ZeroMemory(mldsaEncrypted); }
            var request = new CertificateRequest("CN=Passphrase Memorizer Windows Release Signing", rsa, HashAlgorithmName.SHA512, RSASignaturePadding.Pss);
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new System.Security.Cryptography.OidCollection { new("1.3.6.1.5.5.7.3.3") }, critical: true));
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(5));
            byte[] certPublic = certificate.Export(X509ContentType.Cert);
            var trust = new
            {
                schema = "passphrase-memorizer-windows-trust-v1",
                product = BundleFormat.Product,
                rsaAlgorithm = "RSA-4096-PSS-SHA-512",
                mldsaAlgorithm = "ML-DSA-87",
                mldsaContext = Encoding.ASCII.GetString(Mldsa87.SigningContext),
                rsa = BundleFormat.Fingerprint("rsa4096-spki.der", new MemoryStream(rsaPublic)),
                mldsa = BundleFormat.Fingerprint("mldsa87-public.bin", new MemoryStream(mldsaPublic)),
                protection = "Windows DPAPI CurrentUser; separate role entropy; same Windows profile and machine required",
                certificatePurpose = "Public signing identity metadata; self-signed; no Windows root trust installed"
            };
            byte[] trustJson = JsonSerializer.SerializeToUtf8Bytes(trust, new JsonSerializerOptions { WriteIndented = true });
            WriteNew(Path.Combine(directory, "rsa4096-spki.der"), rsaPublic);
            WriteNew(Path.Combine(directory, "mldsa87-public.bin"), mldsaPublic);
            WriteNew(Path.Combine(directory, "rsa4096-certificate.cer"), certPublic);
            WriteNew(Path.Combine(directory, "trust.json"), trustJson);
            WriteNew(Path.Combine(publicRoot, "rsa4096-spki.der"), rsaPublic);
            WriteNew(Path.Combine(publicRoot, "mldsa87-public.bin"), mldsaPublic);
            WriteNew(Path.Combine(publicRoot, "rsa4096-certificate.cer"), certPublic);
            WriteNew(Path.Combine(publicRoot, "trust.json"), trustJson);
            string rsaFingerprint = Convert.ToHexString(SHA256.HashData(rsaPublic));
            string mldsaFingerprint = Convert.ToHexString(SHA256.HashData(mldsaPublic));
            string source = "namespace PassphraseMemorizer.Crypto;\n\n// Generated public release fingerprints; private keys remain outside the repository.\ninternal static class GeneratedTrust\n{\n"
                + "    internal const string RsaSha256 = \"" + rsaFingerprint + "\";\n"
                + "    internal const string MldsaSha256 = \"" + mldsaFingerprint + "\";\n}\n";
            File.WriteAllText(Path.Combine(publicRoot, "..", "GeneratedTrust.cs"), source, new UTF8Encoding(false));
            Console.WriteLine("Provisioned two independent DPAPI-protected private signing keys at " + directory);
            Console.WriteLine("RSA SPKI SHA-256: " + rsaFingerprint);
            Console.WriteLine("ML-DSA-87 public SHA-256: " + mldsaFingerprint);
            Console.WriteLine("Rebuild the signer and application to embed this public trust.");
        }
        finally { CryptographicOperations.ZeroMemory(rsaPrivate); CryptographicOperations.ZeroMemory(mldsaPrivate); }
    }

    internal static void WriteNew(string path, ReadOnlySpan<byte> bytes)
    {
        SafeFiles.RejectReparsePath(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }
}

internal static unsafe class Dpapi
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { internal int Length; internal byte* Data; }
    internal static byte[] Protect(byte[] value, string role) => Transform(value, role, protect: true);
    internal static byte[] Unprotect(byte[] value, string role) => Transform(value, role, protect: false);
    private static byte[] Transform(byte[] value, string role, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI requires Windows.");
        byte[] entropy = SHA256.HashData(Encoding.UTF8.GetBytes("PassphraseMemorizer/WindowsSigningKey/v1/" + role));
        Blob output = default;
        try
        {
            fixed (byte* inputBytes = value)
            fixed (byte* entropyBytes = entropy)
            {
                Blob input = new() { Length = value.Length, Data = inputBytes };
                Blob extra = new() { Length = entropy.Length, Data = entropyBytes };
                bool success = protect
                    ? CryptProtectData(ref input, null, ref extra, 0, 0, 1, out output)
                    : CryptUnprotectData(ref input, 0, ref extra, 0, 0, 1, out output);
                if (!success) throw new CryptographicException("DPAPI " + (protect ? "protection" : "unprotection") + " failed: " + Marshal.GetLastWin32Error());
                if (output.Length <= 0 || output.Length > 64 * 1024) throw new CryptographicException("DPAPI returned an invalid key envelope.");
                return new ReadOnlySpan<byte>(output.Data, output.Length).ToArray();
            }
        }
        finally
        {
            if (output.Data != null)
            {
                CryptographicOperations.ZeroMemory(new Span<byte>(output.Data, Math.Max(output.Length, 0)));
                _ = LocalFree((nint)output.Data);
            }
            CryptographicOperations.ZeroMemory(entropy);
        }
    }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string? description, ref Blob entropy, nint reserved, nint prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, nint description, ref Blob entropy, nint reserved, nint prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
}
