using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using MkPFS.Build.FPKG;
using MkPFS.Core.Crypto;
using MkPFS.Core.Metadata;
using MkPFS.Core.SELF;

namespace MkPFS.Tests.FPKG;

/// <summary>FPKG plan F3: ELF/SELF handling, param.json generation and the package view of a source folder.</summary>
public sealed class FPKGSourceTests : IDisposable
{
    private const string ContentId = "UP9000-PPSA99999_00-MKPFSTESTS000000";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mkpfs-f3-" + Guid.NewGuid().ToString("N"));

    public FPKGSourceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // Minimal ELF64: header, two program headers (PT_LOAD text + an ignored PT_NOTE), then the text bytes.
    internal static byte[] Elf(byte osAbi = 9, ushort type = 0xFE10, ushort machine = 0x3E)
    {
        byte[] elf = new byte[0x200];
        elf[0] = 0x7F;
        "ELF"u8.CopyTo(elf.AsSpan(1));
        elf[4] = 2;
        elf[5] = 1;
        elf[6] = 1;
        elf[7] = osAbi;
        BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(0x10), type);
        BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(0x12), machine);
        BinaryPrimitives.WriteUInt64LittleEndian(elf.AsSpan(0x20), 0x40);
        BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(0x34), 0x40);
        BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(0x36), 0x38);
        BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(0x38), 2);
        Span<byte> load = elf.AsSpan(0x40, 0x38);
        BinaryPrimitives.WriteUInt32LittleEndian(load, 1);
        BinaryPrimitives.WriteUInt64LittleEndian(load[0x08..], 0x100);
        BinaryPrimitives.WriteUInt64LittleEndian(load[0x20..], 0x100);
        Span<byte> note = elf.AsSpan(0x78, 0x38);
        BinaryPrimitives.WriteUInt32LittleEndian(note, 4);
        BinaryPrimitives.WriteUInt64LittleEndian(note[0x08..], 0x100);
        BinaryPrimitives.WriteUInt64LittleEndian(note[0x20..], 0x10);
        for (int i = 0x100; i < 0x200; i++)
        {
            elf[i] = (byte)i;
        }

        return elf;
    }

    // 8x8 RGB PNG.
    internal static byte[] Png()
    {
        static byte[] Chunk(string kind, byte[] data)
        {
            byte[] typed = [.. Encoding.ASCII.GetBytes(kind), .. data];
            byte[] chunk = new byte[12 + data.Length];
            BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)data.Length);
            typed.CopyTo(chunk, 4);
            BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + data.Length), MkPFS.Core.Util.Crc32.Update(0, typed));
            return chunk;
        }

        byte[] ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, 8);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), 8);
        ihdr[8] = 8;
        ihdr[9] = 2;
        using MemoryStream raw = new();
        using (ZLibStream z = new(raw, CompressionLevel.Optimal, leaveOpen: true))
        {
            for (int y = 0; y < 8; y++)
            {
                z.WriteByte(0);
                for (int x = 0; x < 8; x++)
                {
                    z.Write([(byte)(x * 30), (byte)(y * 30), 128]);
                }
            }
        }

        return [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, .. Chunk("IHDR", ihdr), .. Chunk("IDAT", raw.ToArray()), .. Chunk("IEND", [])];
    }

    private void Write(string rel, byte[] data)
    {
        string path = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
    }

    private Dictionary<string, string> SourceHashes() => Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
        .ToDictionary(f => f, f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));

    private FPKGSource Prepare() => FPKGSource.Prepare(new FPKGSourceOptions { SourceDir = _root, ContentId = ContentId, Title = "Test" });

    [Fact]
    public void Normalize_fixes_machine_abi_and_type_only()
    {
        byte[] elf = Elf(osAbi: 0, type: 0, machine: 0);
        ELFNormalization changed = ELFHeader.NormalizeForModule(elf);
        Assert.Equal(new ELFNormalization(true, true, true), changed);
        Assert.Equal(9, elf[7]);
        Assert.Equal(2, ELFHeader.Type(elf));
        Assert.Equal(ELFHeader.MachineX86_64, ELFHeader.Machine(elf));
        Assert.False(ELFHeader.NormalizeForModule(elf).Changed);
        Assert.Equal(Elf().AsSpan(0x40).ToArray(), elf.AsSpan(0x40).ToArray());
    }

    [Fact]
    public void Fake_SELF_parses_back_with_fake_authority_and_digest()
    {
        byte[] elf = Elf();
        byte[] self = SELFFile.MakeFake(elf);
        SELFImage image = SELFFile.Parse(self);
        Assert.Equal(SELFFile.FakeExecutableAuthorityId, image.AuthorityId);
        Assert.Equal(SHA256.HashData(elf), image.ElfDigest);
        Assert.Equal((ulong)self.Length, image.FileSize);
        Assert.Equal(2, image.Segments.Count);
        Assert.Equal(elf.AsSpan(0x100, 0x100).ToArray(), self.AsSpan((int)image.Segments[1].FileOffset, 0x100).ToArray());
        Assert.Equal((ModuleKind.FakeSELF, SELFFile.FakeExecutableAuthorityId), ModuleClassifier.Classify(self));
        Assert.Equal(ModuleKind.RawELF, ModuleClassifier.Classify(elf).Kind);
        Assert.Equal(ModuleKind.NotExecutable, ModuleClassifier.Classify("text"u8).Kind);
    }

    [Fact]
    public void Fake_SELF_rejects_32_bit_and_segmentless_ELFs()
    {
        byte[] elf32 = Elf();
        elf32[4] = 1;
        Assert.Throws<ArgumentException>(() => SELFFile.MakeFake(elf32));
        byte[] empty = Elf();
        BinaryPrimitives.WriteUInt32LittleEndian(empty.AsSpan(0x40), 4);
        Assert.Throws<ArgumentException>(() => SELFFile.MakeFake(empty));
    }

    [Fact]
    public void Generated_param_json_is_fixed_text()
    {
        string json = Encoding.UTF8.GetString(PS5ParamJson.Create(ContentId, "My Game", "01.02", "standard"));
        Assert.Equal(
            "{\n  \"applicationCategoryType\": 0,\n  \"applicationDrmType\": \"standard\",\n  \"attribute\": 0,\n  \"attribute2\": 0,\n" +
            "  \"attribute3\": 0,\n  \"conceptId\": \"99999\",\n  \"contentId\": \"UP9000-PPSA99999_00-MKPFSTESTS000000\",\n" +
            "  \"contentVersion\": \"01.020.000\",\n  \"downloadDataSize\": 0,\n  \"localizedParameters\": {\n" +
            "    \"defaultLanguage\": \"en-US\",\n    \"en-US\": {\n      \"titleName\": \"My Game\"\n    }\n  },\n" +
            "  \"masterVersion\": \"01.02\",\n  \"requiredSystemSoftwareVersion\": \"0x0000000000000000\",\n" +
            "  \"sdkVersion\": \"0x0000000000000000\",\n  \"titleId\": \"PPSA99999\"\n}\n",
            json);
        Assert.Throws<ArgumentException>(() => PS5ParamJson.Create("bad", null, "01.00", "free"));
        Assert.Throws<ArgumentException>(() => PS5ParamJson.Create(ContentId, null, "1.0", "free"));
        Assert.Throws<ArgumentException>(() => PS5ParamJson.Create(ContentId, null, "01.00", "paid"));
    }

    [Fact]
    public void Prepare_fake_signs_modules_and_generates_system_files_without_touching_the_source()
    {
        Write("eboot.bin", Elf());
        Write("modules/lib.prx", Elf(type: 0xFE18));
        Write("data/blob.bin", [1, 2, 3]);
        Write("sce_sys/icon0.png", Png());
        Write("sce_sys/changeinfo/changeinfo.xml", "<changeinfo/>"u8.ToArray());
        Write("sce_sys/playgo-chunk.dat", [9]);
        Dictionary<string, string> before = SourceHashes();

        FPKGSource view = Prepare();

        Assert.Equal(before, SourceHashes());
        Assert.Empty(view.Errors);
        Assert.Equal(
            ["data/blob.bin", "eboot.bin", "modules/lib.prx", "sce_sys/about/right.sprx", "sce_sys/keystone", "sce_sys/pfs-version.dat"],
            view.InnerFiles.Select(f => f.Path));
        Assert.Equal(["changeinfo/changeinfo.xml", "icon0.dds", "icon0.png", "param.json"], view.Entries.Select(e => e.Path));
        Assert.All(view.Modules, m => Assert.Equal((ModuleKind.RawELF, ModuleKind.FakeSELF), (m.SourceKind, m.PackedKind)));
        FPKGInput eboot = view.InnerFiles.Single(f => f.Path == "eboot.bin");
        Assert.Equal(FPKGInputOrigin.FakeSigned, eboot.Origin);
        Assert.Equal(SELFFile.MakeFake(Elf()), eboot.ReadAll());
        Assert.Equal(PS5Keys.Keystone(new string('0', 32)), view.InnerFiles.Single(f => f.Path == "sce_sys/keystone").ReadAll());
        Assert.Equal("01.000.000"u8.ToArray(), view.InnerFiles.Single(f => f.Path == "sce_sys/pfs-version.dat").ReadAll());
        Assert.Contains(view.Warnings, w => w.Contains("sce_sys/playgo-chunk.dat", StringComparison.Ordinal));
        Assert.Equal(148 + 64, view.Entries.Single(e => e.Path == "icon0.dds").Size);
    }

    [Fact]
    public void Supplied_param_json_keystone_and_SELF_are_kept()
    {
        byte[] self = SELFFile.MakeFake(Elf());
        byte[] keystone = new byte[PS5Keys.KeystoneSize];
        keystone[0] = 7;
        byte[] param = "{\"contentVersion\": \"02.000.000\"}"u8.ToArray();
        Write("eboot.bin", self);
        Write("sce_sys/keystone", keystone);
        Write("sce_sys/param.json", param);

        FPKGSource view = Prepare();

        Assert.Empty(view.Errors);
        Assert.Equal(self, view.InnerFiles.Single(f => f.Path == "eboot.bin").ReadAll());
        Assert.Equal(keystone, view.InnerFiles.Single(f => f.Path == "sce_sys/keystone").ReadAll());
        Assert.Equal(param, view.Entries.Single(e => e.Path == "param.json").ReadAll());
        Assert.Equal("02.000.000"u8.ToArray(), view.InnerFiles.Single(f => f.Path == "sce_sys/pfs-version.dat").ReadAll());
        Assert.Equal(FPKGInputOrigin.Source, view.InnerFiles.Single(f => f.Path == "eboot.bin").Origin);
    }

    [Fact]
    public void Prepare_reports_what_will_not_run()
    {
        Write("sce_sys/param.sfo", [0]);
        Write("données/x.bin", [0]);
        byte[] genuine = SELFFile.MakeFake(Elf(), new FakeSELFOptions { AuthorityId = 0x4500000000000001 });
        Write("module.sprx", genuine);

        FPKGSource view = Prepare();

        Assert.Contains(view.Errors, e => e.Contains("param.sfo", StringComparison.Ordinal));
        Assert.Contains(view.Errors, e => e.StartsWith("non-ASCII path", StringComparison.Ordinal));
        Assert.Contains(view.Errors, e => e == "no eboot.bin at the application root");
        Assert.Contains(view.Errors, e => e.StartsWith("module.sprx will not start", StringComparison.Ordinal));
        Assert.Equal(ModuleKind.GenuineSELF, view.Modules.Single().PackedKind);
    }
}
