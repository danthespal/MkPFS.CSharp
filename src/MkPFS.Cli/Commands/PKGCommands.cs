using System.CommandLine;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using MkPFS.Cli.Output;
using MkPFS.Core.Crypto;
using MkPFS.Core.PFS;
using MkPFS.Core.PFS.PS5;
using MkPFS.Core.PKG;
using MkPFS.Core.Util;

namespace MkPFS.Cli.Commands;

/// <summary>
/// <c>inspect</c>, <c>tree</c>, <c>unpack</c> and <c>verify</c> for PS5 packages (<c>\x7FFIH</c>). Not in
/// Python MkPFS; reached from <see cref="ReadCommands"/> when the input has a package magic.
/// </summary>
internal static class PKGCommands
{
    /// <summary>The <c>--passcode</c> option shared by the read commands.</summary>
    public static Option<string?> PasscodeOption() =>
        new("--passcode") { Description = "PS5 package passcode (32 characters; default all zeros)" };

    /// <summary>Whether <paramref name="path"/> is a PS5 package file.</summary>
    public static bool IsPackage(string path) => File.Exists(path) && PKGFile.IsPackage(path);

    public static int Inspect(CliContext ctx, string path, string? passcode, string? ekpfsHex, bool json)
    {
        if (!TryOpen(ctx, path, passcode, ekpfsHex, out PS5Package? package, out PKGFile? bare))
        {
            return 1;
        }

        using IDisposable owner = (IDisposable?)package ?? bare!;
        PKGFile pkg = package?.Package ?? bare!;
        List<PS5InnerFile> tree = package?.InnerOrNull?.ReadTree() ?? [];
        if (json)
        {
            // Nested entries: Utf8JsonWriter (PythonJson only writes flat values).
            using MemoryStream buffer = new();
            using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteString("image", path);
                writer.WriteString("type", pkg.FIH is null ? "cnt" : pkg.FIH.IsDebug ? "fih-debug" : "fih-retail");
                writer.WriteString("content_id", pkg.CNT.ContentId);
                writer.WriteNumber("content_type", pkg.CNT.ContentType);
                writer.WriteNumber("drm_type", pkg.CNT.DrmType);
                writer.WriteStartArray("entries");
                foreach (CNTEntry e in pkg.Entries)
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", $"0x{e.Id:X4}");
                    writer.WriteString("name", e.Name);
                    writer.WriteNumber("size", e.DataSize);
                    writer.WriteBoolean("encrypted", e.IsEncrypted);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                if (package is not null)
                {
                    writer.WriteBoolean("outer_plaintext", package.Outer.IsPlaintext);
                    if (package.InnerOrNull is { } inner)
                    {
                        writer.WriteNumber("inner_mount_size", inner.Layout.MountSize);
                    }
                    else
                    {
                        writer.WriteString("inner_error", package.InnerError);
                    }
                }

                writer.WriteNumber("inner_files", tree.Count(f => f.Size >= 0));
                writer.WriteEndObject();
            }

            ctx.Out.Write(System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
            ctx.Out.Write(ctx.Out.NewLine);
            return 0;
        }

        ctx.VersionHeader();
        ctx.Info(new string('=', 70));
        ctx.Info("PS5 Package Inspection");
        ctx.Info(new string('=', 70));
        ctx.Info($"Image:         {path}");
        ctx.Info($"Type:          {(pkg.FIH is null ? "metadata container (\\x7FCNT)" : $"finalized image, {(pkg.FIH.IsDebug ? "debug" : "retail")} (format {pkg.FIH.FormatVersion})")}");
        ctx.Info($"Content ID:    {pkg.CNT.ContentId}");
        ctx.Info($"Content type:  0x{pkg.CNT.ContentType:X2}   DRM type: 0x{pkg.CNT.DrmType:X2}   Flags: 0x{pkg.CNT.ContentFlags:X8}");
        if (package is not null)
        {
            ctx.Info($"Outer PFS:     {CliContext.Thousands(pkg.FIH!.PFSImageSize)} bytes, superblock block {package.Outer.SuperblockIndex}, {(package.Outer.IsPlaintext ? "plaintext" : "encrypted")}");
            if (package.InnerOrNull is { } inner)
            {
                ctx.Info($"Inner mount:   {CliContext.Thousands(inner.Layout.MountSize)} bytes in {CliContext.Thousands(inner.Chunks.Count)} chunks");
                ctx.Info($"Inner files:   {CliContext.Thousands(tree.Count(f => f.Size >= 0))} ({Sizes.HumanReadable(tree.Where(f => f.Size > 0).Sum(f => f.Size))})");
            }
            else
            {
                ctx.Warning($"Inner image:   not readable: {package.InnerError}");
            }
        }

        ctx.Info($"CNT entries:   {pkg.Entries.Count}");
        foreach (CNTEntry entry in pkg.Entries)
        {
            ctx.Info($"  0x{entry.Id:X4}  {CliContext.Thousands(entry.DataSize),12}  {(entry.IsEncrypted ? "enc " : "    ")}{entry.Name}");
        }

        return 0;
    }

    public static int Tree(CliContext ctx, string path, string? passcode, string? ekpfsHex)
    {
        if (!TryOpen(ctx, path, passcode, ekpfsHex, out PS5Package? package, out PKGFile? bare))
        {
            return 1;
        }

        using IDisposable owner = (IDisposable?)package ?? bare!;
        if (!HasFileSystem(ctx, package))
        {
            return 1;
        }

        ctx.VersionHeader();
        ctx.Info("/");
        TreeRenderer.RenderPaths(package.Inner.ReadTree().Select(f => (f.Path, f.Size < 0))).ForEach(ctx.Info);
        return 0;
    }

