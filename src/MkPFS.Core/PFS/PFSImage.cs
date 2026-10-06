using System.Runtime.ExceptionServices;
using MkPFS.Core.Crypto;
using MkPFS.Core.PFSC;
using MkPFS.Core.Util;

namespace MkPFS.Core.PFS;

/// <summary>
/// Read access to a PFS image: header, inode table and payloads, with transparent AES-XTS decryption
/// (port of Python <c>read_image_bytes</c>, <c>parse_image_inodes</c>, <c>read_image_inode_payload</c>,
/// <c>iter_inode_logical_blocks</c>).
/// </summary>
public sealed class PFSImage : IDisposable
{
    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly byte[] _ekpfs;
    private readonly bool _newCrypt;
    private XtsAes? _xts;

    private PFSImage(Stream stream, bool ownsStream, PFSHeader header, byte[] ekpfs, bool newCrypt)
    {
        _stream = stream;
        _ownsStream = ownsStream;
        Header = header;
        _ekpfs = ekpfs;
        _newCrypt = newCrypt;
    }

    /// <summary>Parsed header.</summary>
    public PFSHeader Header { get; }

    /// <summary>Image length in bytes.</summary>
    public long Length => _stream.Length;

    /// <summary>EKPFS key used for signature checks.</summary>
    internal byte[] EkpfsForSigning => _ekpfs;

