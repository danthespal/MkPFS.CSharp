using System.Globalization;
using System.Runtime.InteropServices;

namespace MkPFS.Core.Util;

/// <summary>
/// Physical CPU cores of this machine. <see cref="Environment.ProcessorCount"/> counts logical processors, which is
/// twice the core count on a CPU with SMT (Hyper-Threading); .NET has no API for cores, so each OS is asked directly.
/// </summary>
public static partial class CpuTopology
{
    private static readonly Lazy<int> Cores = new(Detect);

    /// <summary>Physical cores, at least 1 and at most <see cref="Environment.ProcessorCount"/>; the logical processor
    /// count when the OS does not say.</summary>
    public static int PhysicalCores => Cores.Value;

    private static int Detect()
    {
        int logical = Environment.ProcessorCount;
        int? cores = null;
        try
        {
            cores = OperatingSystem.IsWindows() ? WindowsCores()
                : OperatingSystem.IsLinux() ? LinuxCores("/sys/devices/system/cpu")
                : OperatingSystem.IsMacOS() ? MacCores()
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException
            or EntryPointNotFoundException or FormatException)
        {
            cores = null;
        }

        return cores is > 0 ? Math.Min(cores.Value, logical) : logical;
    }

    /// <summary>
    /// Distinct (package, core) pairs of the online CPUs under <paramref name="cpuRoot"/>
    /// (<c>/sys/devices/system/cpu</c>), or <see langword="null"/> when the topology is not exposed.
    /// </summary>
    /// <param name="cpuRoot">sysfs CPU folder.</param>
    /// <returns>Core count, or <see langword="null"/>.</returns>
    internal static int? LinuxCores(string cpuRoot)
    {
        HashSet<(string Package, string Core)> cores = [];
        foreach (string cpu in Directory.EnumerateDirectories(cpuRoot, "cpu*"))
        {
            string name = Path.GetFileName(cpu);
            if (!int.TryParse(name.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                continue; // cpufreq, cpuidle
            }

            string online = Path.Combine(cpu, "online");
            if (File.Exists(online) && File.ReadAllText(online).Trim() == "0")
            {
                continue;
            }

            string core = Path.Combine(cpu, "topology", "core_id");
            string package = Path.Combine(cpu, "topology", "physical_package_id");
            if (!File.Exists(core))
            {
                return null;
            }

            cores.Add((File.Exists(package) ? File.ReadAllText(package).Trim() : "0", File.ReadAllText(core).Trim()));
        }

        return cores.Count > 0 ? cores.Count : null;
    }

    // One SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX record per core for RelationProcessorCore.
    private static int? WindowsCores()
    {
        const int relationProcessorCore = 0;
        uint length = 0;
        GetLogicalProcessorInformationEx(relationProcessorCore, null, ref length);
        if (length == 0)
        {
            return null;
        }

        byte[] buffer = new byte[length];
        if (!GetLogicalProcessorInformationEx(relationProcessorCore, buffer, ref length))
        {
            return null;
        }

        int count = 0;
        for (int offset = 0; offset + 8 <= length;)
        {
            int relationship = BitConverter.ToInt32(buffer, offset);
            int size = BitConverter.ToInt32(buffer, offset + 4);
            if (size <= 0)
            {
                break;
            }

            if (relationship == relationProcessorCore)
            {
                count++;
            }

            offset += size;
        }

        return count;
    }

    private static int? MacCores()
    {
        int value = 0;
        nint size = sizeof(int);
        return sysctlbyname("hw.physicalcpu", ref value, ref size, 0, 0) == 0 ? value : null;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLogicalProcessorInformationEx(int relationshipType, byte[]? buffer, ref uint returnedLength);

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int sysctlbyname(string name, ref int oldValue, ref nint oldLength, nint newValue, nint newLength);
}
