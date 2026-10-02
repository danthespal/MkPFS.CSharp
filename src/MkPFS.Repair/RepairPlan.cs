using MkPFS.Core.Compression;
using MkPFS.Core.PFSC;

namespace MkPFS.Repair;

/// <summary>New block layout for a repair (GC <c>compute_new_offsets</c>, plus optional recompression).</summary>
public sealed class RepairPlan
{
    private RepairPlan(bool[] marked, long[] newOffsets, bool recompress, long oldStoredSize)
    {
        Marked = marked;
        NewOffsets = newOffsets;
        Recompress = recompress;
        OldStoredSize = oldStoredSize;
        MarkedBlocks = Enumerable.Range(0, marked.Length).Where(i => marked[i]).Select(i => (long)i).ToList();
    }

    /// <summary>Per block: rewrite this block.</summary>
    public bool[] Marked { get; }

    /// <summary>Marked block indexes, ascending.</summary>
    public IReadOnlyList<long> MarkedBlocks { get; }

    /// <summary>Block offsets after repair, relative to the payload start; <c>BlockCount + 1</c> entries.</summary>
    public long[] NewOffsets { get; }

    /// <summary>Marked blocks are re-encoded with zlib 1.3.1 instead of stored raw.</summary>
    public bool Recompress { get; }

    /// <summary>Stored payload size before repair.</summary>
    public long OldStoredSize { get; }

    /// <summary>Stored payload size after repair.</summary>
    public long NewStoredSize => NewOffsets[^1];

    /// <summary>No block moves toward the start, so the backward in-place copy is safe.</summary>
    public bool SupportsInPlace(PFSCImage image)
    {
        for (long i = 0; i < image.BlockCount; i++)
        {
            if (NewOffsets[i] < image.Offsets[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Build a plan.</summary>
    /// <param name="image">Image.</param>
    /// <param name="marked">Blocks to rewrite; must be compressed blocks.</param>
    /// <param name="recompress">Re-encode with zlib level 7 (kept only when smaller than raw) instead of raw.</param>
    /// <param name="workers">Threads used to size recompressed blocks.</param>
    /// <returns>Plan.</returns>
    public static RepairPlan Create(PFSCImage image, bool[] marked, bool recompress, int workers)
    {
        int[] newLengths = new int[image.BlockCount];
        for (long i = 0; i < image.BlockCount; i++)
        {
            newLengths[i] = marked[i] ? PFSCImage.BlockSize : image.StoredLength(i);
        }

        if (recompress)
        {
            List<long> blocks = Enumerable.Range(0, marked.Length).Where(i => marked[i]).Select(i => (long)i).ToList();
            ParallelOptions parallel = new() { MaxDegreeOfParallelism = Math.Max(1, workers) };
            Parallel.ForEach(blocks, parallel, () => new BlockEncoder(), (block, _, encoder) =>
            {
                // Each worker needs its own read position; RandomAccess reads are positional and thread safe.
                newLengths[block] = encoder.Encode(image, block).Length;
                return encoder;
            }, encoder => encoder.Dispose());
        }

        long[] offsets = new long[image.BlockCount + 1];
        offsets[0] = image.HeaderSize;
        for (long i = 0; i < image.BlockCount; i++)
        {
            offsets[i + 1] = checked(offsets[i] + newLengths[i]);
        }

        return new RepairPlan(marked, offsets, recompress, image.StoredSize);
    }

    /// <summary>Produces the new stored bytes of a marked block.</summary>
    internal sealed class BlockEncoder : IDisposable
    {
        private readonly byte[] _stored = new byte[PFSCImage.BlockSize];
        private readonly byte[] _decoded = new byte[PFSCImage.BlockSize];
        private readonly byte[] _compressed = new byte[Zlib.CompressBound(PFSCImage.BlockSize)];
        private ZlibDeflater? _deflater;

        /// <summary>Decode block <paramref name="block"/> and return its raw or recompressed bytes.</summary>
        /// <param name="image">Image.</param>
        /// <param name="block">Block index.</param>
        /// <param name="recompress">Try zlib before falling back to raw.</param>
        /// <returns>Bytes valid until the next call.</returns>
        public ReadOnlySpan<byte> Encode(PFSCImage image, long block, bool recompress = true)
        {
            image.DecodeBlock(block, _stored, _decoded);
            if (!recompress)
            {
                return _decoded;
            }

            _deflater ??= new ZlibDeflater(Zlib.DefaultLevel);
            if (_deflater.TryCompress(_decoded, _compressed, out int written) &&
                PFSCBlockPolicy.ShouldStoreCompressed(written, PFSCImage.BlockSize, 0))
            {
                return _compressed.AsSpan(0, written);
            }

            return _decoded;
        }

        public void Dispose() => _deflater?.Dispose();
    }
}
