using MkPFS.Build.FPKG;
using MkPFS.Core.PFS.PS5;
using MkPFS.Core.PKG;

namespace MkPFS.Parity;

/// <summary>
/// FPKG plan F6/F7: packages MkPFS builds against PS5PkgTool's stored packages of the same trees
/// (<c>tools/oracle-ppt</c>, same content id, passcode, seed and clock).
/// </summary>
public sealed class FPKGPackageTests
{
    /// <summary>
    /// Whole-package byte parity. Two differences are by design and patched back through test hooks: the RSA
    /// ciphertexts (random PKCS#1 padding; PS5PkgTool's are taken from the same offsets of its package) and the
    /// last zero-gap fill block (PS5PkgTool declares a wrong length; MkPFS writes the exact one).
    /// </summary>
    [Theory]
    [MemberData(nameof(PPTCorpus.BuildableTrees), MemberType = typeof(PPTCorpus))]
    public void Stored_package_matches_PS5PkgTool(string tree)
    {
        byte[] golden = File.ReadAllBytes(PPTCorpus.StoredPackage(tree));
        List<byte[]> ciphertexts = [];
        byte[] first = Build(tree, c =>
        {
            ciphertexts.Add(c);
            return c;
        }, engineGapFill: true);

        // Map each RSA call, in order, to PS5PkgTool's bytes at the offset where MkPFS wrote its own.
        int[] offsets = [.. ciphertexts.Select(c => first.AsSpan().IndexOf(c))];
        int call = 0;
        byte[] swapped = Build(tree, c =>
        {
            int at = offsets[call++];
            return at < 0 ? c : golden.AsSpan(at, c.Length).ToArray();
        }, engineGapFill: true);

        Assert.Equal(golden.Length, swapped.Length);
        int diff = golden.AsSpan().CommonPrefixLength(swapped);
        Assert.True(diff == golden.Length, $"first difference at 0x{diff:X}");
    }

