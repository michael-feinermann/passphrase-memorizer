using System.Security.Cryptography;
using System.Text.Json;
using KalynaArchiver.Signing;

namespace PassphraseMemorizer.Crypto;

public sealed class VerifiedBundleLease : IDisposable
{
    private readonly List<FileStream> streams;
    private readonly HashSet<string> payloads;
    private bool disposed;
    internal VerifiedBundleLease(List<FileStream> streams, HashSet<string> payloads) { this.streams = streams; this.payloads = payloads; }
    public void RequireVerifiedPayload(string absolutePath)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!payloads.Contains(SafeFiles.CanonicalPath(absolutePath)))
            throw new CryptographicException("The requested executable is not in the verified release inventory.");
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var stream in streams) stream.Dispose();
    }
}

public static class BundleVerifier
{
    public static VerifiedBundleLease OpenAndVerifyRunningExecutable(string bundleRoot)
    {
        var lease = OpenAndVerify(bundleRoot, ReleaseTrust.LoadEmbedded());
        try { lease.RequireVerifiedPayload(Environment.ProcessPath ?? throw new CryptographicException("The running executable path is unavailable.")); return lease; }
        catch { lease.Dispose(); throw; }
    }

    public static VerifiedBundleLease OpenAndVerify(string bundleRoot, ReleaseTrust trust)
    {
        string root = SafeFiles.CanonicalPath(bundleRoot);
        SafeFiles.RejectReparsePath(root);
        List<FileStream> streams = [];
        HashSet<string> payloads = new(StringComparer.OrdinalIgnoreCase);
        byte[] Read(string relative, int maximum)
        {
            string path = System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
            var stream = SafeFiles.OpenReadLease(path);
            streams.Add(stream);
            if (stream.Length <= 0 || stream.Length > maximum) throw new InvalidDataException("Release metadata size is invalid.");
            byte[] data = new byte[checked((int)stream.Length)];
            stream.ReadExactly(data);
            return data;
        }
        try
        {
            byte[] inventoryBytes = Read(BundleFormat.InventoryName, 4 * 1024 * 1024);
            byte[] rsaSignature = Read(BundleFormat.RsaInventorySignature, 512);
            byte[] mldsaSignature = Read(BundleFormat.MldsaInventorySignature, Mldsa87.SignatureBytes);
            VerifySignatures(BundleFormat.InventoryMessage(inventoryBytes), rsaSignature, mldsaSignature, trust);
            var inventory = JsonSerializer.Deserialize<BundleInventory>(inventoryBytes, BundleFormat.JsonOptions)
                ?? throw new InvalidDataException("Release inventory is empty.");
            if (inventory.Schema != BundleFormat.Schema || inventory.Product != BundleFormat.Product || inventory.Version != "1.0.1"
                || inventory.Files == null || inventory.Files.Length == 0 || inventory.Files.Length > 4096)
                throw new InvalidDataException("Release inventory policy mismatch.");
            string[] expected = inventory.Files.Select(x => BundleFormat.ValidateRelativePath(x.Path)).ToArray();
            if (expected.Any(BundleFormat.IsMetadata) || !expected.SequenceEqual(expected.OrderBy(x => x, StringComparer.Ordinal))
                || expected.Distinct(StringComparer.OrdinalIgnoreCase).Count() != expected.Length
                || !expected.SequenceEqual(SafeFiles.EnumeratePayload(root)))
                throw new CryptographicException("Release payload inventory mismatch.");
            foreach (var file in inventory.Files)
            {
                if (file.Length < 0 || !IsHex(file.Sha256, 64) || !IsHex(file.Sha3_512, 128) || !IsHex(file.Skein1024, 256))
                    throw new InvalidDataException("Release fingerprint encoding is invalid.");
                string path = System.IO.Path.Combine(root, file.Path.Replace('/', System.IO.Path.DirectorySeparatorChar));
                var stream = SafeFiles.OpenReadLease(path);
                streams.Add(stream);
                if (stream.Length != file.Length || BundleFormat.Fingerprint(file.Path, stream) != file)
                    throw new CryptographicException("Release payload hash mismatch: " + file.Path);
                VerifySignatures(BundleFormat.FileMessage(file), Read(BundleFormat.SignatureDirectory + "/" + file.Path + ".rsa.sig", 512),
                    Read(BundleFormat.SignatureDirectory + "/" + file.Path + ".mldsa.sig", Mldsa87.SignatureBytes), trust);
                payloads.Add(SafeFiles.CanonicalPath(path));
            }
            // Detect insertions that raced the initial enumeration. Leases prevent replacement of every verified file.
            if (!expected.SequenceEqual(SafeFiles.EnumeratePayload(root))) throw new CryptographicException("Release payload inventory changed during verification.");
            return new VerifiedBundleLease(streams, payloads);
        }
        catch { foreach (var stream in streams) stream.Dispose(); throw; }
    }

    private static bool IsHex(string? value, int length) => value != null && value.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

    public static void VerifySignatures(ReadOnlySpan<byte> message, ReadOnlySpan<byte> rsaSignature, ReadOnlySpan<byte> mldsaSignature, ReleaseTrust trust)
    {
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(trust.RsaSpki, out _);
        if (rsaSignature.Length != 512 || !rsa.VerifyData(message, rsaSignature, HashAlgorithmName.SHA512, RSASignaturePadding.Pss)
            || !Mldsa87.Verify(message, mldsaSignature, trust.MldsaPublic))
            throw new CryptographicException("Both RSA-4096/PSS/SHA-512 and ML-DSA-87 release signatures must validate.");
    }
}
