using MkPFS.Core.Compression;
using MkPFS.Core.Exfat;
using MkPFS.Core.PFS;
using MkPFS.Core.PFSC;
using MkPFS.Repair;

namespace MkPFS.Parity;

/// <summary>
/// Seeded mutation fuzzing of the parsers: corrupt real images at random and require that readers only
/// report problems (errors or <see cref="InvalidDataException"/>) and never crash with other exceptions.
/// </summary>
public sealed class MutationFuzzTests
{
    // Default keeps CI fast; set MKPFS_FUZZ_ITERATIONS for longer local runs.
    private static readonly int Iterations =
        int.TryParse(Environment.GetEnvironmentVariable("MKPFS_FUZZ_ITERATIONS"), out int value) && value > 0 ? value : 300;

    public static TheoryData<string, string> Images => new()
    {
        { "raw_app", "out.ffpfs" },
        { "raw_app_signed64", "out.ffpfs" },
        { "raw_fpt_collision", "out.ffpfs" },
        { "raw_app_enc", "out.ffpfs" },
        { "file_app_cpu1", "out.ffpfsc" },
    };

    [Theory]
    [MemberData(nameof(Images))]
    public void Inspector_survives_random_corruption(string caseName, string imageName)
    {
        byte[] original = File.ReadAllBytes(Fixtures.PathOrSkip("goldens", caseName, imageName));
        string path = Path.Combine(Path.GetTempPath(), $"mkpfs-fuzz-{Guid.NewGuid():N}.ffpfs");
        Random random = new(caseName.Aggregate(0x5EED, (hash, c) => unchecked((hash * 31) + c)));
        try
        {
            for (int i = 0; i < Iterations; i++)
            {
                byte[] mutated = Mutate(original, random);
                File.WriteAllBytes(path, mutated);
                string context = $"{caseName} iteration {i}";
                Survives(context, () => PFSInspector.Inspect(path, new PFSInspectOptions { CheckPFSCStreams = true }));
                Survives(context, () => PFSExtractor.OpenInnerExfat(path, null, false)?.Image.Dispose());
                Survives(context, () =>
                {
                    using PFSCImage image = PFSCImage.Open(path);
                    RepairScanner.Scan(image, null, 1, cancellationToken: TestContext.Current.CancellationToken);
                });
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PFSC_reader_and_deflate_inspector_survive_random_corruption()
    {
        byte[] raw = File.ReadAllBytes(Fixtures.PathOrSkip("goldens", "file_app_cpu1", "in.exfat"))[..(65536 * 6)];
        (byte[] payload, _, _) = PFSCEncoder.EncodePayload(raw, new PFSCEncodeOptions());
        Random random = new(42);
        for (int i = 0; i < Iterations; i++)
        {
            byte[] mutated = Mutate(payload, random);
            Survives($"PFSC iteration {i}", () => PFSCReader.DecodePayload(mutated));
            int start = random.Next(0x10000, mutated.Length - 64);
            Survives($"deflate iteration {i}", () => DeflateInspector.InspectZlib(mutated.AsSpan(start, random.Next(2, Math.Min(70000, mutated.Length - start)))));
        }
    }

    [Fact]
    public void Exfat_reader_survives_random_corruption()
    {
        byte[] original = File.ReadAllBytes(Fixtures.PathOrSkip("goldens", "exfat_fpt_collision", "out.exfat"));
        Random random = new(7);
        for (int i = 0; i < Iterations; i++)
        {
            // Corrupt mostly the metadata area (boot region, FAT, bitmap, root directory).
            byte[] mutated = Mutate(original, random, limit: Math.Min(original.Length, 6 * 65536));
            Survives($"exFAT iteration {i}", () =>
            {
                using MemoryStream stream = new(mutated);
                ExfatReader reader = new(stream);
                foreach (ExfatEntry entry in reader.EnumerateFiles().Take(100))
                {
                    foreach (ReadOnlyMemory<byte> _ in reader.ReadFile(entry).Take(4))
                    {
                    }
                }
            });
        }
    }

    private static byte[] Mutate(byte[] original, Random random, int? limit = null)
    {
        byte[] copy = (byte[])original.Clone();
        int range = limit ?? copy.Length;
        int edits = random.Next(1, 9);
        for (int e = 0; e < edits; e++)
        {
            int at = random.Next(range);
            switch (random.Next(4))
            {
                case 0:
                    copy[at] ^= (byte)(1 << random.Next(8));
                    break;
                case 1:
                    copy[at] = (byte)random.Next(256);
                    break;
                case 2:
                    // Interesting values in a 32/64-bit field: zero, all ones, huge, small negative.
                    long[] values = [0, -1, int.MaxValue, long.MaxValue, 65535, 65536, 0x7FFFFFFF00000000];
                    BitConverter.GetBytes(values[random.Next(values.Length)]).AsSpan(0, Math.Min(8, copy.Length - at)).CopyTo(copy.AsSpan(at));
                    break;
                default:
                    int length = Math.Min(random.Next(1, 4096), copy.Length - at);
                    copy.AsSpan(at, length).Clear();
                    break;
            }
        }

        return copy;
    }

    private static void Survives(string context, Action action)
    {
        try
        {
            action();
        }
        catch (InvalidDataException)
        {
            // Expected outcome for malformed input.
        }
        catch (Exception ex)
        {
            Assert.Fail($"{context}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
    }
}
