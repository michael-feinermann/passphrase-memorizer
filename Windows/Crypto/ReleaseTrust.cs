using System.Security.Cryptography;
using KalynaArchiver.Signing;

namespace PassphraseMemorizer.Crypto;

/// <summary>Public trust is compiled into the executable; adjacent policy files never confer trust.</summary>
public sealed class ReleaseTrust
{
    internal byte[] RsaSpki { get; }
    internal byte[] MldsaPublic { get; }

    public ReleaseTrust(ReadOnlySpan<byte> rsaSpki, ReadOnlySpan<byte> mldsaPublic)
    {
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(rsaSpki, out int consumed);
        if (consumed != rsaSpki.Length || rsa.KeySize != 4096 || mldsaPublic.Length != Mldsa87.PublicKeyBytes)
            throw new CryptographicException("Release trust requires RSA-4096 and ML-DSA-87 public keys.");
        RsaSpki = rsaSpki.ToArray();
        MldsaPublic = mldsaPublic.ToArray();
    }

    public static ReleaseTrust LoadEmbedded()
    {
        var assembly = typeof(ReleaseTrust).Assembly;
        byte[] ReadResource(string name)
        {
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new CryptographicException("The Windows release public trust has not been provisioned.");
            using var output = new MemoryStream();
            stream.CopyTo(output);
            return output.ToArray();
        }
        byte[] rsa = ReadResource("PassphraseMemorizer.Windows.RsaTrust");
        byte[] mldsa = ReadResource("PassphraseMemorizer.Windows.MldsaTrust");
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(rsa)), GeneratedTrust.RsaSha256, StringComparison.Ordinal)
            || !string.Equals(Convert.ToHexString(SHA256.HashData(mldsa)), GeneratedTrust.MldsaSha256, StringComparison.Ordinal))
            throw new CryptographicException("Compiled release trust fingerprint mismatch.");
        return new ReleaseTrust(rsa, mldsa);
    }
}
