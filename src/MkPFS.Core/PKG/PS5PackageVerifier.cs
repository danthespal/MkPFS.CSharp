using System.Buffers.Binary;
using System.IO.Compression;
using MkPFS.Core.Crypto;
using MkPFS.Core.PFS;
using MkPFS.Core.PFS.PS5;
using MkPFS.Core.Util;

namespace MkPFS.Core.PKG;

/// <summary>One verification result.</summary>
/// <param name="Name">Check name.</param>
/// <param name="Passed">Outcome.</param>
/// <param name="Detail">Explanation for a failure, or a short summary.</param>
public sealed record PS5Check(string Name, bool Passed, string Detail);

/// <summary>
/// Integrity checks for a PS5 finalized package. Only relations confirmed on Publishing Tools output
/// (<c>tools/oracle-fpkg/README.md</c>, docs/FORMATS.md §13) are checked: FIH digests of the superblock and
/// the naps layout, the CNT entry-digest table, body, package and general digests, the FIH digest copy in the
/// CNT, imagedigs, the outer ICV, the SHA3-256 of every outer block, the SI <c>playgo-chunk.crc</c>, and a full
/// decode of every inner chunk, compared with the SI chunk digests when the SI carries them in plaintext.
/// </summary>
public static class PS5PackageVerifier
{
    private const int CNTBodyDigestOffset = 0x160;
    private const int CNTFIHDigestOffset = 0x460;
    private const int CNTPackageDigestOffset = 0xFE0;

    /// <summary>Run every check.</summary>
    /// <param name="package">Opened package.</param>
    /// <param name="progress">Optional callback with (done, total) inner chunks.</param>
    /// <returns>Checks in order.</returns>
    public static List<PS5Check> Verify(PS5Package package, Action<long, long>? progress = null)
    {
        List<PS5Check> checks = [];
        PKGFile pkg = package.Package;
        FIHHeader fih = pkg.FIH!;

        checks.Add(new("FIH format", fih.FormatVersion == FIHHeader.RequiredFormatVersion && fih.PFSImageOffset == FIHHeader.Size,
            $"format version {fih.FormatVersion}, PFS image at 0x{fih.PFSImageOffset:X}"));
        checks.Add(new("FIH image type", true, fih.IsDebug ? "debug (0x00)" : $"retail (0x{fih.SignedByte:X2})"));

        byte[] superblock = package.Outer.Superblock;
        bool gameDigest = Matches(fih.GameDigest, SHA3256.HashData(superblock));
        checks.Add(new("FIH game digest", gameDigest, "SHA3-256 of the outer superblock block at FIH+0x30"));
        checks.Add(new("FIH naps digest", Matches(fih.NapsDigest, SHA3256.HashData(package.Naps)) && fih.NapsSize == package.Naps.Length,
            $"SHA3-256 and length (0x{fih.NapsSize:X}) of {NAPSLayout.FileName} at FIH+0xB0/0xA8"));
        checks.Add(new("Outer ICV", package.Outer.IcvValid, "SHA3-256 of the outer superblock with the ICV zeroed"));

        byte[] cntHead = new byte[CNTHeader.Size];
        pkg.Read(pkg.CNTOffset, cntHead);
        checks.Add(new("CNT package digest", Matches(cntHead.AsSpan(CNTPackageDigestOffset, 32), SHA3256.HashData(cntHead.AsSpan(0, CNTPackageDigestOffset))),
            "SHA3-256 of CNT[0:0xFE0] at CNT+0xFE0"));
        byte[] fihBlock = new byte[FIHHeader.Size];
        pkg.Read(0, fihBlock);
        checks.Add(new("CNT FIH digest", Matches(cntHead.AsSpan(CNTFIHDigestOffset, 32), SHA3256.HashData(fihBlock)),
            "SHA3-256 of the FIH header block at CNT+0x460"));
        checks.Add(BodyDigest(pkg, cntHead));
        checks.Add(EntryDigests(pkg));
        checks.Add(GeneralDigests(pkg, cntHead));
        checks.Add(ImageDigests(pkg, package.Outer));
        checks.Add(OuterBlocks(package.Outer));
        Dictionary<string, byte[]> si = ReadSI(pkg);
        checks.Add(ChunkCrc(pkg, si));
        if (package.InnerOrNull is { } inner)
        {
            checks.Add(InnerLayout(inner));
            checks.Add(InnerChunks(package, inner, ChunkDigests(si, inner.Chunks.Count), progress));
        }
        else
        {
            checks.Add(new("naps layout", false, package.InnerError ?? "inner image not mapped"));
        }
        return checks;
    }

