using System.Buffers.Binary;
using MkPFS.Build.FPKG.Prospero;
using MkPFS.Core.Compression.Kraken;
using MkPFS.Core.Crypto;
using MkPFS.Core.Diagnostics;
using MkPFS.Core.Metadata;

namespace MkPFS.Build.FPKG;

/// <summary>Options of <see cref="FPKGBuilder.Build"/>.</summary>
public sealed class FPKGBuildOptions
{
    /// <summary>Application folder; never written.</summary>
    public required string SourceDir { get; init; }

    /// <summary>36-character content id.</summary>
    public required string ContentId { get; init; }

    /// <summary>32-character passcode.</summary>
    public string Passcode { get; init; } = new('0', PS5Keys.PasscodeLength);

    /// <summary>Build time: inode and superblock times of both file systems.</summary>
    public required DateTimeOffset Time { get; init; }

    /// <summary>
    /// 16-byte outer PFS seed, also the RSA padding seed; null derives it from the content id and passcode so equal
    /// inputs give equal packages.
    /// </summary>
    public byte[]? Seed { get; init; }

    /// <summary>Kraken mode (PS5PkgTool <c>auto</c>); stored when false.</summary>
    public bool Compress { get; init; }

    /// <summary>Kraken mode only: parse greedily, about twice as fast for a few percent of size.</summary>
    public bool Fast { get; init; }

    /// <summary>CPU cores for Kraken compression and encryption; 0 is <see cref="PS5KrakenChunk.MaxParallelism"/>.</summary>
    public int CpuCount { get; init; }

    /// <summary>Fake-sign plain ELF modules.</summary>
    public bool FakeSign { get; init; } = true;

    /// <summary>Title for a generated param.json.</summary>
    public string? Title { get; init; }

    /// <summary>Master version for a generated param.json.</summary>
    public string Version { get; init; } = "01.00";

    /// <summary><c>applicationDrmType</c> for a generated param.json.</summary>
    public string DrmType { get; init; } = "free";

    /// <summary>
    /// Folder for the staged inner image, a temporary file about as large as the game; null puts it next to the
    /// package.
    /// </summary>
    public string? TempFolder { get; init; }

    /// <summary>Log every file's placement and compression and the package layout.</summary>
    public bool Verbose { get; init; }

    /// <summary>Test hook: replaces each RSA ciphertext (see <c>ProsperoBuildContext.RSAOutput</c>).</summary>
    internal Func<byte[], byte[]>? RSAOutput { get; init; }

    /// <summary>Test hook: write PS5PkgTool's last gap fill block (see <c>PS5InnerWriter.EngineGapFill</c>).</summary>
    internal bool EngineGapFill { get; init; }
}

/// <summary>Result of <see cref="FPKGBuilder.Build"/>.</summary>
/// <param name="Size">Package size.</param>
/// <param name="Warnings">Source warnings.</param>
/// <param name="Modules">Executable modules and how a debug-mode console treats them.</param>
public sealed record FPKGBuildResult(long Size, IReadOnlyList<string> Warnings, IReadOnlyList<FPKGModule> Modules);

/// <summary>
/// Builds a PS5 fake-signed debug package the way PS5PkgTool does (FPKG plan F6–F9): the package view of the source
/// folder (<see cref="FPKGSource"/>), the inner image (<see cref="PS5InnerWriter"/>, staged in a temporary file),
/// the outer PFS streamed into the package (<see cref="PS5OuterWriter"/>), the CNT, the FIH header and the SI
/// segment. The package is written to <c>&lt;output&gt;.tmp</c> and moved into place; memory use does not grow
/// with the size of the game.
/// </summary>
public static class FPKGBuilder
{
    private const int BlockSize = 0x10000;
    private const int IOBuffer = 1 << 20;

