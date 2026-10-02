namespace MkPFS.Build.PFS;

/// <summary>Pack statistics printed in the build summary (Python <c>BuildStats</c>).</summary>
public sealed class BuildStats
{
    /// <summary>Source path shown in the summary.</summary>
    public required string InputPath { get; set; }

    /// <summary>Image path.</summary>
    public required string OutputPath { get; set; }

    /// <summary>Files packed.</summary>
    public long TotalFiles { get; set; }

    /// <summary>Sum of raw file sizes.</summary>
    public long UncompressedTotalSize { get; set; }

    /// <summary>Sum of stored payload sizes.</summary>
    public long StoredTotalSize { get; set; }

    /// <summary>Stored size if every file used PFSC.</summary>
    public long AllCompressedTotalSize { get; set; }

    /// <summary>Files stored compressed.</summary>
    public long CompressedFiles { get; set; }

    /// <summary>Files stored raw.</summary>
    public long UncompressedFiles { get; set; }

    /// <summary>Elapsed build time.</summary>
    public double ElapsedSeconds { get; set; }

    /// <summary>Compression was enabled.</summary>
    public bool CompressionEnabled { get; set; } = true;

    /// <summary>Block size.</summary>
    public int BlockSize { get; set; } = 65536;

    /// <summary>Padding bytes in the last block of each file.</summary>
    public long BlockAlignmentWaste { get; set; }

    /// <summary>Percent saved versus raw.</summary>
    public double ActualGainPercent => UncompressedTotalSize == 0 ? 0.0 : ((double)(UncompressedTotalSize - StoredTotalSize) / UncompressedTotalSize) * 100.0;

    /// <summary>Percent saved if every file used PFSC.</summary>
    public double MaxPossibleGainPercent => UncompressedTotalSize == 0 ? 0.0 : ((double)(UncompressedTotalSize - AllCompressedTotalSize) / UncompressedTotalSize) * 100.0;
}
