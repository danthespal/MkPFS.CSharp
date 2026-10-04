using System.Buffers.Binary;
using MkPFS.Core.AMPR;
using MkPFS.Core.Util;

namespace MkPFS.Tests.AMPR;

/// <summary>AMPR pack format helpers (ampr_emu ampr_pack_format.py); expected values come from Python.</summary>
public sealed class AMPRPackFormatTests
{
    private static readonly byte[] BuildId = [.. Enumerable.Range(0, 16).Select(i => (byte)i)];

    [Theory]
    [InlineData("64KiB", 65536L)]
    [InlineData("1.5MB", 1500000L)]
    [InlineData("1_024", 1024L)]
    [InlineData(" 8 KiB ", 8192L)]
    [InlineData("2g", 2000000000L)]
    [InlineData("0", 0L)]
    [InlineData("1e3", 1000L)]
    [InlineData("10TiB", 10995116277760L)]
    [InlineData("3.99b", 3L)]
    public void ParseSize_matches_python(string text, long expected) => Assert.Equal(expected, AMPRSize.Parse(text));

    [Theory]
    [InlineData("", "empty size")]
    [InlineData("KiB", "invalid size: KiB")]
    [InlineData("12xb", "unknown size suffix: xb")]
    [InlineData("-1", "size must be non-negative")]
    [InlineData("1e400", "invalid size: 1e400")]
    [InlineData("abc5", "invalid size: abc5")]
    public void ParseSize_errors_match_python(string text, string message) =>
        Assert.Equal(message, Assert.Throws<ArgumentException>(() => AMPRSize.Parse(text)).Message);

    [Theory]
    [InlineData("16KiB", 14)]
    [InlineData("64KiB", 16)]
    [InlineData("1MiB", 20)]
    public void BlockShift_accepts_supported_sizes(string text, int shift) => Assert.Equal(shift, AMPRSize.BlockShift(text));

    [Theory]
    [InlineData("48KiB", "block size must be a power of two, got 49152")]
    [InlineData("8KiB", "block size must be between 16384 and 1048576 bytes")]
    [InlineData("2MiB", "block size must be between 16384 and 1048576 bytes")]
    public void BlockShift_rejects_other_sizes(string text, string message) =>
        Assert.Equal(message, Assert.Throws<ArgumentException>(() => AMPRSize.BlockShift(text)).Message);

    [Fact]
    public void Fnv1a64_matches_python()
    {
        Assert.Equal(0xcbf29ce484222325UL, AMPRAssetPath.Fnv1a64([]));
        Assert.Equal(0xaf63dc4c8601ec8cUL, AMPRAssetPath.Fnv1a64("a"u8));
    }

    [Theory]
    [InlineData("/app0/Assets/X.bin", "/app0/Assets/X.bin", 0xe7738fe70d6ed8f9UL)]
    [InlineData("app0\\data\\a.txt", "/app0/data/a.txt", 0x6d353fb6a0bd22eaUL)]
    [InlineData("/app0/donn\u00e9es.txt", "/app0/donn\u00e9es.txt", 0x19e1db4b3788e9f7UL)]
    [InlineData("/APP0/./a//b/../c", "/APP0/a/c", 0)]
    public void Canonical_path_and_hash_match_python(string path, string canonical, ulong hash)
    {
        Assert.Equal(canonical, AMPRAssetPath.Canonical(path));
        if (hash != 0)
        {
            Assert.Equal(hash, AMPRAssetPath.Hash(path));
        }
    }

    [Theory]
    [InlineData("/app0/../../x", "path escapes root: /app0/../../x")]
    [InlineData("/other/x", "asset is outside /app0: /other/x")]
    [InlineData("/app0/a\0b", "NUL in path")]
    public void Canonical_path_errors_match_python(string path, string message) =>
        Assert.Equal(message, Assert.Throws<ArgumentException>(() => AMPRAssetPath.Canonical(path)).Message);