    private static bool Matches(ReadOnlySpan<byte> stored, ReadOnlySpan<byte> computed) => stored.SequenceEqual(computed);

    private static PS5Check BodyDigest(PKGFile pkg, byte[] cntHead)
    {
        SHA3256 hasher = new();
        byte[] buffer = new byte[1 << 20];
        for (long done = 0; done < pkg.CNT.BodySize;)
        {
            int n = (int)Math.Min(buffer.Length, pkg.CNT.BodySize - done);
            pkg.Read(pkg.CNTOffset + pkg.CNT.BodyOffset + done, buffer.AsSpan(0, n));
            hasher.Append(buffer.AsSpan(0, n));
            done += n;
        }

        return new("CNT body digest", Matches(cntHead.AsSpan(CNTBodyDigestOffset, 32), hasher.GetHashAndReset()),
            $"SHA3-256 of the 0x{pkg.CNT.BodySize:X}-byte body at CNT+0x160");
    }

    // Entry 0x0001 holds one SHA3-256 per entry in table order; its own slot is zero.
    private static PS5Check EntryDigests(PKGFile pkg)
    {
        CNTEntry? digests = pkg.FindEntry(0x0001);
        if (digests is null)
        {
            return new("CNT entry digests", false, "entry 0x0001 is missing");
        }

        byte[] table = pkg.ReadEntry(digests);
        List<string> bad = [];
        for (int i = 0; i < pkg.Entries.Count; i++)
        {
            CNTEntry entry = pkg.Entries[i];
            if (entry.Id == 0x0001 || (i + 1) * 32 > table.Length)
            {
                continue;
            }

            if (!Matches(table.AsSpan(i * 32, 32), SHA3256.HashData(pkg.ReadEntry(entry))))
            {
                bad.Add($"0x{entry.Id:X4}");
            }
        }

        return new("CNT entry digests", bad.Count == 0,
            bad.Count == 0 ? $"{pkg.Entries.Count - 1} entries" : "mismatch: " + string.Join(", ", bad));
    }

    private static PS5Check OuterBlocks(PS5OuterImage outer)
    {
        List<(byte[] Sig, long Block, long Inode)> blocks = [];
        try
        {
            foreach (PFSInode inode in outer.Files.Values)
            {
                foreach ((byte[] sig, long block) in outer.BlockList(inode))
                {
                    blocks.Add((sig, block, inode.Number));
                }
            }
        }
        catch (InvalidDataException ex)
        {
            return new("Outer block hashes", false, ex.Message);
        }

        // Blocks are checked in parallel; the first failure in file order is reported, as a serial walk would.
        int bad = FirstFailure(blocks.Count, i => outer.ReadBlock(blocks[i].Block, blocks[i].Sig) is not null);
        return bad >= 0
            ? new("Outer block hashes", false, $"block {blocks[bad].Block} of inode {blocks[bad].Inode} fails its SHA3-256")
            : new("Outer block hashes", true, $"{blocks.Count} file blocks plus metadata");
    }

    // Index of the first item (in order) for which check is false, or −1; items are checked in parallel.
    private static int FirstFailure(int count, Func<int, bool> check)
    {
        bool[] ok = new bool[count];
        Parallel.For(0, count, i => ok[i] = check(i));
        return Array.IndexOf(ok, false);
    }

    private static PS5Check InnerLayout(PS5InnerImage inner) => inner.Problems.Count == 0
        ? new("naps layout", true, $"{inner.Chunks.Count} chunks over 0x{inner.Layout.MountSize:X} bytes")
        : new("naps layout", false, string.Join("; ", inner.Problems.Take(5)));

