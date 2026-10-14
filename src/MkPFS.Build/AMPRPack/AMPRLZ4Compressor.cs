using System.Text;
using MkPFS.Build.PFS;
using MkPFS.Core.AMPR;
using MkPFS.Core.Compression;
using MkPFS.Core.Diagnostics;
using MkPFS.Core.Util;

namespace MkPFS.Build.AMPRPack;

/// <summary>Settings for <see cref="AMPRLZ4Compressor.Build"/>.</summary>
public sealed record AMPRLZ4CompressorOptions
{
    /// <summary>1..12; up to <see cref="AMPRUniversalProfile.FastLevelLimit"/> uses the LZ4 fast encoder, higher ones LZ4 HC.</summary>
    public int Level { get; init; } = AMPRUniversalProfile.DefaultLevel;

    /// <summary>Compression worker threads, or <see langword="null"/> or 0 for Auto (<see cref="CpuCores.Max"/>, which also caps it).</summary>
    public long? Workers { get; init; }

    /// <summary>Folder whose files (AMPR Emu and other fakelib libraries) go into the output's library folder.</summary>
    public string? LibsDir { get; init; }

    /// <summary>Decode every chunk and compare packed and loose files with the source after the build.</summary>
    public bool Verify { get; init; } = true;

    /// <summary>exFAT image path (or existing folder for <c>&lt;titleId&gt;.exfat</c>) to build from the output.</summary>
    public string? ExfatImage { get; init; }

    /// <summary>Free space to leave inside the exFAT image (0: tight).</summary>
    public long ExfatFreeBytes { get; init; }
}

/// <summary>
/// The PS5 LZ4 Game Compressor workflow (port of <c>ampr_backend_pack_game_options</c>): scan the game folder, build
/// the <see cref="AMPRUniversalProfile"/> from its structure, check the destination has room for the worst case,
/// then build the playable folder with <see cref="AMPRGameBuilder"/> (libraries, <c>ampr_emu.index</c>, packs, loose
/// files, verification, optional exFAT image). The source is only read.
/// </summary>
public static class AMPRLZ4Compressor
{
    // Pack metadata of an already converted game (index_builder.h refuses these).
    private static readonly string[] PackMetadata = ["ampr_assets.index", "ampr_assets.index.crc", "ampr_assets.index.runtime"];

    // Root files the source index never lists (index_builder.h, ampr_backend_measure).
    private static readonly HashSet<string> UnindexedRootFiles = new(StringComparer.Ordinal)
    {
        "ampr_emu.index", "ampr_emu.index.tmp", "ampr_commands.bin", "apr_emu.log",
    };

    /// <summary>One source file.</summary>
    /// <param name="Relative">Path relative to the game folder, <c>/</c> separators.</param>
    /// <param name="Size">Bytes.</param>
    public readonly record struct SourceFile(string Relative, long Size);

    /// <summary>Build the playable folder, then optionally an exFAT image of it.</summary>
    /// <param name="sourceRoot">Unpacked game folder (<c>/app0</c>).</param>
    /// <param name="outputDir">New or empty output folder.</param>
    /// <param name="options">Settings.</param>
    /// <param name="log">Step messages.</param>
    /// <param name="packProgress">Pack progress.</param>
    /// <param name="progress">Progress of the phases after packing (<see cref="AMPRGameBuilder.Build"/>).</param>
    /// <returns>Summary.</returns>
    /// <exception cref="BuildException">The inputs cannot give a playable folder, or a check failed.</exception>
    public static AMPRGameResult Build(
        string sourceRoot,
        string outputDir,
        AMPRLZ4CompressorOptions options,
        IMkPFSLog log,
        Action<AMPRBuildProgress>? packProgress = null,
        IProgressSink? progress = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);
        sourceRoot = Path.GetFullPath(sourceRoot);
        outputDir = Path.GetFullPath(outputDir);
        if (options.Level is < 1 or > 12)
        {
            throw new BuildException($"compression level must be between 1 and 12, got {options.Level}");
        }

        if (options.Workers is < 0)
        {
            throw new BuildException("workers must not be negative");
        }

        // 1. The source must be a game folder dump: the compressor needs its loader and its parameters.
        if (!Directory.Exists(sourceRoot))
        {
            throw new BuildException($"game folder not found: {sourceRoot}");
        }

