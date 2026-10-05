using MkPFS.Build.Exfat;
using MkPFS.Build.PFS;
using MkPFS.Core.AMPR;
using MkPFS.Core.Diagnostics;
using MkPFS.Core.Util;

namespace MkPFS.Build.AMPRPack;

/// <summary>Settings for <see cref="AMPRGameBuilder.Build"/>.</summary>
public sealed record AMPRGameOptions
{
    /// <summary>Pack rules.</summary>
    public required AMPRPackConfig Config { get; init; }

    /// <summary>Folder whose files (AMPR Emu and other fakelib libraries) go into the output's library folder.</summary>
    public string? LibsDir { get; init; }

    /// <summary>Decode every chunk and compare packed and loose files with the source after the build.</summary>
    public bool Verify { get; init; } = true;

    /// <summary>exFAT image path (or existing folder for <c>&lt;titleId&gt;.exfat</c>) to build from the output.</summary>
    public string? ExfatImage { get; init; }

    /// <summary>Free space to leave inside the exFAT image (0: tight). A debug run of AMPR Emu writes its log there.</summary>
    public long ExfatFreeBytes { get; init; }
}

/// <summary>Result of <see cref="AMPRGameBuilder.Build"/>.</summary>
/// <param name="OutputDir">Playable <c>/app0</c> folder.</param>
/// <param name="LibraryDir"><c>fakelib</c> or <c>fakelib2</c>.</param>
/// <param name="Pack">Pack build result.</param>
/// <param name="LooseFiles">Files copied from the source.</param>
/// <param name="SourceBytes">Bytes of the source files.</param>
/// <param name="OutputBytes">Bytes of the output folder.</param>
/// <param name="ExfatImage">exFAT image written, if any.</param>
public sealed record AMPRGameResult(
    string OutputDir, string LibraryDir, AMPRBuildResult Pack, int LooseFiles, long SourceBytes, long OutputBytes, string? ExfatImage);

/// <summary>
/// Turns an unpacked game folder into a folder that runs from AMPR packs as is (not in ampr_pack.py): the library
/// folder with the selected AMPR Emu, an <c>ampr_emu.index</c> of that final tree, the packs, and every file that
/// stays loose. The source is only read.
/// </summary>
public static class AMPRGameBuilder
{
    /// <summary>The emulator library that serves the packs.</summary>
    public const string EmulatorLibrary = "libSceAmpr.sprx";

    // Every AMPR Emu build that reads asset packs (0.4.2.1 and newer) embeds its manifest magic; older ones do not.
    private static readonly byte[] PackSupportMarker = "AMPRPAK4"u8.ToArray();