    /// <summary>Open an image file.</summary>
    /// <param name="path">Image path.</param>
    /// <param name="ekpfs">EKPFS key; all zeros when <see langword="null"/>.</param>
    /// <param name="newCrypt">Use the newCrypt key derivation.</param>
    /// <returns>Open image.</returns>
    public static PFSImage Open(string path, byte[]? ekpfs = null, bool newCrypt = false)
    {
        FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
        try
        {
            return Open(stream, ownsStream: true, ekpfs, newCrypt);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Open an image over a seekable stream.</summary>
    /// <param name="stream">Readable, seekable stream.</param>
    /// <param name="ownsStream">Dispose the stream with the image.</param>
    /// <param name="ekpfs">EKPFS key; all zeros when <see langword="null"/>.</param>
    /// <param name="newCrypt">Use the newCrypt key derivation.</param>
    /// <returns>Open image.</returns>
    public static PFSImage Open(Stream stream, bool ownsStream, byte[]? ekpfs = null, bool newCrypt = false)
    {
        byte[] key = ekpfs ?? PFSConstants.ZeroEkpfs.ToArray();
        if (key.Length != PFSConstants.EkpfsSize)
        {
            throw new ArgumentException($"EKPFS key must be {PFSConstants.EkpfsSize} bytes, got {key.Length}", nameof(ekpfs));
        }

        byte[] head = ReadExact(stream, 0, PFSHeader.Size);
        return new PFSImage(stream, ownsStream, PFSHeader.Parse(head), key, newCrypt);
    }

    /// <summary>
    /// Read bytes, decrypting encrypted images (Python <c>read_image_bytes</c>). Block 0 is plaintext;
    /// a read that straddles the end of block 0 is rejected.
    /// </summary>
    /// <param name="offset">Absolute offset.</param>
    /// <param name="size">Byte count.</param>
    /// <returns>Plaintext bytes.</returns>
    public byte[] Read(long offset, int size)
    {
        if (size <= 0)
        {
            return [];
        }

        long blockSize = Header.BlockSize;
        if (!Header.IsEncrypted || offset < blockSize)
        {
            if (offset < blockSize && blockSize < offset + size)
            {
                throw new InvalidDataException("mixed plaintext/encrypted reads are not supported");
            }

            return ReadExact(_stream, offset, size);
        }

        const int Sector = PFSConstants.XtsSectorSize;
        long alignedStart = offset / Sector * Sector;
        long alignedEnd = Sizes.CeilDiv(offset + size, Sector) * Sector;
        byte[] raw = ReadExact(_stream, alignedStart, ToSize(alignedEnd - alignedStart));
        _xts ??= new XtsAes(PFSKeys.XtsKey(_ekpfs, Header.Seed, _newCrypt));
        ulong sector = (ulong)(alignedStart / Sector);
        for (int chunk = 0; chunk < raw.Length; chunk += Sector, sector++)
        {
            _xts.Decrypt(raw.AsSpan(chunk, Sector), sector);
        }

        int inner = (int)(offset - alignedStart);
        return inner == 0 && raw.Length == size ? raw : raw.AsSpan(inner, size).ToArray();
    }

    /// <summary>Read the plaintext bytes without decryption (signatures live in the plaintext header).</summary>
    /// <param name="offset">Absolute offset.</param>
    /// <param name="size">Byte count.</param>
    /// <returns>Raw bytes.</returns>
    public byte[] ReadRaw(long offset, int size) => ReadExact(_stream, offset, size);

    /// <summary>Read one filesystem block.</summary>
    /// <param name="block">Block number.</param>
    /// <returns>Block bytes.</returns>
    public byte[] ReadBlock(long block) => Read(BlockOffset(block), ToSize(Header.BlockSize));

    /// <summary>Parse the inode table (Python <c>parse_image_inodes</c>).</summary>
    /// <returns>Inodes in table order.</returns>
    public List<PFSInode> ReadInodes()
    {
        bool signed = Header.IsSigned;
        int bits = signed ? SignedInodeLayout.BitsFromMode(Header.Mode) : 32;
        int inodeSize = signed ? SignedInodeLayout.For(bits).InodeSize : PFSConstants.InodeD32Size;
        long perBlock = Header.BlockSize / inodeSize;
        if (perBlock <= 0)
        {
            throw new InvalidDataException("block size too small for inode table");
        }

        List<PFSInode> inodes = [];
        long index = 0;
        for (long blockIndex = 0; blockIndex < Header.InodeBlockCount; blockIndex++)
        {
            byte[] block = ReadBlock(1 + blockIndex);
            for (long i = 0; i < perBlock; i++)
            {
                if (index >= Header.InodeCount)
                {
                    return inodes;
                }

                inodes.Add(PFSInode.Parse(block.AsSpan((int)(i * inodeSize), inodeSize), index, signed, bits));
                index++;
            }
        }

        return inodes;
    }

    /// <summary>
    /// Data block numbers of a signed inode: direct blocks, then <c>ib[0]</c> records, then the two-level
    /// <c>ib[1]</c> chain (Python <c>resolve_signed_inode_blocks</c>).
    /// </summary>
    /// <param name="inode">Signed inode.</param>
    /// <param name="errors">Receives chain problems when not <see langword="null"/>.</param>
    /// <returns>Block numbers in payload order.</returns>
    public List<long> ResolveSignedBlocks(PFSInode inode, List<string>? errors = null)
    {
        List<long> blocks = [];
        long direct = Math.Min(inode.Blocks, PFSConstants.MaxDirectBlocks);
        blocks.AddRange(inode.Db.Take((int)direct));
        long remaining = inode.Blocks - direct;
        int bits = SignedInodeLayout.BitsFromMode(Header.Mode);
        long perBlock = Header.BlockSize / SignedInodeLayout.For(bits).EntrySize;

        if (remaining > 0)
        {
            if (inode.Ib[0] <= 0)
            {
                errors?.Add($"inode {inode.Number} missing ib[0] for signed block chain");
                return blocks;
            }

            List<(byte[] Sig, long Block)> records = ReadSigRecords(inode.Ib[0], bits);
            int take = (int)Math.Min(remaining, perBlock);
            blocks.AddRange(records.Take(take).Select(r => r.Block));
            remaining -= take;
        }

        if (remaining > 0)
        {
            if (inode.Ib[1] <= 0)
            {
                errors?.Add($"inode {inode.Number} missing ib[1] for signed block chain");
                return blocks;
            }

            foreach ((byte[] _, long child) in ReadSigRecords(inode.Ib[1], bits))
            {
                if (remaining <= 0)
                {
                    break;
                }

                int take = (int)Math.Min(remaining, perBlock);
                blocks.AddRange(ReadSigRecords(child, bits).Take(take).Select(r => r.Block));
                remaining -= take;
            }
        }

        if (remaining > 0)
        {
            errors?.Add($"inode {inode.Number} uses unsupported signed indirection depth");
        }

        return blocks;
    }

    /// <summary>Parse one signature-record block (Python <c>parse_sig_record_block</c>).</summary>
    /// <param name="block">Block number.</param>
    /// <param name="inodeBits">Signed width.</param>
    /// <returns>(signature, block) records.</returns>
    public List<(byte[] Sig, long Block)> ReadSigRecords(long block, int inodeBits)
    {
        byte[] blob = ReadBlock(block);
        SignedInodeLayout layout = SignedInodeLayout.For(inodeBits);
        List<(byte[], long)> records = [];
        for (int offset = 0; offset + layout.EntrySize <= blob.Length; offset += layout.EntrySize)
        {
            records.Add((blob.AsSpan(offset, PFSConstants.SigSize).ToArray(), layout.ReadPointer(blob.AsSpan(offset))));
        }

        return records;
    }

    /// <summary>Read an inode's stored payload (Python <c>read_image_inode_payload</c>).</summary>
    /// <param name="inode">Inode.</param>
    /// <returns>Stored bytes (PFSC when compressed).</returns>
    public byte[] ReadStoredPayload(PFSInode inode)
    {
        if (inode.Blocks <= 0)
        {
            return [];
        }

        long size = inode.StoredSize;
        if (size < 0)
        {
            throw new InvalidDataException($"inode {inode.Number} has negative stored payload size");
        }

        if (!inode.IsSigned)
        {
            return Read(BlockOffset(inode.Db[0]), ToSize(size));
        }

        using MemoryStream data = new();
        foreach (long block in ResolveSignedBlocks(inode))
        {
            data.Write(ReadBlock(block));
        }

        if (data.Length < size)
        {
            throw new InvalidDataException($"inode {inode.Number} payload truncated");
        }

        return data.GetBuffer().AsSpan(0, ToSize(size)).ToArray();
    }

    /// <summary>
    /// Stream an inode's logical payload in bounded chunks (Python <c>iter_inode_logical_blocks</c>). The
    /// returned memory is valid until the next iteration.
    /// </summary>
    /// <param name="inode">File inode.</param>
    /// <param name="chunkSize">Chunk size for raw payloads.</param>
    /// <returns>Logical chunks in order.</returns>
    public IEnumerable<ReadOnlyMemory<byte>> ReadLogicalChunks(PFSInode inode, int chunkSize = 4 * 1024 * 1024)
    {
        if (chunkSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkSize), "chunk size must be positive");
        }

        if (inode.Blocks <= 0 || inode.LogicalSize <= 0)
        {
            yield break;
        }

        // Signed payloads are size-limited by their layout; decode buffered.
        if (inode.IsSigned)
        {
            byte[] stored = ReadStoredPayload(inode);
            yield return inode.IsCompressed ? PFSCReader.DecodePayload(stored, inode.LogicalSize) : stored;
            yield break;
        }

        long baseOffset = BlockOffset(inode.Db[0]);
        long expected = inode.LogicalSize;
        if (!inode.IsCompressed)
        {
            for (long done = 0; done < expected;)
            {
                int take = (int)Math.Min(chunkSize, expected - done);
                yield return Read(baseOffset + done, take);
                done += take;
            }

            yield break;
        }

        PFSCReader reader = PFSCReader.Open(new PFSImageStream(this), baseOffset, inode.StoredSize);
        if (expected > reader.LogicalSize)
        {
            throw new InvalidDataException($"PFSC logical size {reader.LogicalSize} is smaller than inode size {expected}");
        }

        // Batches of blocks: stored bytes are read in order, decoded in parallel (each block is independent),
        // then handed out in order. A block that fails to decode throws when its turn comes, after the blocks
        // before it, as a block-by-block decode would.
        int blockSize = reader.Header.LogicalBlockSize;
        int batch = Math.Max(1, chunkSize / Math.Max(1, blockSize));
        byte[][] decoded = new byte[batch][];
        byte[]?[] storedBlocks = new byte[batch][];
        ExceptionDispatchInfo?[] errors = new ExceptionDispatchInfo?[batch];
        long blocks = Math.Min(reader.BlockCount, (expected + blockSize - 1) / blockSize);
        long emitted = 0;
        for (long first = 0; first < blocks; first += batch)
        {
            int count = (int)Math.Min(batch, blocks - first);
            for (int k = 0; k < count; k++)
            {
                errors[k] = null;
                try
                {
                    int length = reader.StoredLength(first + k);
                    storedBlocks[k] = length > blockSize ? null : new byte[length];
                    if (storedBlocks[k] is { } bytes)
                    {
                        reader.ReadStoredBlock(first + k, bytes);
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException)
                {
                    errors[k] = ExceptionDispatchInfo.Capture(ex);
                }
            }

            long batchFirst = first;
            Parallel.For(0, count, k =>
            {
                if (errors[k] is not null)
                {
                    return;
                }

                decoded[k] ??= new byte[blockSize];
                try
                {
                    // An oversized block is reported by DecodeBlock's own check, with its message.
                    if (storedBlocks[k] is null)
                    {
                        throw new InvalidDataException($"PFSC block {batchFirst + k} stored size {reader.StoredLength(batchFirst + k)} exceeds logical size {blockSize}");
                    }

                    PFSCReader.DecodeStoredBlock(storedBlocks[k], decoded[k], batchFirst + k);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException)
                {
                    errors[k] = ExceptionDispatchInfo.Capture(ex);
                }
            });

            for (int k = 0; k < count && emitted < expected; k++)
            {
                errors[k]?.Throw();
                int take = (int)Math.Min(blockSize, expected - emitted);
                emitted += take;
                yield return decoded[k].AsMemory(0, take);
            }
        }

        if (emitted != expected)
        {
            throw new InvalidDataException($"PFSC streamed output size {emitted} does not match inode size {expected}");
        }
    }

    /// <summary>Copy an inode's logical payload to <paramref name="destination"/>.</summary>
    /// <param name="inode">File inode.</param>
    /// <param name="destination">Output stream.</param>
    /// <param name="progress">Called with each chunk's length.</param>
    /// <returns>Bytes written.</returns>
    public long CopyLogicalTo(PFSInode inode, Stream destination, Action<int>? progress = null)
    {
        long written = 0;
        foreach (ReadOnlyMemory<byte> chunk in ReadLogicalChunks(inode))
        {
            destination.Write(chunk.Span);
            written += chunk.Length;
            progress?.Invoke(chunk.Length);
        }

        return written;
    }

    /// <summary>
    /// Seekable view over a contiguous unsigned inode's logical payload (Python <c>_LogicalFileView</c>).
    /// </summary>
    /// <param name="inode">Unsigned file inode.</param>
    /// <returns>Read-only stream; the image must stay open.</returns>
    public Stream OpenLogical(PFSInode inode) => new PFSLogicalStream(this, inode);

    /// <inheritdoc />
    public void Dispose()
    {
        _xts?.Dispose();
        if (_ownsStream)
        {
            _stream.Dispose();
        }
    }

    /// <summary>Absolute offset of a block; corrupt block numbers become <see cref="InvalidDataException"/>.</summary>
    /// <param name="block">Block number.</param>
    /// <returns>Byte offset.</returns>
    internal long BlockOffset(long block)
    {
        if (block < 0 || block > long.MaxValue / Math.Max(Header.BlockSize, 1u))
        {
            throw new InvalidDataException($"block number {block} is out of range");
        }

        return block * Header.BlockSize;
    }

    /// <summary>Convert a size field to an in-memory length; corrupt sizes become <see cref="InvalidDataException"/>.</summary>
    private static int ToSize(long size) =>
        size is >= 0 and <= int.MaxValue ? (int)size : throw new InvalidDataException($"size {size} is out of range");

    private static byte[] ReadExact(Stream stream, long offset, int size)
    {
        // Check bounds before allocating, so a corrupt size field cannot request gigabytes.
        long available = offset < 0 ? 0 : Math.Max(0, stream.Length - offset);
        if (offset < 0 || size < 0 || size > available)
        {
            throw new InvalidDataException($"truncated read at offset {offset} (wanted {size}, got {Math.Min(Math.Max(size, 0), available)})");
        }

        byte[] buffer = new byte[size];
        stream.Seek(offset, SeekOrigin.Begin);
        int got = stream.ReadAtLeast(buffer, size, throwOnEndOfStream: false);
        if (got != size)
        {
            throw new InvalidDataException($"truncated read at offset {offset} (wanted {size}, got {got})");
        }

        return buffer;
    }
}

/// <summary>Read-only stream over the decrypted image bytes (lets PFSC readers work on encrypted images).</summary>
internal sealed class PFSImageStream(PFSImage image) : Stream
{
    private long _position;

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => image.Length;

