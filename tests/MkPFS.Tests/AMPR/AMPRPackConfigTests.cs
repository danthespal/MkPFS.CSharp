using System.Text;
using MkPFS.Build.AMPRPack;
using MkPFS.Core.AMPR;

namespace MkPFS.Tests.AMPR;

/// <summary>ampr_pack <c>load_config</c>, <c>select_rule</c> and <c>_pack_output_glob</c>; expectations come from Python.</summary>
public sealed class AMPRPackConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mkpfs-ampr-config-" + Guid.NewGuid().ToString("N"));

    public AMPRPackConfigTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private AMPRPackConfig Load(string toml)
    {
        string path = Path.Combine(_dir, "c.toml");
        File.WriteAllText(path, toml + "\n", new UTF8Encoding(false));
        return AMPRPackConfig.Load(path);
    }

    [Theory]
    [InlineData("ampr_assets-{id:03d}.pak", "ampr_assets-*.pak", "ampr_assets-000.pak", "ampr_assets-007.pak")]
    [InlineData("ampr_assets-{group}-lane{lane:02d}-vol{volume:02d}-{id:03d}.pak", "ampr_assets-*-lane*-vol*-*.pak", "ampr_assets-g-lane00-vol00-000.pak", "ampr_assets-assets-lane03-vol12-007.pak")]
    [InlineData("{id}", "*", "0", "7")]
    [InlineData("a{{b}}{id}", "a{b}*", "a{b}0", "a{b}7")]
    [InlineData("{id!r}.pak", "*.pak", "0.pak", "7.pak")]
    [InlineData("{group!r}.pak", "*.pak", "'g'.pak", "'assets'.pak")]
    [InlineData("{id:>5}", "*", "    0", "    7")]
    [InlineData("{group:05}", "*", "g0000", "assets")]
    [InlineData("{id:+d}", "*", "+0", "+7")]
    [InlineData("{id:x}", "*", "0", "7")]
    [InlineData("{id:#x}", "*", "0x0", "0x7")]
    [InlineData("{id:.2f}", "*", "0.00", "7.00")]
    [InlineData("a/{id}.pak", "a/*.pak", "a/0.pak", "a/7.pak")]
    [InlineData("{id:_>4}", "*", "___0", "___7")]
    [InlineData("{id:,}", "*", "0", "7")]
    [InlineData("sub\\{id}.pak", "sub/*.pak", "sub\\0.pak", "sub\\7.pak")]
    [InlineData("{id: 3d}", "*", "  0", "  7")]
    [InlineData("{id:=+5d}", "*", "+   0", "+   7")]
    [InlineData("{id:^5}", "*", "  0  ", "  7  ")]
    [InlineData("{group:^5}", "*", "  g  ", "assets")]
    [InlineData("{id!s:>3}", "*", "  0", "  7")]
    [InlineData("{id!a}", "*", "0", "7")]
    [InlineData("{id:%}", "*", "0.000000%", "700.000000%")]
    [InlineData("{id:e}", "*", "0.000000e+00", "7.000000e+00")]
    [InlineData("{id:b}", "*", "0", "111")]
    [InlineData("{id:#o}", "*", "0o0", "0o7")]
    [InlineData("{lane:08_b}", "*", "000_0000", "000_0011")]
    public void Pack_pattern_globs_and_renders_like_python(string pattern, string glob, string zero, string sample)
    {
        AMPRPackPattern parsed = AMPRPackPattern.Parse(pattern);
        Assert.Equal(glob, parsed.OutputGlob);
        Assert.Equal(zero, parsed.Render("g", 0, 0, 0));
        Assert.Equal(sample, parsed.Render("assets", 3, 12, 7));
    }

    [Theory]
    [InlineData("{}", "pack_pattern uses unsupported field ''; allowed fields are group, lane, volume and id")]
    [InlineData("{0}", "pack_pattern uses unsupported field '0'; allowed fields are group, lane, volume and id")]
    [InlineData("{id.real}", "pack_pattern uses unsupported field 'id.real'; allowed fields are group, lane, volume and id")]
    [InlineData("{id[0]}", "pack_pattern uses unsupported field 'id[0]'; allowed fields are group, lane, volume and id")]
    [InlineData("{ id }", "pack_pattern uses unsupported field ' id '; allowed fields are group, lane, volume and id")]
    [InlineData("x}y", "invalid pack_pattern: 'x}y'")]
    [InlineData("x{", "invalid pack_pattern: 'x{'")]
    [InlineData("{id", "invalid pack_pattern: '{id'")]
    [InlineData("{id!}", "invalid pack_pattern: '{id!}'")]
    [InlineData("{id!rr}", "invalid pack_pattern: '{id!rr}'")]
    [InlineData("{id!x}.pak", "unsafe or invalid pack_pattern: '{id!x}.pak'")]
    [InlineData("{id:{x}}", "unsafe or invalid pack_pattern: '{id:{x}}'")]
    [InlineData("{id:s}", "unsafe or invalid pack_pattern: '{id:s}'")]
    [InlineData("{group:d}", "unsafe or invalid pack_pattern: '{group:d}'")]
    [InlineData("/abs/{id}", "unsafe or invalid pack_pattern: '/abs/{id}'")]
    [InlineData("../{id}", "unsafe or invalid pack_pattern: '../{id}'")]
    [InlineData("", "pack_pattern cannot be empty")]
    public void Pack_pattern_errors_match_python(string pattern, string message) =>
        Assert.Equal(message, Assert.Throws<AMPRPackException>(() => AMPRPackPattern.Parse(pattern)).Message);

    [Theory]
    [InlineData("pack = 1", "[pack] must be a table")]
    [InlineData("[pack]\nworkers = 0", "workers must be between 1 and 256")]
    [InlineData("[pack]\nworkers = \"4\"\ncompression_level = 13", "compression_level must be between 1 and 12")]
    [InlineData("[pack]\nio_page_size = \"3KiB\"", "io_page_size must be a power of two between 4 KiB and 1 MiB")]
    [InlineData("[pack]\npayload_alignment = 32", "payload_alignment must be a power of two between 64 B and 1 MiB; the writer automatically raises it to the selected io_page_size")]
    [InlineData("[pack]\nchunk_alignment = \"128KiB\"", "chunk_alignment must be a power of two between 64 B and io_page_size")]
    [InlineData("[pack]\ncompression_mode = \"zstd\"", "compression_mode must be fast or hc")]
    [InlineData("[pack]\ndeduplicate_scope = \"all\"", "deduplicate_scope must be lane or group")]
    [InlineData("[pack]\nacceleration = 0", "acceleration must be at least 1")]
    [InlineData("[pack]\nmin_savings_ratio = 1.0", "min_savings_ratio must be in [0, 1)")]
    [InlineData("[pack]\nio_neutral_min_savings_ratio = -0.1", "io_neutral_min_savings_ratio must be in [0, 1)")]
    [InlineData("[pack]\nauto_loose_sample_blocks = 5000", "auto_loose_sample_blocks must be between 1 and 4096")]
    [InlineData("[pack]\nauto_loose_sample_bytes = 0", "auto_loose_sample_bytes must be at least 1 byte")]
    [InlineData("[pack]\nauto_loose_min_savings_ratio = 2", "auto_loose_min_savings_ratio must be in [0, 1)")]
    [InlineData("[pack]\nauto_loose_max_raw_ratio = 1.5", "auto_loose_max_raw_ratio must be in [0, 1]")]
    [InlineData("[pack]\nindex_name = \"../x.index\"", "unsafe index_name: '../x.index'")]
    [InlineData("[pack]\npack_pattern = \"{name}.pak\"", "pack_pattern uses unsupported field 'name'; allowed fields are group, lane, volume and id")]
    [InlineData("[pack]\ndefault_action = \"zip\"", "action must be compress, store or loose; got 'zip'")]
    [InlineData("[pack]\ndefault_action = true", "action must be compress, store or loose; got 'true'")]
    [InlineData("[pack]\nrequired_packed = [1]", "pack.required_packed must be a string or list of strings")]
    [InlineData("groups = 5", "[groups] must be a table")]
    [InlineData("[groups]\nbulk = 1", "[groups.bulk] must be a table")]
    [InlineData("[groups.bulk]\npack_count = 0", "groups.bulk.pack_count must be between 1 and 65535")]
    [InlineData("[groups.bulk]\nassignment = \"random\"", "assignment must be balanced, hash or round_robin; got 'random'")]
    [InlineData("[groups.bulk]\nio_page_size = \"3KiB\"", "groups.bulk.io_page_size must be a power of two between 4 KiB and 1 MiB and a multiple of chunk_alignment")]
    [InlineData("[groups.bulk]\nmax_pack_size = \"64KiB\"", "groups.bulk.max_pack_size must be at least one complete I/O page beyond the payload offset (131072 bytes)")]
    [InlineData("[groups.bulk]\nstripe_group_blocks = 0", "groups.bulk.stripe_group_blocks must be at least 1")]
    [InlineData("rule = \"x\"", "[[rule]] entries must form an array")]
    [InlineData("rule = [1]", "rule 0 must be a table")]
    [InlineData("[[rule]]\ngroup = \"nope\"", "rule 0 references unknown group 'nope'")]
    [InlineData("[[rule]]\ninclude = []", "rule 0 include list cannot be empty")]
    [InlineData("[[rule]]\ninclude = [1, 2]", "include must be a string or list of strings")]
    [InlineData("[[rule]]\ncompression_mode = \"x\"", "rule 0 compression_mode must be fast or hc")]
    [InlineData("[[rule]]\ncompression_level = 0", "rule 0 compression_level must be between 1 and 12")]
    [InlineData("[[rule]]\nacceleration = 0", "rule 0 acceleration must be at least 1")]
    [InlineData("[[rule]]\nmin_savings_ratio = 1", "rule 0 min_savings_ratio must be in [0, 1)")]
    [InlineData("[[rule]]\nio_neutral_min_savings_ratio = 1", "rule 0 io_neutral_min_savings_ratio must be in [0, 1)")]
    [InlineData("[[rule]]\nlayout = \"linear\"", "layout must be auto, random, mixed or streaming; got 'linear'")]
    [InlineData("[[rule]]\naction = \"skip\"", "action must be compress, store or loose; got 'skip'")]
    [InlineData("[[rule]]\ninclude_from = [\"../outside.txt\"]", "include_from entry escapes the TOML directory: ../outside.txt")]
    [InlineData("[[rule]]\ninclude_from = 5", "include_from must be a string or list of strings")]
    public void Config_pack_errors_match_python(string toml, string message) =>
        Assert.Equal(message, Assert.Throws<AMPRPackException>(() => Load(toml)).Message);

    [Theory]
    [InlineData("[pack]\nworkers = \"abc\"", "invalid literal for int() with base 10: 'abc'")]
    [InlineData("[pack]\nmin_savings_ratio = \"x\"", "could not convert string to float: 'x'")]
    [InlineData("[pack]\nmin_savings_bytes = 1.5", "'float' object has no attribute 'strip'")]
    [InlineData("[runtime]\ndecoded_cache_bytes = \"64MiB\"\nphysical_cache_bytes = \"16MiB\"\nworkers = \"4\"\nlatency_reserve_workers = 1", "runtime settings must be integers")]
    [InlineData("[runtime]\ndecoded_cache_bytes = \"64MiB\"\nphysical_cache_bytes = 1000\nworkers = 4\nlatency_reserve_workers = 1", "runtime cache sizes must be nonnegative 16 KiB multiples")]
    [InlineData("[runtime]\ndecoded_cache_bytes = \"64MiB\"\nphysical_cache_bytes = 0\nworkers = 4\nlatency_reserve_workers = 4", "runtime workers must be 1..16 and reserve must be smaller")]
    [InlineData("[[rule]]\nblock_size = \"8KiB\"", "block size must be between 16384 and 1048576 bytes")]
    public void Config_value_errors_match_python(string toml, string message) =>
        Assert.Equal(message, Assert.Throws<ArgumentException>(() => Load(toml)).Message);

    [Theory]
    [InlineData("a = 1\na = 2")]
    [InlineData("[pack]\nworkers = 4\n[pack]\nx = 1")]
    [InlineData("a.b = 1\n[a]\nc = 2")]
    [InlineData("a = {b = 1}\n[a.c]\nd = 1")]
    [InlineData("[[r]]\nx = 1\n[r]\ny = 2")]
    [InlineData("x = ")]
    public void Invalid_toml_is_rejected_like_tomllib(string toml) =>
        Assert.Throws<AMPRPackException>(() => Load(toml));

    [Fact]
    public void Missing_include_from_file_names_the_file() =>
        Assert.StartsWith(
            "cannot read include_from file missing.txt: ",
            Assert.Throws<AMPRPackException>(() => Load("[[rule]]\ninclude_from = [\"missing.txt\"]")).Message);

    [Fact]
    public void Default_config_canonical_bytes_match_python() =>
        Assert.Equal(
            "{\"acceleration\":1,\"auto_loose_hot_files\":false,\"auto_loose_large_files\":true,\"auto_loose_max_raw_ratio\":0.9,"
            + "\"auto_loose_min_file_size\":67108864,\"auto_loose_min_savings_ratio\":0.05,\"auto_loose_sample_blocks\":32,"
            + "\"auto_loose_sample_bytes\":16777216,\"chunk_alignment\":64,\"compression_level\":12,\"compression_mode\":\"hc\","
            + "\"deduplicate\":true,\"deduplicate_scope\":\"lane\",\"deduplicate_streaming\":false,\"default_action\":\"loose\","
            + "\"default_block_shift\":16,\"groups\":[{\"assignment\":\"balanced\",\"io_page_size\":0,\"max_pack_size\":0,"
            + "\"name\":\"default\",\"pack_count\":1,\"stripe_group_blocks\":8,\"stripe_large_files\":false,\"stripe_threshold\":268435456}],"
            + "\"index_name\":\"ampr_assets.index\",\"io_neutral_min_savings_bytes\":8192,\"io_neutral_min_savings_ratio\":0.125,"
            + "\"io_page_size\":65536,\"min_savings_bytes\":64,\"min_savings_ratio\":0.01,\"pack_pattern\":\"ampr_assets-{id:03d}.pak\","
            + "\"payload_alignment\":65536,\"rules\":[],\"tool_version\":\"4.0\"}",
            Encoding.UTF8.GetString(AMPRPackConfig.Load(null).CanonicalBytes()));

    [Fact]
    public void Groups_sort_by_code_point_and_rules_inherit_pack_defaults()
    {
        AMPRPackConfig config = Load(
            "[pack]\nmin_savings_ratio = 0.00001\nworkers = 2\n[groups.\"b\u00e9\"]\npack_count = 2\n[groups.a]\n[[rule]]\ninclude = \"x/\u00e9*\"\nmin_savings_ratio = 0");

        string canonical = Encoding.UTF8.GetString(config.CanonicalBytes());
        Assert.Contains("\"groups\":[{\"assignment\":\"balanced\",\"io_page_size\":0,\"max_pack_size\":0,\"name\":\"a\"", canonical, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"b\\u00e9\",\"pack_count\":2", canonical, StringComparison.Ordinal);
        Assert.Contains("\"min_savings_ratio\":1e-05,\"pack_pattern\"", canonical, StringComparison.Ordinal);
        Assert.Contains("\"include\":[\"x/\\u00e9*\"]", canonical, StringComparison.Ordinal);
        Assert.Contains("\"min_savings_ratio\":0.0,\"mode\":\"hc\"", canonical, StringComparison.Ordinal);
        Assert.Equal(["a", "b\u00e9", "default"], config.SortedGroupNames());
        Assert.Equal(2, config.Workers);
    }

    [Fact]
    public void Last_matching_rule_wins_and_unmatched_files_use_the_default_rule()
    {
        AMPRPackConfig config = Load(
            "[pack]\ndefault_action = \"store\"\n[[rule]]\ninclude = [\"assets/**\"]\nblock_size = \"16KiB\"\n"
            + "[[rule]]\naction = \"loose\"\ninclude = [\"assets/*.bik\"]\n[[rule]]\ninclude = \"**/*.lua\"\nhot = true\nexclude = \"x/**\"");

        Assert.Equal(("compress", 14), (config.SelectRule("assets/a.bin").Action, config.SelectRule("assets/a.bin").BlockShift));
        Assert.Equal("loose", config.SelectRule("assets/intro.bik").Action);
        Assert.Equal("random", config.SelectRule("scripts/a.lua").ResolvedLayout());
        Assert.Equal(("store", "mixed"), (config.SelectRule("x/a.lua").Action, config.SelectRule("x/a.lua").ResolvedLayout()));
    }

    [Fact]
    public void Include_from_reads_stripped_patterns_relative_to_the_toml()
    {
        File.WriteAllText(Path.Combine(_dir, "list.txt"), "# comment\n  scripts/** \r\n\r\nassets/text_*.dat\u2028data/*\n", new UTF8Encoding(false));
        AMPRPackConfig config = Load("[[rule]]\ninclude_from = [\"list.txt\"]\nexclude = [\"x\"]\nexclude_from = \"list.txt\"");

        Assert.Equal(["scripts/**", "assets/text_*.dat", "data/*"], config.Rules[0].Include);
        Assert.Equal(["x", "scripts/**", "assets/text_*.dat", "data/*"], config.Rules[0].Exclude);
    }
}
