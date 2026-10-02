using System.Buffers.Binary;
using System.Security.Cryptography;

namespace MkPFS.Core.Crypto;

/// <summary>
/// AES-XTS (IEEE 1619) for whole 16-byte-aligned data units, built on AES-ECB. PFS uses 0x1000-byte
/// sectors, so ciphertext stealing is never needed. Not thread-safe; use one instance per thread.
/// </summary>
public sealed class XtsAes : IDisposable
{
    private const int BlockSize = 16;
    private readonly Aes _data;
    private readonly Aes _tweak;
    private byte[] _tweaks = [];

    /// <summary>Create an XTS cipher.</summary>
    /// <param name="key">Data key followed by tweak key (32 bytes for AES-128-XTS, 64 for AES-256-XTS).</param>
    public XtsAes(ReadOnlySpan<byte> key)
    {
        if (key.Length is not (32 or 64))
        {
            throw new ArgumentException("XTS key must be 32 or 64 bytes", nameof(key));
        }

        int half = key.Length / 2;
        _data = Aes.Create();
        _data.Key = key[..half].ToArray();
        _tweak = Aes.Create();
        _tweak.Key = key[half..].ToArray();
    }

    /// <summary>Encrypt one data unit in place.</summary>
    /// <param name="buffer">Data unit, a multiple of 16 bytes.</param>
    /// <param name="sectorNumber">Data unit number (little-endian 128-bit tweak input).</param>
    public void Encrypt(Span<byte> buffer, ulong sectorNumber) => Transform(buffer, sectorNumber, encrypt: true);

    /// <summary>Decrypt one data unit in place.</summary>
    /// <param name="buffer">Data unit, a multiple of 16 bytes.</param>
    /// <param name="sectorNumber">Data unit number (little-endian 128-bit tweak input).</param>
    public void Decrypt(Span<byte> buffer, ulong sectorNumber) => Transform(buffer, sectorNumber, encrypt: false);

    /// <inheritdoc />
    public void Dispose()
    {
        _data.Dispose();
        _tweak.Dispose();
    }

    private void Transform(Span<byte> buffer, ulong sectorNumber, bool encrypt)
    {
        if (buffer.Length % BlockSize != 0)
        {
            throw new ArgumentException("XTS data unit must be a multiple of 16 bytes", nameof(buffer));
        }

        if (_tweaks.Length < buffer.Length)
        {
            _tweaks = new byte[buffer.Length];
        }

        // Tweak for block 0 = AES(K2, sector number); each next tweak multiplies by alpha in GF(2^128).
        Span<byte> tweaks = _tweaks.AsSpan(0, buffer.Length);
        Span<byte> input = stackalloc byte[BlockSize];
        input.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(input, sectorNumber);
        _tweak.EncryptEcb(input, tweaks[..BlockSize], PaddingMode.None);
        for (int offset = BlockSize; offset < tweaks.Length; offset += BlockSize)
        {
            MultiplyByAlpha(tweaks.Slice(offset - BlockSize, BlockSize), tweaks.Slice(offset, BlockSize));
        }

        Xor(buffer, tweaks);
        if (encrypt)
        {
            _data.EncryptEcb(buffer, buffer, PaddingMode.None);
        }
        else
        {
            _data.DecryptEcb(buffer, buffer, PaddingMode.None);
        }

        Xor(buffer, tweaks);
    }

    private static void MultiplyByAlpha(ReadOnlySpan<byte> previous, Span<byte> next)
    {
        ulong low = BinaryPrimitives.ReadUInt64LittleEndian(previous);
        ulong high = BinaryPrimitives.ReadUInt64LittleEndian(previous[8..]);
        ulong carry = high >> 63;
        high = (high << 1) | (low >> 63);
        low = (low << 1) ^ (carry * 0x87);
        BinaryPrimitives.WriteUInt64LittleEndian(next, low);
        BinaryPrimitives.WriteUInt64LittleEndian(next[8..], high);
    }

    private static void Xor(Span<byte> target, ReadOnlySpan<byte> mask)
    {
        for (int i = 0; i < target.Length; i++)
        {
            target[i] ^= mask[i];
        }
    }
}
