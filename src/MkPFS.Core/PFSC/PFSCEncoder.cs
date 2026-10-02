using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using MkPFS.Core.Compression;
using MkPFS.Core.PFS;
using MkPFS.Core.Util;

namespace MkPFS.Core.PFSC;

/// <summary>Options for <see cref="PFSCEncoder"/>.</summary>
public sealed record PFSCEncodeOptions
{
    /// <summary>Default in-flight window multiplier (Python <c>DEFAULT_MKPFS_PFSC_WINDOW_FACTOR</c>).</summary>
    public const int DefaultWindowFactor = 8;

    /// <summary>zlib level 0..9.</summary>
    public int Level { get; init; } = Zlib.DefaultLevel;

    /// <summary>Minimum per-block gain percent to keep a compressed block (0..100).</summary>
    public int ThresholdGain { get; init; }

    /// <summary>Minimum whole-file gain percent to keep PFSC (file mode only; <c>100 - max-compressed-ratio</c>).</summary>
    public int MinFileGain { get; init; }

    /// <summary>Compression threads (1 = sequential). Output does not depend on this value.</summary>
    public int Workers { get; init; } = 1;

    /// <summary>Blocks kept in flight per worker; <c>MKPFS_PFSC_WINDOW_FACTOR</c> overrides the default.</summary>
    public int WindowFactor { get; init; } = ResolveWindowFactor();

    /// <summary>Collect SHA-256 of every unpadded raw block (for the Game Compressor <c>.vhash</c> sidecar).</summary>
    public bool ComputeBlockHashes { get; init; }

    private static int ResolveWindowFactor() =>
        int.TryParse(Environment.GetEnvironmentVariable("MKPFS_PFSC_WINDOW_FACTOR"), out int value) && value > 0
            ? value
            : DefaultWindowFactor;
}

/// <summary>Outcome of one PFSC encode.</summary>
/// <param name="StoredSize">Bytes to store: the PFSC payload size, or the raw size when kept raw.</param>
/// <param name="IsCompressed">Whether the payload must be stored as PFSC (inode compressed flag).</param>
/// <param name="GainPercent">Whole-file gain of the PFSC payload versus the raw size.</param>
/// <param name="HypotheticalAllCompressedSize">Header size plus every block's zlib length, kept or not.</param>
/// <param name="BlockCount">Number of logical blocks.</param>
/// <param name="CompressedBlocks">Blocks stored compressed.</param>
/// <param name="BlockHashes">SHA-256 per block (32 bytes each) when requested, else <see langword="null"/>.</param>
public sealed record PFSCEncodeResult(
    long StoredSize,
    bool IsCompressed,
    double GainPercent,
    long HypotheticalAllCompressedSize,
    long BlockCount,
    long CompressedBlocks,
    byte[]? BlockHashes);

/// <summary>Compresses one padded logical block; one instance per worker thread.</summary>
internal interface IPFSCBlockCompressor : IDisposable
{
    /// <summary>Compress <paramref name="block"/>; <paramref name="destination"/> holds <c>compressBound</c> bytes.</summary>
    int Compress(ReadOnlySpan<byte> block, Span<byte> destination);
}

/// <summary>Default compressor: native zlib 1.3.1.</summary>
internal sealed class ZlibBlockCompressor(int level) : IPFSCBlockCompressor
{
    private readonly ZlibDeflater _deflater = new(level);

    public int Compress(ReadOnlySpan<byte> block, Span<byte> destination) =>
        _deflater.TryCompress(block, destination, out int written)
            ? written
            : throw new InvalidOperationException("zlib output exceeded compressBound");

    public void Dispose() => _deflater.Dispose();
}

/// <summary>
/// PFSC encoder (port of Python <c>_encode_pfsc_into_handle</c>, <c>_encode_pfsc_stream_into_handle</c>
/// and <c>encode_pfsc_payload</c>). Blocks compress in parallel batches and are written strictly in
/// order, so output is identical for any worker count.
/// </summary>
public static class PFSCEncoder
{
    private const int BlockSize = PFSConstants.PFSCLogicalBlockSize;

