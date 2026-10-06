using System.Buffers;
using System.Buffers.Binary;
using MkPFS.Build.FPKG.Prospero;
using MkPFS.Core.Crypto;

namespace MkPFS.Build.FPKG;

/// <summary>
/// Write-through stream that hashes what passes through it, so a package is never read back: SHA3-256 of every
/// 64 KiB block (the last zero-padded; outer PFS block digests), and/or SHA3-256 of everything plus CRC-32C of every
/// 64 KiB block (the mount-image digest and <c>playgo-chunk.crc</c>). Hashing runs on a background thread, behind the
/// writes.
/// </summary>
internal sealed class HashingWriteStream(Stream inner, bool blockDigests, bool wholeDigest, Action<long>? written = null) : Stream
{
    private const int BlockSize = 0x10000;

    private readonly byte[] _block = new byte[BlockSize];
    private readonly MemoryStream _digests = new();
    private readonly MemoryStream _crc = new();
    private readonly SHA3256 _whole = new();
    private readonly SerialWorker _hasher = new();
    private int _fill;
    private long _position;

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => _position;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    /// <summary>SHA3-256 of each block, 32 bytes each (call once, after the last write).</summary>
    /// <returns>Digests.</returns>
    public byte[] BlockDigests()
    {
        _hasher.Drain();
        if (_fill > 0 || _digests.Length == 0)
        {
            _block.AsSpan(_fill).Clear();
            _digests.Write(SHA3256.HashData(_block));
            _fill = 0;
        }

        return _digests.ToArray();
    }

    /// <summary>SHA3-256 of everything written and CRC-32C of each block (call once, after the last write).</summary>
    /// <returns>Digest and the little-endian CRC table.</returns>
    public (byte[] Digest, byte[] Crc) WholeDigest()
    {
        _hasher.Drain();
        if (_fill > 0)
        {
            AppendCrc(_block.AsSpan(0, _fill));
            _fill = 0;
        }

        return (_whole.GetHashAndReset(), _crc.ToArray());
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        inner.Write(buffer);
        _position += buffer.Length;
        written?.Invoke(_position);
        byte[] copy = ArrayPool<byte>.Shared.Rent(buffer.Length);
        buffer.CopyTo(copy);
        int length = buffer.Length;
        _hasher.Post(() =>
        {
            Hash(copy.AsSpan(0, length));
            ArrayPool<byte>.Shared.Return(copy);
        });
    }

    public override void Flush() => inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hasher.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Hash(ReadOnlySpan<byte> buffer)
    {
        if (wholeDigest)
        {
            _whole.Append(buffer);
        }

        while (!buffer.IsEmpty)
        {
            int n = Math.Min(BlockSize - _fill, buffer.Length);
            buffer[..n].CopyTo(_block.AsSpan(_fill));
            _fill += n;
            buffer = buffer[n..];
            if (_fill == BlockSize)
            {
                if (blockDigests)
                {
                    _digests.Write(SHA3256.HashData(_block));
                }

                if (wholeDigest)
                {
                    AppendCrc(_block);
                }

                _fill = 0;
            }
        }
    }

    private void AppendCrc(ReadOnlySpan<byte> block)
    {
        Span<byte> value = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(value, ProsperoCrc32C.Compute(block));
        _crc.Write(value);
    }
}
