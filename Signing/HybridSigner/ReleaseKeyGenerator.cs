using System.Buffers;
using System.Buffers.Text;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using KalynaArchiver.Services;
using KalynaArchiver.Signing;

namespace KeepVaultMac.Packaging;

// Explicit provisioning, never an automatic signing fallback. No plaintext PFX
// password or ML-DSA key is written. macOS may create a temporary keychain during
// PFX import; the command dispatcher confines and checks its lifetime exactly as
// the existing signer does. Wrapping-key files live on the protected volume.
internal static class ReleaseKeyGenerator
{
    internal static int Generate(string directory, string referenceLibrary)
    {
        string root = Path.GetFullPath(directory);
        using var directoryLease = MacBoundSecretFile.BindPrivateDirectory(root);
        for (DirectoryInfo? parent = new(root); parent is not null; parent = parent.Parent)
            if (Directory.Exists(Path.Combine(parent.FullName, ".git")) || File.Exists(Path.Combine(parent.FullName, ".git")))
                throw new IOException("Release keys must be external to a repository.");
        if (Directory.EnumerateFileSystemEntries(root).Any())
            throw new IOException("Key generation requires an empty private directory and never overwrites keys.");

        LockedSensitiveBuffer? random = null, password = null, characters = null, pfxWrapping = null,
            mldsaWrapping = null, mldsa = null;
        byte[]? generatedPrivate = null;
        byte[]? pfx = null;
        try
        {
            random = LockedSensitiveBuffer.Create(48);
            password = LockedSensitiveBuffer.Create(64);
            characters = LockedSensitiveBuffer.Create(128);
            pfxWrapping = LockedSensitiveBuffer.Create(32);
            mldsaWrapping = LockedSensitiveBuffer.Create(32);
            RandomNumberGenerator.Fill(random.Bytes);
            RandomNumberGenerator.Fill(pfxWrapping.Bytes);
            do { RandomNumberGenerator.Fill(mldsaWrapping.Bytes); }
            while (CryptographicOperations.FixedTimeEquals(pfxWrapping.Bytes, mldsaWrapping.Bytes));
            if (Base64.EncodeToUtf8(random.Bytes, password.Bytes, out int consumed, out int written) != OperationStatus.Done
                || consumed != 48 || written != 64) throw new CryptographicException("Password encoding failed.");
            Span<char> passwordChars = MemoryMarshal.Cast<byte, char>(characters.Bytes.AsSpan());
            for (int i = 0; i < password.Bytes.Length; i++) passwordChars[i] = (char)password.Bytes[i];

            (byte[] publicKey, generatedPrivate) = Mldsa87.GenerateKeyPair();
            mldsa = LockedSensitiveBuffer.Create(Mldsa87.PrivateKeyBytes);
            generatedPrivate.CopyTo(mldsa.Bytes, 0);
            CryptographicOperations.ZeroMemory(generatedPrivate);
            generatedPrivate = null;
            using RSA rsa = RSA.Create(4096);
            var request = new CertificateRequest("CN=Passphrase Memorizer Release", rsa, HashAlgorithmName.SHA512, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.3") }, false));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(10));
            var contents = new Pkcs12SafeContents();
            var pbe = new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA512, 200_000);
            var keyBag = contents.AddShroudedKey(rsa, passwordChars, pbe);
            var certBag = contents.AddCertificate(certificate);
            byte[] localId = SHA256.HashData(certificate.GetPublicKey());
            keyBag.Attributes.Add(new Pkcs9LocalKeyId(localId));
            certBag.Attributes.Add(new Pkcs9LocalKeyId(localId));
            var builder = new Pkcs12Builder();
            builder.AddSafeContentsUnencrypted(contents);
            builder.SealWithMac(passwordChars, HashAlgorithmName.SHA512, 200_000);
            pfx = builder.Encode();

            // Prove both independent ML-DSA implementations before persisting.
            byte[] challenge = RandomNumberGenerator.GetBytes(128);
            using var reference = new Mldsa87Reference(referenceLibrary);
            byte[] signature = Mldsa87.Sign(challenge, mldsa.Bytes);
            if (!reference.Verify(challenge, signature, publicKey)) throw new CryptographicException("Independent ML-DSA verification failed.");
            signature = reference.Sign(challenge, mldsa.Bytes);
            if (!Mldsa87.Verify(challenge, signature, publicKey)) throw new CryptographicException("Reverse ML-DSA verification failed.");
            byte[] rsaSignature = rsa.SignData(challenge, HashAlgorithmName.SHA512, RSASignaturePadding.Pss);
            using RSA publicRsa = certificate.GetRSAPublicKey()!;
            if (!publicRsa.VerifyData(challenge, rsaSignature, HashAlgorithmName.SHA512, RSASignaturePadding.Pss))
                throw new CryptographicException("RSA-PSS verification failed.");

            Write(directoryLease, "hybrid-rsa4096.pfx", pfx);
            HybridKeyEnvelope.WriteMldsaPrivateKey(Path.Combine(root, "mldsa87-private.key.v12.enc"), mldsa.Bytes, mldsaWrapping.Bytes, directoryLease);
            HybridKeyEnvelope.WritePfxPassword(Path.Combine(root, "hybrid-rsa4096.pfx.password.v12.enc"), password.Bytes, pfxWrapping.Bytes, directoryLease);
            WriteWrapping(directoryLease, "mldsa-v12-wrapping-key.b64", mldsaWrapping.Bytes);
            WriteWrapping(directoryLease, "pfx-v12-wrapping-key.b64", pfxWrapping.Bytes);
            byte[] cert = certificate.Export(X509ContentType.Cert);
            Write(directoryLease, "hybrid-rsa4096.cer", cert);
            Write(directoryLease, "mldsa87-public.key", publicKey);
            Write(directoryLease, "Directory.Build.props", PublicPolicy(certificate, rsa.ExportSubjectPublicKeyInfo(), publicKey));

            directoryLease.Validate();
            // Reload all persisted secrets through the existing bound-file reader.
            using var readMldsaWrapping = UsbWrappingKey.Read(Path.Combine(root, "mldsa-v12-wrapping-key.b64"));
            using var readPfxWrapping = UsbWrappingKey.Read(Path.Combine(root, "pfx-v12-wrapping-key.b64"));
            using var readMldsa = HybridKeyEnvelope.ReadMldsaPrivateKey(Path.Combine(root, "mldsa87-private.key.v12.enc"), readMldsaWrapping.Bytes);
            using var readPassword = HybridKeyEnvelope.ReadPfxPassword(Path.Combine(root, "hybrid-rsa4096.pfx.password.v12.enc"), readPfxWrapping.Bytes);
            using var readPfx = MacBoundSecretFile.ReadPrivateBytes(Path.Combine(root, "hybrid-rsa4096.pfx"), 1, 1024 * 1024, "new RSA PFX");
            if (!CryptographicOperations.FixedTimeEquals(readMldsa.Bytes, mldsa.Bytes)
                || !CryptographicOperations.FixedTimeEquals(readPassword.Bytes, password.Bytes)
                || !CryptographicOperations.FixedTimeEquals(readPfx.Bytes, pfx))
                throw new CryptographicException("Stored release-key roundtrip failed.");
            using var reloaded = X509CertificateLoader.LoadPkcs12(readPfx.Bytes, passwordChars, X509KeyStorageFlags.DefaultKeySet);
            using RSA reloadedRsa = reloaded.GetRSAPrivateKey()!;
            if (!publicRsa.VerifyData(challenge, reloadedRsa.SignData(challenge, HashAlgorithmName.SHA512, RSASignaturePadding.Pss), HashAlgorithmName.SHA512, RSASignaturePadding.Pss))
                throw new CryptographicException("Stored RSA key challenge failed.");
            if (!Mldsa87.Verify(challenge, Mldsa87.Sign(challenge, readMldsa.Bytes), publicKey))
                throw new CryptographicException("Stored ML-DSA key challenge failed.");
            directoryLease.Validate();
            Console.WriteLine(JsonSerializer.Serialize(new { status = "created-and-verified", directory = root,
                certificateThumbprint = certificate.Thumbprint, certificateSha256 = Convert.ToHexString(SHA256.HashData(cert)),
                mldsaPublicSha256 = Convert.ToHexString(SHA256.HashData(publicKey)),
                protection = "RSA-4096 encrypted PKCS12; ML-DSA-87; distinct AES-256-GCM envelopes and independent wrapping keys; ownership-enforced private volume",
                reference = "pq-crystals/dilithium@d35ba3fe5449bee3e6d43e1f296c3ca818bd36be" }));
            return 0;
        }
        finally
        {
            if (generatedPrivate is not null) CryptographicOperations.ZeroMemory(generatedPrivate);
            if (pfx is not null) CryptographicOperations.ZeroMemory(pfx);
            SecureMemory.DisposeAll(random, password, characters, pfxWrapping, mldsaWrapping, mldsa);
        }
    }