    /// <summary>Build the playable folder, then optionally an exFAT image of it.</summary>
    /// <param name="sourceRoot">Unpacked game folder (<c>/app0</c>).</param>
    /// <param name="outputDir">New or empty output folder.</param>
    /// <param name="options">Settings.</param>
    /// <param name="log">Step messages.</param>
    /// <param name="packProgress">Pack progress.</param>
    /// <param name="exfatProgress">exFAT progress.</param>
    /// <returns>Summary.</returns>
    /// <exception cref="BuildException">The inputs cannot give a playable folder, or a check failed.</exception>
    public static AMPRGameResult Build(
        string sourceRoot,
        string outputDir,
        AMPRGameOptions options,
        IMkPFSLog log,
        Action<AMPRBuildProgress>? packProgress = null,
        IProgressSink? exfatProgress = null)
    {
        sourceRoot = Path.GetFullPath(sourceRoot);
        outputDir = Path.GetFullPath(outputDir);
        string? image = options.ExfatImage is null ? null : Path.GetFullPath(options.ExfatImage);
        if (image is not null && Directory.Exists(image))
        {
            image = Path.Combine(image, GameParams.DefaultImageBasename(sourceRoot) + ".exfat");
        }

        CheckInputs(sourceRoot, outputDir, options.Config, image, log);
        string libraryDir = Directory.Exists(Path.Combine(sourceRoot, "fakelib2")) ? "fakelib2" : "fakelib";
        if (options.LibsDir is { } libsDir && !Directory.Exists(libsDir))
        {
            throw new BuildException($"library folder not found: {libsDir}");
        }

        // The selected library replaces the game's own, so check whichever one will end up in the output.
        string? selected = options.LibsDir is { } dir && File.Exists(Path.Combine(dir, EmulatorLibrary)) ? Path.Combine(dir, EmulatorLibrary) : null;
        CheckEmulator(selected ?? Path.Combine(sourceRoot, libraryDir, EmulatorLibrary), libraryDir);

        bool created = !Directory.Exists(outputDir);
        Directory.CreateDirectory(outputDir);
        AMPRGameResult result;
        try
        {
            result = BuildInto(sourceRoot, outputDir, libraryDir, options, log, packProgress);
        }
        catch
        {
            // The folder was new or empty, so nothing of the user's is lost; a retry then starts clean.
            RemoveOutput(outputDir, created);
            throw;
        }

        if (image is null)
        {
            return result;
        }

        // A failed image leaves the finished folder in place, and only the partial image is removed.
        log.Info(options.ExfatFreeBytes > 0
            ? $"Building the exFAT image (64 KiB clusters, {Sizes.HumanReadable(options.ExfatFreeBytes)} free)"
            : "Building the exFAT image (64 KiB clusters)");
        string written;
        try
        {
            written = ExfatImageWriter.Write(outputDir, image, null, exfatProgress, options.ExfatFreeBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            File.Delete(image);
            throw new BuildException($"the game folder is ready, but the exFAT image failed: {ex.Message}");
        }

        log.Info($"exFAT image: {Sizes.HumanReadable(new FileInfo(written).Length)}: {written}");
        log.Info("Mount it read-only (ShadowMountPlus mount_read_only=1, the default) so AMPR Emu never rebuilds the index");
        return result with { ExfatImage = written };
    }

    private static AMPRGameResult BuildInto(
        string sourceRoot,
        string outputDir,
        string libraryDir,
        AMPRGameOptions options,
        IMkPFSLog log,
        Action<AMPRBuildProgress>? packProgress)
    {
        // 1. Libraries: the source's library folder, then the selected files over it. The index needs their final sizes.
        log.Info($"[1/5] Libraries: {libraryDir}/");
        if (libraryDir == "fakelib2")
        {
            log.Info("  The game has fakelib2/, which ShadowMountPlus mounts instead of fakelib/; using it");
        }

        CopyTree(Path.Combine(sourceRoot, libraryDir), Path.Combine(outputDir, libraryDir));
        if (options.LibsDir is { } libs)
        {
            AddLibraries(libs, Path.Combine(outputDir, libraryDir), log);
        }

        // 2. Index of the final tree, before packing: the pack manifest addresses files by their row in it.
        log.Info($"[2/5] Writing {AmprIndex.IndexName}");
        string index = Path.Combine(outputDir, AmprIndex.IndexName);
        int rows = AmprIndex.BuildWithOverlay(sourceRoot, outputDir, libraryDir, index);
        if (rows == 0)
        {
            throw new BuildException($"the game folder has no files: {sourceRoot}");
        }

        log.Info($"  {rows} files indexed");

        // 3. Packs, read from the source.
        log.Info("[3/5] Packing");
        AMPRBuildResult pack = AMPRPackBuilder.Build(sourceRoot, index, outputDir, options.Config, progress: packProgress);
        foreach (string warning in pack.Warnings)
        {
            log.Warning("warning: " + warning);
        }

        foreach (string warning in pack.RuntimeLimitWarnings())
        {
            log.Warning("warning: " + warning);
        }

        log.Info($"  {pack.Stats.FilesPacked} files packed into {pack.Volumes} volume(s), {pack.Stats.FilesLoose} stay loose");
        if (pack.Stats.FilesPacked == 0)
        {
            log.Warning("warning: no files were packed; the output is a plain copy of the game");
        }

        // 4. Loose files: known only now, because the packer leaves large incompressible files loose by sampling them.
        log.Info("[4/5] Copying loose files");
        int loose = CopyLooseFiles(sourceRoot, outputDir, libraryDir, pack.Stats.LoosePaths);
        CopyDirectories(sourceRoot, outputDir);
        log.Info($"  {loose} files copied");

        // 5. Checks.
        if (options.Verify)
        {
            log.Info("[5/5] Verifying");
            Verify(sourceRoot, outputDir, pack.IndexPath, log);
        }
        else
        {
            log.Info("[5/5] Verification skipped");
        }

        long sourceBytes = TreeBytes(sourceRoot);
        long outputBytes = TreeBytes(outputDir);
        log.Info($"Game folder: {Sizes.HumanReadable(sourceBytes)} -> {Sizes.HumanReadable(outputBytes)}: {outputDir}");

        return new AMPRGameResult(outputDir, libraryDir, pack, loose, sourceBytes, outputBytes, null);
    }

    /// <summary><see langword="true"/> when <paramref name="library"/> is an AMPR Emu build that reads asset packs.</summary>
    /// <param name="library"><c>libSceAmpr.sprx</c>.</param>
    /// <returns>Whether the build embeds the AMPRPAK4 manifest magic.</returns>
    public static bool SupportsPacks(string library) =>
        File.ReadAllBytes(library).AsSpan().IndexOf(PackSupportMarker) >= 0;

    private static void CheckInputs(string source, string output, AMPRPackConfig config, string? image, IMkPFSLog log)
    {
        if (!Directory.Exists(source))
        {
            throw new BuildException($"game folder not found: {source}");
        }

        if (IsSameOrInside(output, source) || IsSameOrInside(source, output))
        {
            throw new BuildException("the game folder and the output folder must not contain each other");
        }

        if (File.Exists(output) || (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()))
        {
            throw new BuildException($"the output folder must be new or empty: {output}");
        }

        if (File.Exists(Path.Combine(source, config.IndexName)))
        {
            throw new BuildException($"the game folder already has {config.IndexName}; start from the unpacked game");
        }

        if (!File.Exists(Path.Combine(source, "sce_sys", "param.json")) && !File.Exists(Path.Combine(source, "sce_sys", "param.sfo")))
        {
            log.Warning("warning: the game folder has no sce_sys/param.json; is it the game's /app0 folder?");
        }

        if (image is not null)
        {
            if (IsSameOrInside(image, output) || IsSameOrInside(image, source))
            {
                throw new BuildException("the exFAT image must be outside the game and output folders");
            }

            if (File.Exists(image))
            {
                throw new BuildException($"the exFAT image already exists: {image}");
            }
        }
    }

    private static void RemoveOutput(string outputDir, bool created)
    {
        try
        {
            if (created)
            {
                Directory.Delete(outputDir, recursive: true);
                return;
            }

            foreach (FileSystemInfo entry in new DirectoryInfo(outputDir).EnumerateFileSystemInfos())
            {
                if (entry is DirectoryInfo sub)
                {
                    sub.Delete(recursive: true);
                }
                else
                {
                    entry.Delete();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Keep the build error; the leftover folder is reported by the next run's empty-folder check.
        }
    }

    private static void AddLibraries(string libsDir, string target, IMkPFSLog log)
    {
        Directory.CreateDirectory(target);
        foreach (FileInfo file in new DirectoryInfo(libsDir).EnumerateFiles()
                     .Where(f => !f.Name.StartsWith('.') && !NameRules.IsIgnoredName(f.Name))
                     .OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            string destination = Path.Combine(target, file.Name);
            if (File.Exists(destination) && SameContent(file.FullName, destination))
            {
                log.Info($"  {file.Name}: already up to date");
                continue;
            }

            string state = File.Exists(destination) ? "replaced" : "added";
            file.CopyTo(destination, overwrite: true);
            File.SetLastWriteTimeUtc(destination, file.LastWriteTimeUtc);
            log.Info($"  {file.Name}: {state}");
        }
    }

    private static void CheckEmulator(string library, string libraryDir)
    {
        if (!File.Exists(library))
        {
            throw new BuildException($"no {libraryDir}/{EmulatorLibrary}: select the AMPR Emu library folder");
        }

        if (!SupportsPacks(library))
        {
            throw new BuildException(
                $"{libraryDir}/{EmulatorLibrary} cannot read asset packs; use AMPR Emu 0.4.2.1 or newer from https://github.com/drakmor/ampr_emu/releases");
        }
    }

    private static int CopyLooseFiles(string source, string output, string libraryDir, IReadOnlyList<string> loosePaths)
    {
        string libraryKey = AmprIndex.KeyFor(libraryDir + "/");
        int copied = 0;
        foreach (string relative in loosePaths)
        {
            if (AmprIndex.KeyFor(relative).StartsWith(libraryKey, StringComparison.Ordinal))
            {
                continue; // already final in the output
            }

            string from = AMPRAssetPath.SafeOutputPath(source, relative);
            string to = AMPRAssetPath.SafeOutputPath(output, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(from, to, overwrite: false);
            File.SetLastWriteTimeUtc(to, File.GetLastWriteTimeUtc(from));
            copied++;
        }

        return copied;
    }

    // Empty folders too, so the output has the source's full directory tree.
    private static void CopyDirectories(string source, string output)
    {
        foreach (string dir in Directory.EnumerateDirectories(source, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
        {
            string relative = Path.GetRelativePath(source, dir);
            if (!relative.Split(Path.DirectorySeparatorChar).Any(NameRules.IsIgnoredName))
            {
                Directory.CreateDirectory(Path.Combine(output, relative));
            }
        }
    }

    private static void CopyTree(string from, string to)
    {
        if (!Directory.Exists(from))
        {
            return;
        }

        Directory.CreateDirectory(to);
        foreach (string dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        }

        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string destination = Path.Combine(to, Path.GetRelativePath(from, file));
            File.Copy(file, destination);
            File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(file));
        }
    }

    // Every packed file decodes to the source bytes, and every loose index row is in the output with its size.
    private static void Verify(string source, string output, string manifest, IMkPFSLog log)
    {
        try
        {
            AMPRVerifyResult blocks = AMPRPackTools.Verify(manifest);
            AMPRSourceCompareResult compare = AMPRPackTools.VerifyAgainstRoot(manifest, source);
            log.Info($"  Packs: {blocks.Files} files, {blocks.PhysicalChunks} chunks decoded; {compare.Files} files match the source");
        }
        catch (AMPRPackException ex)
        {
            throw new BuildException($"pack verification failed: {ex.Message}");
        }

        List<string> missing = [];
        int loose = 0;
        foreach (AMPRListEntry entry in AMPRPackTools.List(manifest))
        {
            if (entry.Packed)
            {
                continue;
            }

            FileInfo file = new(AMPRAssetPath.SafeOutputPath(output, AMPRAssetPath.Relative(entry.Path)));
            if (!file.Exists || (ulong)file.Length != entry.LogicalSize)
            {
                missing.Add(AMPRAssetPath.Relative(entry.Path));
            }

            loose++;
        }

        if (missing.Count > 0)
        {
            throw new BuildException($"{missing.Count} loose file(s) are missing or differ in the output, first: {missing[0]}");
        }

        log.Info($"  Loose files: {loose} present with their indexed sizes");
    }

    private static bool IsSameOrInside(string path, string folder)
    {
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string a = Path.TrimEndingDirectorySeparator(path);
        string b = Path.TrimEndingDirectorySeparator(folder);
        return a.Equals(b, comparison) || a.StartsWith(b + Path.DirectorySeparatorChar, comparison);
    }

    private static bool SameContent(string a, string b)
    {
        FileInfo x = new(a);
        FileInfo y = new(b);
        return x.Length == y.Length && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
    }

    private static long TreeBytes(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
}
