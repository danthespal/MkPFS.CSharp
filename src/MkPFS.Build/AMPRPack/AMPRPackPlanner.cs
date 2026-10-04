using MkPFS.Core.AMPR;
using MkPFS.Core.Compression;
using MkPFS.Core.Util;

namespace MkPFS.Build.AMPRPack;

/// <summary>
/// Chooses a rule, source and lane for every AMPRIDX3 file (Python <c>plan_files</c>, <c>_source_path</c>,
/// <c>_assign_lanes</c>, <c>_auto_loose_decision</c>).
/// </summary>
public static class AMPRPackPlanner
{
    /// <summary>Plan every file.</summary>
    /// <param name="entries">AMPRIDX3 rows (file id = row + 1).</param>
    /// <param name="root">Resolved <c>/app0</c> source directory.</param>
    /// <param name="config">Configuration.</param>
    /// <param name="stats">Counters to update.</param>
    /// <param name="includePatterns">CLI include globs; when given, other files stay loose.</param>
    /// <param name="excludePatterns">CLI exclude globs.</param>
    /// <param name="allowMissing">Leave missing sources loose instead of failing.</param>
    /// <param name="progress">Progress callback.</param>
    /// <returns>One planned file per row, and warnings.</returns>
    public static (List<AMPRSelectedFile> Files, List<string> Warnings) Plan(
        IReadOnlyList<AMPRIndexEntry> entries,
        string root,
        AMPRPackConfig config,
        AMPRBuildStats stats,
        IReadOnlyList<string> includePatterns,
        IReadOnlyList<string> excludePatterns,
        bool allowMissing,
        Action<AMPRBuildProgress>? progress = null)
    {
        List<AMPRSelectedFile> candidates = [];
        List<string> warnings = [];
        string indexName = config.IndexName.Replace('\\', '/').TrimStart('/');
        string packOutputGlob = AMPRPackPattern.Parse(config.PackPattern).OutputGlob;
        using LZ4Encoder encoder = new();
        for (int fileId = 1; fileId <= entries.Count; fileId++)
        {
            AMPRIndexEntry entry = entries[fileId - 1];
            string relative = AMPRAssetPath.Relative(entry.Path);
            progress?.Invoke(new AMPRBuildProgress("planning", fileId - 1, entries.Count, 0, 0, relative));
            AMPRRule rule = config.SelectRule(relative);
            if (includePatterns.Count > 0 && !AMPRGlob.Matches(relative, includePatterns))
            {
                rule = AMPRRule.Loose(rule.BlockShift);
            }

            if (excludePatterns.Count > 0 && AMPRGlob.Matches(relative, excludePatterns))
            {
                rule = AMPRRule.Loose(rule.BlockShift);
            }

            // Never consume an index or pack left by an earlier build, including custom nested pack names.
            string normalized = relative.Replace('\\', '/').TrimStart('/');
            if (normalized == indexName || normalized == indexName + ".crc" || normalized == indexName + ".runtime"
                || AMPRGlob.Matches(normalized, [packOutputGlob]))
            {
                rule = AMPRRule.Loose(rule.BlockShift);
            }

            string source = Path.Combine([root, .. relative.Split('/')]);
            if (rule.Action == "loose")
            {
                candidates.Add(new AMPRSelectedFile(fileId, entry, source, relative, rule, (long)entry.Size, entry.MTime, 0));
                continue;
            }

            string resolved;
            try
            {
                (source, resolved) = SourcePath(root, relative);
            }
            catch (Exception exc) when (exc is FileNotFoundException or DirectoryNotFoundException or ArgumentException)
            {
                if (!allowMissing)
                {
                    throw new AMPRPackException($"source is missing or unsafe for file id {fileId}: {relative}", exc);
                }

                warnings.Add($"file id {fileId} left loose because source is missing: {relative}");
                candidates.Add(new AMPRSelectedFile(fileId, entry, source, relative, AMPRRule.Loose(rule.BlockShift), (long)entry.Size, entry.MTime, 0));
                continue;
            }

            FileInfo info = new(resolved);
            if (!info.Exists)
            {
                throw new AMPRPackException($"source is not a regular file: {relative}");
            }

            if (config.ValidateIndexMetadata && (ulong)info.Length != entry.Size)
            {
                throw new AMPRPackException($"AMPRIDX3 size mismatch for {relative}: index={entry.Size}, disk={info.Length}");
            }

            AMPRSelectedFile candidate = new(fileId, entry, source, relative, rule, info.Length, UnixSeconds(info.LastWriteTimeUtc), 0);
            AMPRGroupConfig group = config.Groups[rule.Group];
            AutoLooseDecision autoLoose = DecideAutoLoose(candidate, config, encoder, group.IOPageSize != 0 ? group.IOPageSize : config.IOPageSize);
            stats.AutoLooseSampledBytes += autoLoose.SampledBytes;
            if (autoLoose.UseLoose)
            {
                string details = $"{relative} size={candidate.Size} sampled={autoLoose.SampledBytes} "
                    + $"saving={Percent(autoLoose.SavingsRatio)} rawBlocks={Percent(autoLoose.RawRatio)}; ";
                if (config.SelfContained)
                {
                    warnings.Add("self-contained: auto-loose suppressed for " + details + "incompressible blocks will be stored RAW");
                }
                else
                {
                    stats.FilesAutoLoose++;
                    stats.AutoLooseLogicalBytes += candidate.Size;
                    warnings.Add("auto-loose: " + details + "use force_pack=true or self_contained=true to override");
                    rule = rule with { Action = "loose", Hot = false, Streaming = false, Layout = "mixed" };
                }
            }

            candidates.Add(candidate with { Rule = rule });
        }

        Dictionary<int, int> lanes = AssignLanes(candidates, config);
        List<AMPRSelectedFile> result = [.. candidates.Select(c => c with { Lane = lanes.GetValueOrDefault(c.FileId) })];
        if (config.RequiredPacked.Count > 0)
        {
            List<string> missing = [.. result
                .Where(item => AMPRGlob.Matches(item.Relative, config.RequiredPacked) && item.Rule.Action == "loose")
                .Select(item => item.Relative)];
            if (missing.Count > 0)
            {
                string preview = string.Join(", ", missing.Take(8));
                string suffix = missing.Count <= 8 ? string.Empty : $" (+{missing.Count - 8} more)";
                throw new AMPRPackException($"required_packed matched files that would remain loose: {preview}{suffix}");
            }
        }

        progress?.Invoke(new AMPRBuildProgress("planning", entries.Count, entries.Count, 0, 0));
        return (result, warnings);
    }

