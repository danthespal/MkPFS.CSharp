using MkPFS.Build.AMPRPack;
using MkPFS.Core.AMPR;

namespace MkPFS.Parity;

/// <summary>
/// Loads every golden pack configuration (<c>ampr/goldens/&lt;case&gt;/config.toml</c>) and compares the build-id input
/// with the oracle's <c>_canonical_config_bytes</c> dump (<c>config.canonical.json</c>).
/// </summary>
public sealed class AMPRPackConfigParityTests
{
    public static TheoryData<string> ConfiguredCases => new(
        "default", "unsafe_default", "example", "fast", "fast_accel8", "hc9", "block16k_random", "block1m", "streaming",
        "store_movies", "dedup_off", "dedup_group", "dedup_streaming", "lanes_balanced", "lanes_hash", "lanes_round_robin",
        "stripe_rollover", "auto_loose", "self_contained", "runtime", "io_page_4k", "mtime_preserved", "no_preserve_mtime",
        "cli_include_exclude", "include_from", "allow_missing", "workers1", "workers8");

    [Theory]
    [MemberData(nameof(ConfiguredCases))]
    public void Canonical_config_bytes_match_the_oracle(string caseName)
    {
        string config = Fixtures.PathOrSkip("ampr", "goldens", caseName, "config.toml");
        byte[] expected = File.ReadAllBytes(Fixtures.PathOrSkip("ampr", "goldens", caseName, "config.canonical.json"));

        Assert.Equal(System.Text.Encoding.UTF8.GetString(expected), System.Text.Encoding.UTF8.GetString(AMPRPackConfig.Load(config).CanonicalBytes()));
    }

    [Theory]
    [InlineData("neg_bad_block")]
    [InlineData("neg_unknown_group")]
    [InlineData("neg_runtime_keys")]
    public void Config_errors_match_the_oracle_message(string caseName)
    {
        string config = Fixtures.PathOrSkip("ampr", "goldens", caseName, "config.toml");
        string log = File.ReadAllText(Fixtures.PathOrSkip("ampr", "goldens", caseName, "pack.log"));
        string expected = log.Split('\n').Single(line => line.StartsWith("error: ", StringComparison.Ordinal))["error: ".Length..];

        Exception error = Assert.ThrowsAny<Exception>(() => AMPRPackConfig.Load(config));
        Assert.True(error is AMPRPackException or ArgumentException, error.GetType().Name);
        Assert.Equal(expected, error.Message);
    }
}