    [Fact]
    public void Relative_path_strips_app0()
    {
        Assert.Equal("a/B.bin", AMPRAssetPath.Relative("/app0/a/B.bin"));
        Assert.Equal(string.Empty, AMPRAssetPath.Relative("/App0"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a\\b")]
    [InlineData("/a")]
    [InlineData("a/")]
    [InlineData("a//b")]
    [InlineData("a/./b")]
    [InlineData("a/../b")]
    public void SafeOutputPath_rejects_unsafe_names(string relative) =>
        Assert.StartsWith("unsafe output path: ", Assert.Throws<ArgumentException>(() => AMPRAssetPath.SafeOutputPath(".", relative)).Message);

    [Fact]
    public void SafeOutputPath_joins_safe_names() =>
        Assert.Equal(Path.Combine("root", "a", "b.pak"), AMPRAssetPath.SafeOutputPath("root", "a/b.pak"));

    [Theory]
    [InlineData("a'b", "\"a'b\"")]
    [InlineData("a\"b", "'a\"b'")]
    [InlineData("a'\"b", "'a\\'\"b'")]
    [InlineData("\0\n\t\u00e9", "'\\x00\\n\\t\u00e9'")]
    [InlineData("\u200b\U0001F600\u007f\u00a0\u2028", "'\\u200b\U0001F600\\x7f\\xa0\\u2028'")]
    [InlineData("back\\slash", "'back\\\\slash'")]
    public void Repr_matches_python(string value, string expected) => Assert.Equal(expected, PythonText.Repr(value));

    [Fact]
    public void BuildId_matches_python() =>
        Assert.Equal("481ee681277b00834eea5627b85c71b3", Convert.ToHexStringLower(AMPRBuildId.Compute([[(byte)'x'], []])));

    [Fact]
    public void Runtime_settings_encode_like_python_and_round_trip()
    {
        AMPRRuntimeSettings settings = new(64 * 1024 * 1024, 16 * 1024 * 1024, 4, 1);
        byte[] encoded = settings.Encode(BuildId);

        Assert.Equal(
            "414d5052434647310100000040000000000102030405060708090a0b0c0d0e0f0000000400000000000000010000000004000000010000000b640fd700000000",
            Convert.ToHexStringLower(encoded));
        Assert.Equal(settings, AMPRRuntimeSettings.Parse(encoded, BuildId));
    }

    [Theory]
    [InlineData(0, 0, "runtime workers must be 1..16 and reserve must be smaller")]
    [InlineData(17, 0, "runtime workers must be 1..16 and reserve must be smaller")]
    [InlineData(4, 4, "runtime workers must be 1..16 and reserve must be smaller")]
    [InlineData(4, -1, "runtime workers must be 1..16 and reserve must be smaller")]
    public void Runtime_settings_reject_bad_workers(long workers, long reserve, string message) =>
        Assert.Equal(message, Assert.Throws<ArgumentException>(() => new AMPRRuntimeSettings(0, 0, workers, reserve).Validate()).Message);

    [Fact]
    public void Runtime_settings_reject_unaligned_cache() =>
        Assert.Equal(
            "runtime cache sizes must be nonnegative 16 KiB multiples",
            Assert.Throws<ArgumentException>(() => new AMPRRuntimeSettings(1000, 0, 4, 1).Validate()).Message);

    [Fact]
    public void Runtime_settings_reject_other_build_and_corruption()
    {
        byte[] encoded = new AMPRRuntimeSettings(0, 16384, 1, 0).Encode(BuildId);
        byte[] otherBuild = new byte[16];
        Assert.Throws<InvalidDataException>(() => AMPRRuntimeSettings.Parse(encoded, otherBuild));
        encoded[33] ^= 1;
        Assert.Equal(
            "invalid runtime profile header, build ID or CRC",
            Assert.Throws<InvalidDataException>(() => AMPRRuntimeSettings.Parse(encoded, BuildId)).Message);
        Assert.Equal("invalid runtime profile size", Assert.Throws<InvalidDataException>(() => AMPRRuntimeSettings.Parse(encoded[..63], BuildId)).Message);
    }

    [Fact]
    public void Missing_runtime_settings_read_as_null() =>
        Assert.Null(AMPRRuntimeSettings.Read(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), BuildId));

    [Fact]
    public void Data_header_encodes_like_python_and_round_trips()
    {
        AMPRDataHeader header = new(2, BuildId, 65536, 4096, 3);
        byte[] bytes = header.ToBytes();

        Assert.Equal(
            "414d50524441543303000000400000000200000003000000000102030405060708090a0b0c0d0e0f00000100000000000010000000000000a71d34c200000000",
            Convert.ToHexStringLower(bytes));
        AMPRDataHeader parsed = AMPRDataHeader.Parse(bytes, 2, BuildId, 3);
        Assert.Equal((2U, 65536UL, 4096UL, 3U), (parsed.PackId, parsed.PayloadOffset, parsed.PayloadBytes, parsed.Flags));
        Assert.Equal(BuildId, parsed.BuildId);
    }

    [Fact]
    public void Data_header_rejects_mismatches_in_python_order()
    {
        byte[] bytes = new AMPRDataHeader(2, BuildId, 65536, 4096, 2).ToBytes();
        Assert.Equal("pack id mismatch", Assert.Throws<InvalidDataException>(() => AMPRDataHeader.Parse(bytes, expectedPackId: 1)).Message);
        Assert.Equal("pack build id mismatch", Assert.Throws<InvalidDataException>(() => AMPRDataHeader.Parse(bytes, 2, new byte[16])).Message);
        Assert.Equal("pack flags mismatch", Assert.Throws<InvalidDataException>(() => AMPRDataHeader.Parse(bytes, 2, BuildId, 3)).Message);
        Assert.Equal("pack data header is truncated", Assert.Throws<InvalidDataException>(() => AMPRDataHeader.Parse(bytes[..63])).Message);
        bytes[60] = 1;
        Assert.Equal("invalid pack data header", Assert.Throws<InvalidDataException>(() => AMPRDataHeader.Parse(bytes)).Message);
        bytes[60] = 0;
        bytes[41] ^= 1;
        Assert.Equal("pack data header CRC mismatch", Assert.Throws<InvalidDataException>(() => AMPRDataHeader.Parse(bytes)).Message);
    }

    [Fact]
    public void Chunk_crc_sidecar_encodes_like_python_and_round_trips()
    {
        byte[] bytes = AMPRChunkCrcs.Build(BuildId, [1, 0xFFFFFFFF]);
        Assert.Equal(
            "414d5052435243310100000030000000000102030405060708090a0b0c0d0e0f020000000000000014ff33775e9fbbb301000000ffffffff",
            Convert.ToHexStringLower(bytes));
        Assert.Equal([1U, 0xFFFFFFFFU], AMPRChunkCrcs.Parse(bytes, BuildId, 2));
        Assert.Equal(
            "chunk CRC sidecar build ID or chunk count mismatch",
            Assert.Throws<InvalidDataException>(() => AMPRChunkCrcs.Parse(bytes, BuildId, 3)).Message);
        bytes[^1] ^= 1;
        Assert.Equal(
            "chunk CRC sidecar payload CRC mismatch",
            Assert.Throws<InvalidDataException>(() => AMPRChunkCrcs.Parse(bytes, BuildId, 2)).Message);
    }

    [Fact]
    public void Chunk_record_packs_bit_fields_and_round_trips()
    {
        AMPRChunkRecord chunk = new(0x123456789ABC, 65536, 0, 0x0102, AMPRPackFormat.ChunkCodecLZ4, AMPRPackFormat.ChunkFlagPageAligned);
        byte[] bytes = new byte[12];
        chunk.Write(bytes);

        Assert.Equal(0x0102123456789ABCUL, BinaryPrimitives.ReadUInt64LittleEndian(bytes));
        Assert.Equal(0xFFFFU | (1U << 20) | (8U << 22), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)));
        Assert.Equal(chunk, AMPRChunkRecord.Read(bytes));
        bytes[11] |= 0x40;
        Assert.Equal("chunk descriptor contains reserved bits", Assert.Throws<InvalidDataException>(() => AMPRChunkRecord.Read(bytes)).Message);
    }

