using System.Text;
using MkPFS.Core.AMPR;

namespace MkPFS.Build.AMPRPack;

/// <summary>
/// One AMPRDAT3 data volume being written to a temporary file (Python <c>PackVolumeWriter</c>). The header is
/// written last, once the build id is known.
/// </summary>
public sealed class AMPRPackVolumeWriter : IDisposable
{
    private readonly FileStream _stream;
    private bool _closed;

    internal AMPRPackVolumeWriter(
        int packId,
        string name,
        string outputDir,
        long payloadAlignment,
        long chunkAlignment,
        long ioPageSize,
        long maxPackSize,
        uint flags)
    {
        PackId = packId;
        Name = name;
        FinalPath = AMPRAssetPath.SafeOutputPath(outputDir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(FinalPath)!);
        TempPath = Path.Combine(Path.GetDirectoryName(FinalPath)!, $".{Path.GetFileName(FinalPath)}.tmp-{Environment.ProcessId}-{packId}");
        ChunkAlignment = chunkAlignment;
        IOPageSize = ioPageSize;
        PayloadOffset = Align(AMPRPackFormat.DataHeaderSize, payloadAlignment);
        MaxPackSize = maxPackSize;
        Flags = flags | AMPRPackFormat.PackFlagIOPageLayout;
        _stream = new FileStream(TempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        try
        {
            WriteZeros(PayloadOffset);
        }
        catch
        {
            // A failed header write (disk full) leaves neither the handle nor the temporary file behind.
            _stream.Dispose();
            File.Delete(TempPath);
            throw;
        }

        Position = PayloadOffset;
        PaddingBytes = PayloadOffset - AMPRPackFormat.DataHeaderSize;
    }

    /// <summary>Pack id.</summary>
    public int PackId { get; }

    /// <summary>Volume name relative to the output directory.</summary>
    public string Name { get; }

    /// <summary>Published path.</summary>
    public string FinalPath { get; }

    /// <summary>Temporary path until publication.</summary>
    public string TempPath { get; }

    /// <summary>Chunk alignment.</summary>
    public long ChunkAlignment { get; }

    /// <summary>I/O page size of this volume.</summary>
    public long IOPageSize { get; }

    /// <summary>Payload start.</summary>
    public long PayloadOffset { get; }

    /// <summary>Size limit; 0 or less means unlimited.</summary>
    public long MaxPackSize { get; }

    /// <summary>Pack flags.</summary>
    public uint Flags { get; internal set; }

    /// <summary>Current end of written data.</summary>
    public long Position { get; private set; }

    /// <summary>Alignment padding written so far.</summary>
    public long PaddingBytes { get; private set; }

    /// <summary>Where a chunk would be placed and its placement flags (Python <c>placement_for</c>).</summary>
    /// <param name="storedSize">Stored size (positive).</param>
    /// <param name="layout"><c>random</c>, <c>mixed</c> or <c>streaming</c>.</param>
    /// <param name="extentStart">Start of a streaming extent (page-aligned).</param>
    /// <returns>Offset and <c>ChunkFlagPage*</c> bits.</returns>
    public (long Offset, int Flags) PlacementFor(long storedSize, string layout, bool extentStart = false)
    {
        if (storedSize <= 0)
        {
            throw new AMPRPackException("cannot place an empty physical chunk");
        }

        long position = Position;
        if (extentStart)
        {
            position = Align(position, IOPageSize);
        }

        long offset;
        if (layout == "streaming")
        {
            // Streaming extents stay dense; the runtime merges adjacent ranges.
            offset = Align(position, ChunkAlignment);
        }
        else if (storedSize >= IOPageSize)
        {
            // Large random/mixed chunks start on a page, so a cold read touches ceil(size / page) pages.
            offset = Align(position, IOPageSize);
        }
        else
        {
            // Small random/mixed chunks share pages but never straddle one.
            offset = Align(position, ChunkAlignment);
            long pageEnd = Align(offset + 1, IOPageSize);
            if (offset + storedSize > pageEnd)
            {
                offset = Align(offset, IOPageSize);
            }
        }

        int placement = 0;
        if (storedSize <= IOPageSize && offset / IOPageSize == (offset + storedSize - 1) / IOPageSize)
        {
            placement |= AMPRPackFormat.ChunkFlagPageContained;
        }

        if (offset % IOPageSize == 0)
        {
            placement |= AMPRPackFormat.ChunkFlagPageAligned;
        }

        return (offset, placement);
    }

    /// <summary>Whether a chunk still fits under <see cref="MaxPackSize"/> (Python <c>can_fit</c>).</summary>
    /// <param name="storedSize">Stored size.</param>
    /// <param name="layout">Layout.</param>
    /// <param name="extentStart">Start of a streaming extent.</param>
    /// <returns><see langword="true"/> when the page-rounded end stays within the limit.</returns>
    public bool CanFit(long storedSize, string layout, bool extentStart = false)
    {
        (long offset, _) = PlacementFor(storedSize, layout, extentStart);
        return MaxPackSize <= 0 || Align(offset + storedSize, IOPageSize) <= MaxPackSize;
    }

    /// <summary>Append a chunk with its padding (Python <c>write</c>).</summary>
    /// <param name="payload">Stored bytes.</param>
    /// <param name="layout">Layout.</param>
    /// <param name="extentStart">Start of a streaming extent.</param>
    /// <returns>Offset and placement flags.</returns>
    public (long Offset, int Flags) Write(byte[] payload, string layout, bool extentStart = false)
    {
        (long offset, int placement) = PlacementFor(payload.Length, layout, extentStart);
        long padding = offset - Position;
        if (padding > 0)
        {
            WriteZeros(padding);
            PaddingBytes += padding;
        }

        _stream.Write(payload);
        Position = offset + payload.Length;
        return (offset, placement);
    }

    /// <summary>File size after tail padding to a whole page.</summary>
    /// <returns>Size.</returns>
    public long ProjectedFinalSize() => Align(Position, IOPageSize);

    /// <summary>Pad the tail, write the header and close the temporary file (Python <c>finalize</c>).</summary>
    /// <param name="buildId">Build id.</param>
    /// <returns>Pack record without name.</returns>
    public AMPRPackRecord Finalize(byte[] buildId)
    {
        if (_closed)
        {
            throw new AMPRPackException("pack was already finalized");
        }

        long finalSize = ProjectedFinalSize();
        long tail = finalSize - Position;
        if (tail > 0)
        {
            WriteZeros(tail);
            PaddingBytes += tail;
            Position = finalSize;
        }

        long payloadBytes = Position - PayloadOffset;
        byte[] header = new AMPRDataHeader((uint)PackId, buildId, (ulong)PayloadOffset, (ulong)payloadBytes, Flags).ToBytes();
        _stream.Seek(0, SeekOrigin.Begin);
        _stream.Write(header);
        _stream.Flush(flushToDisk: true);
        _stream.Dispose();
        _closed = true;
        long fileSize = new FileInfo(TempPath).Length;
        return fileSize == Position
            ? new AMPRPackRecord((ulong)payloadBytes, (ulong)fileSize, 0, 0, Flags, (uint)IOPageSize)
            : throw new AMPRPackException($"unexpected size for {Name}");
    }

    /// <summary>Move the finished volume into place.</summary>
    public void Publish()
    {
        if (!_closed)
        {
            throw new AMPRPackException("cannot publish an open pack");
        }

        File.Move(TempPath, FinalPath, overwrite: true);
    }

    /// <summary>Close and delete the temporary file.</summary>
    public void Abort()
    {
        Dispose();
        File.Delete(TempPath);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_closed)
        {
            _stream.Dispose();
            _closed = true;
        }
    }