        foreach (string required in (string[])["eboot.bin", "sce_sys/param.json"])
        {
            if (!File.Exists(Path.Combine(sourceRoot, required)))
            {
                throw new BuildException($"the game folder has no {required}; select an extracted PS5 game folder (/app0)");
            }
        }

        List<SourceFile> files = Scan(sourceRoot);
        if (files.Count == 0)
        {
            throw new BuildException($"the game folder has no files: {sourceRoot}");
        }

        // 2. Rules from the folder structure.
        string toml = AMPRUniversalProfile.RenderToml(files.Select(f => f.Relative), options.Level);
        AMPRPackConfig config = AMPRUniversalProfile.ToConfig(toml);
        if (options.Workers is > 0 and { } count)
        {
            config.Workers = count;
        }

        string codec = options.Level <= AMPRUniversalProfile.FastLevelLimit ? "LZ4 fast" : $"LZ4 HC level {options.Level}";
        log.Info($"Rules: LZ4 Game Compressor universal profile ({codec}), {config.Rules.Count} rules from the folder structure of {files.Count} files");
        bool unreal = AMPRUniversalProfile.IsUnrealIoStore(files.Select(f => f.Relative));
        if (unreal)
        {
            log.Info("  Unreal Engine IoStore: .utoc tables and .pak archives stay loose; .ucas containers are packed only if they compress");
        }

        // 3. What can be packed, before the long copy: a game whose large files are already compressed (Unreal's Oodle
        // .ucas and .pak) gains next to nothing, and the build would only copy it.
        long total = files.Where(f => !UnindexedRootFiles.Contains(f.Relative)).Sum(f => f.Size);
        long packable = PackableBytes(sourceRoot, files, config);
        if (total > 0 && packable * 10 < total)
        {
            log.Warning($"warning: only {Sizes.HumanReadable(packable)} of {Sizes.HumanReadable(total)} can be packed: the large files do not "
                + $"compress with LZ4 (their data is already compressed{(unreal ? ", like Unreal Engine's Oodle .ucas and .pak" : string.Empty)}), "
                + "so the packed game would be about as large as the original");
        }
        else
        {
            log.Info($"  {Sizes.HumanReadable(packable)} of {Sizes.HumanReadable(total)} can be packed (large files sampled)");
        }

        // 4. Room for the worst case, where nothing compresses (ampr_backend_measure).
        CheckFreeSpace(files, outputDir, options, log);