    [Theory]
    [InlineData(1UL << 48, 1, 0, 0, 0, "chunk offset exceeds the 48-bit AMPRPAK4 domain")]
    [InlineData(0UL, 0, 0, 0, 0, "chunk stored size is outside the AMPRPAK4 domain")]
    [InlineData(0UL, (1 << 20) + 1, 0, 0, 0, "chunk stored size is outside the AMPRPAK4 domain")]
    [InlineData(0UL, 1, 0x10000, 0, 0, "chunk pack id exceeds uint16")]
    [InlineData(0UL, 1, 0, 4, 0, "chunk codec is outside the compact descriptor domain")]
    [InlineData(0UL, 1, 0, 0, 0x10, "chunk contains unknown flags")]
    public void Chunk_record_rejects_out_of_domain_fields(ulong offset, int stored, int packId, int codec, int flags, string message) =>
        Assert.Equal(message, Assert.Throws<ArgumentException>(() => new AMPRChunkRecord(offset, stored, 0, packId, codec, flags).Write(new byte[12])).Message);

    [Fact]
    public void String_table_deduplicates_and_terminates()
    {
        AMPRStringTable table = new();
        Assert.Equal((0U, 3U), table.Add("abc"));
        Assert.Equal((4U, 2U), table.Add("\u00e9"));
        Assert.Equal((0U, 3U), table.Add("abc"));
        Assert.Equal("abc\0\u00e9\0"u8.ToArray(), table.ToArray());
        Assert.Throws<ArgumentException>(() => table.Add("a\0"));
    }

