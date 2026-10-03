using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MkPFS.Core.Compression;
using MkPFS.Core.Crypto;
using MkPFS.Core.Diagnostics;
using MkPFS.Core.PFSC;
using MkPFS.Core.Util;

namespace MkPFS.Core.PFS;

/// <summary>When the PS5 game-file checklist runs.</summary>
public enum ChecklistMode
{
    /// <summary>Never (Python <c>verify</c> without <c>--require-game-files</c>).</summary>
    Never,

    /// <summary>Unless the image wraps a single exFAT (Python <c>inspect</c>).</summary>
    UnlessWrapsExfat,

    /// <summary>Always (Python <c>verify --require-game-files</c>).</summary>
    Always,
}

/// <summary>Options for <see cref="PFSInspector.Inspect"/>.</summary>
public sealed record PFSInspectOptions
{
    /// <summary>EKPFS key; all zeros when <see langword="null"/>.</summary>
    public byte[]? Ekpfs { get; init; }

    /// <summary>newCrypt key derivation.</summary>
    public bool NewCrypt { get; init; }

    /// <summary>Decode and hash every file (off for structure-only callers like <c>tree</c> and <c>unpack</c>).</summary>
    public bool VerifyPayloads { get; init; } = true;

    /// <summary>When the game-file checklist runs.</summary>
    public ChecklistMode Checklist { get; init; } = ChecklistMode.UnlessWrapsExfat;

    /// <summary>Optional source to compare paths and contents against.</summary>
    public SourceTree? Source { get; init; }

    /// <summary>Also compare file contents with <see cref="Source"/> (paths are always compared).</summary>
    public bool CompareSourceContents { get; init; } = true;

    /// <summary>
    /// With <see cref="VerifyPayloads"/>, count compressed blocks the PS5 may misdecode (ISA-L back-references,
    /// see <see cref="DeflateInspector.IsRiskyForPS5"/>) into <see cref="PFSInspection.RiskyBlocks"/>.
    /// </summary>
    public bool CheckPFSCStreams { get; init; }

    /// <summary>Expected cumulative CRC32 of all logical payloads.</summary>
    public uint? ExpectedCrc32 { get; init; }

    /// <summary>Expected manifest SHA-256 (hex).</summary>
    public string? ExpectedManifestSha256 { get; init; }

    /// <summary>Progress for the <c>verify</c> and <c>compare</c> passes.</summary>
    public IProgressSink? Progress { get; init; }
}

/// <summary>Inspection result (Python <c>PFSImageInspection</c>).</summary>
public sealed class PFSInspection
{
    /// <summary>Inspected image path.</summary>
    public required string ImagePath { get; init; }

    /// <summary>Fatal or validation errors, in detection order.</summary>
    public List<string> Errors { get; } = [];

    /// <summary>Non-fatal warnings.</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>Image size on disk.</summary>
    public long SizeBytes { get; set; }

    /// <summary>Parsed header when readable.</summary>
    public PFSHeader? Header { get; set; }

    /// <summary>Parsed inode table.</summary>
    public List<PFSInode> Inodes { get; set; } = [];

    /// <summary>Root directory inode, or -1.</summary>
    public long UrootInode { get; set; } = -1;

