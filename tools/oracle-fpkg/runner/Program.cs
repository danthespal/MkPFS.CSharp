// FPKG oracle runner: drives the patched LibProsperoPkg copy for build_fpkg_goldens.py.
//
//   FPKGOracle build   --src DIR --out DIR --content-id ID --passcode P --seed HEX --timestamp N
//                      [--title-id ID] [--title T] [--version NN.NN] [--app-type NAME] [--raw]
//   FPKGOracle vectors --trees DIR --out DIR
//
// build: copies --src to <out>/work (the oracle rewrites modules in place and may add param.json),
// builds <out>/out.pkg, extracts it to <out>/extract, and writes <out>/result.json.
// vectors: writes key, keystone, FLT hash, SHA3, CRC-32C, fSELF and DDS vectors to <out>.

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using LibProsperoPkg;
using LibProsperoPkg.Content;
using LibProsperoPkg.PFS;
using LibProsperoPkg.PKG;
using LibProsperoPkg.Util;

namespace FPKGOracle;

internal static class Program
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: FPKGOracle build|vectors [options]");
            return 2;
        }

        Dictionary<string, string?> opts = ParseOptions(args.AsSpan(1));
        try
        {
            return args[0] switch
            {
                "build" => Build(opts),
                "vectors" => Vectors(opts),
                _ => throw new ArgumentException($"unknown command {args[0]}"),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static Dictionary<string, string?> ParseOptions(ReadOnlySpan<string> args)
    {
        var opts = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"unexpected argument {args[i]}");
            string key = args[i][2..];
            string? value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : null;
            opts[key] = value;
        }
        return opts;
    }

    private static string Require(Dictionary<string, string?> opts, string key) =>
        opts.TryGetValue(key, out string? v) && v is not null ? v : throw new ArgumentException($"--{key} is required");

    private static string? Optional(Dictionary<string, string?> opts, string key) =>
        opts.TryGetValue(key, out string? v) ? v : null;

    // ---- build ---------------------------------------------------------------------------------

    private static int Build(Dictionary<string, string?> opts)
    {
        string src = Path.GetFullPath(Require(opts, "src"));
        string outDir = Path.GetFullPath(Require(opts, "out"));
        string contentId = Require(opts, "content-id");
        string passcode = Require(opts, "passcode");
        long timestamp = long.Parse(Require(opts, "timestamp"), System.Globalization.CultureInfo.InvariantCulture);

        OracleHooks.OuterSeed = Convert.FromHexString(Require(opts, "seed"));
        OracleHooks.UtcNow = DateTime.UnixEpoch.AddSeconds(timestamp);
        OracleHooks.StoreAllRaw = opts.ContainsKey("raw");

        if (Directory.Exists(outDir))
            Directory.Delete(outDir, recursive: true);
        string work = Path.Combine(outDir, "work");
        string pkgDir = Path.Combine(outDir, "pkg");
        string extract = Path.Combine(outDir, "extract");
        CopyTree(src, work);

        var options = new ProsperoBuildOptions
        {
            Mode = ProsperoPackageMode.Application,
            OutputFormat = ProsperoOutputFormat.DebugImage,
            SourceFolder = work,
            OutputFolder = pkgDir,
            ContentId = contentId,
            Passcode = passcode,
            TitleId = Optional(opts, "title-id") ?? contentId.Substring(7, 9),
            Title = Optional(opts, "title") ?? "",
            Version = Optional(opts, "version") ?? "01.00",
            ApplicationType = Optional(opts, "app-type") is { } appType
                ? ProsperoApplicationTypes.Parse(appType)
                : ProsperoApplicationType.NotSpecified,
            LicenseFree = true,
        };

        var log = new List<string>();
        ProsperoBuildResult result = ProsperoPackageBuilder.Build(options, log.Add);
        string pkgPath = Path.Combine(outDir, "out.pkg");
        File.Move(result.OutputPath, pkgPath);
        Directory.Delete(pkgDir, recursive: true);
        File.WriteAllLines(Path.Combine(outDir, "build.log"), log);

        var json = new JsonObject
        {
            ["oracle_output_name"] = Path.GetFileName(result.OutputPath),
            ["raw"] = OracleHooks.StoreAllRaw,
            ["warnings"] = new JsonArray(result.Warnings.Select(w => (JsonNode?)w).ToArray()),
            ["segments"] = DescribeSegments(pkgPath),
            ["entries"] = DescribeEntries(pkgPath),
            ["header_signature"] = DescribeHeaderSignature(pkgPath),
        };

        try
        {
            ProsperoPackageManifest manifest = ProsperoPackageExtractor.Extract(pkgPath, extract, passcode);
            json["extract"] = new JsonObject
            {
                ["ok"] = true,
                ["package_type"] = manifest.PackageType.ToString(),
                ["ekpfs_fingerprint"] = manifest.EkpfsFingerprint,
                ["outer_file_count"] = manifest.OuterFileCount,
                ["inner_image_compressed"] = manifest.InnerImageCompressed,
                ["file_count"] = manifest.ExtractedFileCount,
            };
        }
        catch (Exception ex)
        {
            json["extract"] = new JsonObject { ["ok"] = false, ["error"] = Describe(ex) };
        }

        File.WriteAllText(Path.Combine(outDir, "result.json"), json.ToJsonString(Indented) + "\n");
        return 0;
    }

    private static string Describe(Exception ex)
    {
        var parts = new List<string>();
        for (Exception? e = ex; e is not null; e = e.InnerException)
            parts.Add($"{e.GetType().Name}: {e.Message}");
        return string.Join(" <- ", parts);
    }

    private static void CopyTree(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (string dir in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dst, Path.GetRelativePath(src, dir)));
        foreach (string file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(dst, Path.GetRelativePath(src, file)));
    }

    // FIH / outer PFS / CNT / SI split. The SI start comes from the ZIP end-of-central-directory
    // record, so it does not depend on how the oracle sizes the CNT.
    private static JsonObject DescribeSegments(string pkgPath)
    {
        byte[] pkg = File.ReadAllBytes(pkgPath);
        long pfsOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(pkg.AsSpan(0x10));
        long pfsSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(pkg.AsSpan(0x18));
        long cntOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(pkg.AsSpan(0x58));
        long siOffset = FindZipStart(pkg) ?? pkg.Length;

        return new JsonObject
        {
            ["file_size"] = pkg.Length,
            ["fih"] = Segment(pkg, 0, pfsOffset),
            ["gap_fih_pfs"] = pfsOffset == 0x10000 ? null : Segment(pkg, 0x10000, pfsOffset - 0x10000),
            ["pfs"] = Segment(pkg, pfsOffset, pfsSize),
            ["gap_pfs_cnt"] = Segment(pkg, pfsOffset + pfsSize, cntOffset - (pfsOffset + pfsSize)),
            ["cnt"] = Segment(pkg, cntOffset, siOffset - cntOffset),
            ["si"] = Segment(pkg, siOffset, pkg.Length - siOffset),
        };
    }

    private static JsonObject Segment(byte[] data, long offset, long size) => new()
    {
        ["offset"] = offset,
        ["size"] = size,
        ["sha256"] = size < 0 || offset + size > data.Length
            ? "out-of-range"
            : Convert.ToHexStringLower(SHA256.HashData(data.AsSpan((int)offset, (int)size))),
    };

    private static long? FindZipStart(byte[] data)
    {
        // EOCD: "PK\x05\x06", central directory size @+12, offset (relative to the ZIP start) @+16.
        for (int pos = data.Length - 22; pos >= Math.Max(0, data.Length - 0x10000 - 22); pos--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos)) != 0x06054B50)
                continue;
            long cdSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 12));
            long cdOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 16));
            return pos - cdSize - cdOffset;
        }
        return null;
    }

    // CNT+0x1000 holds the SHA3-256 header digest RSA-encrypted with the PKG-metadata key. Decrypting
    // it with the private key proves the (hooked, deterministic) PKCS#1 padding is well formed.
    private static JsonObject DescribeHeaderSignature(string pkgPath)
    {
        ProsperoPkg pkg = ProsperoPkgReader.Read(pkgPath);
        using FileStream fs = File.OpenRead(pkgPath);
        var signature = new byte[ProsperoPkgSigner.SignatureSize];
        fs.Position = (long)(pkg.Fih?.EmbeddedCntOffset ?? 0) + 0x1000;
        fs.ReadExactly(signature);
        try
        {
            return new JsonObject { ["ok"] = true, ["digest"] = Hex(ProsperoPkgSigner.DecryptHeaderDigest(signature)) };
        }
        catch (CryptographicException ex)
        {
            return new JsonObject { ["ok"] = false, ["error"] = ex.Message };
        }
    }

    private static JsonArray DescribeEntries(string pkgPath)
    {
        ProsperoPkg pkg = ProsperoPkgReader.Read(pkgPath);
        long cntOffset = (long)(pkg.Fih?.EmbeddedCntOffset ?? 0);
        using FileStream fs = File.OpenRead(pkgPath);
        var entries = new JsonArray();
        foreach (ProsperoPkgEntry e in pkg.Entries)
        {
            var payload = new byte[e.DataSize];
            fs.Position = cntOffset + e.DataOffset;
            fs.ReadExactly(payload);
            entries.Add(new JsonObject
            {
                ["id"] = $"0x{e.RawId:X4}",
                ["name"] = e.Name,
                ["offset"] = e.DataOffset,
                ["size"] = e.DataSize,
                ["flags1"] = $"0x{e.Flags1:X8}",
                ["flags2"] = $"0x{e.Flags2:X8}",
                ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(payload)),
            });
        }
        return entries;
    }

    // ---- vectors -------------------------------------------------------------------------------

    private static readonly string[] VectorContentIds =
    [
        "UP9000-PPSA99999_00-MKPFSORACLE00000",
        "EP0001-PPSA00001_00-ABCDEFGHIJKLMNOP",
    ];

    private static readonly string[] VectorPasscodes =
    [
        new string('0', 32),
        "abcdefghijklmnopqrstuvwxyz012345",
    ];

    private static readonly string[] VectorNames =
    [
        "pfs_image.dat", "naps_pkg_layout.dat", "uroot", "eboot.bin", "sce_sys", "param.json",
        "keystone", "right.sprx", "a", "MixedCase.Bin", "",
    ];

    private static readonly string[] VectorPaths =
    [
        "/sce_sys/keystone", "/sce_sys/about/right.sprx", "/eboot.bin", "eboot.bin", "/data/deep/a/b/c/d/leaf.txt",
        "/Assets/UPPER.DAT", "/données/ñandú.txt", "/日本語/テスト.bin", "/straße", "/emoji😀.bin", "/", "/12345678",
        "/123456789", "/ıſ.bin",
    ];

    private static int Vectors(Dictionary<string, string?> opts)
    {
        string trees = Path.GetFullPath(Require(opts, "trees"));
        string outDir = Path.GetFullPath(Require(opts, "out"));
        if (Directory.Exists(outDir))
            Directory.Delete(outDir, recursive: true);
        Directory.CreateDirectory(outDir);

        byte[] seed = Convert.FromHexString("000102030405060708090a0b0c0d0e0f");
        var keys = new JsonArray();
        foreach (string contentId in VectorContentIds)
        {
            foreach (string passcode in VectorPasscodes)
            {
                byte[] ekpfs = ProsperoPfsKeys.DeriveEkpfs(contentId, passcode);
                (byte[] tweak, byte[] data) = ProsperoPfsKeys.DeriveImageEncryptionKeys(ekpfs, seed);
                keys.Add(new JsonObject
                {
                    ["content_id"] = contentId,
                    ["passcode"] = passcode,
                    ["ekpfs_sha3"] = Hex(ekpfs),
                    ["ekpfs_sha256"] = Hex(Crypto.ComputeKeys(contentId, passcode, 1, useSha3: false)),
                    ["seed"] = Hex(seed),
                    ["xts_tweak_key"] = Hex(tweak),
                    ["xts_data_key"] = Hex(data),
                    ["sign_key"] = Hex(ProsperoPfsKeys.DeriveImageSignKey(ekpfs, seed)),
                });
            }
        }

        var keystones = new JsonArray();
        foreach (string passcode in VectorPasscodes)
            keystones.Add(new JsonObject { ["passcode"] = passcode, ["keystone"] = Hex(Crypto.CreateKeystone(passcode)) });

        var flt = new JsonArray();
        foreach (string name in VectorNames)
            flt.Add(new JsonObject { ["name"] = name, ["hash"] = $"0x{ProsperoOuterPfsBuilder.FltPathHash(name):X16}" });

        var innerPaths = new JsonArray();
        foreach (string path in VectorPaths)
            innerPaths.Add(new JsonObject { ["path"] = path, ["hash"] = $"0x{ProsperoPs5FlatPathTable.HashPath(path):X16}" });

        // Outer-image XTS: one 0x10000 data unit per block, sector = block index (| bit 47 when signed).
        var xtsVectors = new JsonArray();
        {
            byte[] ekpfs = ProsperoPfsKeys.DeriveEkpfs(VectorContentIds[0], VectorPasscodes[0]);
            (byte[] tweak, byte[] data) = ProsperoPfsKeys.DeriveImageEncryptionKeys(ekpfs, seed);
            byte[] plain = Pattern(0x10000);
            foreach ((int block, bool signed) in new[] { (0, false), (5, false), (5, true), (10, true) })
            {
                byte[] buffer = (byte[])plain.Clone();
                using var xts = new XtsBlockTransform(data, tweak);
                xts.CryptSector(buffer, ProsperoOuterPfsSignature.BlockSector(block, signed), encrypt: true);
                xtsVectors.Add(new JsonObject
                {
                    ["block"] = block,
                    ["signed"] = signed,
                    ["sector"] = $"0x{ProsperoOuterPfsSignature.BlockSector(block, signed):X}",
                    ["ciphertext_sha256"] = Hex(SHA256.HashData(buffer)),
                    ["ciphertext_head"] = Hex(buffer[..64]),
                });
            }
        }

        // Deterministic PKCS#1 v1.5 wraps (OracleHooks) with the CNT entry-key and metadata moduli.
        var rsa = new JsonArray();
        {
            OracleHooks.OuterSeed = seed;
            using RSA metadata = LibProsperoPkg.Keys.ProsperoKeys.CreateMetadataRsa();
            RSAParameters pub = metadata.ExportParameters(false);
            var moduli = new List<(string Name, byte[] Modulus, byte[] Exponent)>
            {
                ("pkg_public_key_0", CryptoKeys.PkgPublicKeys[0], [0x01, 0x00, 0x01]),
                ("pkg_meta_rsa", pub.Modulus!, pub.Exponent!),
            };
            foreach ((string name, byte[] modulus, byte[] exponent) in moduli)
            {
                foreach (int len in new[] { 0, 32, 64 })
                {
                    byte[] message = Pattern(len);
                    rsa.Add(new JsonObject
                    {
                        ["key"] = name,
                        ["modulus"] = Hex(modulus),
                        ["exponent"] = Hex(exponent),
                        ["seed"] = Hex(seed),
                        ["message"] = Hex(message),
                        ["ciphertext"] = Hex(OracleHooks.RsaPkcs1Encrypt(modulus, message, exponent)!),
                    });
                }
            }
            OracleHooks.OuterSeed = null;
        }

        var sha3 = new JsonArray();
        foreach (int len in new[] { 0, 1, 135, 136, 137, 0x10000 })
        {
            byte[] input = Pattern(len);
            sha3.Add(new JsonObject
            {
                ["length"] = len,
                ["sha3_256"] = Hex(Crypto.Sha3_256(input)),
                ["crc32c"] = $"0x{ProsperoCrc32C.Compute(input):X8}",
            });
        }

        var fself = new JsonArray();
        var dds = new JsonArray();
        foreach (string file in Directory.EnumerateFiles(trees, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string rel = Path.GetRelativePath(trees, file).Replace('\\', '/');
            byte[] bytes = File.ReadAllBytes(file);
            if (ProsperoFself.IsElf(bytes))
                fself.Add(WriteVector(outDir, "fself", rel + ".self", () => ProsperoFself.MakeFself(bytes)));
            else if (rel.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                dds.Add(WriteVector(outDir, "dds", Path.ChangeExtension(rel, ".dds"), () => ProsperoDdsEncoder.EncodePngToDds(bytes), rel));
        }

        var json = new JsonObject
        {
            ["keys"] = keys,
            ["keystone"] = keystones,
            ["flt_path_hash"] = flt,
            ["inner_path_hash"] = innerPaths,
            ["outer_xts"] = xtsVectors,
            ["rsa_pkcs1"] = rsa,
            ["digests"] = sha3,
            ["fself"] = fself,
            ["dds"] = dds,
        };
        File.WriteAllText(Path.Combine(outDir, "vectors.json"), json.ToJsonString(Indented) + "\n");
        return 0;
    }

    private static JsonObject WriteVector(string outDir, string kind, string rel, Func<byte[]> make, string? source = null)
    {
        var node = new JsonObject { ["source"] = source ?? rel[..rel.LastIndexOf('.')], ["vector"] = $"{kind}/{rel}" };
        try
        {
            byte[] bytes = make();
            string path = Path.Combine(outDir, kind, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            node["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes));
        }
        catch (Exception ex)
        {
            node["error"] = $"{ex.GetType().Name}: {ex.Message}";
        }
        return node;
    }

    private static byte[] Pattern(int length)
    {
        var data = new byte[length];
        for (int i = 0; i < length; i++)
            data[i] = (byte)((i * 31 + 7) & 0xFF);
        return data;
    }

    private static string Hex(byte[] data) => Convert.ToHexStringLower(data);
}
