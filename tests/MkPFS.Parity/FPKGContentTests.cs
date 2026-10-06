using MkPFS.Build.FPKG;
using MkPFS.Core.Crypto;
using MkPFS.Core.Images;
using MkPFS.Core.PFS.PS5;
using MkPFS.Core.PKG;
using MkPFS.Core.SELF;

namespace MkPFS.Parity;

/// <summary>
/// FPKG plan F3/F5: the content MkPFS generates for a package (fake SELF, keystone, right.sprx, DDS, the package
/// view of a source folder) against PS5PkgTool's packages of the same fixture trees (<c>tools/oracle-ppt</c>,
/// stored mode).
/// </summary>
public sealed class FPKGContentTests
{
    private const string Passcode = PPTCorpus.Passcode;

    [Theory]
    [MemberData(nameof(PPTCorpus.StoredTrees), MemberType = typeof(PPTCorpus))]
    public void Generated_inner_files_match_PS5PkgTool(string tree)
    {
        string source = PPTCorpus.TreeDir(tree);
        using PS5Package package = Open(tree);
        foreach (PS5InnerFile file in package.Inner.ReadTree().Where(f => f.Size >= 0))
        {
            byte[] packed = Read(package, file);
            string original = Path.Combine(source, file.Path);
            byte[]? expected = file.Path switch
            {
                "sce_sys/keystone" => PS5Keys.Keystone(Passcode),
                "sce_sys/about/right.sprx" => FPKGSource.RightSprx(),
                _ when File.Exists(original) && SELFFile.IsSELF(packed) => SELFFile.MakeFake(File.ReadAllBytes(original)),
                _ => null,
            };

            if (expected is not null)
            {
                Assert.True(expected.AsSpan().SequenceEqual(packed), file.Path);
            }
        }
    }

    [Theory]
    [MemberData(nameof(PPTCorpus.StoredTrees), MemberType = typeof(PPTCorpus))]
    public void DDS_matches_PS5PkgTool(string tree)
    {
        using PS5Package package = Open(tree);
        foreach ((string png, string dds) in CNTEntryNames.DdsMedia)
        {
            if (package.Package.FindEntry(CNTEntryNames.NameToId[png]) is { } image)
            {
                byte[] expected = package.Package.ReadEntry(package.Package.FindEntry(CNTEntryNames.NameToId[dds])!);
                Assert.True(expected.AsSpan().SequenceEqual(DDSEncoder.EncodePngToDds(package.Package.ReadEntry(image))), dds);
            }
        }
    }

    [Theory]
    [MemberData(nameof(PPTCorpus.StoredTrees), MemberType = typeof(PPTCorpus))]
    public void Package_view_of_the_source_matches_PS5PkgTool(string tree)
    {
        using PS5Package package = Open(tree);
        FPKGSource view = FPKGSource.Prepare(new FPKGSourceOptions { SourceDir = PPTCorpus.TreeDir(tree), ContentId = package.Package.CNT.ContentId });
        if (tree == "app_unicode")
        {
            // PS5PkgTool writes non-ASCII characters as '?'; MkPFS refuses the paths instead.
            Assert.All(view.Errors, e => Assert.StartsWith("non-ASCII path", e, StringComparison.Ordinal));
            Assert.Equal(2, view.Errors.Count);
            return;
        }

        Assert.Empty(view.Errors);
        FPKGComparison result = FPKGSourceComparer.Compare(package, view);
        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
    }

    private static PS5Package Open(string tree) =>
        PS5Package.Open(PPTCorpus.StoredPackage(tree), Passcode);

    private static byte[] Read(PS5Package package, PS5InnerFile file)
    {
        using MemoryStream bytes = new();
        package.Inner.CopyFile(file, bytes);
        return bytes.ToArray();
    }
}
