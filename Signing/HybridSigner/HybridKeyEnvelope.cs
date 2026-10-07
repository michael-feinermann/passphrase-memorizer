using System.Buffers.Binary;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using KalynaArchiver.Services;

namespace KeepVaultMac.Packaging;

/// <summary>
/// The two deliberately incompatible at-rest formats used by the macOS
/// release signer. An RSA-password envelope can never be interpreted as an
/// ML-DSA private-key envelope, even if a caller supplies the wrong key.
/// </summary>
internal static class HybridKeyEnvelope
{
    internal const string MldsaMagicText = "KVMDSA12";
    internal const string PfxPasswordMagicText = "KVPFXP12";
    internal const int WrappingKeyBytes = 32;
    internal const int MldsaPrivateKeyBytes = 4_896;
    internal const int MaximumPfxPasswordBytes = 4_096;

    private const int MagicBytes = 8;
    private const int LengthBytes = 4;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int HeaderBytes = MagicBytes + LengthBytes + NonceBytes;
    private const int FixedEnvelopeBytes = HeaderBytes + TagBytes;

    private static readonly byte[] MldsaMagic = Encoding.ASCII.GetBytes(MldsaMagicText);
    private static readonly byte[] PfxPasswordMagic = Encoding.ASCII.GetBytes(PfxPasswordMagicText);

    internal static void WriteMldsaPrivateKey(
        string path,
        ReadOnlySpan<byte> privateKey,
        ReadOnlySpan<byte> wrappingKey,
        MacBoundSecretFile.PrivateDirectoryLease? directoryLease = null)
    {
        if (privateKey.Length != MldsaPrivateKeyBytes)
        {
            throw new CryptographicException(
                $"The ML-DSA-87 private key must contain exactly {MldsaPrivateKeyBytes} bytes.");
        }

        Write(path, privateKey, wrappingKey, MldsaMagic, "ML-DSA-87 private key", directoryLease);
    }

    internal static LockedSensitiveBuffer ReadMldsaPrivateKey(
        string path,
        ReadOnlySpan<byte> wrappingKey) =>
        Read(
            path,
            wrappingKey,
            MldsaMagic,
            MldsaPrivateKeyBytes,
            MldsaPrivateKeyBytes,
            "ML-DSA-87 private key");

    internal static void WritePfxPassword(
        string path,
        ReadOnlySpan<byte> encodedPassword,
        ReadOnlySpan<byte> wrappingKey,
        MacBoundSecretFile.PrivateDirectoryLease? directoryLease = null)
    {
        if (encodedPassword.IsEmpty || encodedPassword.Length > MaximumPfxPasswordBytes)
        {
            throw new CryptographicException(
                $"The UTF-8 RSA PFX password must contain 1 to {MaximumPfxPasswordBytes} bytes.");
        }

        Write(path, encodedPassword, wrappingKey, PfxPasswordMagic, "RSA PFX password", directoryLease);
    }

    internal static LockedSensitiveBuffer ReadPfxPassword(
        string path,
        ReadOnlySpan<byte> wrappingKey) =>
        Read(
            path,
            wrappingKey,
            PfxPasswordMagic,
            1,
            MaximumPfxPasswordBytes,
            "RSA PFX password");

