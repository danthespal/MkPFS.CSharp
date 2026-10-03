using MkPFS.Build.PFS;
using MkPFS.Core.Diagnostics;

namespace MkPFS.Build;

/// <summary>AMPR emulation settings for packing a game folder.</summary>
public sealed record AmprOptions
{
    /// <summary>Folder holding the AMPR Emu libraries to copy into <c>fakelib/</c>, or <see langword="null"/>.</summary>
    public string? LibsDir { get; init; }

    /// <summary>Inject the libraries even when the source has no PlayGo chunk file.</summary>
    public bool ForceAprTitle { get; init; }

    /// <summary>Generate <c>ampr_emu.index</c> when <c>fakelib/libSceAmpr.sprx</c> is present.</summary>
    public bool Index { get; init; } = true;

    /// <summary>Keep a valid existing index.</summary>
    public bool SkipRegenIfExists { get; init; }

    /// <summary>Always rebuild the index.</summary>
    public bool ForceRegen { get; init; }
}

/// <summary>
/// AMPR Emu (drakmor/ampr_emu) setup: copies the user's emulator libraries into <c>fakelib/</c> of APR titles and
/// builds <c>ampr_emu.index</c>. The libraries are never bundled or downloaded.
/// </summary>
public static class AmprLibs
{
    /// <summary>Library that enables the emulator; required in <see cref="AmprOptions.LibsDir"/>.</summary>
    public const string AmprLibrary = "libSceAmpr.sprx";

    /// <summary>Libraries copied when present, in copy order.</summary>
    public static IReadOnlyList<string> LibraryNames { get; } = [AmprLibrary, "libScePlayGo.sprx"];

    /// <summary><see langword="true"/> when <c>sce_sys</c> holds a PlayGo chunk file (APR title).</summary>
    /// <param name="sourceRoot">Game folder.</param>
    /// <returns>Whether the title uses PlayGo.</returns>
    public static bool IsAprTitle(string sourceRoot) =>
        File.Exists(Path.Combine(sourceRoot, "sce_sys", "playgo-chunk.dat")) ||
        File.Exists(Path.Combine(sourceRoot, "sce_sys", "playgo_chunk.dat"));

    /// <summary>Copy the libraries from <paramref name="libsDir"/> into <c>fakelib/</c>; identical files are left alone.</summary>
    /// <param name="sourceRoot">Game folder.</param>
    /// <param name="libsDir">Library folder.</param>
    /// <param name="log">Messages.</param>
    /// <returns>Libraries copied.</returns>
    /// <exception cref="BuildException">The folder or <see cref="AmprLibrary"/> is missing, or a copy fails.</exception>
    public static int Inject(string sourceRoot, string libsDir, IMkPFSLog log)
    {
        if (!File.Exists(Path.Combine(libsDir, AmprLibrary)))
        {
            throw new BuildException(Directory.Exists(libsDir)
                ? $"--ampr-libs folder has no {AmprLibrary}: {libsDir}"
                : $"--ampr-libs must be an existing directory: {libsDir}");
        }

        string fakelib = Path.Combine(sourceRoot, "fakelib");
        int copied = 0;
        try
        {
            Directory.CreateDirectory(fakelib);
            foreach (string name in LibraryNames)
            {
                string from = Path.Combine(libsDir, name);
                string to = Path.Combine(fakelib, name);
                if (!File.Exists(from))
                {
                    continue;
                }

                if (File.Exists(to) && File.ReadAllBytes(from).AsSpan().SequenceEqual(File.ReadAllBytes(to)))
                {
                    log.Info($"fakelib/{name} is up to date");
                    continue;
                }

                File.Copy(from, to, overwrite: true);
                log.Info($"Copied {name} into fakelib/");
                copied++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BuildException($"failed to copy AMPR Emu libraries into {fakelib}: {ex.Message}");
        }

        return copied;
    }

    /// <summary>
    /// Inject the libraries into APR titles when <see cref="AmprOptions.LibsDir"/> is set, then generate the index
    /// (<see cref="AmprIndex.Ensure"/>). Runs before packing so the image includes both.
    /// </summary>
    /// <param name="sourceRoot">Game folder.</param>
    /// <param name="options">Settings.</param>
    /// <param name="log">Messages.</param>
    /// <exception cref="BuildException">Injection failed.</exception>
    public static void Prepare(string sourceRoot, AmprOptions options, IMkPFSLog log)
    {
        bool apr = options.ForceAprTitle || IsAprTitle(sourceRoot);
        bool hasLibrary = File.Exists(Path.Combine(sourceRoot, AmprIndex.FakelibMarker));
        if (options.LibsDir is { } libs)
        {
            if (apr)
            {
                Inject(sourceRoot, libs, log);
            }
            else if (!hasLibrary)
            {
                log.Info("No sce_sys/playgo-chunk.dat; not an APR title, AMPR Emu libraries not added (use --ampr-title to force)");
            }
        }
        else if (apr && !hasLibrary && options.Index)
        {
            log.Warning("APR title (sce_sys/playgo-chunk.dat) without fakelib/libSceAmpr.sprx; pass --ampr-libs <dir> to add AMPR Emu");
        }

        AmprIndex.Ensure(sourceRoot, log, options.Index, options.SkipRegenIfExists, options.ForceRegen);
    }
}
