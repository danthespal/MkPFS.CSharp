using MkPFS.Core.AMPR;

namespace MkPFS.Build.AMPRPack;

/// <summary>Counters reported by a pack build (Python <c>BuildStats</c>; the CLI prints them as JSON).</summary>
public sealed class AMPRBuildStats
{
    /// <summary>AMPRIDX3 files.</summary>
    public long FilesTotal { get; set; }

    /// <summary>Files stored in pack volumes.</summary>
    public long FilesPacked { get; set; }

    /// <summary>Files left on the filesystem.</summary>
    public long FilesLoose { get; set; }

    /// <summary>Relative paths that must stay in <c>/app0</c>.</summary>
    public List<string> LoosePaths { get; } = [];

    /// <summary>Files left loose by sampling.</summary>
    public long FilesAutoLoose { get; set; }

    /// <summary>Bytes of auto-loose files.</summary>
    public long AutoLooseLogicalBytes { get; set; }

    /// <summary>Bytes read while sampling.</summary>
    public long AutoLooseSampledBytes { get; set; }

    /// <summary>Chunk records.</summary>
    public long Chunks { get; set; }

    /// <summary>LZ4 chunk records.</summary>
    public long ChunksLZ4 { get; set; }

    /// <summary>RAW chunk records.</summary>
    public long ChunksRaw { get; set; }

    /// <summary>Chunk records that reuse an earlier block.</summary>
    public long ChunksShared { get; set; }

    /// <summary>Decoded bytes of packed files.</summary>
    public long LogicalBytes { get; set; }

    /// <summary>Bytes written to volumes (excluding padding).</summary>
    public long StoredBytes { get; set; }

    /// <summary>Alignment padding in volumes.</summary>
    public long PaddingBytes { get; set; }

    /// <summary>I/O pages covered by volume payloads.</summary>
    public long IOPagesTouched { get; set; }

    /// <summary>Chunks placed page-contained or page-aligned.</summary>
    public long IOPageSafeChunks { get; set; }

    /// <summary>Chunks in dense streaming extents.</summary>
    public long DenseStreamingChunks { get; set; }
}

/// <summary>Progress snapshot (Python <c>BuildProgress</c>).</summary>
/// <param name="Phase"><c>reading-index</c>, <c>planning</c>, <c>packing</c>, <c>finalizing</c>, <c>publishing</c> or <c>complete</c>.</param>
/// <param name="FilesDone">Files done in this phase.</param>
/// <param name="FilesTotal">Files in this phase.</param>
/// <param name="LogicalBytesDone">Bytes packed.</param>
/// <param name="LogicalBytesTotal">Bytes to pack.</param>
/// <param name="CurrentPath">File being processed, if any.</param>
public sealed record AMPRBuildProgress(
    string Phase, long FilesDone, long FilesTotal, long LogicalBytesDone, long LogicalBytesTotal, string CurrentPath = "");

/// <summary>A planned file (Python <c>SelectedFile</c>).</summary>
/// <param name="FileId">AMPRIDX3 row + 1.</param>
/// <param name="IndexEntry">AMPRIDX3 row.</param>
/// <param name="Source">Source path under the root.</param>
/// <param name="Relative">Path relative to <c>/app0</c>.</param>
/// <param name="Rule">Effective rule (loose for files that stay on the filesystem).</param>
/// <param name="Size">Size on disk (index size for loose files).</param>
/// <param name="MTime">Modification time on disk (index time for loose files).</param>
/// <param name="Lane">Pack lane within the rule's group.</param>
public sealed record AMPRSelectedFile(
    int FileId, AMPRIndexEntry IndexEntry, string Source, string Relative, AMPRRule Rule, long Size, long MTime, int Lane);

/// <summary>Result of a pack build.</summary>
/// <param name="IndexPath">Published manifest.</param>
/// <param name="Stats">Counters.</param>
/// <param name="Warnings">Auto-loose and missing-source notes.</param>
/// <param name="Volumes">Pack volumes named by the manifest.</param>
public sealed record AMPRBuildResult(string IndexPath, AMPRBuildStats Stats, IReadOnlyList<string> Warnings, int Volumes = 0)
{
    /// <summary>ampr_emu <c>AMPR_EMU_PACK_MAX_FILES</c>: manifest file records the runtime loads.</summary>
    public const long RuntimeMaxFiles = 2_000_000;

    /// <summary>ampr_emu <c>AMPR_EMU_PACK_MAX_CHUNKS</c>: chunk records the runtime loads.</summary>
    public const long RuntimeMaxChunks = 16_000_000;

    /// <summary>ampr_emu <c>AMPR_EMU_PACK_MAX_PACKS</c>: pack volumes the runtime loads.</summary>
    public const int RuntimeMaxVolumes = 1024;

    /// <summary>
    /// Limits of the default ampr_emu build that this set exceeds; the runtime then rejects the whole manifest
    /// (<c>apr.pack.index.invalid</c>). ampr_pack.py only checks the format limits, so it publishes such sets.
    /// </summary>
    /// <returns>One message per exceeded limit.</returns>
    public IReadOnlyList<string> RuntimeLimitWarnings()
    {
        List<string> messages = [];
        if (Stats.FilesTotal > RuntimeMaxFiles)
        {
            messages.Add($"{Stats.FilesTotal} files exceed the AMPR Emu limit of {RuntimeMaxFiles}; the console will reject this pack set");
        }

        if (Stats.Chunks > RuntimeMaxChunks)
        {
            messages.Add($"{Stats.Chunks} chunks exceed the AMPR Emu limit of {RuntimeMaxChunks}; use larger blocks or leave some files loose");
        }

        if (Volumes > RuntimeMaxVolumes)
        {
            messages.Add($"{Volumes} pack volumes exceed the AMPR Emu limit of {RuntimeMaxVolumes}; raise max_pack_size or lower pack_count");
        }

        return messages;
    }
}