    // Decodes every chunk; with the SI chunk digests also compares the plaintext, which catches a chunk that
    // decodes without error to wrong bytes. Chunks decode in parallel, each worker through its own stream; the
    // first failure in chunk order is reported, as a serial walk would.
    private static PS5Check InnerChunks(PS5Package package, PS5InnerImage inner, byte[]? digests, Action<long, long>? progress)
    {
        int count = inner.Chunks.Count;
        string?[] errors = new string?[count];
        long done = 0;
        Lock gate = new();
        Parallel.For(0, count, package.OpenInnerStream, (i, _, stored) =>
        {
            PS5Chunk chunk = inner.Chunks[i];
            try
            {
                byte[] plain = inner.DecodeChunk(i, stored);
                if (digests is not null && !Matches(digests.AsSpan(32 * i, 32), SHA3256.HashData(plain)))
                {
                    errors[i] = $"chunk {i} at inner offset 0x{chunk.LogicalOffset:X} does not match its SI digest";
                }
            }
            catch (InvalidDataException ex)
            {
                errors[i] = ex.Message;
            }

            if (progress is not null)
            {
                lock (gate)
                {
                    progress(++done, count);
                }
            }

            return stored;
        }, stored => stored.Dispose());

        string? first = errors.FirstOrDefault(e => e is not null);
        return first is not null
            ? new("Inner chunks", false, first)
            : new("Inner chunks", true, digests is null
                ? $"{count} chunks decode (no SI chunk digests)"
                : $"{count} chunks decode and match the SI digests");
    }

    // General digests (entry 0x0080): u16 0xD256, u16 type, set mask at +0x1C, then 32-byte slots.
    private static PS5Check GeneralDigests(PKGFile pkg, byte[] cntHead)
    {
        CNTEntry? entry = pkg.FindEntry(0x0080);
        if (entry is null)
        {
            return new("CNT general digests", false, "entry 0x0080 is missing");
        }

        byte[] general = pkg.ReadEntry(entry);
        uint set = general.Length >= 0x20 ? BinaryPrimitives.ReadUInt32BigEndian(general.AsSpan(0x1C)) : 0;
        byte[] game = cntHead.AsSpan(0x440, 32).ToArray();
        byte[] Concat(params uint[] ids) => [.. ids.Order().Select(pkg.FindEntry).OfType<CNTEntry>().SelectMany(e => SHA3256.HashData(pkg.ReadEntry(e)))];
        (uint Bit, int Offset, string Name, Func<byte[]> Compute)[] slots =
        [
            (0x0002, 0x20, "content", () => SHA3256.HashData([.. cntHead.AsSpan(0x40, 0x38), .. game, .. new byte[32]])),
            (0x0004, 0x40, "game", () => game),
            (0x0008, 0x60, "header", () => SHA3256.HashData([.. cntHead.AsSpan(0, 0x40), .. cntHead.AsSpan(0x400, 0x80)])),
            (0x0010, 0x80, "system", () => SHA3256.HashData(Concat(0x1006, 0x100D, 0x1200, 0x1220, 0x1240, 0x1280, 0x12A0, 0x12C0, 0x2040, 0x2060))),
            (0x0040, 0xC0, "param", () => pkg.FindEntry(CNTEntryNames.ParamJson) is { } p ? SHA3256.HashData(pkg.ReadEntry(p)) : new byte[32]),
            (0x0080, 0xE0, "playgo", () => SHA3256.HashData(Concat(0x1001, 0x2010, 0x2011, 0x3000))),
            (0x1000, 0x180, "target", () => game),
        ];

        List<string> bad = [.. slots.Where(s => (set & s.Bit) != 0 && (s.Offset + 32 > general.Length || !Matches(general.AsSpan(s.Offset, 32), s.Compute()))).Select(s => s.Name)];
        return new("CNT general digests", bad.Count == 0, bad.Count == 0 ? $"set 0x{set:X}" : "mismatch: " + string.Join(", ", bad));
    }

    // imagedigs (entry 0x040A): SHA3-256 of every plaintext outer block, each stored byte-reversed.
    private static PS5Check ImageDigests(PKGFile pkg, PS5OuterImage outer)
    {
        CNTEntry? entry = pkg.FindEntry(0x040A);
        long blocks = pkg.FIH!.PFSImageSize / PS5OuterImage.BlockSize;
        if (entry is null || entry.DataSize != blocks * 32)
        {
            return new("CNT image digests", false, entry is null ? "entry 0x040A is missing" : $"entry 0x040A holds {entry.DataSize / 32} digests for {blocks} blocks");
        }

        byte[] table = pkg.ReadEntry(entry);
        byte[] sbDigest = SHA3256.HashData(outer.Superblock);
        int bad = FirstFailure(checked((int)blocks), b =>
        {
            byte[] digest = table.AsSpan(32 * b, 32).ToArray();
            Array.Reverse(digest);
            return b == outer.SuperblockIndex ? Matches(digest, sbDigest) : outer.ReadBlock(b, digest) is not null;
        });
        return bad >= 0
            ? new("CNT image digests", false, $"outer block {bad} does not match its entry 0x040A digest")
            : new("CNT image digests", true, $"{blocks} outer blocks");
    }

