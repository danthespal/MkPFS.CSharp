using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace MkPFS.Core.Crypto;

/// <summary>
/// Managed SHA3-256 (FIPS 202, Keccak-f[1600], rate 136, domain byte 0x06). PS5 packages use SHA3-256
/// for EKPFS derivation, block hashes and CNT digests. <c>System.Security.Cryptography.SHA3_256</c> is
/// platform crypto and unavailable on macOS, so MkPFS carries its own, and uses the platform's (about 30% faster)
/// where it exists; both give the same digests. Not thread-safe; use one instance per thread.
/// </summary>
public sealed class SHA3256
{
    /// <summary>Digest size in bytes.</summary>
    public const int HashSize = 32;

    private const int Rate = 136;

    private static readonly ulong[] RoundConstants =
    [
        0x0000000000000001UL, 0x0000000000008082UL, 0x800000000000808AUL, 0x8000000080008000UL,
        0x000000000000808BUL, 0x0000000080000001UL, 0x8000000080008081UL, 0x8000000000008009UL,
        0x000000000000008AUL, 0x0000000000000088UL, 0x0000000080008009UL, 0x000000008000000AUL,
        0x000000008000808BUL, 0x800000000000008BUL, 0x8000000000008089UL, 0x8000000000008003UL,
        0x8000000000008002UL, 0x8000000000000080UL, 0x000000000000800AUL, 0x800000008000000AUL,
        0x8000000080008081UL, 0x8000000000008080UL, 0x0000000080000001UL, 0x8000000080008008UL,
    ];

    private readonly ulong[] _state = new ulong[25];
    private readonly byte[] _buffer = new byte[Rate];
    private readonly IncrementalHash? _platform;
    private int _buffered;

    /// <summary>Create a hasher (platform SHA3-256 where the operating system has it).</summary>
    public SHA3256()
        : this(SHA3_256.IsSupported)
    {
    }

    /// <summary>Create a hasher on the platform or the managed implementation (tests compare the two).</summary>
    /// <param name="platform">Use the platform's SHA3-256.</param>
    internal SHA3256(bool platform)
    {
        _platform = platform ? IncrementalHash.CreateHash(HashAlgorithmName.SHA3_256) : null;
    }

    /// <summary>Hash <paramref name="data"/> in one call.</summary>
    /// <param name="data">Input bytes.</param>
    /// <returns>32-byte digest.</returns>
    public static byte[] HashData(ReadOnlySpan<byte> data)
    {
        byte[] hash = new byte[HashSize];
        HashData(data, hash);
        return hash;
    }

    /// <summary>Hash <paramref name="data"/> into <paramref name="destination"/>.</summary>
    /// <param name="data">Input bytes.</param>
    /// <param name="destination">At least 32 bytes.</param>
    public static void HashData(ReadOnlySpan<byte> data, Span<byte> destination)
    {
        if (SHA3_256.IsSupported)
        {
            SHA3_256.HashData(data, destination);
            return;
        }

        SHA3256 hasher = new(platform: false);
        hasher.Append(data);
        hasher.GetHashAndReset(destination);
    }

    /// <summary>Absorb more input.</summary>
    /// <param name="data">Next bytes.</param>
    public void Append(ReadOnlySpan<byte> data)
    {
        if (_platform is not null)
        {
            _platform.AppendData(data);
            return;
        }

        if (_buffered > 0)
        {
            int take = Math.Min(Rate - _buffered, data.Length);
            data[..take].CopyTo(_buffer.AsSpan(_buffered));
            _buffered += take;
            data = data[take..];
            if (_buffered < Rate)
            {
                return;
            }

            AbsorbBlock(_buffer);
            _buffered = 0;
        }

        while (data.Length >= Rate)
        {
            AbsorbBlock(data[..Rate]);
            data = data[Rate..];
        }

        data.CopyTo(_buffer);
        _buffered = data.Length;
    }

    /// <summary>Finish the digest into <paramref name="destination"/> and reset for reuse.</summary>
    /// <param name="destination">At least 32 bytes.</param>
    public void GetHashAndReset(Span<byte> destination)
    {
        if (destination.Length < HashSize)
        {
            throw new ArgumentException("Destination must hold 32 bytes", nameof(destination));
        }

        if (_platform is not null)
        {
            _platform.GetHashAndReset(destination);
            return;
        }

        // FIPS 202 padding: SHA-3 domain bits 01, then pad10*1.
        _buffer.AsSpan(_buffered).Clear();
        _buffer[_buffered] ^= 0x06;
        _buffer[Rate - 1] ^= 0x80;
        AbsorbBlock(_buffer);

        for (int i = 0; i < HashSize / 8; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(destination[(i * 8)..], _state[i]);
        }

        Array.Clear(_state);
        _buffered = 0;
    }

    /// <summary>Finish the digest and reset for reuse.</summary>
    /// <returns>32-byte digest.</returns>
    public byte[] GetHashAndReset()
    {
        byte[] hash = new byte[HashSize];
        GetHashAndReset(hash);
        return hash;
    }

