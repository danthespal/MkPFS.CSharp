using MkPFS.Core.Util;

namespace MkPFS.Tests.Util;

/// <summary>Every command uses at most all logical processors but two.</summary>
public sealed class CpuCoresTests
{
    [Fact]
    public void Auto_keeps_two_logical_processors_free_and_caps_every_count()
    {
        Assert.Equal(Math.Max(1, Environment.ProcessorCount - 2), CpuCores.Max);
        Assert.Equal(CpuCores.Max, CpuCores.Resolve(0));
        Assert.Equal(CpuCores.Max, CpuCores.Resolve(-3));
        Assert.Equal(1, CpuCores.Resolve(1));
        Assert.Equal(CpuCores.Max, CpuCores.Resolve(long.MaxValue));
    }
}