    /// <summary>Resolve a CLI CPU count: 0 means <c>min(16, max(1, cores - 1))</c> (Python <c>resolve_compression_worker_count</c>).</summary>
    /// <param name="requested">Requested count, 0 for auto.</param>
    /// <returns>Worker count ≥ 1.</returns>
    public static int ResolveWorkerCount(int requested)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requested);
        int resolved = requested == 0 ? Math.Min(16, Math.Max(1, Environment.ProcessorCount - 1)) : requested;
        return Math.Max(1, resolved);
    }

    /// <summary>
    /// File mode: encode <paramref name="rawSize"/> bytes of <paramref name="source"/> into <paramref name="output"/>
    /// at <paramref name="baseOffset"/>. Blocks are always written there; the header is written only when the
    /// result is PFSC. When <see cref="PFSCEncodeResult.IsCompressed"/> is false the caller stores the file raw over
    /// the same region (no block compressed, payload not smaller, or gain below <see cref="PFSCEncodeOptions.MinFileGain"/>).
    /// </summary>
    /// <param name="source">Readable stream positioned at the file start.</param>
    /// <param name="rawSize">File size in bytes.</param>
    /// <param name="output">Writable, seekable stream.</param>
    /// <param name="baseOffset">Absolute offset of the PFSC payload in <paramref name="output"/>.</param>
    /// <param name="options">Encode options.</param>
    /// <param name="progress">Called with each block's raw length, in order.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Encode result.</returns>
    public static PFSCEncodeResult EncodeFile(
        Stream source,
        long rawSize,
        Stream output,
        long baseOffset,
        PFSCEncodeOptions options,
        Action<int>? progress = null,
        CancellationToken cancellationToken = default) =>
        EncodeFile(source, rawSize, output, baseOffset, options, level => new ZlibBlockCompressor(level), progress, cancellationToken);

    internal static PFSCEncodeResult EncodeFile(
        Stream source,
        long rawSize,
        Stream output,
        long baseOffset,
        PFSCEncodeOptions options,
        Func<int, IPFSCBlockCompressor> compressorFactory,
        Action<int>? progress,
        CancellationToken cancellationToken)
    {
        ValidateOptions(options);
        ArgumentOutOfRangeException.ThrowIfNegative(rawSize);
        if (rawSize == 0)
        {
            return new PFSCEncodeResult(0, false, 0.0, 0, 0, 0, null);
        }

        long blockCount = Sizes.CeilDiv(rawSize, BlockSize);
        long remaining = rawSize;
        int ReadBlock(Span<byte> buffer)
        {
            int want = (int)Math.Min(BlockSize, remaining);
            int got = want == 0 ? 0 : source.ReadAtLeast(buffer[..want], want, throwOnEndOfStream: false);
            remaining -= want;
            return want == 0 ? -1 : got;
        }

        PumpResult pump = Pump(ReadBlock, blockCount, output, baseOffset, options, compressorFactory, progress, cancellationToken);
        long encodedSize = pump.Offsets[^1];
        if (pump.CompressedBlocks == 0 || encodedSize >= rawSize)
        {
            return new PFSCEncodeResult(rawSize, false, 0.0, pump.Hypothetical, blockCount, pump.CompressedBlocks, pump.Hashes);
        }

        double gain = PFSCBlockPolicy.GainPercent(rawSize, encodedSize);
        if (gain < options.MinFileGain)
        {
            return new PFSCEncodeResult(rawSize, false, gain, pump.Hypothetical, blockCount, pump.CompressedBlocks, pump.Hashes);
        }

        WriteHeaderAndOffsets(output, baseOffset, blockCount, pump.Offsets);
        return new PFSCEncodeResult(encodedSize, true, gain, pump.Hypothetical, blockCount, pump.CompressedBlocks, pump.Hashes);
    }

    /// <summary>
    /// Stream mode: encode a forward-only source of exactly <paramref name="rawSize"/> bytes. Always PFSC (a
    /// stream cannot be re-read for a raw fallback), as in Python <c>_encode_pfsc_stream_into_handle</c>.
    /// </summary>
    /// <param name="source">Readable stream.</param>
    /// <param name="rawSize">Expected stream length.</param>
    /// <param name="output">Writable, seekable stream.</param>
    /// <param name="baseOffset">Absolute offset of the PFSC payload in <paramref name="output"/>.</param>
    /// <param name="options">Encode options; <see cref="PFSCEncodeOptions.MinFileGain"/> is ignored.</param>
    /// <param name="progress">Called with each block's raw length, in order.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Encode result with <see cref="PFSCEncodeResult.IsCompressed"/> = true.</returns>
    /// <exception cref="InvalidDataException">The stream length does not match <paramref name="rawSize"/>.</exception>
    public static PFSCEncodeResult EncodeStream(
        Stream source,
        long rawSize,
        Stream output,
        long baseOffset,
        PFSCEncodeOptions options,
        Action<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateOptions(options);
        ArgumentOutOfRangeException.ThrowIfNegative(rawSize);
        long blockCount = Sizes.CeilDiv(rawSize, BlockSize);
        int ReadBlock(Span<byte> buffer)
        {
            int got = source.ReadAtLeast(buffer, BlockSize, throwOnEndOfStream: false);
            return got == 0 ? -1 : got;
        }

        PumpResult pump = Pump(ReadBlock, blockCount, output, baseOffset, options, level => new ZlibBlockCompressor(level), progress, cancellationToken);
        long emitted = pump.Offsets.Count - 1;
        if (emitted == blockCount && source.ReadByte() >= 0)
        {
            emitted++;
        }

        if (emitted != blockCount)
        {
            throw new InvalidDataException($"exFAT stream length mismatch: expected {blockCount} blocks, got {(emitted > blockCount ? "more" : emitted.ToString(System.Globalization.CultureInfo.InvariantCulture))}");
        }

        WriteHeaderAndOffsets(output, baseOffset, blockCount, pump.Offsets);
        long stored = pump.Offsets[^1];
        double gain = rawSize == 0 ? 0.0 : PFSCBlockPolicy.GainPercent(rawSize, stored);
        return new PFSCEncodeResult(stored, true, gain, pump.Hypothetical, blockCount, pump.CompressedBlocks, pump.Hashes);
    }

    /// <summary>
    /// In-memory encode (Python <c>encode_pfsc_payload</c>): returns the PFSC payload, or <paramref name="raw"/>
    /// itself when no block compressed or the payload is not smaller.
    /// </summary>
    /// <param name="raw">File bytes.</param>
    /// <param name="options">Encode options; <see cref="PFSCEncodeOptions.MinFileGain"/> is ignored.</param>
    /// <param name="progress">Called with each block's raw length, in order.</param>
    /// <returns>Payload bytes, gain percent and hypothetical all-compressed size.</returns>
    public static (byte[] Payload, double GainPercent, long HypotheticalAllCompressedSize) EncodePayload(
        byte[] raw,
        PFSCEncodeOptions options,
        Action<int>? progress = null) =>
        EncodePayload(raw, options, level => new ZlibBlockCompressor(level), progress);

    internal static (byte[] Payload, double GainPercent, long HypotheticalAllCompressedSize) EncodePayload(
        byte[] raw,
        PFSCEncodeOptions options,
        Func<int, IPFSCBlockCompressor> compressorFactory,
        Action<int>? progress)
    {
        using MemoryStream source = new(raw, writable: false);
        using MemoryStream output = new();
        PFSCEncodeResult result = EncodeFile(source, raw.Length, output, 0, options with { MinFileGain = 0 }, compressorFactory, progress, CancellationToken.None);
        return result.IsCompressed
            ? (output.GetBuffer().AsSpan(0, checked((int)result.StoredSize)).ToArray(), result.GainPercent, result.HypotheticalAllCompressedSize)
            : (raw, 0.0, result.HypotheticalAllCompressedSize);
    }

    /// <summary>Write the PFSC header and offset table at <paramref name="baseOffset"/> (Python <c>_write_pfsc_header_and_offsets</c>).</summary>
    internal static void WriteHeaderAndOffsets(Stream output, long baseOffset, long blockCount, IReadOnlyList<long> offsets)
    {
        PFSCHeader header = PFSCHeader.ForBlocks(blockCount);
        byte[] area = new byte[checked((int)header.DataOffset)];
        header.Write(area);
        for (int i = 0; i < offsets.Count; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(area.AsSpan(PFSConstants.PFSCBlockOffsetsOffset + (i * PFSConstants.PFSCOffsetEntrySize)), offsets[i]);
        }

        output.Seek(baseOffset, SeekOrigin.Begin);
        output.Write(area);
    }

    private static void ValidateOptions(PFSCEncodeOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Level, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.Level, 9);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.ThresholdGain, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.ThresholdGain, 100);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MinFileGain, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.MinFileGain, 100);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Workers, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.WindowFactor, 1);
    }

    private sealed record PumpResult(List<long> Offsets, long Hypothetical, long CompressedBlocks, byte[]? Hashes);

    private sealed class BlockJob
    {
        public readonly byte[] Raw = new byte[BlockSize];
        public readonly byte[] Compressed = new byte[Zlib.CompressBound(BlockSize)];
        public long Index;
        public int RawLength;
        public int CompressedLength;
    }

    /// <summary>Read, compress and write <paramref name="blockCount"/> blocks in order; returns offsets relative to the payload start.</summary>
    /// <param name="readBlock">Fills a 64 KiB buffer; returns bytes read (short at EOF) or -1 when the source is exhausted.</param>
    private static PumpResult Pump(
        ReadBlockFunc readBlock,
        long blockCount,
        Stream output,
        long baseOffset,
        PFSCEncodeOptions options,
        Func<int, IPFSCBlockCompressor> compressorFactory,
        Action<int>? progress,
        CancellationToken cancellationToken)
    {
        long headerSize = PFSCHeader.HeaderSize(blockCount);
        List<long> offsets = [headerSize];
        byte[]? hashes = options.ComputeBlockHashes ? new byte[checked(blockCount * SHA256.HashSizeInBytes)] : null;
        int batchSize = options.Workers == 1 ? 1 : options.Workers * options.WindowFactor;
        BlockJob[] jobs = new BlockJob[(int)Math.Min(batchSize, Math.Max(blockCount, 1))];
        for (int i = 0; i < jobs.Length; i++)
        {
            jobs[i] = new BlockJob();
        }

        ConcurrentBag<IPFSCBlockCompressor> compressors = [];
        long hypothetical = 0;
        long compressedBlocks = 0;
        long nextIndex = 0;
        output.Seek(baseOffset + headerSize, SeekOrigin.Begin);
        try
        {
            while (nextIndex < blockCount)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Fill one batch in source order; a short source ends the batch early.
                int filled = 0;
                while (filled < jobs.Length && nextIndex < blockCount)
                {
                    BlockJob job = jobs[filled];
                    int got = readBlock(job.Raw);
                    if (got < 0)
                    {
                        break;
                    }

                    job.Raw.AsSpan(got).Clear();
                    job.RawLength = got;
                    job.Index = nextIndex++;
                    filled++;
                }

                if (filled == 0)
                {
                    break;
                }

                // Compress the batch, in parallel when more than one worker.
                void CompressJob(int i, IPFSCBlockCompressor compressor)
                {
                    BlockJob job = jobs[i];
                    job.CompressedLength = compressor.Compress(job.Raw, job.Compressed);
                    if (hashes is not null)
                    {
                        SHA256.HashData(job.Raw.AsSpan(0, job.RawLength), hashes.AsSpan(checked((int)(job.Index * SHA256.HashSizeInBytes))));
                    }
                }

                if (options.Workers == 1 || filled == 1)
                {
                    if (!compressors.TryTake(out IPFSCBlockCompressor? single))
                    {
                        single = compressorFactory(options.Level);
                    }

                    try
                    {
                        for (int i = 0; i < filled; i++)
                        {
                            CompressJob(i, single);
                        }
                    }
                    finally
                    {
                        compressors.Add(single);
                    }
                }
                else
                {
                    Parallel.For(
                        0,
                        filled,
                        new ParallelOptions { MaxDegreeOfParallelism = options.Workers, CancellationToken = cancellationToken },
                        () => compressors.TryTake(out IPFSCBlockCompressor? c) ? c : compressorFactory(options.Level),
                        (i, _, compressor) =>
                        {
                            CompressJob(i, compressor);
                            return compressor;
                        },
                        compressors.Add);
                }

                // Write in block order.
                for (int i = 0; i < filled; i++)
                {
                    BlockJob job = jobs[i];
                    hypothetical += job.CompressedLength;
                    bool keep = PFSCBlockPolicy.ShouldStoreCompressed(job.CompressedLength, BlockSize, options.ThresholdGain);
                    ReadOnlySpan<byte> selected = keep ? job.Compressed.AsSpan(0, job.CompressedLength) : job.Raw;
                    if (keep)
                    {
                        compressedBlocks++;
                    }

                    output.Write(selected);
                    offsets.Add(offsets[^1] + selected.Length);
                    progress?.Invoke(job.RawLength);
                }

                if (filled < jobs.Length && nextIndex < blockCount)
                {
                    break;
                }
            }
        }
        finally
        {
            foreach (IPFSCBlockCompressor compressor in compressors)
            {
                compressor.Dispose();
            }
        }

        return new PumpResult(offsets, headerSize + hypothetical, compressedBlocks, hashes);
    }

    private delegate int ReadBlockFunc(Span<byte> buffer);
}
