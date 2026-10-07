using System.Buffers;
using System.Buffers.Text;
using System.Security.Cryptography;
using KalynaArchiver.Services;

namespace KeepVaultMac.Packaging;

/// <summary>
/// Explicit, bounded key-file input for the user's offline signing volume.
/// Never falls back to another credential source and never creates a string
/// containing secret bytes. The caller selects the role-specific envelope.
/// </summary>
internal static class UsbWrappingKey
{
    internal static LockedSensitiveBuffer Read(string path)
    {
        LockedSensitiveBuffer? encoded = null;
        LockedSensitiveBuffer? canonical = null;
        LockedSensitiveBuffer? key = null;
        Exception? failure = null;
        try
        {
            encoded = MacBoundSecretFile.ReadPrivateBytes(
                path, 44, 46, "USB wrapping key");
            int length = encoded.Bytes.Length;
            if (length == 46 && encoded.Bytes[44] == '\r' && encoded.Bytes[45] == '\n')
                length = 44;
            else if (length == 45 && encoded.Bytes[44] == '\n')
                length = 44;

            key = LockedSensitiveBuffer.Create(HybridKeyEnvelope.WrappingKeyBytes);
            canonical = LockedSensitiveBuffer.Create(44);
            if (length != 44
                || Base64.DecodeFromUtf8(encoded.Bytes.AsSpan(0, length), key.Bytes,
                    out int consumed, out int written) != OperationStatus.Done
                || consumed != 44 || written != HybridKeyEnvelope.WrappingKeyBytes
                || Base64.EncodeToUtf8(key.Bytes, canonical.Bytes,
                    out consumed, out written) != OperationStatus.Done
                || consumed != HybridKeyEnvelope.WrappingKeyBytes || written != 44
                || !CryptographicOperations.FixedTimeEquals(
                    canonical.Bytes, encoded.Bytes.AsSpan(0, 44)))
            {
                throw new CryptographicException("The USB wrapping key must be one canonical 32-byte base64 value.");
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        return LockedBufferTransfer.Complete(
            key, failure, "USB wrapping-key cleanup failed.", [encoded, canonical], []);
    }
}