    [Theory]
    [MemberData(nameof(PPTCorpus.BuildableTrees), MemberType = typeof(PPTCorpus))]
    public void Inner_image_matches_PS5PkgTool_except_the_exact_gap_fill(string tree)
    {
        string path = Path.Combine(Path.GetTempPath(), "mkpfs-f6-" + Guid.NewGuid().ToString("N") + ".pkg");
        try
        {
            File.WriteAllBytes(path, Build(tree, null, engineGapFill: false));
            using PS5Package mine = PS5Package.Open(path, PPTCorpus.Passcode);
            using PS5Package engine = PS5Package.Open(PPTCorpus.StoredPackage(tree), PPTCorpus.Passcode);
            Assert.Equal(engine.Naps, mine.Naps);

            byte[] a = engine.Outer.ReadFile(engine.Outer.Files[PS5Package.InnerImageName]);
            byte[] b = mine.Outer.ReadFile(mine.Outer.Files[PS5Package.InnerImageName]);
            Assert.Equal(a.Length, b.Length);

            // Only the last gap chunk's 16 fill bytes may differ.
            PS5Chunk last = engine.Inner.Chunks.Last(c => c.Kind == PS5ChunkKind.Fill);
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    Assert.InRange(i, last.StoredOffset, last.StoredOffset + 15);
                }
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [MemberData(nameof(PPTCorpus.BuildableTrees), MemberType = typeof(PPTCorpus))]
    public void Package_passes_every_check_and_matches_its_source(string tree)
    {
        string path = Path.Combine(Path.GetTempPath(), "mkpfs-f7-" + Guid.NewGuid().ToString("N") + ".pkg");
        try
        {
            File.WriteAllBytes(path, Build(tree, null, engineGapFill: false));
            using PS5Package package = PS5Package.Open(path, PPTCorpus.Passcode);
            Assert.All(PS5PackageVerifier.Verify(package), c => Assert.True(c.Passed, $"{c.Name}: {c.Detail}"));

            FPKGSource view = FPKGSource.Prepare(new FPKGSourceOptions { SourceDir = PPTCorpus.TreeDir(tree), ContentId = PPTCorpus.ContentId });
            FPKGComparison result = FPKGSourceComparer.Compare(package, view);
            Assert.Empty(result.Errors);
            Assert.Empty(result.Warnings);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Kraken mode (PS5PkgTool <c>auto</c>): the encoders differ, so the package matches in structure — the same chunk
    /// boundaries, the same compress-or-store decision for every chunk, the same naps header word — and its files
    /// read back equal to PS5PkgTool's.
    /// </summary>
    [Theory]
    [MemberData(nameof(PPTCorpus.BuildableTrees), MemberType = typeof(PPTCorpus))]
    public void Auto_package_matches_PS5PkgTool_in_structure(string tree)
    {
        string path = Path.Combine(Path.GetTempPath(), "mkpfs-f8-" + Guid.NewGuid().ToString("N") + ".pkg");
        try
        {
            File.WriteAllBytes(path, Build(tree, null, engineGapFill: false, compress: true));
            using PS5Package mine = PS5Package.Open(path, PPTCorpus.Passcode);
            using PS5Package engine = PS5Package.Open(Fixtures.PathOrSkip("fpkg", "ppt", tree + "_auto", "out.pkg"), PPTCorpus.Passcode);
            Assert.All(PS5PackageVerifier.Verify(mine), c => Assert.True(c.Passed, $"{c.Name}: {c.Detail}"));
            Assert.Equal(engine.Naps.AsSpan(0, 8).ToArray(), mine.Naps.AsSpan(0, 8).ToArray());

            static string Shape(PS5Chunk c) => $"{c.LogicalOffset:X}+{c.Length:X} {(c.Kind == PS5ChunkKind.Raw ? "raw" : "packed")}";
            Assert.Equal(engine.Inner.Chunks.Select(Shape), mine.Inner.Chunks.Select(Shape));

            List<PS5InnerFile> engineFiles = engine.Inner.ReadTree();
            foreach (PS5InnerFile file in mine.Inner.ReadTree().Where(f => (f.Mode & 0xF000) == 0x8000))
            {
                using MemoryStream a = new();
                using MemoryStream b = new();
                engine.Inner.CopyFile(engineFiles.Single(f => f.Path == file.Path), a);
                mine.Inner.CopyFile(file, b);
                Assert.True(a.ToArray().AsSpan().SequenceEqual(b.ToArray()), file.Path);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Without_a_seed_equal_inputs_give_equal_packages()
    {
        string dir = PPTCorpus.TreeDir("app_multi");
        string a = Path.Combine(Path.GetTempPath(), "mkpfs-seed-" + Guid.NewGuid().ToString("N") + ".pkg");
        string b = Path.Combine(Path.GetTempPath(), "mkpfs-seed-" + Guid.NewGuid().ToString("N") + ".pkg");
        try
        {
            FPKGBuildOptions options = new() { SourceDir = dir, ContentId = PPTCorpus.ContentId, Time = PPTCorpus.Time, Compress = true };
            FPKGBuilder.Build(options, a);
            FPKGBuilder.Build(options, b);
            Assert.Equal(File.ReadAllBytes(a), File.ReadAllBytes(b));
            Assert.False(File.Exists(a + ".tmp"));
        }
        finally
        {
            File.Delete(a);
            File.Delete(b);
        }
    }

    private static byte[] Build(string tree, Func<byte[], byte[]>? rsa, bool engineGapFill, bool compress = false)
    {
        string path = Path.Combine(Path.GetTempPath(), "mkpfs-fpkg-" + Guid.NewGuid().ToString("N") + ".pkg");
        FPKGBuilder.Build(new FPKGBuildOptions
        {
            SourceDir = PPTCorpus.TreeDir(tree),
            ContentId = PPTCorpus.ContentId,
            Passcode = PPTCorpus.Passcode,
            Seed = PPTCorpus.Seed,
            Time = PPTCorpus.Time,
            RSAOutput = rsa,
            EngineGapFill = engineGapFill,
            Compress = compress,
        }, path);
        try
        {
            return File.ReadAllBytes(path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