    /// <summary>Build the package.</summary>
    /// <param name="options">Options.</param>
    /// <param name="outputPath">Package path.</param>
    /// <param name="log">Progress messages.</param>
    /// <param name="progress">Byte progress: phase <c>inner</c> (the inner image), then <c>write</c> (the package).</param>
    /// <returns>Size, warnings and modules.</returns>
    /// <exception cref="InvalidDataException">The source cannot be packaged.</exception>
    public static FPKGBuildResult Build(FPKGBuildOptions options, string outputPath, Action<string>? log = null, IProgressSink? progress = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        log ??= _ => { };
        byte[] seed = options.Seed ?? DefaultSeed(options.ContentId, options.Passcode);
        if (seed.Length != 16)
        {
            throw new ArgumentException("seed must be 16 bytes", nameof(options));
        }

        FPKGSource view = FPKGSource.Prepare(new FPKGSourceOptions
        {
            SourceDir = options.SourceDir,
            ContentId = options.ContentId,
            Passcode = options.Passcode,
            Title = options.Title,
            Version = options.Version,
            DrmType = options.DrmType,
            FakeSign = options.FakeSign,
        });
        if (view.Errors.Count > 0)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, view.Errors));
        }

        string full = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string temp = full + ".tmp";
        string tempFolder = options.TempFolder is { Length: > 0 } folder ? Path.GetFullPath(folder) : Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(tempFolder);
        string innerPath = Path.Combine(tempFolder, $"{Path.GetFileName(full)}.{Environment.ProcessId}.inner");
        try
        {
            ProsperoBuildContext.Seed = seed;
            ProsperoBuildContext.RSAOutput = options.RSAOutput;
            long size = Write(options, view, seed, temp, innerPath, log, progress);
            File.Move(temp, full, overwrite: true);
            return new FPKGBuildResult(size, view.Warnings, view.Modules);
        }
        finally
        {
            ProsperoBuildContext.Seed = null!;
            ProsperoBuildContext.RSAOutput = null;
            PS5KrakenChunk.ReleaseScratch();
            File.Delete(temp);
            File.Delete(innerPath);
        }
    }

    /// <summary>Seed used when none is given: the first 16 bytes of SHA3-256(content id ‖ passcode).</summary>
    /// <param name="contentId">Content id.</param>
    /// <param name="passcode">Passcode.</param>
    /// <returns>16 bytes.</returns>
    public static byte[] DefaultSeed(string contentId, string passcode) =>
        SHA3256.HashData(System.Text.Encoding.ASCII.GetBytes(contentId + passcode))[..16];

    private static long Write(FPKGBuildOptions options, FPKGSource view, byte[] seed, string path, string innerPath, Action<string> log, IProgressSink? progress)
    {
        long seconds = options.Time.ToUnixTimeSeconds();
        uint nanoseconds = (uint)(options.Time.UtcTicks % TimeSpan.TicksPerSecond * 100);
        string contentId = options.ContentId;
        string passcode = options.Passcode;

        // ---- Inner image, staged in a temporary file; its 64 KiB block digests (the outer PFS
        // signatures of pfs_image.dat) are taken as it is written. ----
        PS5InnerWriter inner = PS5InnerWriter.Plan(view.InnerFiles, seconds, nanoseconds, options.Compress);
        inner.EngineGapFill = options.EngineGapFill;
        int workers = options.CpuCount > 0 ? options.CpuCount : PS5KrakenChunk.MaxParallelism;
        inner.Workers = workers;
        inner.Fast = options.Fast;
        using FileStream innerFile = new(innerPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, IOBuffer, FileOptions.DeleteOnClose);
        log($"Writing the inner image ({inner.Files.Count} files)...");
        if (options.Verbose)
        {
            log($"  staging it in {innerPath}");
            inner.FileWritten = (file, chunks) => LogFile(log, file, chunks);
        }

        long innerBytes = Math.Max(1, inner.Files.Sum(f => f.Size));
        using HashingWriteStream innerHashing = new(innerFile, blockDigests: true, wholeDigest: false,
            progress is null ? null : done => progress.Step("inner", Math.Min(done, innerBytes), innerBytes, done));
        PS5InnerResult written = inner.Write(innerHashing);
        long innerAligned = (written.StoredSize + BlockSize - 1) / BlockSize * BlockSize;
        if (options.Verbose)
        {
            LogInner(log, inner, written);
        }

        // ---- Outer PFS plan: structural blocks and every block digest, before any of it is written. ----
        byte[] ekpfs = Crypto.ComputeKeys(contentId, passcode, 1, useSha3: true);
        PS5OuterLayout outer = PS5OuterWriter.Plan(
        [
            new PS5OuterFile("pfs_image.dat", written.StoredSize, inner.MountSize, Signed: false, innerHashing.BlockDigests()),
            new PS5OuterFile("naps_pkg_layout.dat", written.Naps.Length, written.Naps.Length, Signed: true, PS5OuterWriter.BlockDigestsOf(written.Naps)),
        ], seconds, nanoseconds, seed);

        // ---- CNT: entries, digests and seal; the FIH block it digests. ----
        // The PlayGo mchunks tile [0, CNT): the block-aligned inner image, then the rest.
        long mchunkTotal = BlockSize + outer.Size;
        long mchunk0 = innerAligned > 0 && innerAligned < mchunkTotal ? innerAligned : mchunkTotal - BlockSize;
        FPKGInput param = view.Entries.Single(e => e.Path == "param.json");
        byte[] paramJson = param.ReadAll();
        ProsperoCnt pkg = ProsperoPkgBuilder.BuildContainer(contentId, passcode, ekpfs, paramJson,
            [.. view.Entries.Where(e => e != param).Select(e => (e.Path, e.ReadAll()))],
            (ulong)outer.Size, outer.BlockDigests.Length, (uint)(2 * inner.Files.Count), [.. inner.Files.Select(f => f.Path)],
            (ulong)mchunk0, (ulong)(mchunkTotal - mchunk0));

        // imagedigs (0x040A): the plaintext block digests, each stored byte-reversed.
        byte[] imagedigs = (byte[])outer.BlockDigests.Clone();
        for (int at = 0; at < imagedigs.Length; at += SHA3256.HashSize)
        {
            Array.Reverse(imagedigs, at, SHA3256.HashSize);
        }

        ((ProsperoCntGenericEntry)pkg.Entries.First(e => (uint)e.Id == 0x040A)).FileData = imagedigs;
        ProsperoFihNwonlyFields fields = new()
        {
            ContentVersionHi = ProsperoPkgBuilder.ContentVersionHigh(PS5ParamJson.ReadContentVersion(paramJson) ?? "01.000.000"),
            InnerContentInodes = inner.Files.Count + 2,
            EmptyFiles = inner.Files.Count(f => f.Size == 0),
            AppFileCount = inner.Files.Count(f => !f.Path.StartsWith("sce_sys/", StringComparison.Ordinal)),
            Ndblock = inner.MountSize / BlockSize,
            InnerImageBlocks = innerAligned / BlockSize,
        };
        long cntSize = (long)(pkg.Header.body_offset + pkg.Header.body_size);
        if (options.Verbose)
        {
            log($"Outer PFS: {outer.BlockCount} blocks of 64 KiB ({outer.Size:N0} bytes), superblock at block {outer.SuperblockIndex}");
            log($"CNT: {pkg.Entries.Count} entries, {cntSize:N0} bytes");
            foreach (ProsperoCntEntry entry in pkg.Entries)
            {
                log($"  0x{(uint)entry.Id:X4} {(string.IsNullOrEmpty(entry.Name) ? "(system)" : entry.Name)}: {entry.Length:N0} bytes");
            }
        }

        using MemoryStream cnt = new();
        cnt.SetLength(cntSize);
        byte[] superblock = outer.Superblock.ToArray();
        byte[] fih = ProsperoPkgBuilder.FinishContainer(pkg, cnt, contentId, passcode, superblock, (long)outer.SuperblockIndex * BlockSize,
            SHA3256.HashData(written.Naps), written.Naps.Length, inner.MetadataBase / BlockSize, fields);

        // ---- The package in one ordered pass: FIH, encrypted outer PFS, CNT (hashed on the way for the SI), SI. ----
        using FileStream output = new(path, FileMode.Create, FileAccess.Write, FileShare.None, IOBuffer);
        long mountBytes = BlockSize + outer.Size + cntSize;
        using HashingWriteStream mount = new(output, blockDigests: false, wholeDigest: true,
            progress is null ? null : done => progress.Step("write", Math.Min(done, mountBytes), mountBytes, done));
        mount.Write(fih);
        innerFile.Position = 0;
        log("Writing the outer image...");
        PS5OuterWriter.Write(outer, mount, [innerFile, new MemoryStream(written.Naps, writable: false)], seed, ekpfs, workers);
        cnt.Position = 0;
        cnt.CopyTo(mount);
        (byte[] mountDigest, byte[] crc) = mount.WholeDigest();
        byte[] si = ProsperoSiArchive.BuildDebugSiSegment(new ProsperoSiBuildInputs
        {
            ContentId = contentId,
            PfsImageSize = outer.Size,
            InnerImageSize = innerAligned,
            InnerFiles = [.. inner.Files.Select(f => (f.Path, f.Size))],
            InnerChunks = written.Chunks,
            // naps_meta_18 stores extent lengths in 32 bits and digests that many bytes (PS5PkgTool), so an extent of 4 GiB or
            // more is hashed again over its truncated length.
            InnerDigest = (offset, length) => written.ExtentDigests.TryGetValue((offset, length), out byte[]? digest) ? digest : HashRange(innerFile, offset, length),
            SuperblockDigest = SHA3256.HashData(superblock),
            MountDigest = mountDigest,
            ChunkCrc = crc,
            PlayGoChunkDat = (pkg.Entries.First(e => (uint)e.Id == 0x1001) as ProsperoCntGenericEntry)?.FileData,
        });
        output.Write(si);
        if (options.Verbose)
        {
            log($"SI: {si.Length:N0} bytes; package {output.Length:N0} bytes");
        }

        return output.Length;
    }

    // One file as it is written: origin, logical and stored placement, and how its 128 KiB chunks were stored.
    private static void LogFile(Action<string> log, PS5InnerPlacement file, IReadOnlyList<PS5InnerChunk> chunks)
    {
        string origin = file.Input.Origin switch
        {
            FPKGInputOrigin.FakeSigned => " [fake-signed]",
            FPKGInputOrigin.Generated => " [generated]",
            _ => string.Empty,
        };
        if (chunks.Count == 0)
        {
            log($"  {file.Path}{origin}: empty");
            return;
        }

        int kraken = chunks.Count(c => c.StoredLength < c.Length && c.StoredLength > 16);
        int fill = chunks.Count(c => c.StoredLength <= 16 && c.Length > 16);
        int raw = chunks.Count - kraken - fill;
        long stored = chunks.Sum(c => (long)c.StoredLength);
        string how = kraken + fill == 0 ? $"{raw} raw chunk{(raw == 1 ? "" : "s")}"
            : $"{raw} raw, {kraken} Kraken, {fill} fill; {stored * 100.0 / Math.Max(1, file.Size):F1}% of its size";
        log($"  {file.Path}{origin}: {file.Size:N0} bytes at 0x{file.LogicalOffset:X}, stored at 0x{chunks[0].StoredOffset:X} ({stored:N0} bytes, {how}){(file.Executable ? ", executable" : string.Empty)}");
    }

    // The inner image totals, once it is written.
    private static void LogInner(Action<string> log, PS5InnerWriter inner, PS5InnerResult written)
    {
        int metaKraken = written.Chunks.Count(c => c.Role == PS5InnerChunkRole.Metadata && c.StoredLength < c.Length);
        int metaChunks = written.Chunks.Count(c => c.Role == PS5InnerChunkRole.Metadata);
        log($"Inner image: mount {inner.MountSize:N0} bytes (data to 0x{inner.DataEnd:X}, metadata at 0x{inner.MetadataBase:X}), stored {written.StoredSize:N0} bytes in {written.Chunks.Count} chunks; metadata {metaChunks} chunk{(metaChunks == 1 ? "" : "s")}, {metaKraken} Kraken; naps {written.Naps.Length:N0} bytes");
    }

    private static byte[] HashRange(Stream stream, long offset, long length)
    {
        SHA3256 sha = new();
        byte[] buffer = new byte[IOBuffer];
        stream.Position = offset;
        for (long left = length; left > 0;)
        {
            int n = (int)Math.Min(buffer.Length, left);
            stream.ReadExactly(buffer, 0, n);
            sha.Append(buffer.AsSpan(0, n));
            left -= n;
        }

        return sha.GetHashAndReset();
    }
}
