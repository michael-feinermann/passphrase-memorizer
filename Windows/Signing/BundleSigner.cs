using System.Security.Cryptography;
using System.Text.Json;
using KalynaArchiver.Signing;
using PassphraseMemorizer.Crypto;

namespace PassphraseMemorizer.WindowsSigning;

internal static class BundleSigner
{
    internal static void SignFile(string targetPath, string keyDirectory)
    {
        string target = Path.GetFullPath(targetPath);
        BundleFormat.ValidateRelativePath(Path.GetFileName(target));
        using var lease = SafeFiles.OpenReadLease(target);
        byte[] rsaPrivate = WindowsKeyStore.ReadPrivate(keyDirectory, "RSA-4096", WindowsKeyStore.RsaEnvelope);
        byte[]? mldsaPrivate = null;
        try
        {
            mldsaPrivate = WindowsKeyStore.ReadPrivate(keyDirectory, "ML-DSA-87", WindowsKeyStore.MldsaEnvelope);
            using var rsa = RSA.Create();
            rsa.ImportPkcs8PrivateKey(rsaPrivate, out int consumed);
            if (consumed != rsaPrivate.Length || rsa.KeySize != 4096) throw new CryptographicException("RSA signing key policy mismatch.");
            var fingerprint = BundleFormat.Fingerprint(Path.GetFileName(target), lease);
            byte[] message = BundleFormat.FileMessage(fingerprint);
            byte[] rsaSignature = rsa.SignData(message, HashAlgorithmName.SHA512, RSASignaturePadding.Pss);
            byte[] mldsaSignature = Mldsa87.Sign(message, mldsaPrivate);
            BundleVerifier.VerifySignatures(message, rsaSignature, mldsaSignature, ReleaseTrust.LoadEmbedded());
            WindowsKeyStore.WriteNew(target + ".hashes.json", JsonSerializer.SerializeToUtf8Bytes(fingerprint, BundleFormat.JsonOptions));
            WindowsKeyStore.WriteNew(target + ".rsa.sig", rsaSignature);
            WindowsKeyStore.WriteNew(target + ".mldsa.sig", mldsaSignature);
            Console.WriteLine("Signed release archive with both algorithms; SHA256/SHA3-512/Skein1024 fingerprints exported.");
        }
        finally { CryptographicOperations.ZeroMemory(rsaPrivate); if (mldsaPrivate != null) CryptographicOperations.ZeroMemory(mldsaPrivate); }
    }

    internal static void VerifyFile(string targetPath)
    {
        string target = Path.GetFullPath(targetPath);
        using var lease = SafeFiles.OpenReadLease(target);
        byte[] ReadMetadata(string suffix, int max)
        {
            using var metadata = SafeFiles.OpenReadLease(target + suffix);
            if (metadata.Length < 1 || metadata.Length > max) throw new InvalidDataException("Archive signing metadata length is invalid.");
            byte[] bytes = new byte[checked((int)metadata.Length)];
            metadata.ReadExactly(bytes); return bytes;
        }
        var fingerprint = JsonSerializer.Deserialize<FileFingerprint>(ReadMetadata(".hashes.json", 4096), BundleFormat.JsonOptions)
            ?? throw new InvalidDataException("Missing archive fingerprint.");
        BundleVerifier.VerifySignatures(BundleFormat.FileMessage(fingerprint), ReadMetadata(".rsa.sig", 512), ReadMetadata(".mldsa.sig", Mldsa87.SignatureBytes), ReleaseTrust.LoadEmbedded());
        if (fingerprint.Path != Path.GetFileName(target) || BundleFormat.Fingerprint(fingerprint.Path, lease) != fingerprint)
            throw new CryptographicException("Release archive fingerprint mismatch.");
        Console.WriteLine("Release archive, both detached signatures and three independent hashes verified.");
    }

