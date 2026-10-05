using MkPFS.Core.Compression;
using MkPFS.Core.Util;

namespace MkPFS.Core.AMPR;

/// <summary>
/// Reads and checks decoded chunks of a pack set: opens every volume, validates its header against the manifest,
/// and verifies each decoded chunk against the CRC sidecar (Python <c>PackReader</c>).
/// </summary>
public sealed class AMPRPackReader : IDisposable
{
    private readonly FileStream[] _volumes;
    private readonly (long Begin, long End)[] _payloads;

    /// <summary>Open the volumes and CRC sidecar of a loaded manifest.</summary>
    /// <param name="manifest">Manifest loaded from disk (<see cref="AMPRPackManifest.Path"/> set).</param>
    /// <exception cref="AMPRPackException">The sidecar or a volume is missing or does not match.</exception>
    /// <exception cref="InvalidDataException">A volume header is malformed.</exception>
    public AMPRPackReader(AMPRPackManifest manifest)
    {
        Manifest = manifest;
        string indexPath = manifest.Path ?? throw new ArgumentException("manifest has no path", nameof(manifest));
        try
        {
            ChunkCrcs = AMPRChunkCrcs.Load(AMPRPackFormat.ChunkCrcPath(indexPath), manifest.BuildId, manifest.Chunks.Count);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new AMPRPackException($"invalid or missing chunk CRC sidecar: {exc.Message}", exc);
        }

        string directory = Path.GetDirectoryName(Path.GetFullPath(indexPath))!;
        List<FileStream> opened = [];
        _payloads = new (long, long)[manifest.Packs.Count];
        try
        {
            for (int packId = 0; packId < manifest.Packs.Count; packId++)
            {
                AMPRPackRecord record = manifest.Packs[packId];
                string name = manifest.PackName(packId);
                FileStream stream = File.OpenRead(AMPRAssetPath.SafeOutputPath(directory, name));
                opened.Add(stream);
                byte[] header = new byte[AMPRPackFormat.DataHeaderSize];
                int read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
                AMPRDataHeader parsed = AMPRDataHeader.Parse(header.AsSpan(0, read), (uint)packId, manifest.BuildId, record.Flags);
                if ((ulong)stream.Length != record.FileSize)
                {
                    throw new AMPRPackException($"pack size mismatch: {name}");
                }

                if (parsed.PayloadBytes != record.PayloadBytes)
                {
                    throw new AMPRPackException($"pack payload size mismatch: {name}");
                }

                if (parsed.PayloadOffset + parsed.PayloadBytes != (ulong)stream.Length)
                {
                    throw new AMPRPackException($"pack payload range mismatch: {name}");
                }

                _payloads[packId] = ((long)parsed.PayloadOffset, (long)(parsed.PayloadOffset + parsed.PayloadBytes));
            }
        }
        catch
        {
            foreach (FileStream stream in opened)
            {
                stream.Dispose();
            }

            throw;
        }

        _volumes = [.. opened];
    }

    /// <summary>The manifest.</summary>
    public AMPRPackManifest Manifest { get; }

    /// <summary>Decoded CRC-32 per chunk.</summary>
    public uint[] ChunkCrcs { get; }

    /// <summary>Read, decode and CRC-check one chunk (Python <c>read_chunk</c>).</summary>
    /// <param name="chunkIndex">Chunk index.</param>
    /// <returns>Decoded bytes.</returns>
    /// <exception cref="AMPRPackException">The chunk is out of range, truncated, undecodable or fails its CRC.</exception>
    public byte[] ReadChunk(int chunkIndex)
    {
        if (chunkIndex < 0 || chunkIndex >= ChunkCrcs.Length)
        {
            throw new AMPRPackException("chunk CRC index is out of range");
        }

        AMPRChunkRecord chunk = Manifest.Chunks[chunkIndex];
        if (chunk.PackId >= _volumes.Length)
        {
            throw new AMPRPackException("chunk references an invalid pack");
        }

        (long begin, long end) = _payloads[chunk.PackId];
        if ((long)chunk.Offset < begin || (long)chunk.Offset + chunk.StoredSize > end)
        {
            throw new AMPRPackException("chunk is outside pack payload");
        }

        FileStream volume = _volumes[chunk.PackId];
        volume.Seek((long)chunk.Offset, SeekOrigin.Begin);
        byte[] stored = new byte[chunk.StoredSize];
        if (volume.ReadAtLeast(stored, stored.Length, throwOnEndOfStream: false) != stored.Length)
        {
            throw new AMPRPackException("chunk is truncated");
        }

        byte[] raw;
        if (chunk.Codec == AMPRPackFormat.ChunkCodecRaw)
        {
            raw = stored;
        }
        else if (chunk.Codec == AMPRPackFormat.ChunkCodecLZ4)
        {
            raw = new byte[chunk.RawSize];
            try
            {
                if (LZ4Codec.Decompress(stored, raw) != raw.Length)
                {
                    throw new InvalidDataException("LZ4 output size mismatch");
                }
            }
            catch (InvalidDataException exc)
            {
                throw new AMPRPackException("LZ4 chunk decompression failed", exc);
            }
        }
        else
        {
            throw new AMPRPackException($"unsupported chunk codec: {chunk.Codec}");
        }

        return raw.Length == chunk.RawSize && Crc32.Update(0, raw) == ChunkCrcs[chunkIndex]
            ? raw
            : throw new AMPRPackException("raw chunk CRC/size mismatch");
    }

    /// <summary>Decoded chunks of a packed file in order (Python <c>iter_file_data</c>).</summary>
    /// <param name="fileId">File id.</param>
    /// <returns>Decoded chunks.</returns>
    /// <exception cref="AMPRPackException">The file id is invalid or loose, or the data does not add up.</exception>
    public IEnumerable<byte[]> ReadFile(int fileId)
    {
        if (fileId <= 0 || fileId > Manifest.Files.Count)
        {
            throw new AMPRPackException($"invalid file id: {fileId}");
        }

        AMPRFileRecord record = Manifest.Files[fileId - 1];
        if (!record.IsPacked)
        {
            throw new AMPRPackException($"file id {fileId} is loose and has no packed data");
        }

        return Iterate(record, fileId);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (FileStream volume in _volumes)
        {
            volume.Dispose();
        }
    }

    private IEnumerable<byte[]> Iterate(AMPRFileRecord record, int fileId)
    {
        ulong total = 0;
        for (uint local = 0; local < record.ChunkCount; local++)
        {
            byte[] raw = ReadChunk((int)(record.FirstChunk + local));
            total += (ulong)raw.Length;
            yield return raw;
        }

        if (total != record.LogicalSize)
        {
            throw new AMPRPackException($"file id {fileId} logical size mismatch");
        }
    }
}