    public static int Unpack(CliContext ctx, string path, string outputPath, string? passcode, string? ekpfsHex, bool showProgress)
    {
        if (!TryOpen(ctx, path, passcode, ekpfsHex, out PS5Package? package, out PKGFile? bare))
        {
            return 1;
        }

        using IDisposable owner = (IDisposable?)package ?? bare!;
        if (!HasFileSystem(ctx, package))
        {
            return 1;
        }

        PS5ExtractionResult result;
        try
        {
            result = PS5PackageExtractor.Extract(package, outputPath, includeEntries: true, ctx.CreateProgress(showProgress));
        }
        catch (InvalidDataException ex)
        {
            ctx.Error(ex.Message);
            return 1;
        }

        ctx.VersionHeader();
        ctx.Info("Extraction complete:");
        ctx.Info($"  Output:       {outputPath}");
        ctx.Info($"  Files written: {result.FilesWritten} ({result.EntriesWritten} from CNT entries under sce_sys)");
        ctx.Info($"  Dirs created:  {result.DirectoriesCreated}");
        ctx.Info($"  Bytes written: {result.BytesWritten}");
        return 0;
    }

    public static int Verify(CliContext ctx, string path, string? passcode, string? ekpfsHex, string? sourceDir = null)
    {
        if (!TryOpen(ctx, path, passcode, ekpfsHex, out PS5Package? package, out PKGFile? bare))
        {
            return 1;
        }

        using IDisposable owner = (IDisposable?)package ?? bare!;
        if (package is null)
        {
            ctx.Error("verify needs a finalized (\\x7FFIH) package; this is a bare \\x7FCNT container");
            return 1;
        }

        Core.Diagnostics.IProgressSink? progress = ctx.CreateProgress();
        List<PS5Check> checks = PS5PackageVerifier.Verify(package, progress is null ? null : (done, total) => progress.Step("verify", done, total, 0));
        ctx.VersionHeader();
        ctx.Info(new string('=', 70));
        ctx.Info("PS5 Package Check Report");
        ctx.Info(new string('=', 70));
        ctx.Info($"Image:   {path}");
        foreach (PS5Check check in checks)
        {
            string line = $"  {(check.Passed ? "ok  " : "FAIL")}  {check.Name,-20} {check.Detail}";
            if (check.Passed)
            {
                ctx.Info(line);
            }
            else
            {
                ctx.Error(line);
            }
        }

        int failed = checks.Count(c => !c.Passed);
        if (sourceDir is not null)
        {
            failed += CompareSource(ctx, package, sourceDir, passcode);
        }

        ctx.Info($"Errors:  {failed}");
        ctx.Info(new string('=', 70));
        return failed > 0 ? 1 : 0;
    }

    // Compare with the package view of the source folder (modules fake-signed, generated system files).
    private static int CompareSource(CliContext ctx, PS5Package package, string sourceDir, string? passcode)
    {
        if (package.InnerError is not null)
        {
            ctx.Error("  FAIL  Source compare       the inner image is not readable");
            return 1;
        }

        Build.FPKG.FPKGSource source = Build.FPKG.FPKGSource.Prepare(new Build.FPKG.FPKGSourceOptions
        {
            SourceDir = sourceDir,
            ContentId = package.Package.CNT.ContentId,
            Passcode = passcode ?? new string('0', PS5Keys.PasscodeLength),
        });
        Build.FPKG.FPKGComparison result = Build.FPKG.FPKGSourceComparer.Compare(package, source);
        string summary = $"{result.FilesCompared} files, {result.EntriesCompared} CNT entries against {sourceDir}";
        if (result.Errors.Count == 0)
        {
            ctx.Info($"  ok    Source compare       {summary}");
        }
        else
        {
            ctx.Error($"  FAIL  Source compare       {summary}");
        }

        result.Errors.ForEach(e => ctx.Error($"        {e}"));
        result.Warnings.ForEach(w => ctx.Warning($"        {w}"));
        return result.Errors.Count == 0 ? 0 : 1;
    }

    private static bool HasFileSystem(CliContext ctx, [NotNullWhen(true)] PS5Package? package)
    {
        if (package is null)
        {
            ctx.Error("a bare \\x7FCNT container has no file system");
            return false;
        }

        if (package.InnerError is { } error)
        {
            ctx.Error($"cannot read the inner image: {error}");
            return false;
        }

        return true;
    }

    // A finalized image opens fully; a bare CNT only as a container. Any failure is reported, not thrown.
    private static bool TryOpen(CliContext ctx, string path, string? passcode, string? ekpfsHex, out PS5Package? package, out PKGFile? bare)
    {
        package = null;
        bare = null;
        try
        {
            byte[]? ekpfs = string.IsNullOrWhiteSpace(ekpfsHex) ? null : PFSKeys.ParseEkpfsHex(ekpfsHex);
            using (PKGFile probe = PKGFile.Open(path))
            {
                if (probe.FIH is null)
                {
                    bare = PKGFile.Open(path);
                    return true;
                }
            }

            package = PS5Package.Open(path, passcode, ekpfs);
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or ArgumentException or IOException)
        {
            ctx.Error($"cannot read PS5 package: {ex.Message}");
            return false;
        }
    }
}