    /// <summary>
    /// Sample positions for auto-loose: all blocks when few, the middle one for one sample, else an even
    /// integer interpolation that includes the first and last block (Python <c>_sample_block_indices</c>).
    /// </summary>
    /// <param name="totalBlocks">Blocks in the file.</param>
    /// <param name="sampleBlocks">Samples wanted.</param>
    /// <returns>Sorted distinct block indices.</returns>
    public static List<long> SampleBlockIndices(long totalBlocks, long sampleBlocks)
    {
        if (totalBlocks <= 0 || sampleBlocks <= 0)
        {
            return [];
        }

        if (totalBlocks <= sampleBlocks)
        {
            return [.. Enumerable.Range(0, (int)totalBlocks).Select(i => (long)i)];
        }

        if (sampleBlocks == 1)
        {
            return [totalBlocks / 2];
        }

        SortedSet<long> indices = [];
        for (long sample = 0; sample < sampleBlocks; sample++)
        {
            indices.Add(checked(sample * (totalBlocks - 1)) / (sampleBlocks - 1));
        }

        return [.. indices];
    }

    /// <summary>Unix seconds truncated toward zero, like Python <c>int(st_mtime)</c>.</summary>
    /// <param name="utc">UTC time.</param>
    /// <returns>Seconds.</returns>
    internal static long UnixSeconds(DateTime utc) => (utc.Ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerSecond;

    /// <summary>Python <c>f"{value:.2%}"</c>.</summary>
    private static string Percent(double value) => PythonText.FormatFixed(value * 100.0, 2) + "%";

    // Python _source_path: a safe relative path whose resolved target stays under the resolved root.
    private static (string Source, string Resolved) SourcePath(string root, string relative)
    {
        string candidate = AMPRAssetPath.SafeOutputPath(root, relative);
        string rootReal = RealPath(root);
        if (!File.Exists(candidate) && !Directory.Exists(candidate))
        {
            throw new FileNotFoundException("source is missing", candidate);
        }

        string resolved = RealPath(candidate);
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string prefix = Path.EndsInDirectorySeparator(rootReal) ? rootReal : rootReal + Path.DirectorySeparatorChar;
        return resolved.Equals(rootReal, comparison) || resolved.StartsWith(prefix, comparison)
            ? (candidate, resolved)
            : throw new AMPRPackException($"source symlink escapes root: {relative}");
    }

    // Absolute path with every symlink or junction component resolved (Path.resolve()).
    private static string RealPath(string path, int depth = 0)
    {
        if (depth > 40)
        {
            throw new AMPRPackException($"too many levels of symbolic links: {path}");
        }

        string full = Path.GetFullPath(path);
        string current = Path.GetPathRoot(full)!;
        foreach (string part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            string next = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            if (info.Exists && info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
            {
                next = RealPath(target.FullName, depth + 1);
            }

            current = next;
        }

        return Path.TrimEndingDirectorySeparator(current);
    }

    private static Dictionary<int, int> AssignLanes(List<AMPRSelectedFile> candidates, AMPRPackConfig config)
    {
        Dictionary<string, List<AMPRSelectedFile>> byGroup = new(StringComparer.Ordinal);
        foreach (AMPRSelectedFile item in candidates.Where(c => c.Rule.Action != "loose"))
        {
            if (!byGroup.TryGetValue(item.Rule.Group, out List<AMPRSelectedFile>? files))
            {
                byGroup[item.Rule.Group] = files = [];
            }

            files.Add(item);
        }

        Dictionary<int, int> result = [];
        foreach ((string groupName, List<AMPRSelectedFile> files) in byGroup)
        {
            AMPRGroupConfig group = config.Groups[groupName];
            int packCount = (int)group.PackCount;
            if (group.Assignment == "balanced")
            {
                // Largest first, then lower-cased path by code point, then file id; each goes to the lightest lane.
                long[] loads = new long[packCount];
                IEnumerable<AMPRSelectedFile> ordered = files
                    .OrderBy(f => -f.Size)
                    .ThenBy(f => f.Relative.ToLowerInvariant(), PythonCodePointComparer.Instance)
                    .ThenBy(f => f.FileId);
                foreach (AMPRSelectedFile file in ordered)
                {
                    int lane = 0;
                    for (int candidate = 1; candidate < packCount; candidate++)
                    {
                        if (loads[candidate] < loads[lane])
                        {
                            lane = candidate;
                        }
                    }

                    result[file.FileId] = lane;
                    loads[lane] += file.Size;
                }
            }
            else if (group.Assignment == "hash")
            {
                foreach (AMPRSelectedFile file in files)
                {
                    result[file.FileId] = (int)(AMPRAssetPath.Fnv1a64(AMPRAssetPath.AsciiFoldPathBytes(file.Relative)) % (ulong)packCount);
                }
            }
            else
            {
                foreach (AMPRSelectedFile file in files)
                {
                    result[file.FileId] = (file.FileId - 1) % packCount;
                }
            }
        }

        return result;
    }

    private static AutoLooseDecision DecideAutoLoose(AMPRSelectedFile item, AMPRPackConfig config, LZ4Encoder encoder, long ioPageSize)
    {
        AMPRRule rule = item.Rule;
        if (!config.AutoLooseLargeFiles
            || rule.Action != "compress"
            || rule.ForcePack
            || (rule.Hot && !config.AutoLooseHotFiles)
            || item.Size < config.AutoLooseMinFileSize
            || item.Size == 0)
        {
            return new AutoLooseDecision(false, 0, 0, 0, 0);
        }

        long blockSize = 1L << rule.BlockShift;
        long totalBlocks = (item.Size + blockSize - 1) / blockSize;
        long byteLimitedBlocks = Math.Max(1, config.AutoLooseSampleBytes / blockSize);
        List<long> indices = SampleBlockIndices(totalBlocks, Math.Min(config.AutoLooseSampleBlocks, byteLimitedBlocks));
        long sampledBytes = 0;
        long storedBytes = 0;
        long rawBlocks = 0;
        using (FileStream handle = File.OpenRead(item.Source))
        {
            (long Length, DateTime Written) before = FileStamp(item.Source);
            foreach (long blockIndex in indices)
            {
                long offset = blockIndex * blockSize;
                int expected = (int)Math.Min(blockSize, item.Size - offset);
                byte[] raw = new byte[expected];
                handle.Seek(offset, SeekOrigin.Begin);
                if (handle.ReadAtLeast(raw, expected, throwOnEndOfStream: false) != expected)
                {
                    throw new AMPRPackException($"source was truncated while sampling compression: {item.Relative}");
                }

                AMPRCompressedBlock block = AMPRBlockCompressor.Compress(encoder, raw, rule, ioPageSize);
                sampledBytes += raw.Length;
                storedBytes += block.Stored.Length;
                rawBlocks += block.Codec == AMPRPackFormat.ChunkCodecRaw ? 1 : 0;
            }

            if (FileStamp(item.Source) != before)
            {
                throw new AMPRPackException($"source changed while sampling compression: {item.Relative}");
            }
        }

        AutoLooseDecision decision = new(false, sampledBytes, storedBytes, rawBlocks, indices.Count);
        bool useLoose = decision.SavingsRatio < config.AutoLooseMinSavingsRatio || decision.RawRatio >= config.AutoLooseMaxRawRatio;
        return decision with { UseLoose = useLoose };
    }

    /// <summary>Size and write time; Python also compares inode and device, which .NET does not expose portably.</summary>
    internal static (long Length, DateTime Written) FileStamp(string path)
    {
        FileInfo info = new(path);
        return (info.Length, info.LastWriteTimeUtc);
    }

    private sealed record AutoLooseDecision(bool UseLoose, long SampledBytes, long StoredBytes, long RawBlocks, long SampledBlocks)
    {
        public double SavingsRatio => SampledBytes == 0 ? 0.0 : 1.0 - ((double)StoredBytes / SampledBytes);

        public double RawRatio => SampledBlocks == 0 ? 0.0 : (double)RawBlocks / SampledBlocks;
    }
}