    internal static long Align(long value, long alignment) =>
        checked((long)AMPRPackFormat.AlignUp((ulong)value, (ulong)alignment));

    private void WriteZeros(long count)
    {
        Span<byte> zeros = stackalloc byte[4096];
        zeros.Clear();
        while (count > 0)
        {
            int n = (int)Math.Min(count, zeros.Length);
            _stream.Write(zeros[..n]);
            count -= n;
        }
    }
}

/// <summary>Volume allocation per (group, lane) with rollover (Python <c>PackLayout</c>).</summary>
public sealed class AMPRPackLayout
{
    private readonly AMPRPackConfig _config;
    private readonly AMPRPackPattern _pattern;
    private readonly string _outputDir;
    private readonly Dictionary<(string Group, int Lane), AMPRPackVolumeWriter> _current = [];
    private readonly Dictionary<(string Group, int Lane), int> _volumeNumbers = [];
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);

    /// <summary>Create a layout.</summary>
    /// <param name="config">Configuration.</param>
    /// <param name="outputDir">Output directory.</param>
    public AMPRPackLayout(AMPRPackConfig config, string outputDir)
    {
        _config = config;
        _pattern = AMPRPackPattern.Parse(config.PackPattern);
        _outputDir = outputDir;
    }

    /// <summary>Volumes in pack-id order.</summary>
    public List<AMPRPackVolumeWriter> Volumes { get; } = [];

    /// <summary>The volume to append a chunk to, opening a new one when the current one is full (Python <c>writer_for</c>).</summary>
    /// <param name="group">Group.</param>
    /// <param name="lane">Lane.</param>
    /// <param name="storedSize">Stored size.</param>
    /// <param name="striped">Whether the file is striped.</param>
    /// <param name="layout">Layout.</param>
    /// <param name="extentStart">Start of a streaming extent.</param>
    /// <returns>Writer.</returns>
    public AMPRPackVolumeWriter WriterFor(AMPRGroupConfig group, int lane, long storedSize, bool striped, string layout, bool extentStart)
    {
        uint flags = AMPRPackFormat.PackFlagIOPageLayout | (striped ? AMPRPackFormat.PackFlagStriped : 0);
        if (!_current.TryGetValue((group.Name, lane), out AMPRPackVolumeWriter? writer))
        {
            writer = NewVolume(group, lane, flags);
        }

        if (!writer.CanFit(storedSize, layout, extentStart))
        {
            writer = NewVolume(group, lane, flags);
            if (!writer.CanFit(storedSize, layout, extentStart: true))
            {
                throw new AMPRPackException($"one block ({storedSize} bytes) exceeds max pack size for group {group.Name}");
            }
        }

        if (striped)
        {
            writer.Flags |= AMPRPackFormat.PackFlagStriped;
        }

        return writer;
    }

    /// <summary>Delete every temporary volume.</summary>
    public void Abort()
    {
        foreach (AMPRPackVolumeWriter writer in Volumes)
        {
            writer.Abort();
        }
    }

    private AMPRPackVolumeWriter NewVolume(AMPRGroupConfig group, int lane, uint flags)
    {
        int packId = Volumes.Count;
        if (packId > 0xFFFF)
        {
            throw new AMPRPackException("pack count exceeds uint16 format limit");
        }

        int volumeNumber = _volumeNumbers.GetValueOrDefault((group.Name, lane));
        _volumeNumbers[(group.Name, lane)] = volumeNumber + 1;
        string name = RenderName(group.Name, lane, volumeNumber, packId);
        long pageSize = group.IOPageSize != 0 ? group.IOPageSize : _config.IOPageSize;
        AMPRPackVolumeWriter writer = new(
            packId, name, _outputDir, Math.Max(_config.PayloadAlignment, pageSize), _config.ChunkAlignment, pageSize, group.MaxPackSize, flags);
        Volumes.Add(writer);
        _current[(group.Name, lane)] = writer;
        return writer;
    }

    private string RenderName(string group, int lane, int volume, int packId)
    {
        string name;
        try
        {
            name = _pattern.Render(group, lane, volume, packId);
        }
        catch (FormatException exc)
        {
            throw new AMPRPackException($"invalid pack_pattern: {Core.Util.PythonText.Repr(_config.PackPattern)}", exc);
        }

        name = name.Replace('\\', '/');
        // The runtime root is /app0; keep room for it, a slash and NUL within SCE_KERNEL_PATH_MAX (1024).
        if (Encoding.UTF8.GetByteCount(name) > 1017)
        {
            throw new AMPRPackException("pack_pattern produced a name longer than the runtime path limit");
        }

        if (!_names.Add(name))
        {
            throw new AMPRPackException($"pack_pattern produced duplicate name: {name}");
        }

        AMPRAssetPath.SafeOutputPath(_outputDir, name);
        return name;
    }
}
