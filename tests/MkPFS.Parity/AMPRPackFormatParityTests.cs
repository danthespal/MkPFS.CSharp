using System.Security.Cryptography;
using System.Text.Json;
using MkPFS.Core.AMPR;
using MkPFS.Core.Compression;
using MkPFS.Core.Util;

namespace MkPFS.Parity;

/// <summary>
/// Reads every pack set built by ampr_emu <c>ampr_pack.py</c> (<c>ampr/goldens</c>, see
/// <c>tools/oracle/build_ampr_goldens.py</c>), re-serializes each structure byte for byte, and decodes every
/// packed file against the oracle's unpack hashes and the decoded-chunk CRC sidecar.
/// </summary>
public sealed class AMPRPackFormatParityTests
{
    public static TheoryData<string> BuiltCases => new(
        "default", "unsafe_default", "no_config", "example", "fast", "fast_accel8", "hc9", "block16k_random", "block1m",
        "streaming", "store_movies", "dedup_off", "dedup_group", "dedup_streaming", "lanes_balanced", "lanes_hash",
        "lanes_round_robin", "stripe_rollover", "auto_loose", "self_contained", "runtime", "io_page_4k", "mtime_preserved",
        "no_preserve_mtime", "cli_include_exclude", "include_from", "allow_missing", "workers1", "workers8");

    private static Dictionary<string, string> OracleHashes(string caseName)
    {
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(Fixtures.PathOrSkip("ampr", "goldens", "manifest.json")));
        return manifest.RootElement.GetProperty("cases").GetProperty(caseName).GetProperty("sha256")
            .EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
    }

    [Theory]
    [MemberData(nameof(BuiltCases))]
    public void Manifest_sidecars_and_volume_headers_round_trip(string caseName)
    {
        string outDir = Fixtures.PathOrSkip("ampr", "goldens", caseName, "out");
        string indexPath = Path.Combine(outDir, "ampr_assets.index");
        byte[] indexBytes = File.ReadAllBytes(indexPath);
        AMPRPackManifest manifest = AMPRPackManifest.Load(indexPath);
        Assert.Equal(indexBytes, manifest.Serialize());

        byte[] crcBytes = File.ReadAllBytes(AMPRPackFormat.ChunkCrcPath(indexPath));
        uint[] crcs = AMPRChunkCrcs.Parse(crcBytes, manifest.BuildId, manifest.Chunks.Count);
        Assert.Equal(crcBytes, AMPRChunkCrcs.Build(manifest.BuildId, crcs));

        string runtimePath = AMPRPackFormat.RuntimePath(indexPath);
        AMPRRuntimeSettings? runtime = AMPRRuntimeSettings.Read(runtimePath, manifest.BuildId);
        Assert.Equal(File.Exists(runtimePath), runtime is not null);
        if (runtime is not null)
        {
            Assert.Equal(File.ReadAllBytes(runtimePath), runtime.Encode(manifest.BuildId));
        }

        for (int packId = 0; packId < manifest.Packs.Count; packId++)
        {
            AMPRPackRecord record = manifest.Packs[packId];
            string volume = AMPRAssetPath.SafeOutputPath(outDir, manifest.PackName(packId));
            byte[] head = new byte[AMPRPackFormat.DataHeaderSize];
            using (FileStream stream = File.OpenRead(volume))
            {
                stream.ReadExactly(head);
            }

            AMPRDataHeader header = AMPRDataHeader.Parse(head, (uint)packId, manifest.BuildId, record.Flags);
            Assert.Equal(head, header.ToBytes());
            Assert.Equal(record.FileSize, (ulong)new FileInfo(volume).Length);
            Assert.Equal(record.PayloadBytes, header.PayloadBytes);
            Assert.Equal(record.FileSize, header.PayloadOffset + header.PayloadBytes);
        }

        // AMPRIDX3 rows map one-to-one onto manifest file records (file id = row + 1).
        List<AMPRIndexEntry> index = AMPRIndexReader.Read(Fixtures.PathOrSkip("ampr", "goldens", caseName, "ampr_emu.index"));
        Assert.Equal(index.Count, manifest.Files.Count);
        for (int i = 0; i < index.Count; i++)
        {
            Assert.Equal(index[i].Path, manifest.FilePath(i + 1));
            Assert.Equal(AMPRAssetPath.Hash(index[i].Path), manifest.Files[i].PathHash);
        }
    }

    [Theory]
    [MemberData(nameof(BuiltCases))]
    public void Decoded_files_match_oracle_unpack_hashes_and_chunk_crcs(string caseName)
    {
        string outDir = Fixtures.PathOrSkip("ampr", "goldens", caseName, "out");
        string indexPath = Path.Combine(outDir, "ampr_assets.index");
        AMPRPackManifest manifest = AMPRPackManifest.Load(indexPath);
        uint[] crcs = AMPRChunkCrcs.Load(AMPRPackFormat.ChunkCrcPath(indexPath), manifest.BuildId, manifest.Chunks.Count);
        Dictionary<string, string> oracle = OracleHashes(caseName);
        byte[][] volumes = [.. Enumerable.Range(0, manifest.Packs.Count)
            .Select(id => File.ReadAllBytes(AMPRAssetPath.SafeOutputPath(outDir, manifest.PackName(id))))];

        int packed = 0;
        for (int fileId = 1; fileId <= manifest.Files.Count; fileId++)
        {
            AMPRFileRecord record = manifest.Files[fileId - 1];
            if (!record.IsPacked)
            {
                continue;
            }

            packed++;
            using IncrementalHash sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (uint local = 0; local < record.ChunkCount; local++)
            {
                int chunkIndex = (int)(record.FirstChunk + local);
                AMPRChunkRecord chunk = manifest.Chunks[chunkIndex];
                ReadOnlySpan<byte> stored = volumes[chunk.PackId].AsSpan((int)chunk.Offset, chunk.StoredSize);
                byte[] raw = new byte[chunk.RawSize];
                if (chunk.Codec == AMPRPackFormat.ChunkCodecLZ4)
                {
                    Assert.Equal(chunk.RawSize, LZ4Codec.Decompress(stored, raw));
                }
                else
                {
                    stored.CopyTo(raw);
                }

                Assert.Equal(crcs[chunkIndex], Crc32.Update(0, raw));
                sha.AppendData(raw);
            }

            string relative = AMPRAssetPath.Relative(manifest.FilePath(fileId));
            Assert.Equal(oracle[$"unpacked/{relative}"], Convert.ToHexStringLower(sha.GetHashAndReset()));
        }

        Assert.Equal(oracle.Keys.Count(k => k.StartsWith("unpacked/", StringComparison.Ordinal)), packed);
    }
}
