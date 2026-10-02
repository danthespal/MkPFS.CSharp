using System.Security.Cryptography;
using MkPFS.Core.Compression;
using MkPFS.Core.Diagnostics;
using MkPFS.Core.PFSC;

namespace MkPFS.Repair;

/// <summary>A block that failed to decode.</summary>
/// <param name="Block">Block index.</param>
/// <param name="Message">Decoder error.</param>
public readonly record struct BlockError(long Block, string Message);

/// <summary>Offline scan of every PFSC block.</summary>
public sealed class RepairScanResult
{
    internal RepairScanResult(long blockCount)
    {
        BlockCount = blockCount;
        Risky = new bool[blockCount];
        Hashes = new byte[blockCount * PFSCVHash.HashSize];
    }

    /// <summary>Number of blocks.</summary>
    public long BlockCount { get; }

    /// <summary>Blocks stored compressed.</summary>
    public long CompressedBlocks { get; internal set; }

    /// <summary>Per block: compressed stream the PS5 may decode wrongly (see <see cref="RepairScanner.IsRisky"/>).</summary>
    public bool[] Risky { get; }

    /// <summary>Number of risky blocks.</summary>
    public long RiskyCount { get; internal set; }

    /// <summary>SHA-256 of each decoded block over its unpadded length, same as a <c>.vhash</c> entry.</summary>
    public byte[] Hashes { get; }

    /// <summary>Sidecar lookup result.</summary>
    public PFSCVHashMode HashMode { get; internal set; } = PFSCVHashMode.Missing;

    /// <summary>Blocks whose decoded hash differs from the sidecar.</summary>
    public List<long> HashMismatches { get; } = [];

    /// <summary>Blocks that did not decode.</summary>
    public List<BlockError> DecodeErrors { get; } = [];

    /// <summary>Hash of block <paramref name="index"/>.</summary>
    /// <param name="index">Block index.</param>
    /// <returns>32 bytes.</returns>
    public ReadOnlySpan<byte> HashOf(long index) => Hashes.AsSpan(checked((int)(index * PFSCVHash.HashSize)), PFSCVHash.HashSize);
}

/// <summary>
/// Decodes every block, flags streams the PS5 may misdecode, and checks the <c>.vhash</c> sidecar when one
/// matches. Replaces GC's mounted-image comparison, which needs a PS5, with offline checks.
/// </summary>
public static class RepairScanner
{
    private const int SlabBlocks = 256;

    /// <summary>
    /// A compressed stream is risky when it uses back-references zlib never emits (ISA-L output) or when the
    /// stream walker cannot parse it although zlib decoded it.
    /// </summary>
    /// <param name="stored">zlib stream.</param>
    /// <returns><see langword="true"/> when the block should be rewritten.</returns>
    public static bool IsRisky(ReadOnlySpan<byte> stored)
    {
        DeflateStreamReport report = DeflateInspector.InspectZlib(stored, PFSCImage.BlockSize);
        return !report.Valid || report.HasFarDistance;
    }

    /// <summary>Scan an opened image.</summary>
    /// <param name="image">Image.</param>
    /// <param name="vhashPath">Sidecar path to check, or <see langword="null"/> to skip.</param>
    /// <param name="workers">Decode threads (at least 1).</param>
    /// <param name="progress">Optional progress sink.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <param name="phase">Progress phase name.</param>
    /// <returns>Scan result.</returns>
    public static RepairScanResult Scan(PFSCImage image, string? vhashPath, int workers, IProgressSink? progress = null, CancellationToken cancellationToken = default, string phase = "scan")
    {
        RepairScanResult result = new(image.BlockCount);
        FileStream? vhash = null;
        if (vhashPath is not null)
        {
            result.HashMode = PFSCVHash.Probe(vhashPath, image.VHashIdentity);
            if (result.HashMode == PFSCVHashMode.Used)
            {
                vhash = new FileStream(vhashPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
        }

        try
        {
            byte[] slab = new byte[SlabBlocks * PFSCImage.BlockSize];
            byte[] expected = new byte[SlabBlocks * PFSCVHash.HashSize];
            bool[] risky = result.Risky;
            string?[] errors = new string?[SlabBlocks];
            ParallelOptions parallel = new() { MaxDegreeOfParallelism = Math.Max(1, workers), CancellationToken = cancellationToken };
            long processedBytes = 0;
            for (long start = 0; start < image.BlockCount; start += SlabBlocks)
            {
                int count = (int)Math.Min(SlabBlocks, image.BlockCount - start);
                long slabStart = image.Offsets[start];
                int slabLength = (int)(image.Offsets[start + count] - slabStart);
                image.ReadAt(image.FileStart + slabStart, slab.AsSpan(0, slabLength));

                Array.Clear(errors);
                Parallel.For(0, count, parallel, () => new byte[PFSCImage.BlockSize], (local, _, decoded) =>
                {
                    long index = start + local;
                    int offset = (int)(image.Offsets[index] - slabStart);
                    int length = image.StoredLength(index);
                    ReadOnlySpan<byte> stored = slab.AsSpan(offset, length);
                    ReadOnlySpan<byte> raw;
                    if (length == PFSCImage.BlockSize)
                    {
                        raw = stored;
                    }
                    else
                    {
                        try
                        {
                            PFSCReader.DecodeStoredBlock(stored, decoded, index);
                        }
                        catch (InvalidDataException ex)
                        {
                            errors[local] = length == 0 ? "empty PFSC block span" : ex.Message;
                            return decoded;
                        }

                        risky[index] = IsRisky(stored);
                        raw = decoded;
                    }

                    SHA256.HashData(raw[..image.CompareLength(index)], result.Hashes.AsSpan((int)(index * PFSCVHash.HashSize), PFSCVHash.HashSize));
                    return decoded;
                }, _ => { });

                if (vhash is not null)
                {
                    vhash.Seek(PFSCVHash.HeaderSize + (start * PFSCVHash.HashSize), SeekOrigin.Begin);
                    vhash.ReadExactly(expected.AsSpan(0, count * PFSCVHash.HashSize));
                }

                for (int local = 0; local < count; local++)
                {
                    long index = start + local;
                    if (errors[local] is { } message)
                    {
                        result.DecodeErrors.Add(new BlockError(index, message));
                        continue;
                    }

                    if (image.StoredLength(index) < PFSCImage.BlockSize)
                    {
                        result.CompressedBlocks++;
                    }

                    if (risky[index])
                    {
                        result.RiskyCount++;
                    }

                    if (vhash is not null &&
                        !result.HashOf(index).SequenceEqual(expected.AsSpan(local * PFSCVHash.HashSize, PFSCVHash.HashSize)))
                    {
                        result.HashMismatches.Add(index);
                    }
                }

                processedBytes += slabLength;
                progress?.Report(phase, start + count, image.BlockCount, processedBytes);
            }
        }
        finally
        {
            vhash?.Dispose();
        }

        return result;
    }
}