    private void AbsorbBlock(ReadOnlySpan<byte> block)
    {
        for (int i = 0; i < Rate / 8; i++)
        {
            _state[i] ^= BinaryPrimitives.ReadUInt64LittleEndian(block[(i * 8)..]);
        }

        Permute(_state);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Permute(ulong[] s)
    {
        ulong a00 = s[0], a01 = s[1], a02 = s[2], a03 = s[3], a04 = s[4];
        ulong a05 = s[5], a06 = s[6], a07 = s[7], a08 = s[8], a09 = s[9];
        ulong a10 = s[10], a11 = s[11], a12 = s[12], a13 = s[13], a14 = s[14];
        ulong a15 = s[15], a16 = s[16], a17 = s[17], a18 = s[18], a19 = s[19];
        ulong a20 = s[20], a21 = s[21], a22 = s[22], a23 = s[23], a24 = s[24];

        for (int round = 0; round < 24; round++)
        {
            // Theta.
            ulong c0 = a00 ^ a05 ^ a10 ^ a15 ^ a20;
            ulong c1 = a01 ^ a06 ^ a11 ^ a16 ^ a21;
            ulong c2 = a02 ^ a07 ^ a12 ^ a17 ^ a22;
            ulong c3 = a03 ^ a08 ^ a13 ^ a18 ^ a23;
            ulong c4 = a04 ^ a09 ^ a14 ^ a19 ^ a24;
            ulong d0 = c4 ^ ulong.RotateLeft(c1, 1);
            ulong d1 = c0 ^ ulong.RotateLeft(c2, 1);
            ulong d2 = c1 ^ ulong.RotateLeft(c3, 1);
            ulong d3 = c2 ^ ulong.RotateLeft(c4, 1);
            ulong d4 = c3 ^ ulong.RotateLeft(c0, 1);

            // Rho and pi: b[y, 2x + 3y] = rot(a[x, y] ^ d[x], r[x, y]).
            ulong b00 = a00 ^ d0;
            ulong b10 = ulong.RotateLeft(a01 ^ d1, 1);
            ulong b20 = ulong.RotateLeft(a02 ^ d2, 62);
            ulong b05 = ulong.RotateLeft(a03 ^ d3, 28);
            ulong b15 = ulong.RotateLeft(a04 ^ d4, 27);
            ulong b16 = ulong.RotateLeft(a05 ^ d0, 36);
            ulong b01 = ulong.RotateLeft(a06 ^ d1, 44);
            ulong b11 = ulong.RotateLeft(a07 ^ d2, 6);
            ulong b21 = ulong.RotateLeft(a08 ^ d3, 55);
            ulong b06 = ulong.RotateLeft(a09 ^ d4, 20);
            ulong b07 = ulong.RotateLeft(a10 ^ d0, 3);
            ulong b17 = ulong.RotateLeft(a11 ^ d1, 10);
            ulong b02 = ulong.RotateLeft(a12 ^ d2, 43);
            ulong b12 = ulong.RotateLeft(a13 ^ d3, 25);
            ulong b22 = ulong.RotateLeft(a14 ^ d4, 39);
            ulong b23 = ulong.RotateLeft(a15 ^ d0, 41);
            ulong b08 = ulong.RotateLeft(a16 ^ d1, 45);
            ulong b18 = ulong.RotateLeft(a17 ^ d2, 15);
            ulong b03 = ulong.RotateLeft(a18 ^ d3, 21);
            ulong b13 = ulong.RotateLeft(a19 ^ d4, 8);
            ulong b14 = ulong.RotateLeft(a20 ^ d0, 18);
            ulong b24 = ulong.RotateLeft(a21 ^ d1, 2);
            ulong b09 = ulong.RotateLeft(a22 ^ d2, 61);
            ulong b19 = ulong.RotateLeft(a23 ^ d3, 56);
            ulong b04 = ulong.RotateLeft(a24 ^ d4, 14);

            // Chi and iota.
            a00 = b00 ^ (~b01 & b02) ^ RoundConstants[round];
            a01 = b01 ^ (~b02 & b03);
            a02 = b02 ^ (~b03 & b04);
            a03 = b03 ^ (~b04 & b00);
            a04 = b04 ^ (~b00 & b01);
            a05 = b05 ^ (~b06 & b07);
            a06 = b06 ^ (~b07 & b08);
            a07 = b07 ^ (~b08 & b09);
            a08 = b08 ^ (~b09 & b05);
            a09 = b09 ^ (~b05 & b06);
            a10 = b10 ^ (~b11 & b12);
            a11 = b11 ^ (~b12 & b13);
            a12 = b12 ^ (~b13 & b14);
            a13 = b13 ^ (~b14 & b10);
            a14 = b14 ^ (~b10 & b11);
            a15 = b15 ^ (~b16 & b17);
            a16 = b16 ^ (~b17 & b18);
            a17 = b17 ^ (~b18 & b19);
            a18 = b18 ^ (~b19 & b15);
            a19 = b19 ^ (~b15 & b16);
            a20 = b20 ^ (~b21 & b22);
            a21 = b21 ^ (~b22 & b23);
            a22 = b22 ^ (~b23 & b24);
            a23 = b23 ^ (~b24 & b20);
            a24 = b24 ^ (~b20 & b21);
        }

        s[0] = a00; s[1] = a01; s[2] = a02; s[3] = a03; s[4] = a04;
        s[5] = a05; s[6] = a06; s[7] = a07; s[8] = a08; s[9] = a09;
        s[10] = a10; s[11] = a11; s[12] = a12; s[13] = a13; s[14] = a14;
        s[15] = a15; s[16] = a16; s[17] = a17; s[18] = a18; s[19] = a19;
        s[20] = a20; s[21] = a21; s[22] = a22; s[23] = a23; s[24] = a24;
    }
}