        return AMPRGameBuilder.Build(
            sourceRoot,
            outputDir,
            new AMPRGameOptions
            {
                Config = config,
                LibsDir = options.LibsDir,
                Verify = options.Verify,
                ExfatImage = options.ExfatImage,
                ExfatFreeBytes = options.ExfatFreeBytes,
            },
            log,
            packProgress,
            progress);
    }

    /// <summary>
    /// Worst-case output size for <paramref name="files"/>: every file stored raw in 64 KiB pages, plus manifest,
    /// chunk tables, volume headers and a 16 MiB margin (<c>ampr_backend_measure</c>).
    /// </summary>
    /// <param name="files">Source files.</param>
    /// <returns>Bytes.</returns>
    public static long EstimateOutputBytes(IEnumerable<SourceFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        const long page = 64 * 1024;
        checked
        {
            long physical = 0;
            long metadata = 512;
            long count = 0;
            foreach (SourceFile file in files)
            {
                if (UnindexedRootFiles.Contains(file.Relative))
                {
                    continue;
                }

                physical += Sizes.CeilDiv(file.Size, page) * page;
                metadata += 128 + (Encoding.UTF8.GetByteCount(file.Relative) * 2L) + (16 * Sizes.CeilDiv(file.Size, 16 * 1024));
                count++;
            }

            physical += metadata;
            physical += (5 + (physical / ((16L << 30) - (128 * 1024))) + (count / 65536)) * page;
            return physical + (16L << 20);
        }
    }

    // Bytes of the files the rules pack and the planner would keep packed: large files are sampled the way the planner's
    // auto-loose check samples them, so the result matches what the build will do.
    private static long PackableBytes(string sourceRoot, List<SourceFile> files, AMPRPackConfig config)
    {
        using LZ4Encoder encoder = new();
        long packable = 0;
        foreach (SourceFile file in files)
        {
            AMPRRule rule = config.SelectRule(file.Relative);
            if (rule.Action == "loose" || UnindexedRootFiles.Contains(file.Relative)
                || (config.KeepOnDisk && AMPRDiskFiles.MustStayOnDisk(file.Relative, (ulong)file.Size)))
            {
                continue;
            }

            string source = Path.Combine([sourceRoot, .. file.Relative.Split('/')]);
            AMPRSelectedFile item = new(0, new AMPRIndexEntry(file.Relative, (ulong)file.Size, 0), source, file.Relative, rule, file.Size, 0, 0);
            AMPRGroupConfig group = config.Groups[rule.Group];
            if (!AMPRPackPlanner.DecideAutoLoose(item, config, encoder, group.IOPageSize != 0 ? group.IOPageSize : config.IOPageSize).UseLoose)
            {
                packable += file.Size;
            }
        }

        return packable;
    }

    // Every file of the game, in walk order. Links and existing pack metadata are refused like the compressor does.
    private static List<SourceFile> Scan(string sourceRoot)
    {
        List<SourceFile> files = [];
        EnumerationOptions options = new() { RecurseSubdirectories = false, AttributesToSkip = 0, IgnoreInaccessible = false };
        Stack<DirectoryInfo> pending = new();
        pending.Push(new DirectoryInfo(sourceRoot));
        while (pending.Count > 0)
        {
            DirectoryInfo dir = pending.Pop();
            foreach (FileSystemInfo entry in dir.EnumerateFileSystemInfos("*", options))
            {
                if (NameRules.IsIgnoredName(entry.Name))
                {
                    continue;
                }

                string relative = Path.GetRelativePath(sourceRoot, entry.FullName).Replace('\\', '/');
                if (entry.LinkTarget is not null)
                {
                    throw new BuildException($"the game folder has a link, which the LZ4 Game Compressor does not follow: {relative}");
                }

                if (entry is DirectoryInfo sub)
                {
                    pending.Push(sub);
                    continue;
                }

                if (PackMetadata.Contains(relative, StringComparer.OrdinalIgnoreCase))
                {
                    throw new BuildException($"the game folder already has pack metadata ({relative}); select the original uncompressed game folder");
                }

                files.Add(new SourceFile(relative, ((FileInfo)entry).Length));
            }
        }

        return files;
    }

    private static void CheckFreeSpace(List<SourceFile> files, string outputDir, AMPRLZ4CompressorOptions options, IMkPFSLog log)
    {
        long estimate = EstimateOutputBytes(files);
        Dictionary<string, long> needed = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        Dictionary<string, long> free = new(needed.Comparer);
        void Need(string path, long bytes)
        {
            if (FreeSpace(path) is not ({ } root, { } available))
            {
                log.Info($"  Free space of {path} is unknown; not checked");
                return;
            }

            needed[root] = checked(needed.GetValueOrDefault(root) + bytes);
            free[root] = available;
        }

        Need(outputDir, estimate);
        if (options.ExfatImage is { } image)
        {
            // The image holds the output folder plus the requested free space.
            Need(Path.GetFullPath(image), checked(estimate + Math.Max(0, options.ExfatFreeBytes)));
        }

        foreach ((string root, long bytes) in needed)
        {
            if (free[root] < bytes)
            {
                throw new BuildException(
                    $"not enough free space on {root}: up to {Sizes.HumanReadable(bytes)} may be needed (if nothing compresses), {Sizes.HumanReadable(free[root])} is free");
            }
        }
    }

    // The mounted volume holding path (the longest matching root) and its free bytes, or nulls when unknown.
    private static (string? Root, long? Available) FreeSpace(string path)
    {
        try
        {
            DriveInfo? best = null;
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                string root = drive.RootDirectory.FullName;
                if (IsUnder(path, root) && (best is null || root.Length > best.RootDirectory.FullName.Length))
                {
                    best = drive;
                }
            }

            return best is { IsReady: true } ? (best.RootDirectory.FullName, best.AvailableFreeSpace) : (null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, null);
        }
    }

    private static bool IsUnder(string path, string root)
    {
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, comparison) || Path.TrimEndingDirectorySeparator(path).Equals(Path.TrimEndingDirectorySeparator(root), comparison);
    }
}
