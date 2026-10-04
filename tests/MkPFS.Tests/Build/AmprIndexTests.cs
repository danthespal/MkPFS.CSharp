using System.Buffers.Binary;
using MkPFS.Build;
using MkPFS.Cli;
using MkPFS.Cli.Output;
using MkPFS.Core.Diagnostics;

namespace MkPFS.Tests.Build;

public sealed class AmprIndexTests
{
    private sealed class ListLog : IMkPFSLog
    {
        public List<string> Lines { get; } = [];

        public void Log(LogLevel level, string message, LogIcon icon = LogIcon.None) => Lines.Add($"{level}: {message}");
    }

    private static string AmprTree(TempDir dir)
    {
        string source = dir.Dir("src");
        dir.File("src/fakelib/libSceAmpr.sprx", "sprx");
        dir.File("src/eboot.bin", "eboot");
        dir.File("src/Data/Level1.pak", new string('x', 1234));
        dir.File("src/data2/b.bin", "b");
        dir.File("src/.DS_Store", "ignored");
        File.SetLastWriteTimeUtc(Path.Combine(source, "eboot.bin"), DateTime.UnixEpoch.AddSeconds(1_600_000_000.75));
        return source;
    }

    [Fact]
    public void Index_lists_every_file_sorted_by_lower_case_path_with_size_and_mtime()
    {
        using TempDir dir = new();
        string source = AmprTree(dir);
        ListLog log = new();

        string? index = AmprIndex.Ensure(source, log);

        Assert.Equal(Path.Combine(source, AmprIndex.IndexName), index);
        Assert.Equal(["Info: Detected fakelib/libSceAmpr.sprx; generating ampr_emu.index...", "Info: Generated ampr_emu.index with 4 entries"], log.Lines);
        List<AmprIndex.Row> rows = AmprIndex.ReadRows(File.ReadAllBytes(index!));
        Assert.Equal(["/app0/Data/Level1.pak", "/app0/data2/b.bin", "/app0/eboot.bin", "/app0/fakelib/libSceAmpr.sprx"], rows.Select(r => r.Path));
        Assert.Equal(1234, rows[0].Size);
        Assert.Equal(1_600_000_000, rows[2].MTime); // whole seconds, truncated
    }

    [Fact]
    public void Failed_build_removes_its_temporary_index()
    {
        using TempDir dir = new();
        string source = AmprTree(dir);
        string output = dir.Dir("blocked.index");

        Exception? error = Record.Exception(() => AmprIndex.Build(source, output));

        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.False(File.Exists(output + ".tmp"));
    }