    internal static void Sign(string bundleRoot, string keyDirectory)
    {
        byte[] rsaPrivate = WindowsKeyStore.ReadPrivate(keyDirectory, "RSA-4096", WindowsKeyStore.RsaEnvelope);
        byte[]? mldsaPrivate = null;
        try
        {
            mldsaPrivate = WindowsKeyStore.ReadPrivate(keyDirectory, "ML-DSA-87", WindowsKeyStore.MldsaEnvelope);
            using var rsa = RSA.Create();
            rsa.ImportPkcs8PrivateKey(rsaPrivate, out int consumed);
            if (consumed != rsaPrivate.Length || rsa.KeySize != 4096) throw new CryptographicException("RSA signing key policy mismatch.");
            var actualTrust = new ReleaseTrust(rsa.ExportSubjectPublicKeyInfo(), Mldsa87.DerivePublicKey(mldsaPrivate));
            var expectedTrust = ReleaseTrust.LoadEmbedded();
            byte[] proof = RandomNumberGenerator.GetBytes(32);
            BundleVerifier.VerifySignatures(proof, rsa.SignData(proof, HashAlgorithmName.SHA512, RSASignaturePadding.Pss), Mldsa87.Sign(proof, mldsaPrivate), expectedTrust);
            SignWithKeys(bundleRoot, rsa, mldsaPrivate);
            using var lease = BundleVerifier.OpenAndVerify(bundleRoot, actualTrust);
            Console.WriteLine("Signed and verified every release payload with RSA-4096/PSS/SHA-512 and ML-DSA-87.");
        }
        finally { CryptographicOperations.ZeroMemory(rsaPrivate); if (mldsaPrivate != null) CryptographicOperations.ZeroMemory(mldsaPrivate); }
    }

    internal static void SignWithKeys(string bundleRoot, RSA rsa, byte[] mldsaPrivate)
    {
        string root = SafeFiles.CanonicalPath(bundleRoot);
        SafeFiles.RejectReparsePath(root);
        if (File.Exists(Path.Combine(root, BundleFormat.InventoryName)) || Directory.Exists(Path.Combine(root, BundleFormat.SignatureDirectory)))
            throw new IOException("Release already contains signing metadata. Build a fresh staging bundle to sign.");
        string[] files = SafeFiles.EnumeratePayload(root);
        if (files.Length == 0 || files.Length > 4096) throw new IOException("Invalid release bundle file count.");
        List<FileStream> streams = [];
        try
        {
            List<FileFingerprint> fingerprints = [];
            foreach (string relative in files)
            {
                var stream = SafeFiles.OpenReadLease(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
                streams.Add(stream);
                fingerprints.Add(BundleFormat.Fingerprint(relative, stream));
            }
            if (!files.SequenceEqual(SafeFiles.EnumeratePayload(root))) throw new IOException("Bundle changed during signing.");
            foreach (var fingerprint in fingerprints)
            {
                string prefix = Path.Combine(root, BundleFormat.SignatureDirectory, fingerprint.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(prefix)!);
                byte[] message = BundleFormat.FileMessage(fingerprint);
                WindowsKeyStore.WriteNew(prefix + ".rsa.sig", rsa.SignData(message, HashAlgorithmName.SHA512, RSASignaturePadding.Pss));
                WindowsKeyStore.WriteNew(prefix + ".mldsa.sig", Mldsa87.Sign(message, mldsaPrivate));
            }
            byte[] inventory = JsonSerializer.SerializeToUtf8Bytes(new BundleInventory(BundleFormat.Schema, BundleFormat.Product, "1.0.1", fingerprints.ToArray()), BundleFormat.JsonOptions);
            byte[] inventoryMessage = BundleFormat.InventoryMessage(inventory);
            WindowsKeyStore.WriteNew(Path.Combine(root, BundleFormat.InventoryName), inventory);
            WindowsKeyStore.WriteNew(Path.Combine(root, BundleFormat.RsaInventorySignature), rsa.SignData(inventoryMessage, HashAlgorithmName.SHA512, RSASignaturePadding.Pss));
            WindowsKeyStore.WriteNew(Path.Combine(root, BundleFormat.MldsaInventorySignature), Mldsa87.Sign(inventoryMessage, mldsaPrivate));
        }
        finally { foreach (var stream in streams) stream.Dispose(); }
    }
}