    public override long Position
    {
        get => _position;
        set => _position = value;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int take = (int)Math.Min(buffer.Length, Math.Max(0, Length - _position));
        if (take == 0)
        {
            return 0;
        }

        image.Read(_position, take).CopyTo(buffer);
        _position += take;
        return take;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _position = origin switch
        {
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => offset,
        };
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Seekable logical view with a 16-block LRU cache (Python <c>_LogicalFileView</c>).</summary>
internal sealed class PFSLogicalStream : Stream
{
    private const int CacheBlocks = 32;
    private const int ReadAheadBlocks = 16;
    private readonly PFSImage _image;
    private readonly long _base;
    private readonly long _size;
    private readonly PFSCReader? _pfsc;
    private readonly Dictionary<long, byte[]> _cache = [];
    private readonly Queue<long> _order = new();
    private long _position;

    public PFSLogicalStream(PFSImage image, PFSInode inode)
    {
        if (inode.IsSigned)
        {
            throw new NotSupportedException("signed payloads are not served by the logical view");
        }

        _image = image;
        _base = image.BlockOffset(inode.Db[0]);
        _size = inode.LogicalSize;
        _pfsc = inode.IsCompressed ? PFSCReader.Open(new PFSImageStream(image), _base, inode.StoredSize) : null;
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => _size;

    public override long Position
    {
        get => _position;
        set => _position = Math.Max(0, value);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        long end = Math.Min(_position + buffer.Length, _size);
        if (end <= _position)
        {
            return 0;
        }

        int total = (int)(end - _position);
        if (_pfsc is null)
        {
            _image.Read(_base + _position, total).CopyTo(buffer);
            _position = end;
            return total;
        }

        int blockSize = _pfsc.Header.LogicalBlockSize;
        int written = 0;
        while (_position < end)
        {
            long index = _position / blockSize;
            int within = (int)(_position % blockSize);
            int take = (int)Math.Min(blockSize - within, end - _position);
            Block(index).AsSpan(within, take).CopyTo(buffer[written..]);
            written += take;
            _position += take;
        }

        return written;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _size + offset,
            _ => offset,
        };
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private byte[] Block(long index)
    {
        if (_cache.TryGetValue(index, out byte[]? cached))
        {
            return cached;
        }

        // Reads are mostly sequential (exFAT clusters in order), so a miss also decodes the blocks after it, in
        // parallel. Stored bytes are read in order first (one shared stream). Only the requested block's failure
        // is raised; a read-ahead block that fails is left uncached, to fail when it is actually read.
        PFSCReader pfsc = _pfsc!;
        int blockSize = pfsc.Header.LogicalBlockSize;
        List<(long Index, byte[] Stored)> batch = [];
        for (long i = index; i < pfsc.BlockCount && batch.Count < ReadAheadBlocks; i++)
        {
            if (i != index && _cache.ContainsKey(i))
            {
                break;
            }

            if (i == index)
            {
                int length = pfsc.StoredLength(i);
                if (length > blockSize)
                {
                    break;
                }

                byte[] stored = new byte[length];
                pfsc.ReadStoredBlock(i, stored);
                batch.Add((i, stored));
                continue;
            }

            try
            {
                int length = pfsc.StoredLength(i);
                if (length > blockSize)
                {
                    break;
                }

                byte[] stored = new byte[length];
                pfsc.ReadStoredBlock(i, stored);
                batch.Add((i, stored));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                break;
            }
        }

        if (batch.Count == 0)
        {
            // An oversized requested block: DecodeBlock raises its own error.
            byte[] single = new byte[blockSize];
            pfsc.DecodeBlock(index, single);
            return single;
        }

        byte[]?[] decoded = new byte[batch.Count][];
        ExceptionDispatchInfo? requested = null;
        Parallel.For(0, batch.Count, k =>
        {
            byte[] block = new byte[blockSize];
            try
            {
                PFSCReader.DecodeStoredBlock(batch[k].Stored, block, batch[k].Index);
                decoded[k] = block;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                if (k == 0)
                {
                    requested = ExceptionDispatchInfo.Capture(ex);
                }
            }
        });
        requested?.Throw();
        for (int k = 0; k < batch.Count; k++)
        {
            if (decoded[k] is not { } block)
            {
                continue;
            }

            _cache[batch[k].Index] = block;
            _order.Enqueue(batch[k].Index);
            if (_order.Count > CacheBlocks)
            {
                _cache.Remove(_order.Dequeue());
            }
        }

        return decoded[0]!;
    }
}
