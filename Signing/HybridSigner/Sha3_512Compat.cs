using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Digests;

namespace KalynaArchiver.Signing;

public static class Sha3_512Compat
{
    public const int HashSizeInBytes = 64;

    public static byte[] HashData(ReadOnlySpan<byte> source)
    {
        byte[] result = new byte[HashSizeInBytes];
        _ = HashData(source, result);
        return result;
    }

    public static int HashData(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (destination.Length < HashSizeInBytes)
        {
            throw new ArgumentException($"SHA3-512 needs a {HashSizeInBytes}-byte destination.", nameof(destination));
        }

        var digest = new Sha3Digest(512);
        try
        {
            digest.BlockUpdate(source);
            int written = digest.DoFinal(destination);
            if (written != HashSizeInBytes)
                throw new CryptographicException("The SHA3-512 provider returned an invalid digest length.");
            return written;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(destination[..HashSizeInBytes]);
            throw;
        }
        finally { digest.Reset(); }
    }
}

public sealed class Sha3_512Incremental : IDisposable
{
    private Sha3Digest? _digest = new(512);

    public void AppendData(ReadOnlySpan<byte> data)
    {
        (_digest ?? throw new ObjectDisposedException(nameof(Sha3_512Incremental))).BlockUpdate(data);
    }

    public byte[] GetHashAndReset()
    {
        byte[] result = new byte[Sha3_512Compat.HashSizeInBytes];
        GetHashAndReset(result);
        return result;
    }

    public int GetHashAndReset(Span<byte> destination)
    {
        if (destination.Length < Sha3_512Compat.HashSizeInBytes)
            throw new ArgumentException("SHA3-512 destination is too short.", nameof(destination));
        Sha3Digest digest = _digest ?? throw new ObjectDisposedException(nameof(Sha3_512Incremental));
        try
        {
            int written = digest.DoFinal(destination);
            if (written != Sha3_512Compat.HashSizeInBytes)
                throw new CryptographicException("The SHA3-512 provider returned an invalid digest length.");
            return written;
        }
        catch { CryptographicOperations.ZeroMemory(destination[..Sha3_512Compat.HashSizeInBytes]); throw; }
        finally { digest.Reset(); }
    }

    public void Dispose()
    {
        try { _digest?.Reset(); }
        finally { _digest = null; }
    }
}

/// <summary>
/// RFC 2104 HMAC over SHA3-512's 72-byte rate. Own the two pads explicitly so
/// disposal clears them; a provider HMac.Reset intentionally retains its key.
/// Provider digest arrays are reset, but this is not a claim about registers,
/// runtime copies, or universal erasure of the managed heap.
/// </summary>
public sealed class HmacSha3_512 : IDisposable
{
    private const int BlockBytes = 72;
    private Sha3Digest? _digest;
    private readonly byte[] _innerPad = new byte[BlockBytes];
    private readonly byte[] _outerPad = new byte[BlockBytes];

    public HmacSha3_512(ReadOnlySpan<byte> key)
    {
        try
        {
            if (key.Length > BlockBytes) Sha3_512Compat.HashData(key, _innerPad);
            else key.CopyTo(_innerPad);
            for (int i = 0; i < BlockBytes; ++i)
            {
                _outerPad[i] = (byte)(_innerPad[i] ^ 0x5c);
                _innerPad[i] ^= 0x36;
            }
            _digest = new Sha3Digest(512);
            _digest.BlockUpdate(_innerPad);
        }
        catch { Dispose(); throw; }
    }

    public void AppendData(ReadOnlySpan<byte> data) =>
        (_digest ?? throw new ObjectDisposedException(nameof(HmacSha3_512))).BlockUpdate(data);

    public int GetHashAndReset(Span<byte> destination)
    {
        if (destination.Length < Sha3_512Compat.HashSizeInBytes)
            throw new ArgumentException("Destination span is too short for HMAC-SHA3-512 tag.", nameof(destination));
        Sha3Digest digest = _digest ?? throw new ObjectDisposedException(nameof(HmacSha3_512));
        Span<byte> innerTag = stackalloc byte[Sha3_512Compat.HashSizeInBytes];
        try
        {
            if (digest.DoFinal(innerTag) != innerTag.Length)
                throw new CryptographicException("The HMAC inner digest has an invalid length.");
            digest.BlockUpdate(_outerPad);
            digest.BlockUpdate(innerTag);
            int written = digest.DoFinal(destination);
            if (written != Sha3_512Compat.HashSizeInBytes)
                throw new CryptographicException("The HMAC-SHA3-512 provider returned an invalid tag length.");
            return written;
        }
        catch { CryptographicOperations.ZeroMemory(destination[..Sha3_512Compat.HashSizeInBytes]); throw; }
        finally
        {
            CryptographicOperations.ZeroMemory(innerTag);
            digest.Reset();
            digest.BlockUpdate(_innerPad);
        }
    }

    public byte[] GetHashAndReset()
    {
        byte[] result = new byte[Sha3_512Compat.HashSizeInBytes];
        GetHashAndReset(result);
        return result;
    }

    public void Dispose()
    {
        try { _digest?.Reset(); }
        finally
        {
            CryptographicOperations.ZeroMemory(_innerPad);
            CryptographicOperations.ZeroMemory(_outerPad);
            _digest = null;
        }
    }
}
