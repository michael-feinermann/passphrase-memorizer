using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Org.BouncyCastle.Crypto.Digests;

namespace PassphraseMemorizer.Crypto;

public sealed record FileFingerprint(string Path, long Length, string Sha256, string Sha3_512, string Skein1024);
public sealed record BundleInventory(string Schema, string Product, string Version, FileFingerprint[] Files);

public static class BundleFormat
{
    public const string InventoryName = "windows-release.json";
    public const string RsaInventorySignature = "windows-release.rsa.sig";
    public const string MldsaInventorySignature = "windows-release.mldsa.sig";
    public const string SignatureDirectory = ".signatures";
    public const string Schema = "passphrase-memorizer-windows-release-v1";
    public const string Product = "local.passphrasereminder.reminder.windows";
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };

    public static byte[] InventoryMessage(ReadOnlySpan<byte> bytes) => DomainMessage("PassphraseMemorizer/WindowsReleaseInventory/v1", bytes);
    public static byte[] FileMessage(FileFingerprint file) => DomainMessage("PassphraseMemorizer/WindowsReleaseFile/v1", JsonSerializer.SerializeToUtf8Bytes(file, JsonOptions));

    private static byte[] DomainMessage(string domain, ReadOnlySpan<byte> payload)
    {
        byte[] prefix = Encoding.ASCII.GetBytes(domain + "\0");
        byte[] result = new byte[prefix.Length + payload.Length];
        prefix.CopyTo(result, 0);
        payload.CopyTo(result.AsSpan(prefix.Length));
        return result;
    }

    public static FileFingerprint Fingerprint(string relativePath, Stream stream)
    {
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var sha3 = new Sha3Digest(512);
        var skein = new SkeinDigest(1024, 1024);
        byte[] buffer = new byte[128 * 1024];
        long length = 0;
        try
        {
            stream.Position = 0;
            int read;
            while ((read = stream.Read(buffer)) != 0)
            {
                length = checked(length + read);
                sha256.AppendData(buffer.AsSpan(0, read));
                sha3.BlockUpdate(buffer.AsSpan(0, read));
                skein.BlockUpdate(buffer.AsSpan(0, read));
            }
            byte[] s3 = new byte[64];
            byte[] sk = new byte[128];
            sha3.DoFinal(s3);
            skein.DoFinal(sk);
            return new FileFingerprint(relativePath, length, Convert.ToHexString(sha256.GetHashAndReset()), Convert.ToHexString(s3), Convert.ToHexString(sk));
        }
        finally { CryptographicOperations.ZeroMemory(buffer); sha3.Reset(); skein.Reset(); }
    }

    public static bool IsMetadata(string path) => path.Equals(InventoryName, StringComparison.OrdinalIgnoreCase)
        || path.Equals(RsaInventorySignature, StringComparison.OrdinalIgnoreCase)
        || path.Equals(MldsaInventorySignature, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(SignatureDirectory + "/", StringComparison.OrdinalIgnoreCase);

    public static string ValidateRelativePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 240 || path.Contains('\\') || path.StartsWith('/')
            || path.Split('/').Any(p => p is "" or "." or ".." || p.EndsWith('.') || p.EndsWith(' ')
                || p.IndexOfAny([':', '*', '?', '"', '<', '>', '|']) >= 0 || p.Any(char.IsControl)
                || IsReservedDevice(p)))
            throw new InvalidDataException("Invalid or ambiguous release inventory path.");
        return path;
    }

    private static bool IsReservedDevice(string component)
    {
        string name = component.Split('.')[0].ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || (name.Length == 4 && (name.StartsWith("COM") || name.StartsWith("LPT")) && name[3] is >= '0' and <= '9');
    }
}
