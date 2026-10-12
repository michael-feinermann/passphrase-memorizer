using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KalynaArchiver.Signing;
using PassphraseMemorizer.Crypto;

namespace PassphraseMemorizer.WindowsSigning;

internal static class SelfTests
{
    internal static void Run(string? referenceLibrary, string? skeinVectors)
    {
        int checks = 0;
        void Check(bool value, string label) { if (!value) throw new CryptographicException("Self-test failed: " + label); checks++; }
        void Reject(Action action, string label)
        {
            bool rejected = false;
            try { action(); } catch (Exception ex) when (ex is CryptographicException or IOException or InvalidDataException) { rejected = true; }
            Check(rejected, label);
        }
        byte[] message = Encoding.ASCII.GetBytes("Passphrase Memorizer Windows hybrid signing self-test");
        FileFingerprint empty = BundleFormat.Fingerprint("empty", new MemoryStream([]));
        Check(empty.Sha256 == "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855", "SHA256 empty known-answer");
        Check(empty.Sha3_512 == "A69F73CCA23A9AC5C8B567DC185A756E97C982164FE25859E0D1DCC1475C80A615B2123AF1F5F94C11E3E9402C3AC558F500199D95B6D3E301758586281DCD26", "SHA3-512 empty known-answer");
        Check(empty.Skein1024 == "0FFF9563BB3279289227AC77D319B6FFF8D7E9F09DA1247B72A0A265CD6D2A62645AD547ED8193DB48CFF847C06494A03F55666D3B47EB4C20456C9373C86297D630D5578EBD34CB40991578F9F52B18003EFA35D3DA6553FF35DB91B81AB890BEC1B189B7F52CB2A783EBB7D823D725B0B4A71F6824E88F68F982EEFC6D19C6", "Skein1024 empty published known-answer");
        if (skeinVectors != null)
        {
            using var vectors = JsonDocument.Parse(File.ReadAllBytes(skeinVectors));
            foreach (var vector in vectors.RootElement.EnumerateArray())
            {
                byte[] fixture = Convert.FromHexString(vector.GetProperty("message").GetString()!);
                FileFingerprint fingerprint = BundleFormat.Fingerprint("fixture", new MemoryStream(fixture));
                Check(fingerprint.Skein1024.Equals(vector.GetProperty("output").GetString(), StringComparison.OrdinalIgnoreCase), "Skein published golden vector");
            }
        }
        using var rsa = RSA.Create(4096);
        var (publicKey, privateKey) = Mldsa87.GenerateKeyPair();
        try
        {
            var trust = new ReleaseTrust(rsa.ExportSubjectPublicKeyInfo(), publicKey);
            byte[] rsaSignature = rsa.SignData(message, HashAlgorithmName.SHA512, RSASignaturePadding.Pss);
            byte[] mldsaSignature = Mldsa87.Sign(message, privateKey);
            BundleVerifier.VerifySignatures(message, rsaSignature, mldsaSignature, trust); checks++;
            Check(Mldsa87.DerivePublicKey(privateKey).SequenceEqual(publicKey), "derive ML-DSA public key");
            rsaSignature[5] ^= 1;
            Reject(() => BundleVerifier.VerifySignatures(message, rsaSignature, mldsaSignature, trust), "RSA signature tamper");
            rsaSignature[5] ^= 1;
            mldsaSignature[7] ^= 1;
            Reject(() => BundleVerifier.VerifySignatures(message, rsaSignature, mldsaSignature, trust), "ML-DSA signature tamper");
            mldsaSignature[7] ^= 1;
            message[0] ^= 1;
            Reject(() => BundleVerifier.VerifySignatures(message, rsaSignature, mldsaSignature, trust), "message tamper");
            message[0] ^= 1;
            byte[] envelope = Dpapi.Protect(privateKey, "ML-DSA-87");
            byte[] roundtrip = Dpapi.Unprotect(envelope, "ML-DSA-87");
            try { Check(CryptographicOperations.FixedTimeEquals(privateKey, roundtrip), "DPAPI CurrentUser roundtrip"); }
            finally { CryptographicOperations.ZeroMemory(roundtrip); }
            Reject(() => { byte[] wrong = Dpapi.Unprotect(envelope, "RSA-4096"); CryptographicOperations.ZeroMemory(wrong); }, "independent DPAPI role binding");
            envelope[^1] ^= 1;
            Reject(() => { byte[] wrong = Dpapi.Unprotect(envelope, "ML-DSA-87"); CryptographicOperations.ZeroMemory(wrong); }, "DPAPI ciphertext tamper");
            if (referenceLibrary != null)
            {
                using var reference = new Mldsa87Reference(referenceLibrary);
                Check(reference.Verify(message, mldsaSignature, publicKey), "pq-crystals verifies BouncyCastle signature");
                byte[] referenceSignature = reference.Sign(message, privateKey);
                Check(Mldsa87.Verify(message, referenceSignature, publicKey), "BouncyCastle verifies pq-crystals signature");
                referenceSignature[100] ^= 1;
                Check(!Mldsa87.Verify(message, referenceSignature, publicKey) && !reference.Verify(message, referenceSignature, publicKey), "independent ML-DSA tamper rejection");
                var (referencePublic, referencePrivate) = reference.GenerateKeyPair();
                try
                {
                    byte[] signature = Mldsa87.Sign(message, referencePrivate);
                    Check(reference.Verify(message, signature, referencePublic), "pq-crystals key pair interoperability");
                }
                finally { CryptographicOperations.ZeroMemory(referencePrivate); }
            }
            string temp = Path.Combine(Path.GetTempPath(), "PassphraseMemorizer-SigningTest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                File.WriteAllText(Path.Combine(temp, "test.exe"), "inert executable fixture");
                Directory.CreateDirectory(Path.Combine(temp, "native"));
                File.WriteAllText(Path.Combine(temp, "native", "runner.exe"), "inert helper fixture");
                BundleSigner.SignWithKeys(temp, rsa, privateKey);
                using (var lease = BundleVerifier.OpenAndVerify(temp, trust))
                {
                    checks++;
                    lease.RequireVerifiedPayload(Path.Combine(temp, "native", "runner.exe")); checks++;
                    Reject(() => lease.RequireVerifiedPayload(Path.Combine(temp, "unsigned.exe")), "unsigned helper rejected");
                    Reject(() => File.WriteAllText(Path.Combine(temp, "native", "runner.exe"), "replacement"), "read lease blocks replacement");
                    Reject(() => File.Delete(Path.Combine(temp, "native", "runner.exe")), "read lease blocks deletion");
                }
                string payload = Path.Combine(temp, "test.exe");
                File.AppendAllText(payload, "tamper");
                Reject(() => { using var lease = BundleVerifier.OpenAndVerify(temp, trust); }, "payload tamper");
                File.WriteAllText(payload, "inert executable fixture");
                File.WriteAllText(Path.Combine(temp, "extra.dll"), "extra");
                Reject(() => { using var lease = BundleVerifier.OpenAndVerify(temp, trust); }, "unsigned extra file");
                File.Delete(Path.Combine(temp, "extra.dll"));
                byte[] signatureBytes = File.ReadAllBytes(Path.Combine(temp, BundleFormat.MldsaInventorySignature));
                signatureBytes[1] ^= 1;
                File.WriteAllBytes(Path.Combine(temp, BundleFormat.MldsaInventorySignature), signatureBytes);
                Reject(() => { using var lease = BundleVerifier.OpenAndVerify(temp, trust); }, "inventory ML-DSA tamper");
            }
            finally
            {
                // The only recursive delete is the exact freshly created, owned fixture directory.
                string fullTemp = SafeFiles.CanonicalPath(temp);
                string tempRoot = SafeFiles.CanonicalPath(Path.GetTempPath()) + Path.DirectorySeparatorChar;
                if (!fullTemp.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(fullTemp).StartsWith("PassphraseMemorizer-SigningTest-", StringComparison.Ordinal))
                    throw new IOException("Unsafe test cleanup target.");
                Directory.Delete(fullTemp, recursive: true);
            }
            Console.WriteLine("PASS: " + checks + " crypto, DPAPI, dual-signature, inventory, payload and lease checks" + (referenceLibrary == null ? "; independent pq-crystals check not requested." : "; including independent pinned pq-crystals interoperability."));
        }
        finally { CryptographicOperations.ZeroMemory(privateKey); }
    }
}