    // One volume with a 64 KiB page, one packed file of 70000 bytes in two LZ4 chunks, one loose file.
    private static AMPRPackManifest SampleManifest(
        Func<AMPRFileRecord, AMPRFileRecord>? editPacked = null,
        Func<AMPRChunkRecord, AMPRChunkRecord>? editFirstChunk = null,
        Func<AMPRPackRecord, AMPRPackRecord>? editPack = null)
    {
        AMPRStringTable strings = new();
        (uint packedOffset, uint packedLength) = strings.Add("/app0/data/Big.bin");
        (uint looseOffset, uint looseLength) = strings.Add("/app0/eboot.bin");
        (uint nameOffset, uint nameLength) = strings.Add("ampr_assets-000.pak");
        const int contained = AMPRPackFormat.ChunkFlagPageContained | AMPRPackFormat.ChunkFlagPageAligned;
        AMPRChunkRecord first = new(65536, 30000, 65536, 0, AMPRPackFormat.ChunkCodecLZ4, contained);
        AMPRChunkRecord second = new(65536 + 30016, 4464, 4464, 0, AMPRPackFormat.ChunkCodecRaw, AMPRPackFormat.ChunkFlagPageContained);
        AMPRFileRecord packed = new(
            AMPRAssetPath.Hash("/app0/data/Big.bin"), 70000, 1600000000, 0, 2, packedOffset, packedLength,
            AMPRPackFormat.FileFlagPacked, 16);
        AMPRFileRecord loose = new(AMPRAssetPath.Hash("/app0/eboot.bin"), 1000, 1600000000, 0, 0, looseOffset, looseLength, 0, 0);
        AMPRPackRecord pack = new(65536, 131072, nameOffset, nameLength, AMPRPackFormat.PackFlagIOPageLayout, 65536);
        return new AMPRPackManifest(
            BuildId,
            0,
            [editPacked?.Invoke(packed) ?? packed, loose],
            [editFirstChunk?.Invoke(first) ?? first, second],
            [editPack?.Invoke(pack) ?? pack],
            strings.ToArray());
    }

    [Fact]
    public void Manifest_round_trips_and_derives_raw_sizes()
    {
        byte[] bytes = SampleManifest().Serialize();
        AMPRPackManifest parsed = AMPRPackManifest.Parse(bytes);

        Assert.Equal(bytes, parsed.Serialize());
        Assert.Equal([65536, 4464], parsed.Chunks.Select(c => c.RawSize));
        Assert.Equal("/app0/data/Big.bin", parsed.FilePath(1));
        Assert.Equal("ampr_assets-000.pak", parsed.PackName(0));
    }