    /// <summary>Relative file path → inode.</summary>
    public Dictionary<string, long> FileInodes { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Relative directory path → inode ("" is the root).</summary>
    public Dictionary<string, long> DirInodes { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Directory inode → entries.</summary>
    public Dictionary<long, List<PFSDirent>> DirentsByInode { get; set; } = [];

    /// <summary>flat_path_table hash → value.</summary>
    public Dictionary<uint, uint> FptMap { get; set; } = [];

    /// <summary>Colliding hash → resolver entries.</summary>
    public Dictionary<uint, List<PFSDirent>> CollisionMap { get; set; } = [];

    /// <summary>Superroot, flat_path_table, collision_resolver and uroot inodes.</summary>
    public HashSet<long> SpecialInodes { get; set; } = [];

    /// <summary>Files hash-checked.</summary>
    public int CheckedFiles { get; set; }

    /// <summary>Per file path: compressed blocks the PS5 may misdecode (only with <see cref="PFSInspectOptions.CheckPFSCStreams"/>).</summary>
    public SortedDictionary<string, long> RiskyBlocks { get; } = new(StringComparer.Ordinal);

    /// <summary>Cumulative CRC32 of logical payloads (sorted path order).</summary>
    public uint DataCrc32 { get; set; }

    /// <summary>SHA-256 over <c>path \0 sha256(file)</c> for every file in sorted order.</summary>
    public string ManifestSha256 { get; set; } = string.Empty;

    /// <summary>Files stored compressed.</summary>
    public int CompressedFiles { get; set; }

    /// <summary>Total logical file bytes.</summary>
    public long LogicalFileBytes { get; set; }

    /// <summary>Total stored file bytes.</summary>
    public long StoredFileBytes { get; set; }

    /// <summary>A filesystem tree was parsed.</summary>
    public bool HasTree => UrootInode >= 0 && DirentsByInode.Count > 0;
}

/// <summary>
/// Structural and payload validation of PFS images (port of Python <c>inspect_pfs_image</c> and the shared
/// checks used by <c>run_image_check</c>).
/// </summary>
public static class PFSInspector
{
    private const long ProgressInterval = 8L * 1024 * 1024;

    // Deeper trees are reported instead of walked: the renderers recurse per level (Python stops near 1000 levels
    // with RecursionError), and same limit as the exFAT reader.
    private const int MaxDirectoryDepth = 1024;
    private static readonly byte[] ExfatSignature = "EXFAT   "u8.ToArray();

    /// <summary>Inspect an image file.</summary>
    /// <param name="imagePath">Image path.</param>
    /// <param name="options">Options.</param>
    /// <returns>Inspection report.</returns>
    public static PFSInspection Inspect(string imagePath, PFSInspectOptions? options = null)
    {
        options ??= new PFSInspectOptions();
        PFSInspection result = new() { ImagePath = imagePath, SizeBytes = File.Exists(imagePath) ? new FileInfo(imagePath).Length : 0 };
        if (!File.Exists(imagePath))
        {
            result.Errors.Add($"image path does not exist or is not a file: {imagePath}");
            return result;
        }

        try
        {
            using PFSImage image = PFSImage.Open(imagePath, options.Ekpfs, options.NewCrypt);
            Run(image, result, options);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            result.Errors.Add($"failed to inspect image: {ex.Message}");
        }

        return result;
    }

    /// <summary>Whether the tree is a single unsigned file that starts with an exFAT boot sector.</summary>
    /// <param name="image">Open image.</param>
    /// <param name="inspection">Structure of the image.</param>
    /// <returns><see langword="true"/> for exFAT-wrapped images.</returns>
    public static bool WrapsSingleExfat(PFSImage image, PFSInspection inspection)
    {
        if (inspection.FileInodes.Count != 1)
        {
            return false;
        }

        PFSInode inode = inspection.Inodes[(int)inspection.FileInodes.Values.First()];
        if (inode.IsSigned || inode.Blocks <= 0 || inode.LogicalSize < ExfatSignature.Length + 3)
        {
            return false;
        }

        try
        {
            using Stream view = image.OpenLogical(inode);
            byte[] head = new byte[ExfatSignature.Length + 3];
            view.ReadExactly(head);
            return head.AsSpan(3).SequenceEqual(ExfatSignature);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException)
        {
            return false;
        }
    }

    private static void Run(PFSImage image, PFSInspection result, PFSInspectOptions options)
    {
        PFSHeader header = image.Header;
        result.Header = header;

        try
        {
            result.Inodes = image.ReadInodes();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            result.Errors.Add($"failed to parse inode table: {ex.Message}");
            return;
        }

        ValidateInodeLayout(header, result.Inodes, result.Errors, result.Warnings);

        try
        {
            VerifySignatures(image, result.Inodes, result.Errors);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        {
            result.Errors.Add($"failed to verify image signatures: {ex.Message}");
        }

        try
        {
            ParseSuperroot(image, result);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        {
            result.Errors.Add($"failed to parse superroot and indexes: {ex.Message}");
            return;
        }

        if (result.UrootInode < 0)
        {
            return;
        }

        try
        {
            BuildTree(image, result);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        {
            result.Errors.Add($"failed to build filesystem tree: {ex.Message}");
            return;
        }

        ValidateFpt(result, header.IsCaseInsensitive);

        // Payload passes decode every file; structure-only callers skip them.
        if (options.VerifyPayloads)
        {
            bool runChecklist = options.Checklist switch
            {
                ChecklistMode.Always => true,
                ChecklistMode.UnlessWrapsExfat => !WrapsSingleExfat(image, result),
                _ => false,
            };
            if (runChecklist)
            {
                ValidatePS5Checklist(image, result);
            }

            try
            {
                HashPayloads(image, result, options.Progress);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                result.Errors.Add($"failed to verify file payload hashes: {ex.Message}");
            }

            if (options.CheckPFSCStreams)
            {
                CheckPFSCStreams(image, result);
            }

            if (options.ExpectedCrc32 is uint crc && result.DataCrc32 != crc)
            {
                result.Errors.Add($"CRC32 mismatch: actual 0x{result.DataCrc32:X8}, expected 0x{crc:X8}");
            }

            if (options.ExpectedManifestSha256 is string manifest &&
                !string.Equals(result.ManifestSha256, manifest, StringComparison.OrdinalIgnoreCase))
            {
                result.Errors.Add($"Manifest SHA256 mismatch: actual {result.ManifestSha256}, expected {manifest.ToLowerInvariant()}");
            }
        }

        HashSet<long> reachable = [.. result.FileInodes.Values, .. result.DirInodes.Values, .. result.SpecialInodes];
        List<long> orphans = [.. result.Inodes.Select(i => i.Number).Where(n => !reachable.Contains(n)).Order()];
        if (orphans.Count > 0)
        {
            result.Errors.Add("orphan inodes not reachable from filesystem tree: " +
                string.Join(", ", orphans.Take(20)) + (orphans.Count > 20 ? " ..." : string.Empty));
        }

        if (options.Source is not null)
        {
            CompareSource(image, result, options.Source, options.CompareSourceContents, options.Progress);
        }

        foreach (long number in result.FileInodes.Values)
        {
            PFSInode inode = result.Inodes[(int)number];
            result.CompressedFiles += inode.IsCompressed ? 1 : 0;
            result.LogicalFileBytes += Math.Max(0, inode.LogicalSize);
            result.StoredFileBytes += Math.Max(0, inode.StoredSize);
        }
    }

    // Count risky PFSC blocks per compressed file. Undecodable payloads are already errors from the hash pass.
    private static void CheckPFSCStreams(PFSImage image, PFSInspection result)
    {
        const int batch = 256;
        foreach ((string path, long number) in result.FileInodes)
        {
            PFSInode inode = result.Inodes[(int)number];
            if (!inode.IsCompressed || inode.Blocks <= 0)
            {
                continue;
            }

            try
            {
                using Stream source = inode.IsSigned ? new MemoryStream(image.ReadStoredPayload(inode), writable: false) : new PFSImageStream(image);
                PFSCReader reader = PFSCReader.Open(source, inode.IsSigned ? 0 : image.BlockOffset(inode.Db[0]), inode.StoredSize);
                long risky = 0;
                List<byte[]> stored = new(batch);
                for (long start = 0; start < reader.BlockCount; start += batch)
                {
                    stored.Clear();
                    for (long i = start; i < Math.Min(reader.BlockCount, start + batch); i++)
                    {
                        if (reader.IsBlockCompressed(i))
                        {
                            byte[] block = new byte[reader.StoredLength(i)];
                            reader.ReadStoredBlock(i, block);
                            stored.Add(block);
                        }
                    }

                    risky += stored.AsParallel().Count(block => DeflateInspector.IsRiskyForPS5(block));
                }

                if (risky > 0)
                {
                    result.RiskyBlocks[path] = risky;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                // Reported by the payload hash pass.
            }
        }
    }

    /// <summary>Header and block-range checks (Python <c>validate_inode_layout</c>).</summary>
    internal static void ValidateInodeLayout(PFSHeader header, List<PFSInode> inodes, List<string> errors, List<string> warnings)
    {
        if (header.Magic != PFSConstants.PFSMagic)
        {
            errors.Add($"header magic mismatch: 0x{header.Magic:X16} != 0x{PFSConstants.PFSMagic:X16}");
        }

        if (header.BlockSize == 0 || (header.BlockSize & (header.BlockSize - 1)) != 0)
        {
            errors.Add($"invalid block size {header.BlockSize}");
        }

        if (header.ReadOnly != 1)
        {
            warnings.Add($"header readonly byte is {header.ReadOnly}, expected 1");
        }

        if (header.InodeCount != inodes.Count)
        {
            errors.Add($"inode count mismatch: header={header.InodeCount} parsed={inodes.Count}");
        }

        List<(long Start, long End, long Inode)> ranges = [];
        foreach (PFSInode inode in inodes)
        {
            if (inode.Blocks <= 0)
            {
                continue;
            }

            long start = inode.Db[0];
            long end = start + inode.Blocks - 1;
            if (start < 0)
            {
                errors.Add($"inode {inode.Number} has negative db[0]={start}");
                continue;
            }

            if (end >= header.NDBlock)
            {
                errors.Add($"inode {inode.Number} range [{start},{end}] exceeds ndblock {header.NDBlock}");
            }

            ranges.Add((start, end, inode.Number));
        }

        ranges.Sort();
        for (int i = 1; i < ranges.Count; i++)
        {
            (long prevStart, long prevEnd, long prevInode) = ranges[i - 1];
            (long start, long end, long inode) = ranges[i];
            if (start <= prevEnd)
            {
                errors.Add($"block overlap between inode {prevInode} [{prevStart},{prevEnd}] and inode {inode} [{start},{end}]");
            }
        }
    }

    /// <summary>
    /// HMAC checks for signed images (Python <c>verify_signed_image_signatures</c>). Unlike Python, the
    /// <c>ib[0]</c> records are read whether or not the <c>ib[0]</c> signature matches (Python only reads them
    /// on mismatch and then fails with an unbound variable on valid images; oracle finding 9).
    /// </summary>
    internal static void VerifySignatures(PFSImage image, List<PFSInode> inodes, List<string> errors)
    {
        PFSHeader header = image.Header;
        if (!header.IsSigned)
        {
            return;
        }

        byte[] signKey = PFSKeys.SignKey(image.EkpfsForSigning, header.Seed);
        int bits = SignedInodeLayout.BitsFromMode(header.Mode);
        SignedInodeLayout layout = SignedInodeLayout.For(bits);
        byte[] Mac(byte[] data) => HMACSHA256.HashData(signKey, data);

        for (long i = 0; i < header.InodeBlockCount; i++)
        {
            long blockNumber = 1 + i;
            byte[] expected = Mac(image.ReadBlock(blockNumber));
            byte[] actual = image.ReadRaw(0xB8 + (40 * i), PFSConstants.SigSize);
            if (!actual.AsSpan().SequenceEqual(expected))
            {
                errors.Add($"inode block signature mismatch for block {blockNumber}");
            }
        }

        byte[] region = image.ReadRaw(0, PFSConstants.HeaderDigestSize);
        region.AsSpan(PFSConstants.HeaderDigestOffset, PFSConstants.SigSize).Clear();
        if (!image.ReadRaw(PFSConstants.HeaderDigestOffset, PFSConstants.SigSize).AsSpan().SequenceEqual(Mac(region)))
        {
            errors.Add("header signature region digest mismatch");
        }

        long perBlock = header.BlockSize / layout.EntrySize;
        foreach (PFSInode inode in inodes)
        {
            long remaining = inode.Blocks;
            int direct = (int)Math.Min(remaining, PFSConstants.MaxDirectBlocks);
            for (int idx = 0; idx < direct; idx++)
            {
                long block = inode.Db[idx];
                if (block <= 0)
                {
                    errors.Add($"inode {inode.Number} has invalid direct block db[{idx}]={block}");
                    continue;
                }

                if (!inode.DbSig[idx].AsSpan().SequenceEqual(Mac(image.ReadBlock(block))))
                {
                    errors.Add($"inode {inode.Number} direct signature mismatch at db[{idx}] -> block {block}");
                }
            }

            remaining -= direct;
            if (remaining > 0)
            {
                long ib0 = inode.Ib[0];
                if (ib0 <= 0)
                {
                    errors.Add($"inode {inode.Number} missing ib[0] for signed block chain");
                }
                else
                {
                    if (!inode.IbSig[0].AsSpan().SequenceEqual(Mac(image.ReadBlock(ib0))))
                    {
                        errors.Add($"inode {inode.Number} indirect signature mismatch at ib[0] -> block {ib0}");
                    }

                    List<(byte[] Sig, long Block)> records = image.ReadSigRecords(ib0, bits);
                    int take = (int)Math.Min(remaining, perBlock);
                    for (int rec = 0; rec < Math.Min(take, records.Count); rec++)
                    {
                        (byte[] sig, long block) = records[rec];
                        if (block <= 0)
                        {
                            errors.Add($"inode {inode.Number} ib[0] record {rec} has invalid block {block}");
                            continue;
                        }

                        if (!sig.AsSpan().SequenceEqual(Mac(image.ReadBlock(block))))
                        {
                            errors.Add($"inode {inode.Number} ib[0] record {rec} signature mismatch for block {block}");
                        }
                    }

                    remaining -= take;
                }
            }

            if (remaining > 0)
            {
                long ib1 = inode.Ib[1];
                if (ib1 <= 0)
                {
                    errors.Add($"inode {inode.Number} missing ib[1] for signed block chain");
                }
                else
                {
                    if (!inode.IbSig[1].AsSpan().SequenceEqual(Mac(image.ReadBlock(ib1))))
                    {
                        errors.Add($"inode {inode.Number} indirect signature mismatch at ib[1] -> block {ib1}");
                    }

                    List<(byte[] Sig, long Block)> parents = image.ReadSigRecords(ib1, bits);
                    for (int parent = 0; parent < parents.Count && remaining > 0; parent++)
                    {
                        (byte[] parentSig, long child) = parents[parent];
                        if (child <= 0)
                        {
                            errors.Add($"inode {inode.Number} ib[1] record {parent} has invalid block {child}");
                            continue;
                        }

                        if (!parentSig.AsSpan().SequenceEqual(Mac(image.ReadBlock(child))))
                        {
                            errors.Add($"inode {inode.Number} ib[1] record {parent} signature mismatch for block {child}");
                        }

                        List<(byte[] Sig, long Block)> children = image.ReadSigRecords(child, bits);
                        int take = (int)Math.Min(remaining, perBlock);
                        for (int rec = 0; rec < Math.Min(take, children.Count); rec++)
                        {
                            (byte[] sig, long block) = children[rec];
                            if (block <= 0)
                            {
                                errors.Add($"inode {inode.Number} ib[1][{parent}] record {rec} has invalid block {block}");
                                continue;
                            }

                            if (!sig.AsSpan().SequenceEqual(Mac(image.ReadBlock(block))))
                            {
                                errors.Add($"inode {inode.Number} ib[1][{parent}] record {rec} signature mismatch for block {block}");
                            }
                        }

                        remaining -= take;
                    }
                }
            }

            if (remaining > 0)
            {
                errors.Add($"inode {inode.Number} exceeds supported signed verification depth");
            }
        }
    }

    /// <summary>Superroot entries, flat_path_table and collision resolver (Python <c>parse_superroot_and_indexes</c>).</summary>
    private static void ParseSuperroot(PFSImage image, PFSInspection result)
    {
        PFSHeader header = image.Header;
        List<PFSInode> inodes = result.Inodes;
        byte[] blob = image.ReadBlock(1 + header.InodeBlockCount);
        (List<PFSDirent> entries, List<string> parseErrors) = PFSDirent.ParseAll(blob, strict: true);
        result.Errors.AddRange(parseErrors.Select(e => $"superroot: {e}"));

        long? fpt = null;
        long? collision = null;
        long? uroot = null;
        foreach (PFSDirent entry in entries)
        {
            switch (entry.Name)
            {
                case "flat_path_table":
                    fpt = entry.InodeNumber;
                    break;
                case "collision_resolver":
                    collision = entry.InodeNumber;
                    break;
                case "uroot":
                    uroot = entry.InodeNumber;
                    break;
            }
        }

        if (fpt is null)
        {
            result.Errors.Add("superroot missing 'flat_path_table' entry");
        }

        if (uroot is null)
        {
            result.Errors.Add("superroot missing 'uroot' entry");
        }

        result.SpecialInodes = [0];
        foreach (long? special in new[] { fpt, collision, uroot })
        {
            if (special is long value)
            {
                result.SpecialInodes.Add(value);
            }
        }

        if (fpt is long fptInode && fptInode >= 0 && fptInode < inodes.Count)
        {
            byte[] table = image.ReadStoredPayload(inodes[(int)fptInode]);
            if (table.Length % 8 != 0)
            {
                result.Errors.Add("flat_path_table size is not divisible by 8");
            }

            for (int i = 0; i + 8 <= table.Length; i += 8)
            {
                uint hash = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(i));
                uint value = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(i + 4));
                if (result.FptMap.ContainsKey(hash))
                {
                    result.Errors.Add($"flat_path_table has duplicate hash 0x{hash:X8}");
                }

                result.FptMap[hash] = value;
            }

            if (result.FptMap.Values.Any(v => (v & FlatPathTable.CollisionBit) != 0))
            {
                if (collision is null)
                {
                    result.Errors.Add("flat_path_table has collision entries but no collision_resolver inode");
                }
                else if (collision.Value >= 0 && collision.Value < inodes.Count)
                {
                    byte[] resolver = image.ReadStoredPayload(inodes[(int)collision.Value]);
                    foreach ((uint hash, uint value) in result.FptMap)
                    {
                        if ((value & FlatPathTable.CollisionBit) == 0)
                        {
                            continue;
                        }

                        uint offset = value & 0x7FFFFFFF;
                        if (offset >= resolver.Length)
                        {
                            result.Errors.Add($"collision_resolver offset {offset} out of range for hash 0x{hash:X8}");
                            continue;
                        }

                        (List<PFSDirent> colliding, List<string> errors) = PFSDirent.ParseAll(resolver.AsSpan((int)offset), strict: true);
                        result.Errors.AddRange(errors.Select(e => $"collision_resolver hash 0x{hash:X8}: {e}"));
                        result.CollisionMap[hash] = colliding;
                    }
                }
            }
        }

        result.UrootInode = uroot ?? -1;
    }

    /// <summary>Walk directories from uroot with all consistency checks (Python <c>build_tree_from_uroot</c>).</summary>
    private static void BuildTree(PFSImage image, PFSInspection result)
    {
        List<PFSInode> inodes = result.Inodes;
        long uroot = result.UrootInode;
        result.DirInodes[string.Empty] = uroot;
        HashSet<long> visited = [];
        Dictionary<long, string> dirPathByInode = new() { [uroot] = string.Empty };
        List<string> errors = result.Errors;

        // One frame per open directory instead of one call per level: same pre-order (so the same errors and entry
        // order), but a deep crafted tree cannot overflow the stack, which would end the process. The ancestors of
        // the directory being walked are exactly the directories on the stack.
        Stack<WalkFrame> stack = new();
        HashSet<long> ancestors = [];

        void Enter(long dirInode, string relPath, long parentInode)
        {
            if (!visited.Add(dirInode))
            {
                return;
            }

            string shown = relPath.Length == 0 ? "/" : relPath;
            if (dirInode < 0 || dirInode >= inodes.Count)
            {
                errors.Add($"directory inode {dirInode} is out of range");
                return;
            }

            PFSInode inode = inodes[(int)dirInode];
            if (!inode.IsDir)
            {
                errors.Add($"inode {dirInode} referenced as directory but mode is 0x{inode.Mode:X4}");
                return;
            }

            (List<PFSDirent> entries, List<string> parseErrors) = PFSDirent.ParseAll(image.ReadStoredPayload(inode), strict: true);
            result.DirentsByInode[dirInode] = entries;
            errors.AddRange(parseErrors.Select(e => $"inode {dirInode}: {e}"));

            List<PFSDirent> dots = [.. entries.Where(e => e.Name == ".")];
            List<PFSDirent> dotdots = [.. entries.Where(e => e.Name == "..")];
            PFSDirent? dot = dots.FirstOrDefault();
            PFSDirent? dotdot = dotdots.FirstOrDefault();
            if (dots.Count != 1)
            {
                errors.Add($"directory '{shown}' must contain exactly one '.' entry");
            }

            if (dot is null)
            {
                errors.Add($"directory '{shown}' missing '.' entry");
            }
            else if (dot.InodeNumber != dirInode)
            {
                errors.Add($"directory '{shown}' has '.' -> {dot.InodeNumber}, expected {dirInode}");
            }
            else if (dot.TypeCode != PFSConstants.DirentTypeDot)
            {
                errors.Add($"directory '{shown}' has '.' with invalid type {dot.TypeCode}");
            }

            if (dotdots.Count != 1)
            {
                errors.Add($"directory '{shown}' must contain exactly one '..' entry");
            }

            if (dotdot is null)
            {
                errors.Add($"directory '{shown}' missing '..' entry");
            }
            else
            {
                long expectedParent = relPath.Length == 0 ? dirInode : parentInode;
                if (dotdot.InodeNumber != expectedParent)
                {
                    errors.Add($"directory '{shown}' has '..' -> {dotdot.InodeNumber}, expected {expectedParent}");
                }

                if (dotdot.TypeCode != PFSConstants.DirentTypeDotDot)
                {
                    errors.Add($"directory '{shown}' has '..' with invalid type {dotdot.TypeCode}");
                }
            }

            ancestors.Add(dirInode);
            stack.Push(new WalkFrame(dirInode, relPath, shown, entries));
        }

        Enter(uroot, string.Empty, uroot);
        while (stack.Count > 0)
        {
            WalkFrame frame = stack.Peek();
            if (frame.Next >= frame.Entries.Count)
            {
                stack.Pop();
                ancestors.Remove(frame.DirInode);
                continue;
            }

            PFSDirent entry = frame.Entries[frame.Next++];
            string relPath = frame.RelPath;
            string shown = frame.Shown;
            if (entry.Name is "." or "..")
            {
                continue;
            }

            if (!frame.NamesSeen.Add(entry.Name))
            {
                errors.Add($"directory '{shown}' has duplicate entry '{entry.Name}'");
                continue;
            }

            if (entry.Name.Contains('/', StringComparison.Ordinal))
            {
                errors.Add($"directory '{shown}' has invalid entry name containing '/': {entry.Name}");
                continue;
            }

            string childPath = relPath.Length == 0 ? entry.Name : $"{relPath}/{entry.Name}";
            if (entry.InodeNumber < 0 || entry.InodeNumber >= inodes.Count)
            {
                errors.Add($"entry '{childPath}' references out-of-range inode {entry.InodeNumber}");
                continue;
            }

            PFSInode child = inodes[(int)entry.InodeNumber];
            if (entry.TypeCode == PFSConstants.DirentTypeDirectory)
            {
                if (!child.IsDir)
                {
                    errors.Add($"entry '{childPath}' typed directory but inode mode is 0x{child.Mode:X4}");
                    continue;
                }

                if (ancestors.Contains(entry.InodeNumber))
                {
                    errors.Add($"directory cycle detected at '{childPath}' (inode {entry.InodeNumber})");
                    continue;
                }

                if (dirPathByInode.TryGetValue(entry.InodeNumber, out string? previous) && previous != childPath)
                {
                    errors.Add($"directory inode {entry.InodeNumber} is reachable from multiple paths: '{previous}' and '{childPath}'");
                    continue;
                }

                if (ancestors.Count > MaxDirectoryDepth)
                {
                    errors.Add($"directory '{childPath}' is nested deeper than {MaxDirectoryDepth} levels");
                    continue;
                }

                dirPathByInode[entry.InodeNumber] = childPath;
                result.DirInodes[childPath] = entry.InodeNumber;
                Enter(entry.InodeNumber, childPath, frame.DirInode);
            }
            else if (entry.TypeCode == PFSConstants.DirentTypeFile)
            {
                if (!child.IsFile)
                {
                    errors.Add($"entry '{childPath}' typed file but inode mode is 0x{child.Mode:X4}");
                    continue;
                }

                result.FileInodes[childPath] = entry.InodeNumber;
            }
            else
            {
                errors.Add($"directory '{shown}' has unsupported dirent type {entry.TypeCode}");
            }
        }
    }

    /// <summary>Compare the flat_path_table with the walked tree (Python <c>build_expected_fpt</c> + <c>validate_fpt_maps</c>).</summary>
    private static void ValidateFpt(PFSInspection result, bool caseInsensitive)
    {
        Dictionary<uint, List<(string Path, bool IsDir, long Inode)>> expected = [];
        void Add(string path, bool isDir, long inode)
        {
            uint hash = FlatPathTable.Hash(path, caseInsensitive);
            if (!expected.TryGetValue(hash, out List<(string, bool, long)>? list))
            {
                expected[hash] = list = [];
            }

            list.Add((path, isDir, inode));
        }

        foreach ((string rel, long inode) in result.DirInodes)
        {
            if (rel.Length > 0)
            {
                Add("/" + rel, true, inode);
            }
        }

        foreach ((string rel, long inode) in result.FileInodes)
        {
            Add("/" + rel, false, inode);
        }

        foreach (uint hash in expected.Keys.Where(h => !result.FptMap.ContainsKey(h)).Order())
        {
            result.Errors.Add($"flat_path_table missing hash 0x{hash:X8}");
        }

        foreach (uint hash in result.FptMap.Keys.Where(h => !expected.ContainsKey(h)).Order())
        {
            result.Errors.Add($"flat_path_table has unexpected hash 0x{hash:X8}");
        }

        foreach (uint hash in expected.Keys.Where(result.FptMap.ContainsKey).Order())
        {
            List<(string Path, bool IsDir, long Inode)> entries = expected[hash];
            uint value = result.FptMap[hash];
            if (entries.Count == 1)
            {
                (string path, bool isDir, long inode) = entries[0];
                if ((value & FlatPathTable.CollisionBit) != 0)
                {
                    result.Errors.Add($"hash 0x{hash:X8} for {path} unexpectedly points to collision resolver");
                    continue;
                }

                bool actualDir = (value & FlatPathTable.DirectoryBit) != 0;
                long actualInode = value & FlatPathTable.InodeMask;
                if (actualDir != isDir || actualInode != inode)
                {
                    result.Errors.Add($"hash 0x{hash:X8} mismatch: actual inode={actualInode} dir={PyBool(actualDir)}, expected inode={inode} dir={PyBool(isDir)} ({path})");
                }
            }
            else
            {
                if ((value & FlatPathTable.CollisionBit) == 0)
                {
                    result.Errors.Add($"hash 0x{hash:X8} has collisions but does not point to collision resolver");
                    continue;
                }

                HashSet<(string, bool, long)> actual = [.. (result.CollisionMap.GetValueOrDefault(hash) ?? [])
                    .Select(e => (e.Name, e.TypeCode == PFSConstants.DirentTypeDirectory, e.InodeNumber))];
                if (!entries.All(actual.Contains))
                {
                    result.Errors.Add($"collision resolver for hash 0x{hash:X8} is missing expected entries");
                }
            }
        }
    }

    /// <summary>Game-file checklist (Python <c>validate_ps5_checklist</c>).</summary>
    private static void ValidatePS5Checklist(PFSImage image, PFSInspection result)
    {
        if (result.FileInodes.TryGetValue("sce_sys/param.json", out long paramInode))
        {
            PFSInode inode = result.Inodes[(int)paramInode];
            byte[] payload = image.ReadStoredPayload(inode);
            if (inode.IsCompressed)
            {
                try
                {
                    payload = PFSC.PFSCReader.DecodePayload(payload, inode.LogicalSize);
                }
                catch (InvalidDataException ex)
                {
                    result.Errors.Add($"sce_sys/param.json payload decode failed: {ex.Message}");
                    payload = [];
                }
            }

            if (payload.Length > 0)
            {
                try
                {
                    using JsonDocument document = JsonDocument.Parse(new UTF8Encoding(false, true).GetString(payload));
                    if (document.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        throw new JsonException("root is not an object");
                    }

                    bool hasTitle = document.RootElement.TryGetProperty("titleId", out JsonElement title) && !GameParams.IsFalsy(title);
                    bool hasAlternate = document.RootElement.TryGetProperty("title_id", out JsonElement alternate) && !GameParams.IsFalsy(alternate);
                    if (!hasTitle && !hasAlternate)
                    {
                        result.Warnings.Add("sce_sys/param.json missing titleId/title_id");
                    }
                }
                catch (Exception ex) when (ex is JsonException or DecoderFallbackException)
                {
                    result.Errors.Add($"sce_sys/param.json invalid JSON: {ex.Message}");
                }
            }
        }
        else
        {
            result.Warnings.Add("sce_sys/param.json not found");
        }

        if (!result.FileInodes.ContainsKey("eboot.bin"))
        {
            result.Warnings.Add("eboot.bin not found");
        }

        if (!result.FileInodes.ContainsKey("sce_sys/pfs-version.dat"))
        {
            result.Warnings.Add("sce_sys/pfs-version.dat not found");
        }
    }

    /// <summary>Hash every file in sorted path order (Python <c>verify_file_payload_hashes</c>).</summary>
    private static void HashPayloads(PFSImage image, PFSInspection result, IProgressSink? progress)
    {
        using IncrementalHash manifest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        uint crc = 0;
        int checkedFiles = 0;
        long total = result.FileInodes.Values.Sum(n => Math.Max(0, result.Inodes[(int)n].LogicalSize));
        long progressTotal = Math.Max(total, 1);
        long processed = 0;
        long lastReported = 0;
        progress?.Report("verify", 0, progressTotal);

        foreach (string rel in result.FileInodes.Keys.Order(StringComparer.Ordinal))
        {
            long number = result.FileInodes[rel];
            PFSInode inode = result.Inodes[(int)number];
            using IncrementalHash fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long length = 0;
            try
            {
                foreach (ReadOnlyMemory<byte> chunk in image.ReadLogicalChunks(inode))
                {
                    fileHash.AppendData(chunk.Span);
                    crc = Crc32.Update(crc, chunk.Span);
                    length += chunk.Length;
                    processed += chunk.Length;
                    if (progress is not null && processed - lastReported >= ProgressInterval)
                    {
                        lastReported = processed;
                        progress.Report("verify", Math.Min(processed, total), progressTotal, processed);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                result.Errors.Add($"failed to read file payload '{rel}' (inode {number}): {ex.Message}");
                continue;
            }

            if (inode.LogicalSize >= 0 && length != inode.LogicalSize)
            {
                result.Errors.Add($"file '{rel}' size {length} does not match inode size {inode.LogicalSize}");
            }

            manifest.AppendData(Encoding.UTF8.GetBytes(rel));
            manifest.AppendData([0]);
            manifest.AppendData(fileHash.GetHashAndReset());
            checkedFiles++;
        }

        progress?.Report("verify", progressTotal, progressTotal, total);
        result.CheckedFiles = checkedFiles;
        result.DataCrc32 = crc;
        result.ManifestSha256 = Convert.ToHexStringLower(manifest.GetHashAndReset());
    }

    /// <summary>
    /// Path and content comparison with a source (Python <c>validate_source_match</c>). Python's
    /// <c>verify</c> runs the path check twice and reports each difference twice (oracle finding 10);
    /// this runs it once.
    /// </summary>
    private static void CompareSource(PFSImage image, PFSInspection result, SourceTree source, bool compareContents, IProgressSink? progress)
    {
        if (!source.IsValid)
        {
            result.Errors.Add($"source path does not exist or is not a directory: {source.Description}");
            return;
        }

        HashSet<string> sourcePaths = [.. source.Files.Keys];
        HashSet<string> imagePaths = [.. result.FileInodes.Keys];
        foreach (string rel in sourcePaths.Except(imagePaths).Order(StringComparer.Ordinal))
        {
            result.Errors.Add($"missing in image: {rel}");
        }

        foreach (string rel in imagePaths.Except(sourcePaths).Order(StringComparer.Ordinal))
        {
            result.Errors.Add($"extra in image: {rel}");
        }

        if (!compareContents)
        {
            return;
        }

        List<string> common = [.. sourcePaths.Intersect(imagePaths).Order(StringComparer.Ordinal)];
        long total = common.Sum(rel => Math.Max(0, result.Inodes[(int)result.FileInodes[rel]].LogicalSize));
        long progressTotal = Math.Max(total, 1);
        long processed = 0;
        long lastReported = 0;
        progress?.Report("compare", 0, progressTotal);

        foreach (string rel in common)
        {
            PFSInode inode = result.Inodes[(int)result.FileInodes[rel]];
            using IncrementalHash imageHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            try
            {
                foreach (ReadOnlyMemory<byte> chunk in image.ReadLogicalChunks(inode))
                {
                    imageHash.AppendData(chunk.Span);
                    processed += chunk.Length;
                    if (progress is not null && processed - lastReported >= ProgressInterval)
                    {
                        lastReported = processed;
                        progress.Report("compare", Math.Min(processed, total), progressTotal, processed);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                result.Errors.Add($"file '{rel}' failed to read payload: {ex.Message}");
                continue;
            }

            byte[] sourceHash;
            using (FileStream file = File.OpenRead(source.Files[rel]))
            {
                sourceHash = SHA256.HashData(file);
            }

            if (!imageHash.GetHashAndReset().AsSpan().SequenceEqual(sourceHash))
            {
                result.Errors.Add($"content mismatch for file: {rel}");
            }
        }

        progress?.Report("compare", progressTotal, progressTotal, total);
    }

    private static string PyBool(bool value) => value ? "True" : "False";

    /// <summary>A directory whose entries <see cref="BuildTree"/> is walking.</summary>
    private sealed class WalkFrame(long dirInode, string relPath, string shown, List<PFSDirent> entries)
    {
        public long DirInode { get; } = dirInode;

        public string RelPath { get; } = relPath;

        public string Shown { get; } = shown;

        public List<PFSDirent> Entries { get; } = entries;

        public HashSet<string> NamesSeen { get; } = new(StringComparer.Ordinal);

        public int Next { get; set; }
    }
}