    // playgo-chunk.crc: CRC-32C of every 64 KiB block of the package up to the CNT end.
    private static PS5Check ChunkCrc(PKGFile pkg, Dictionary<string, byte[]> si)
    {
        string name = $"config/{pkg.CNT.ContentId}/playgo-chunk.crc";
        if (!si.TryGetValue(name, out byte[]? crc))
        {
            return new("SI chunk CRC", false, $"SI archive has no {name}");
        }

        long end = pkg.CNTOffset + pkg.CNT.BodyOffset + pkg.CNT.BodySize;
        long blocks = (end + PS5OuterImage.BlockSize - 1) / PS5OuterImage.BlockSize;
        if (crc.Length != blocks * 4)
        {
            return new("SI chunk CRC", false, $"{crc.Length / 4} CRCs for {blocks} blocks");
        }

        byte[] buffer = new byte[PS5OuterImage.BlockSize];
        for (long b = 0; b < blocks; b++)
        {
            int n = (int)Math.Min(buffer.Length, end - (b * buffer.Length));
            pkg.Read(b * buffer.Length, buffer.AsSpan(0, n));
            if (BinaryPrimitives.ReadUInt32LittleEndian(crc.AsSpan((int)(4 * b))) != Crc32C.Update(0, buffer.AsSpan(0, n)))
            {
                return new("SI chunk CRC", false, $"block {b} does not match playgo-chunk.crc");
            }
        }

        return new("SI chunk CRC", true, $"{blocks} blocks");
    }

    // The SI segment after the CNT: a ZIP of install metadata. Missing or unreadable gives an empty map.
    private static Dictionary<string, byte[]> ReadSI(PKGFile pkg)
    {
        Dictionary<string, byte[]> members = new(StringComparer.Ordinal);
        long end = pkg.CNTOffset + pkg.CNT.BodyOffset + pkg.CNT.BodySize;
        if (pkg.Length <= end || pkg.Length - end > 64 << 20)
        {
            return members;
        }

        byte[] si = new byte[pkg.Length - end];
        pkg.Read(end, si);
        try
        {
            using ZipArchive zip = new(new MemoryStream(si), ZipArchiveMode.Read);
            foreach (ZipArchiveEntry entry in zip.Entries.Where(e => e.Length <= 16 << 20))
            {
                using Stream stream = entry.Open();
                using MemoryStream copy = new();
                stream.CopyTo(copy);
                members[entry.FullName] = copy.ToArray();
            }
        }
        catch (InvalidDataException)
        {
            members.Clear();
        }

        return members;
    }

    // ihsh records (0x30 per chunk, SHA3-256 of the plaintext at +8) of a plaintext naps_meta_18.dat.
    private static byte[]? ChunkDigests(Dictionary<string, byte[]> si, int chunkCount)
    {
        if (!si.TryGetValue("common/etc/naps_meta_18.dat", out byte[]? meta))
        {
            return null;
        }

        for (int o = 0; o + 16 <= meta.Length;)
        {
            long length = BinaryPrimitives.ReadInt64LittleEndian(meta.AsSpan(o + 8));
            if (length < 0 || length > meta.Length - o - 16)
            {
                return null;
            }

            if (meta.AsSpan(o, 4).SequenceEqual("hshi"u8))
            {
                if (length != 0x30L * chunkCount)
                {
                    return null;
                }

                byte[] digests = new byte[32 * chunkCount];
                for (int i = 0; i < chunkCount; i++)
                {
                    meta.AsSpan(o + 16 + (0x30 * i) + 8, 32).CopyTo(digests.AsSpan(32 * i));
                }

                return digests;
            }

            o += 16 + (int)length;
        }

        return null;
    }
}