    [Fact]
    public void Hash_table_finds_every_row_by_probing()
    {
        using TempDir dir = new();
        string source = AmprTree(dir);
        AmprIndex.Ensure(source, new ListLog());
        byte[] data = File.ReadAllBytes(Path.Combine(source, AmprIndex.IndexName));
        long hashOffset = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(32));
        int slots = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(44));
        List<AmprIndex.Row> rows = AmprIndex.ReadRows(data);

        Assert.Equal(AmprIndex.HashSlotCount(rows.Count), slots);
        Assert.Equal(0, hashOffset % AmprIndex.HashSlotSize);
        for (int i = 0; i < rows.Count; i++)
        {
            ulong hash = AmprIndex.PathHash(rows[i].Path.ToUpperInvariant()); // lookups are case-insensitive
            int pos = (int)(hash & (ulong)(slots - 1));
            while (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)hashOffset + (pos * 16) + 8)) != i + 1)
            {
                Assert.NotEqual(0u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)hashOffset + (pos * 16) + 8)));
                pos = (pos + 1) & (slots - 1);
            }

            Assert.Equal(hash, BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan((int)hashOffset + (pos * 16))));
        }
    }

    // The ampr_emu resolver (compact_index_hash_key): FNV-1a 64 over UTF-8 bytes, '\' as '/', only ASCII A..Z folded.
    private static ulong RuntimeHash(string path)
    {
        ulong hash = 1469598103934665603UL;
        foreach (byte b in System.Text.Encoding.UTF8.GetBytes(path))
        {
            byte c = b == (byte)'\\' ? (byte)'/' : b is >= 0x41 and <= 0x5A ? (byte)(b + 0x20) : b;
            hash ^= c;
            hash = unchecked(hash * 1099511628211UL);
        }

        return hash == 0 ? 1 : hash;
    }

    [Fact]
    public void Non_ascii_paths_use_the_console_resolver_hash_and_folding()
    {
        using TempDir dir = new();
        string source = AmprTree(dir);
        dir.File("src/Données/Écran.bin", "e");
        dir.File("src/zz/ünité.bin", "u");
        AmprIndex.Ensure(source, new ListLog());
        byte[] data = File.ReadAllBytes(Path.Combine(source, AmprIndex.IndexName));
        long hashOffset = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(32));
        int slots = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(44));
        List<AmprIndex.Row> rows = AmprIndex.ReadRows(data);

        Assert.Contains(rows, r => r.Path == "/app0/Données/Écran.bin");
        Assert.Contains(rows, r => r.Path == "/app0/zz/ünité.bin");
        Assert.NotEqual(AmprIndex.KeyFor("/app0/É"), AmprIndex.KeyFor("/app0/é")); // only ASCII folds
        for (int i = 0; i < rows.Count; i++)
        {
            // The game asks with ASCII letters upper-cased; the resolver must still land on this row.
            string asked = new([.. rows[i].Path.Select(c => c is >= 'a' and <= 'z' ? char.ToUpperInvariant(c) : c)]);
            ulong hash = RuntimeHash(asked);
            Assert.Equal(RuntimeHash(rows[i].Path), AmprIndex.PathHash(rows[i].Path));
            int pos = (int)(hash & (ulong)(slots - 1));
            while (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)hashOffset + (pos * 16) + 8)) != i + 1)
            {
                Assert.NotEqual(0u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)hashOffset + (pos * 16) + 8)));
                pos = (pos + 1) & (slots - 1);
            }
        }

        // Records are sorted by the ASCII-folded UTF-8 bytes, the order the resolver's binary search expects.
        List<byte[]> keys = [.. rows.Select(r => System.Text.Encoding.UTF8.GetBytes(AmprIndex.KeyFor(r.Path)))];
        for (int i = 1; i < keys.Count; i++)
        {
            Assert.True(keys[i - 1].AsSpan().SequenceCompareTo(keys[i]) < 0, $"{rows[i - 1].Path} !< {rows[i].Path}");
        }
    }

    [Fact]
    public void Emulator_trace_and_log_files_are_not_indexed()
    {
        using TempDir dir = new();
        string source = AmprTree(dir);
        dir.File("src/ampr_commands.bin", "trace");
        dir.File("src/APR_EMU.LOG", "log");
        dir.File("src/data2/ampr_commands.bin", "kept below the root");

        AmprIndex.Ensure(source, new ListLog());

        List<AmprIndex.Row> rows = AmprIndex.ReadRows(File.ReadAllBytes(Path.Combine(source, AmprIndex.IndexName)));
        Assert.Equal(["/app0/Data/Level1.pak", "/app0/data2/ampr_commands.bin", "/app0/data2/b.bin", "/app0/eboot.bin", "/app0/fakelib/libSceAmpr.sprx"], rows.Select(r => r.Path));
    }

    [Fact]
    public void Fakelib2_also_marks_an_emulation_build()
    {
        using TempDir dir = new();
        string source = dir.Dir("src");
        dir.File("src/fakelib2/libSceAmpr.sprx", "sprx");
        dir.File("src/eboot.bin", "eboot");
        ListLog log = new();

        Assert.NotNull(AmprIndex.Ensure(source, log));
        Assert.Equal("Info: Detected fakelib2/libSceAmpr.sprx; generating ampr_emu.index...", log.Lines[0]);
        Assert.True(AmprLibs.HasEmulator(source));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(5, 16)]
    public void Slot_count_is_the_power_of_two_at_least_twice_the_entries(int entries, int expected) =>
        Assert.Equal(expected, AmprIndex.HashSlotCount(entries));

    [Fact]
    public void Nothing_happens_without_the_marker_or_when_disabled()
    {
        using TempDir dir = new();
        string plain = dir.Dir("plain");
        dir.File("plain/eboot.bin", "e");
        string ampr = AmprTree(dir);

        Assert.Null(AmprIndex.Ensure(plain, new ListLog()));
        Assert.Null(AmprIndex.Ensure(ampr, new ListLog(), enabled: false));
        Assert.False(File.Exists(Path.Combine(plain, AmprIndex.IndexName)));
        Assert.False(File.Exists(Path.Combine(ampr, AmprIndex.IndexName)));
    }

    [Fact]
    public void Validation_compares_every_path_and_size_but_not_mtimes()
    {
        using TempDir dir = new();
        string source = AmprTree(dir);
        AmprIndex.Ensure(source, new ListLog());
        string index = Path.Combine(source, AmprIndex.IndexName);

        File.SetLastWriteTimeUtc(Path.Combine(source, "eboot.bin"), DateTime.UnixEpoch); // a copied folder
        Assert.True(AmprIndex.Validate(index, source));

        File.WriteAllText(Path.Combine(source, "eboot.bin"), "resized eboot"); // same file count
        Assert.False(AmprIndex.Validate(index, source));

        AmprIndex.Ensure(source, new ListLog());
        File.Move(Path.Combine(source, "data2", "b.bin"), Path.Combine(source, "data2", "c.bin")); // swapped name
        Assert.False(AmprIndex.Validate(index, source));

        AmprIndex.Ensure(source, new ListLog());
        File.Move(Path.Combine(source, "Data", "Level1.pak"), Path.Combine(source, "Data", "LEVEL1.PAK")); // case only
        Assert.True(AmprIndex.Validate(index, source));

        File.WriteAllBytes(index, File.ReadAllBytes(index)[..60]); // truncated
        Assert.False(AmprIndex.Validate(index, source));
    }

    [Fact]
    public void Create_if_missing_keeps_a_valid_index_and_rebuilds_a_stale_one()
    {
        using TempDir dir = new();
        string source = AmprTree(dir);
        AmprIndex.Ensure(source, new ListLog());
        string index = Path.Combine(source, AmprIndex.IndexName);
        Assert.True(AmprIndex.Validate(index, source));

        ListLog keep = new();
        Assert.Null(AmprIndex.Ensure(source, keep, createIfMissing: true));
        Assert.Equal(["Info: ampr_emu.index valid and present; skipping generation (create_if_missing)"], keep.Lines);

        dir.File("src/new.bin", "n");
        Assert.False(AmprIndex.Validate(index, source));
        ListLog stale = new();
        Assert.NotNull(AmprIndex.Ensure(source, stale, createIfMissing: true));
        Assert.Equal("Warning: ampr_emu.index failed validation; regenerating...", stale.Lines[0]);
        Assert.Equal(5, AmprIndex.ReadRows(File.ReadAllBytes(index)).Count);

        ListLog forced = new();
        Assert.NotNull(AmprIndex.Ensure(source, forced, createIfMissing: true, forceRegen: true));
        Assert.StartsWith("Info: Detected", forced.Lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void An_ampr_pack_set_keeps_its_index_unless_regeneration_is_forced()
    {
        using TempDir dir = new();
        string source = AmprTree(dir);
        AmprIndex.Ensure(source, new ListLog());
        string index = Path.Combine(source, AmprIndex.IndexName);
        byte[] original = File.ReadAllBytes(index);
        dir.File("src/ampr_assets.index", "manifest");
        dir.File("src/ampr_assets-000.pak", "volume");

        // Default and create-if-missing both keep the index the packs were built against.
        ListLog kept = new();
        Assert.Null(AmprIndex.Ensure(source, kept));
        Assert.Null(AmprIndex.Ensure(source, kept, createIfMissing: true));
        Assert.Equal(original, File.ReadAllBytes(index));
        Assert.All(kept.Lines, line => Assert.StartsWith("Warning: ampr_emu.index kept: the AMPR packs in this folder", line, StringComparison.Ordinal));

        ListLog forced = new();
        Assert.NotNull(AmprIndex.Ensure(source, forced, forceRegen: true));
        Assert.StartsWith("Warning: Rebuilding ampr_emu.index although ampr_assets.index is present", forced.Lines[0], StringComparison.Ordinal);
        Assert.Equal(6, AmprIndex.ReadRows(File.ReadAllBytes(index)).Count);
    }

    [Fact]
    public void Pack_folder_writes_the_index_into_the_image_unless_disabled()
    {
        using TempDir dir = new();
        string source = AmprTree(dir);
        StringWriter stdout = new() { NewLine = "\n" };
        StringWriter stderr = new() { NewLine = "\n" };
        CliContext ctx = new(stdout, stderr, useColor: false, utf8: false, progress: false);

        int noIndex = MkPFSCli.Run(["pack", "folder", source, Path.Combine(dir.Path, "a.ffpfs"), "--raw", "--no-ampr-index", "--skip-verification"], ctx);
        Assert.Equal(0, noIndex);
        Assert.False(File.Exists(Path.Combine(source, AmprIndex.IndexName)));

        int withIndex = MkPFSCli.Run(["pack", "folder", source, Path.Combine(dir.Path, "b.ffpfs"), "--raw", "--verify", "--no-adjust-output-file-extension"], ctx);
        Assert.True(withIndex == 0, stdout.ToString() + stderr.ToString());
        Assert.StartsWith("Detected fakelib/libSceAmpr.sprx; generating ampr_emu.index...\nGenerated ampr_emu.index with 4 entries\n", stdout.ToString()[stdout.ToString().IndexOf("Detected", StringComparison.Ordinal)..], StringComparison.Ordinal);
        Assert.Contains(AmprIndex.IndexName, Core.PFS.PFSInspector.Inspect(Path.Combine(dir.Path, "b.ffpfs")).FileInodes.Keys);
    }
}
