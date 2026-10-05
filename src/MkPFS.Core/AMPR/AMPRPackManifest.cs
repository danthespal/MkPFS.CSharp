using System.Buffers.Binary;
using System.Text;
using MkPFS.Core.Util;

namespace MkPFS.Core.AMPR;

/// <summary>
/// AMPRPAK4 runtime storage manifest (<c>ampr_assets.index</c>): a 128-byte header, then file, chunk and pack
/// records and a NUL-terminated UTF-8 string table. Port of ampr_pack_format.py <c>PackManifest</c>,
/// <c>build_manifest_bytes</c>, <c>load_manifest</c> and <c>validate_manifest</c>; error messages match.
/// </summary>
public sealed class AMPRPackManifest
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Create a manifest from records.</summary>
    /// <param name="buildId">16-byte build id.</param>
    /// <param name="flags">Header flags (must be 0).</param>
    /// <param name="files">File records in file-id order.</param>
    /// <param name="chunks">Chunk records.</param>
    /// <param name="packs">Pack records.</param>
    /// <param name="strings">String table.</param>
    /// <param name="path">Manifest path, when loaded from disk.</param>
    public AMPRPackManifest(
        byte[] buildId,
        uint flags,
        IReadOnlyList<AMPRFileRecord> files,
        IReadOnlyList<AMPRChunkRecord> chunks,
        IReadOnlyList<AMPRPackRecord> packs,
        byte[] strings,
        string? path = null)
    {
        BuildId = buildId;
        Flags = flags;
        Files = files;
        Chunks = chunks;
        Packs = packs;
        Strings = strings;
        Path = path;
    }

    /// <summary>Manifest path, when loaded from disk.</summary>
    public string? Path { get; }

    /// <summary>16-byte build id shared by the manifest, volumes, CRC sidecar and runtime settings.</summary>
    public byte[] BuildId { get; }

    /// <summary>Header flags.</summary>
    public uint Flags { get; }

    /// <summary>File records; file id <c>n</c> is <c>Files[n - 1]</c>.</summary>
    public IReadOnlyList<AMPRFileRecord> Files { get; }

    /// <summary>Chunk records; loaded manifests carry the derived <see cref="AMPRChunkRecord.RawSize"/>.</summary>
    public IReadOnlyList<AMPRChunkRecord> Chunks { get; }

    /// <summary>Pack volume records; pack id <c>n</c> is <c>Packs[n]</c>.</summary>
    public IReadOnlyList<AMPRPackRecord> Packs { get; }

    /// <summary>String table (paths and volume names, each followed by NUL).</summary>
    public byte[] Strings { get; }

    /// <summary>Decode a string-table entry.</summary>
    /// <param name="offset">Offset.</param>
    /// <param name="length">Length without NUL.</param>
    /// <returns>The string.</returns>
    /// <exception cref="InvalidDataException">The entry is out of bounds, not NUL-terminated, or not UTF-8.</exception>
    public string StringAt(uint offset, uint length)
    {
        if ((ulong)offset + length >= (ulong)Strings.Length)
        {
            throw new InvalidDataException("string is out of bounds");
        }

        if (Strings[offset + length] != 0)
        {
            throw new InvalidDataException("string is not NUL terminated");
        }

        try
        {
            return StrictUtf8.GetString(Strings, (int)offset, (int)length);
        }
        catch (DecoderFallbackException exc)
        {
            throw new InvalidDataException("string is not valid UTF-8", exc);
        }
    }

    /// <summary>Path of a file id (1-based).</summary>
    /// <param name="fileId">File id.</param>
    /// <returns>Canonical <c>/app0</c> path.</returns>
    public string FilePath(int fileId)
    {
        if (fileId <= 0 || fileId > Files.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(fileId), "invalid file id");
        }

        AMPRFileRecord record = Files[fileId - 1];
        return StringAt(record.PathOffset, record.PathLength);
    }

    /// <summary>Volume file name of a pack id.</summary>
    /// <param name="packId">Pack id.</param>
    /// <returns>Name relative to the manifest directory.</returns>
    public string PackName(int packId)
    {
        if (packId < 0 || packId >= Packs.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(packId), "invalid pack id");
        }

        AMPRPackRecord record = Packs[packId];
        return StringAt(record.NameOffset, record.NameLength);
    }

    /// <summary>Serialize the manifest (Python <c>build_manifest_bytes</c>).</summary>
    /// <returns>Manifest bytes.</returns>
    /// <exception cref="ArgumentException">The build id or a count is outside the format limits.</exception>
    public byte[] Serialize()
    {
        if (BuildId.Length != AMPRPackFormat.BuildIdSize)
        {
            throw new ArgumentException("build id must contain 16 bytes");
        }

        if (Packs.Count > 0xFFFF)
        {
            throw new ArgumentException("pack count exceeds uint16 chunk-record limits");
        }

        long filesOffset = AMPRPackFormat.IndexHeaderSize;
        long chunksOffset = checked(filesOffset + ((long)Files.Count * AMPRPackFormat.FileRecordSize));
        long packsOffset = checked(chunksOffset + ((long)Chunks.Count * AMPRPackFormat.ChunkRecordSize));
        long stringsOffset = checked(packsOffset + ((long)Packs.Count * AMPRPackFormat.PackRecordSize));
        byte[] data = new byte[checked((int)(stringsOffset + Strings.Length))];
        Span<byte> span = data;
        for (int i = 0; i < Files.Count; i++)
        {
            Files[i].Write(span[(int)(filesOffset + ((long)i * AMPRPackFormat.FileRecordSize))..]);
        }

        for (int i = 0; i < Chunks.Count; i++)
        {
            Chunks[i].Write(span[(int)(chunksOffset + ((long)i * AMPRPackFormat.ChunkRecordSize))..]);
        }

        for (int i = 0; i < Packs.Count; i++)
        {
            Packs[i].Write(span[(int)(packsOffset + ((long)i * AMPRPackFormat.PackRecordSize))..]);
        }

        Strings.CopyTo(span[(int)stringsOffset..]);
        uint payloadCrc = Crc32.Update(0, span[AMPRPackFormat.IndexHeaderSize..]);

        Span<byte> header = span[..AMPRPackFormat.IndexHeaderSize];
        AMPRPackFormat.IndexMagic.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], AMPRPackFormat.IndexVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], AMPRPackFormat.IndexHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], Flags);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], AMPRPackFormat.EndianMarker);
        BuildId.CopyTo(header[24..]);
        BinaryPrimitives.WriteUInt64LittleEndian(header[40..], (ulong)Files.Count);
        BinaryPrimitives.WriteUInt64LittleEndian(header[48..], (ulong)Chunks.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(header[56..], (uint)Packs.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(header[60..], AMPRPackFormat.FileRecordSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[64..], AMPRPackFormat.ChunkRecordSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[68..], AMPRPackFormat.PackRecordSize);
        BinaryPrimitives.WriteUInt64LittleEndian(header[72..], (ulong)filesOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(header[80..], (ulong)chunksOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(header[88..], (ulong)packsOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(header[96..], (ulong)stringsOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(header[104..], (ulong)Strings.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header[112..], payloadCrc);
        // header_crc (116) covers the header with itself zeroed; reserved (120) stays 0.
        BinaryPrimitives.WriteUInt32LittleEndian(header[116..], Crc32.Update(0, header));
        return data;
    }

    /// <summary>Load and validate a manifest file (Python <c>load_manifest</c>).</summary>
    /// <param name="path">Manifest path.</param>
    /// <returns>The manifest.</returns>
    /// <exception cref="InvalidDataException">The manifest is malformed.</exception>
    public static AMPRPackManifest Load(string path) => Parse(File.ReadAllBytes(path), path);

    /// <summary>Parse and validate manifest bytes (Python <c>load_manifest</c>).</summary>
    /// <param name="data">Manifest bytes.</param>
    /// <param name="path">Manifest path to record.</param>
    /// <returns>The manifest.</returns>
    /// <exception cref="InvalidDataException">The manifest is malformed.</exception>
    public static AMPRPackManifest Parse(ReadOnlySpan<byte> data, string? path = null)
    {
        if (data.Length < AMPRPackFormat.IndexHeaderSize)
        {
            throw new InvalidDataException("pack index is truncated");
        }

        ReadOnlySpan<byte> header = data[..AMPRPackFormat.IndexHeaderSize];
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        if (!header[..8].SequenceEqual(AMPRPackFormat.IndexMagic) || version != AMPRPackFormat.IndexVersion)
        {
            throw new InvalidDataException("unsupported AMPR pack index");
        }

        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
        uint endian = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
        byte[] buildId = header.Slice(24, AMPRPackFormat.BuildIdSize).ToArray();
        ulong fileCount = BinaryPrimitives.ReadUInt64LittleEndian(header[40..]);
        ulong chunkCount = BinaryPrimitives.ReadUInt64LittleEndian(header[48..]);
        uint packCount = BinaryPrimitives.ReadUInt32LittleEndian(header[56..]);
        uint fileRecordSize = BinaryPrimitives.ReadUInt32LittleEndian(header[60..]);
        uint chunkRecordSize = BinaryPrimitives.ReadUInt32LittleEndian(header[64..]);
        uint packRecordSize = BinaryPrimitives.ReadUInt32LittleEndian(header[68..]);
        ulong filesOffset = BinaryPrimitives.ReadUInt64LittleEndian(header[72..]);
        ulong chunksOffset = BinaryPrimitives.ReadUInt64LittleEndian(header[80..]);
        ulong packsOffset = BinaryPrimitives.ReadUInt64LittleEndian(header[88..]);
        ulong stringsOffset = BinaryPrimitives.ReadUInt64LittleEndian(header[96..]);
        ulong stringsSize = BinaryPrimitives.ReadUInt64LittleEndian(header[104..]);
        uint payloadCrc = BinaryPrimitives.ReadUInt32LittleEndian(header[112..]);
        uint headerCrc = BinaryPrimitives.ReadUInt32LittleEndian(header[116..]);
        ulong reserved = BinaryPrimitives.ReadUInt64LittleEndian(header[120..]);
        if (headerSize != AMPRPackFormat.IndexHeaderSize
            || endian != AMPRPackFormat.EndianMarker
            || (flags & ~AMPRPackFormat.IndexKnownFlags) != 0
            || reserved != 0)
        {
            throw new InvalidDataException("invalid AMPR pack header");
        }

        if (fileRecordSize != AMPRPackFormat.FileRecordSize
            || chunkRecordSize != AMPRPackFormat.ChunkRecordSize
            || packRecordSize != AMPRPackFormat.PackRecordSize)
        {
            throw new InvalidDataException("unsupported AMPR pack record size");
        }

        byte[] headerCopy = header.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(headerCopy.AsSpan(116), 0);
        if (Crc32.Update(0, headerCopy) != headerCrc)
        {
            throw new InvalidDataException("pack index header CRC mismatch");
        }

        if (Crc32.Update(0, data[AMPRPackFormat.IndexHeaderSize..]) != payloadCrc)
        {
            throw new InvalidDataException("pack index payload CRC mismatch");
        }

        // Python ints do not overflow; UInt128 keeps the layout check exact for any header values.
        UInt128 expectedFiles = AMPRPackFormat.IndexHeaderSize;
        UInt128 expectedChunks = expectedFiles + ((UInt128)fileCount * AMPRPackFormat.FileRecordSize);
        UInt128 expectedPacks = expectedChunks + ((UInt128)chunkCount * AMPRPackFormat.ChunkRecordSize);
        UInt128 expectedStrings = expectedPacks + ((UInt128)packCount * AMPRPackFormat.PackRecordSize);
        if (filesOffset != expectedFiles
            || chunksOffset != expectedChunks
            || packsOffset != expectedPacks
            || stringsOffset != expectedStrings
            || (UInt128)stringsOffset + stringsSize != (UInt128)data.Length)
        {
            throw new InvalidDataException("invalid pack index section layout");
        }

        AMPRFileRecord[] files = new AMPRFileRecord[fileCount];
        for (int i = 0; i < files.Length; i++)
        {
            files[i] = AMPRFileRecord.Read(data[(int)(filesOffset + ((ulong)i * AMPRPackFormat.FileRecordSize))..]);
        }

        AMPRChunkRecord[] chunks = new AMPRChunkRecord[chunkCount];
        for (int i = 0; i < chunks.Length; i++)
        {
            chunks[i] = AMPRChunkRecord.Read(data[(int)(chunksOffset + ((ulong)i * AMPRPackFormat.ChunkRecordSize))..]);
        }

        // AMPRPAK4 derives the raw chunk size from the owning file's block geometry.
        foreach (AMPRFileRecord file in files)
        {
            if (!file.IsPacked)
            {
                continue;
            }

            // An invalid shift is reported by Validate; here it only has to stay larger than any file.
            ulong blockSize = file.BlockShift < 63 ? 1UL << file.BlockShift : ulong.MaxValue;
            for (ulong local = 0; local < file.ChunkCount; local++)
            {
                ulong chunkIndex = file.FirstChunk + local;
                if (chunkIndex >= (ulong)chunks.Length)
                {
                    throw new InvalidDataException("chunk range is invalid while decoding AMPRPAK4");
                }

                UInt128 blockBegin = (UInt128)local * blockSize;
                if (blockBegin >= file.LogicalSize)
                {
                    throw new InvalidDataException("AMPRPAK4 file has too many chunks");
                }

                ulong remaining = file.LogicalSize - (ulong)blockBegin;
                chunks[chunkIndex] = chunks[chunkIndex] with { RawSize = (int)Math.Min(blockSize, Math.Min(remaining, int.MaxValue)) };
            }
        }

        AMPRPackRecord[] packs = new AMPRPackRecord[packCount];
        for (int i = 0; i < packs.Length; i++)
        {
            packs[i] = AMPRPackRecord.Read(data[(int)(packsOffset + ((ulong)i * AMPRPackFormat.PackRecordSize))..]);
        }

        AMPRPackManifest manifest = new(buildId, flags, files, chunks, packs, data[(int)stringsOffset..].ToArray(), path);
        manifest.Validate();
        return manifest;
    }

    /// <summary>Check every structural invariant the runtime relies on (Python <c>validate_manifest</c>).</summary>
    /// <exception cref="InvalidDataException">An invariant is violated.</exception>
    public void Validate()
    {
        if ((Flags & ~AMPRPackFormat.IndexKnownFlags) != 0)
        {
            throw new InvalidDataException("unknown pack index flags");
        }

        for (int packId = 0; packId < Packs.Count; packId++)
        {
            ValidatePack(packId, Packs[packId]);
        }

        for (int fileId = 1; fileId <= Files.Count; fileId++)
        {
            ValidateFile(fileId, Files[fileId - 1]);
        }
    }

    private void ValidatePack(int packId, AMPRPackRecord record)
    {
        string name = StringAt(record.NameOffset, record.NameLength);
        try
        {
            AMPRAssetPath.SafeOutputPath(".", name);
        }
        catch (ArgumentException exc)
        {
            throw new InvalidDataException($"unsafe pack name {packId}: {PythonText.Repr(name)}", exc);
        }

        if ((record.Flags & ~AMPRPackFormat.PackKnownFlags) != 0)
        {
            throw new InvalidDataException($"unknown flags for pack {packId}");
        }

        uint page = record.IOPageSize;
        if (page < 1U << AMPRPackFormat.MinIOPageShift
            || page > 1U << AMPRPackFormat.MaxIOPageShift
            || (page & (page - 1)) != 0
            || page % AMPRPackFormat.PhysicalChunkAlignment != 0)
        {
            throw new InvalidDataException($"invalid I/O page size for pack {packId}");
        }

        if ((record.Flags & AMPRPackFormat.PackFlagIOPageLayout) == 0)
        {
            throw new InvalidDataException($"pack {packId} does not declare page-aware layout");
        }

        if (record.FileSize < AMPRPackFormat.DataHeaderSize || record.PayloadBytes > record.FileSize)
        {
            throw new InvalidDataException($"invalid size for pack {packId}");
        }

        ulong payloadOffset = record.FileSize - record.PayloadBytes;
        if (payloadOffset < AMPRPackFormat.DataHeaderSize || payloadOffset % page != 0 || record.FileSize % page != 0)
        {
            throw new InvalidDataException($"invalid payload offset for pack {packId}");
        }
    }

    private void ValidateFile(int fileId, AMPRFileRecord record)
    {
        string path = StringAt(record.PathOffset, record.PathLength);
        string canonical;
        try
        {
            canonical = AMPRAssetPath.Canonical(path);
        }
        catch (ArgumentException exc)
        {
            throw new InvalidDataException(exc.Message, exc);
        }

        if (canonical != path || canonical == "/app0")
        {
            throw new InvalidDataException($"non-canonical path for file id {fileId}");
        }

        if (record.Reserved != 0 || (record.Flags & ~AMPRPackFormat.FileKnownFlags) != 0)
        {
            throw new InvalidDataException($"unknown flags for file id {fileId}");
        }

        if (AMPRAssetPath.Hash(canonical) != record.PathHash)
        {
            throw new InvalidDataException($"path hash mismatch for file id {fileId}");
        }

        if (!record.IsPacked)
        {
            if (record.FirstChunk != 0 || record.ChunkCount != 0 || record.BlockShift != 0 || record.PackingClass != 0 || record.Flags != 0)
            {
                throw new InvalidDataException($"loose file id {fileId} references chunks");
            }

            return;
        }

        if (record.BlockShift is < AMPRPackFormat.MinBlockShift or > AMPRPackFormat.MaxBlockShift)
        {
            throw new InvalidDataException($"invalid block shift for file id {fileId}");
        }

        bool streaming = (record.Flags & AMPRPackFormat.FileFlagStreaming) != 0;
        if (streaming && (record.Flags & AMPRPackFormat.FileFlagRandomAccess) != 0)
        {
            throw new InvalidDataException($"file id {fileId} cannot be both streaming and random-access");
        }

        if ((ulong)record.FirstChunk + record.ChunkCount > (ulong)Chunks.Count)
        {
            throw new InvalidDataException($"chunk range is invalid for file id {fileId}");
        }

        ulong total = 0;
        int blockSize = 1 << record.BlockShift;
        for (uint local = 0; local < record.ChunkCount; local++)
        {
            AMPRChunkRecord chunk = Chunks[(int)(record.FirstChunk + local)];
            ValidateChunk(fileId, record, chunk, local, blockSize, streaming);
            total += (ulong)chunk.RawSize;
        }

        if (total != record.LogicalSize)
        {
            throw new InvalidDataException($"logical size mismatch for file id {fileId}");
        }
    }

    private void ValidateChunk(int fileId, AMPRFileRecord record, AMPRChunkRecord chunk, uint local, int blockSize, bool streaming)
    {
        if (chunk.RawSize == 0 || chunk.RawSize > blockSize)
        {
            throw new InvalidDataException($"invalid raw chunk size for file id {fileId}");
        }

        if (chunk.StoredSize == 0 || chunk.StoredSize > blockSize)
        {
            throw new InvalidDataException($"invalid stored chunk size for file id {fileId}");
        }

        if (local + 1 != record.ChunkCount && chunk.RawSize != blockSize)
        {
            throw new InvalidDataException($"short non-final chunk for file id {fileId}");
        }

        if (chunk.Codec is not (AMPRPackFormat.ChunkCodecRaw or AMPRPackFormat.ChunkCodecLZ4))
        {
            throw new InvalidDataException($"unknown chunk codec for file id {fileId}");
        }

        if (chunk.Codec == AMPRPackFormat.ChunkCodecRaw && chunk.StoredSize != chunk.RawSize)
        {
            throw new InvalidDataException($"raw chunk size mismatch for file id {fileId}");
        }

        if (chunk.PackId >= Packs.Count)
        {
            throw new InvalidDataException($"invalid pack id for file id {fileId}");
        }

        if ((chunk.Flags & ~AMPRPackFormat.ChunkKnownFlags) != 0)
        {
            throw new InvalidDataException($"invalid chunk flags/reserved for file id {fileId}");
        }

        if (streaming != ((chunk.Flags & AMPRPackFormat.ChunkFlagStreaming) != 0))
        {
            throw new InvalidDataException($"streaming flag mismatch for file id {fileId}");
        }

        if ((record.Flags & AMPRPackFormat.FileFlagStoreOnly) != 0 && chunk.Codec != AMPRPackFormat.ChunkCodecRaw)
        {
            throw new InvalidDataException($"store-only file id {fileId} contains a compressed chunk");
        }

        AMPRPackRecord pack = Packs[chunk.PackId];
        ulong payloadOffset = pack.FileSize - pack.PayloadBytes;
        ulong end = chunk.Offset + (ulong)chunk.StoredSize;
        if (chunk.Offset < payloadOffset || end > pack.FileSize || chunk.Offset % AMPRPackFormat.PhysicalChunkAlignment != 0)
        {
            throw new InvalidDataException($"chunk range/alignment is invalid for file id {fileId}");
        }

        ulong page = pack.IOPageSize;
        ulong pageBegin = AMPRPackFormat.AlignDown(chunk.Offset, page);
        ulong pageEnd = AMPRPackFormat.AlignUp(end, page);
        bool pageContained = (chunk.Flags & AMPRPackFormat.ChunkFlagPageContained) != 0;
        bool pageAligned = (chunk.Flags & AMPRPackFormat.ChunkFlagPageAligned) != 0;
        if (pageContained && ((ulong)chunk.StoredSize > page || pageBegin != AMPRPackFormat.AlignDown(end - 1, page)))
        {
            throw new InvalidDataException($"page-contained chunk crosses an I/O page for file id {fileId}");
        }

        if (pageAligned && chunk.Offset % page != 0)
        {
            throw new InvalidDataException($"page-aligned chunk is not page aligned for file id {fileId}");
        }

        bool pageSafe = pageContained || pageAligned;
        if ((ulong)chunk.StoredSize <= page && pageAligned && !pageContained)
        {
            // Small page-aligned extents are also page-contained; requiring both bits catches packers
            // that omit the stronger invariant used by the random-access planner.
            throw new InvalidDataException($"small page-aligned chunk lacks containment flag for file id {fileId}");
        }

        if ((record.Flags & AMPRPackFormat.FileFlagRandomAccess) != 0 && !pageSafe)
        {
            throw new InvalidDataException($"random-access file uses a non-page-safe chunk for file id {fileId}");
        }

        if (!streaming && !pageSafe)
        {
            throw new InvalidDataException($"non-streaming file uses a non-page-safe chunk for file id {fileId}");
        }

        if (pageBegin < payloadOffset || pageEnd > pack.FileSize)
        {
            throw new InvalidDataException($"aligned I/O range is outside the pack for file id {fileId}");
        }
    }
}