    private static void Write(
        string path,
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> wrappingKey,
        ReadOnlySpan<byte> magic,
        string description,
        MacBoundSecretFile.PrivateDirectoryLease? directoryLease)
    {
        RequireWrappingKey(wrappingKey);
        string fullPath = Path.GetFullPath(path);
        string parent = Path.GetDirectoryName(fullPath)
            ?? throw new IOException("The envelope path has no parent directory.");
        var parentInfo = new DirectoryInfo(parent);
        if (!parentInfo.Exists
            || parentInfo.LinkTarget is not null
            || (parentInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The envelope parent is missing, not a directory, or a symbolic link.");
        }
        if (File.Exists(fullPath) || Directory.Exists(fullPath))
        {
            throw new IOException($"An envelope already exists and will not be overwritten: {fullPath}");
        }

        byte[] envelope = new byte[checked(FixedEnvelopeBytes + payload.Length)];
        magic.CopyTo(envelope);
        BinaryPrimitives.WriteUInt32LittleEndian(
            envelope.AsSpan(MagicBytes, LengthBytes),
            checked((uint)payload.Length));
        RandomNumberGenerator.Fill(envelope.AsSpan(MagicBytes + LengthBytes, NonceBytes));
        try
        {
            using (var aes = new AesGcm(wrappingKey, TagBytes))
            {
                aes.Encrypt(
                    envelope.AsSpan(MagicBytes + LengthBytes, NonceBytes),
                    payload,
                    envelope.AsSpan(HeaderBytes, payload.Length),
                    envelope.AsSpan(HeaderBytes + payload.Length, TagBytes),
                    envelope.AsSpan(0, MagicBytes + LengthBytes));
            }

            MacBoundSecretFile? staging = null;
            try
            {
                staging = MacBoundSecretFile.Create(fullPath, directoryLease);
                FileStream stream = staging.Stream;
                stream.Write(envelope);
                stream.Flush(flushToDisk: true);

                // The proof is read from the same O_EXCL descriptor. A path
                // substitution cannot make the round trip validate a
                // different object.
                stream.Position = 0;
                byte[] stagedEnvelope = new byte[envelope.Length];
                LockedSensitiveBuffer? recovered = null;
                Exception? proofFailure = null;
                try
                {
                    stream.ReadExactly(stagedEnvelope);
                    recovered = DecryptEnvelopeBytes(
                        stagedEnvelope,
                        wrappingKey,
                        magic,
                        payload.Length,
                        payload.Length,
                        description);
                    if (!CryptographicOperations.FixedTimeEquals(recovered.Bytes, payload))
                    {
                        throw new CryptographicException(
                            $"The wrapped {description} did not decrypt back to the original.");
                    }
                }
                catch (Exception ex)
                {
                    proofFailure = ex;
                }
                CryptographicOperations.ZeroMemory(stagedEnvelope);
                SecureMemory.ZeroAndDisposeAllPreservingFailure(
                    proofFailure,
                    $"The {description} round-trip proof failed during secure cleanup.",
                    recovered);
                if (proofFailure is not null)
                {
                    ExceptionDispatchInfo.Capture(proofFailure).Throw();
                }

                staging.Publish();
                staging.Dispose();
                staging = null;
            }
            catch (Exception primaryFailure)
            {
                if (staging is null)
                {
                    throw;
                }

                var cleanupFailures = new List<Exception>();
                try
                {
                    WipeOpenFile(staging.Stream);
                }
                catch (Exception cleanupFailure)
                {
                    cleanupFailures.Add(cleanupFailure);
                }
                try
                {
                    _ = staging.RemoveCurrentNameIfStillOwned();
                }
                catch (Exception cleanupFailure)
                {
                    cleanupFailures.Add(cleanupFailure);
                }
                try
                {
                    staging.Dispose();
                }
                catch (Exception cleanupFailure)
                {
                    cleanupFailures.Add(cleanupFailure);
                }
                if (cleanupFailures.Count != 0)
                {
                    throw new AggregateException(
                        new[] { primaryFailure }.Concat(cleanupFailures));
                }
                throw;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(envelope);
        }
    }

    private static LockedSensitiveBuffer Read(
        string path,
        ReadOnlySpan<byte> wrappingKey,
        ReadOnlySpan<byte> expectedMagic,
        int minimumPayloadBytes,
        int maximumPayloadBytes,
        string description)
    {
        RequireWrappingKey(wrappingKey);
        int minimumEnvelopeBytes = checked(FixedEnvelopeBytes + minimumPayloadBytes);
        int maximumEnvelopeBytes = checked(FixedEnvelopeBytes + maximumPayloadBytes);
        LockedSensitiveBuffer? envelope = null;
        LockedSensitiveBuffer? result = null;
        Exception? operationFailure = null;
        try
        {
            envelope = MacBoundSecretFile.ReadPrivateBytes(
                path,
                minimumEnvelopeBytes,
                maximumEnvelopeBytes,
                $"{description} envelope");
            result = DecryptEnvelopeBytes(
                envelope.Bytes,
                wrappingKey,
                expectedMagic,
                minimumPayloadBytes,
                maximumPayloadBytes,
                description);
        }
        catch (InvalidDataException ex)
        {
            operationFailure = new CryptographicException(
                $"The {description} envelope has a non-canonical length.",
                ex);
        }
        catch (Exception ex)
        {
            operationFailure = ex;
        }

        return LockedBufferTransfer.Complete(
            result,
            operationFailure,
            $"The {description} envelope operation failed during secure cleanup.",
            [envelope],
            []);
    }

    private static LockedSensitiveBuffer DecryptEnvelopeBytes(
        ReadOnlySpan<byte> envelope,
        ReadOnlySpan<byte> wrappingKey,
        ReadOnlySpan<byte> expectedMagic,
        int minimumPayloadBytes,
        int maximumPayloadBytes,
        string description)
    {
        if (envelope.Length < FixedEnvelopeBytes + minimumPayloadBytes
            || envelope.Length > FixedEnvelopeBytes + maximumPayloadBytes
            || !CryptographicOperations.FixedTimeEquals(
                envelope[..MagicBytes],
                expectedMagic))
        {
            throw new CryptographicException(
                $"The {description} envelope has the wrong secret type, version, or length.");
        }

        uint encodedLength = BinaryPrimitives.ReadUInt32LittleEndian(
            envelope.Slice(MagicBytes, LengthBytes));
        if (encodedLength < minimumPayloadBytes
            || encodedLength > maximumPayloadBytes
            || encodedLength != envelope.Length - FixedEnvelopeBytes)
        {
            throw new CryptographicException(
                $"The {description} envelope carries a non-canonical payload length.");
        }

        int payloadLength = checked((int)encodedLength);
        LockedSensitiveBuffer payload = LockedSensitiveBuffer.Create(payloadLength);
        try
        {
            using var aes = new AesGcm(wrappingKey, TagBytes);
            aes.Decrypt(
                envelope.Slice(MagicBytes + LengthBytes, NonceBytes),
                envelope.Slice(HeaderBytes, payloadLength),
                envelope.Slice(HeaderBytes + payloadLength, TagBytes),
                payload.Bytes,
                envelope[..(MagicBytes + LengthBytes)]);
            return payload;
        }
        catch (Exception operationFailure)
        {
            return LockedBufferTransfer.Complete(
                null,
                operationFailure,
                $"The {description} payload failed during secure cleanup.",
                [payload],
                []);
        }
    }

    internal static void WipeOpenFile(FileStream stream)
    {
        long remaining = stream.Length;
        stream.Position = 0;
        byte[] zeros = new byte[64 * 1024];
        try
        {
            while (remaining > 0)
            {
                int count = (int)Math.Min(zeros.Length, remaining);
                stream.Write(zeros, 0, count);
                remaining -= count;
            }
            stream.Flush(flushToDisk: true);
            stream.SetLength(0);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(zeros);
        }
    }

    private static void RequireWrappingKey(ReadOnlySpan<byte> wrappingKey)
    {
        if (wrappingKey.Length != WrappingKeyBytes)
        {
            throw new CryptographicException(
                $"A signing-secret wrapping key must contain exactly {WrappingKeyBytes} bytes.");
        }
    }
}