    private static byte[] PublicPolicy(X509Certificate2 certificate, ReadOnlySpan<byte> spki, ReadOnlySpan<byte> publicKey)
    {
        var rsa = HybridSignatureService.Fingerprint(spki);
        var mldsa = HybridSignatureService.Fingerprint(publicKey);
        var document = new XDocument(new XElement("Project", new XElement("PropertyGroup",
            new XElement("KalynaSigningCertificateThumbprint", certificate.Thumbprint),
            new XElement("KalynaExpectedSignerSha256", Convert.ToHexString(rsa.Sha256)),
            new XElement("KalynaExpectedSignerSha3_512", Convert.ToHexString(rsa.Sha3_512)),
            new XElement("KalynaExpectedSignerSkein1024", Convert.ToHexString(rsa.Skein1024)),
            new XElement("KalynaExpectedMldsa87Sha256", Convert.ToHexString(mldsa.Sha256)),
            new XElement("KalynaExpectedMldsa87Sha3_512", Convert.ToHexString(mldsa.Sha3_512)),
            new XElement("KalynaExpectedMldsa87Skein1024", Convert.ToHexString(mldsa.Skein1024)))));
        return Encoding.UTF8.GetBytes(document.ToString() + "\n");
    }

    private static void WriteWrapping(MacBoundSecretFile.PrivateDirectoryLease directoryLease, string name, ReadOnlySpan<byte> key)
    {
        using var encoded = LockedSensitiveBuffer.Create(44);
        if (Base64.EncodeToUtf8(key, encoded.Bytes, out int consumed, out int written) != OperationStatus.Done || consumed != 32 || written != 44)
            throw new CryptographicException("Wrapping-key encoding failed.");
        Write(directoryLease, name, encoded.Bytes);
    }

    private static void Write(MacBoundSecretFile.PrivateDirectoryLease directoryLease, string name, ReadOnlySpan<byte> bytes)
    {
        directoryLease.Validate();
        using var file = MacBoundSecretFile.Create(Path.Combine(directoryLease.Path, name), directoryLease);
        try
        {
            file.Stream.Write(bytes);
            file.Stream.Flush(flushToDisk: true);
            file.Publish();
        }
        catch (Exception primaryFailure)
        {
            // Own only the descriptor created for this write. A substituted
            // pathname must never become a cleanup target, even after rename.
            var cleanupFailures = new List<Exception>();
            try { HybridKeyEnvelope.WipeOpenFile(file.Stream); }
            catch (Exception cleanupFailure) { cleanupFailures.Add(cleanupFailure); }
            try { _ = file.RemoveCurrentNameIfStillOwned(); }
            catch (Exception cleanupFailure) { cleanupFailures.Add(cleanupFailure); }
            if (cleanupFailures.Count != 0)
                throw new AggregateException(new[] { primaryFailure }.Concat(cleanupFailures));
            throw;
        }
    }
}