    [Fact]
    public void Manifest_rejects_header_and_payload_corruption()
    {
        byte[] bytes = SampleManifest().Serialize();
        Assert.Equal("pack index is truncated", Assert.Throws<InvalidDataException>(() => AMPRPackManifest.Parse(bytes.AsSpan(0, 127))).Message);

        byte[] header = (byte[])bytes.Clone();
        header[40] ^= 1;
        Assert.Equal("pack index header CRC mismatch", Assert.Throws<InvalidDataException>(() => AMPRPackManifest.Parse(header)).Message);

        byte[] payload = (byte[])bytes.Clone();
        payload[^2] ^= 1;
        Assert.Equal("pack index payload CRC mismatch", Assert.Throws<InvalidDataException>(() => AMPRPackManifest.Parse(payload)).Message);

        byte[] magic = (byte[])bytes.Clone();
        magic[7] = (byte)'5';
        Assert.Equal("unsupported AMPR pack index", Assert.Throws<InvalidDataException>(() => AMPRPackManifest.Parse(magic)).Message);
    }

    public static TheoryData<string, string> InvalidManifests => new()
    {
        { "hash", "path hash mismatch for file id 1" },
        { "shift", "invalid block shift for file id 1" },
        { "both", "file id 1 cannot be both streaming and random-access" },
        { "storeonly", "store-only file id 1 contains a compressed chunk" },
        { "misaligned", "chunk range/alignment is invalid for file id 1" },
        { "uncontained", "small page-aligned chunk lacks containment flag for file id 1" },
        { "pagesize", "invalid I/O page size for pack 0" },
        { "nolayout", "pack 0 does not declare page-aware layout" },
        { "payload", "invalid payload offset for pack 0" },
        { "streaming", "streaming flag mismatch for file id 1" },
        { "size", "logical size mismatch for file id 1" },
    };

    [Theory]
    [MemberData(nameof(InvalidManifests))]
    public void Manifest_validation_messages_match_python(string edit, string message)
    {
        AMPRPackManifest manifest = edit switch
        {
            "hash" => SampleManifest(editPacked: f => f with { PathHash = f.PathHash + 1 }),
            "shift" => SampleManifest(editPacked: f => f with { BlockShift = 13 }),
            "both" => SampleManifest(editPacked: f => f with { Flags = f.Flags | AMPRPackFormat.FileFlagStreaming | AMPRPackFormat.FileFlagRandomAccess }),
            "storeonly" => SampleManifest(editPacked: f => f with { Flags = f.Flags | AMPRPackFormat.FileFlagStoreOnly }),
            "misaligned" => SampleManifest(editFirstChunk: c => c with { Offset = c.Offset + 1 }),
            "uncontained" => SampleManifest(editFirstChunk: c => c with { Flags = AMPRPackFormat.ChunkFlagPageAligned }),
            "pagesize" => SampleManifest(editPack: p => p with { IOPageSize = 3000 }),
            "nolayout" => SampleManifest(editPack: p => p with { Flags = 0 }),
            "payload" => SampleManifest(editPack: p => p with { PayloadBytes = 1000 }),
            "streaming" => SampleManifest(editPacked: f => f with { Flags = f.Flags | AMPRPackFormat.FileFlagStreaming }),
            "size" => SampleManifest(editPacked: f => f with { ChunkCount = 0 }),
            _ => throw new ArgumentOutOfRangeException(nameof(edit)),
        };

        // Parse derives raw sizes like load_manifest, so the raw sizes always add up unless the chunk count is wrong.
        Assert.Equal(message, Assert.Throws<InvalidDataException>(() => AMPRPackManifest.Parse(manifest.Serialize())).Message);
    }

    [Fact]
    public void Manifest_rejects_extra_chunks_while_deriving_raw_sizes() =>
        Assert.Equal(
            "AMPRPAK4 file has too many chunks",
            Assert.Throws<InvalidDataException>(() => AMPRPackManifest.Parse(SampleManifest(editPacked: f => f with { LogicalSize = 65536 }).Serialize())).Message);

    [Fact]
    public void Index_reader_parses_the_build_writer_output()
    {
        byte[] index = MkPFS.Build.AmprIndex.Serialize(
            [new(10, 1600000000, "/app0/a/B.bin"), new(0, 1600000001, "/app0/c.txt")]);

        Assert.Equal(
            [new AMPRIndexEntry("/app0/a/B.bin", 10, 1600000000), new AMPRIndexEntry("/app0/c.txt", 0, 1600000001)],
            AMPRIndexReader.Parse(index));
        Assert.Equal("AMPRIDX3 is truncated", Assert.Throws<InvalidDataException>(() => AMPRIndexReader.Parse(index.AsSpan(0, 47))).Message);
    }
}
