using MkPFS.Core.Util;

namespace MkPFS.Tests.Util;

public sealed class CpuTopologyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mkpfs-cpu-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void Cpu(int index, int package, int core, bool online = true)
    {
        string topology = Directory.CreateDirectory(Path.Combine(_root, $"cpu{index}", "topology")).FullName;
        File.WriteAllText(Path.Combine(topology, "core_id"), $"{core}\n");
        File.WriteAllText(Path.Combine(topology, "physical_package_id"), $"{package}\n");
        if (index > 0)
        {
            File.WriteAllText(Path.Combine(_root, $"cpu{index}", "online"), online ? "1\n" : "0\n");
        }
    }

    [Fact]
    public void Linux_counts_cores_not_hardware_threads()
    {
        // 16 cores with two threads each: 32 logical processors.
        for (int thread = 0; thread < 32; thread++)
        {
            Cpu(thread, package: 0, core: thread % 16);
        }

        Directory.CreateDirectory(Path.Combine(_root, "cpufreq"));
        Assert.Equal(16, CpuTopology.LinuxCores(_root));
    }

    [Fact]
    public void Linux_counts_each_package_and_skips_offline_cpus()
    {
        Cpu(0, package: 0, core: 0);
        Cpu(1, package: 1, core: 0); // same core id on the second socket
        Cpu(2, package: 1, core: 1, online: false);

        Assert.Equal(2, CpuTopology.LinuxCores(_root));
    }

    [Fact]
    public void Physical_cores_stay_within_the_logical_count() =>
        Assert.InRange(CpuTopology.PhysicalCores, 1, Environment.ProcessorCount);
}
