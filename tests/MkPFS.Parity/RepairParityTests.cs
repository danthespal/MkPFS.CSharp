using MkPFS.Core.PFS;
using MkPFS.Core.PFSC;
using MkPFS.Repair;

namespace MkPFS.Parity;

/// <summary>Repair against images built by Python MkPFS: zlib images are clean, ISA-L images get fixed.</summary>
public sealed class RepairParityTests
{
    public static TheoryData<string> ZlibImages => ["file_app_cpu1", "file_app_cpu4", "file_app_user_flags", "file_many_files"];

    [Theory]
    [MemberData(nameof(ZlibImages))]
    public void Zlib_built_images_need_no_repair(string caseName)
    {
        string image = Copy(Fixtures.PathOrSkip("goldens", caseName, "out.ffpfsc"), out string dir);
        try
        {
            PFSCRepairResult result = PFSCRepair.Run(image, new PFSCRepairOptions { ScanOnly = true, Workers = 4 }, TestContext.Current.CancellationToken);

            Assert.Equal(RepairStatus.Noop, result.Status);
            Assert.Equal(0, result.RiskyBlocks);
            Assert.True(result.CompressedBlocks > 0);
            Assert.Equal(0, result.Slack!.Value.FixedBytes);
            Assert.True(result.Slack.Value.Applicable, result.Slack.Value.Reason);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(RepairMode.Copy, false)]
    [InlineData(RepairMode.InPlace, false)]
    [InlineData(RepairMode.Copy, true)]
    public void ISAL_built_image_is_repaired_and_verifies_against_its_source(RepairMode mode, bool recompress)
    {
        string source = Fixtures.PathOrSkip("goldens", "file_app_isal", "in.exfat");
        string image = Copy(Fixtures.PathOrSkip("goldens", "file_app_isal", "out.ffpfsc"), out string dir);
        try
        {
            PFSCRepairResult result = PFSCRepair.Run(image, new PFSCRepairOptions { Mode = mode, Recompress = recompress, Workers = 4 }, TestContext.Current.CancellationToken);

            Assert.Equal(RepairStatus.Repaired, result.Status);
            Assert.True(result.MarkedBlocks.Count > 0);
            Assert.Equal(mode, result.AppliedMode);
            PFSInspection inspection = PFSInspector.Inspect(image, new PFSInspectOptions
            {
                Source = SourceTree.FromSingleFile(source, "in.exfat"),
                Checklist = ChecklistMode.Never,
            });
            Assert.Empty(inspection.Errors);
            Assert.Equal(1, inspection.CheckedFiles);

            PFSCRepairResult again = PFSCRepair.Run(image, new PFSCRepairOptions { ScanOnly = true }, TestContext.Current.CancellationToken);
            Assert.Equal(RepairStatus.Noop, again.Status);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Copy_and_in_place_give_identical_bytes_on_the_ISAL_image()
    {
        string golden = Fixtures.PathOrSkip("goldens", "file_app_isal", "out.ffpfsc");
        string a = Copy(golden, out string dirA);
        string b = Copy(golden, out string dirB);
        try
        {
            PFSCRepair.Run(a, new PFSCRepairOptions { Mode = RepairMode.Copy }, TestContext.Current.CancellationToken);
            PFSCRepair.Run(b, new PFSCRepairOptions { Mode = RepairMode.InPlace }, TestContext.Current.CancellationToken);

            Assert.Equal(File.ReadAllBytes(a), File.ReadAllBytes(b));
        }
        finally
        {
            Directory.Delete(dirA, recursive: true);
            Directory.Delete(dirB, recursive: true);
        }
    }

    [Theory]
    [InlineData("file_app_enc")]
    [InlineData("file_app_nc")]
    [InlineData("raw_app")]
    public void Unsupported_images_are_rejected(string caseName)
    {
        string path = Directory.GetFiles(Fixtures.PathOrSkip("goldens", caseName), "out.*")[0];

        Assert.Throws<InvalidDataException>(() => PFSCImage.Open(path).Dispose());
    }

    private static string Copy(string golden, out string dir)
    {
        dir = Path.Combine(Path.GetTempPath(), $"mkpfs-repair-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "image.ffpfsc");
        File.Copy(golden, path);
        return path;
    }
}
