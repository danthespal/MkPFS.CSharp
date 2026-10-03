using System.CommandLine;
using MkPFS.Build;
using MkPFS.Core.Util;

namespace MkPFS.Cli.Commands;

/// <summary>AMPR Emu options shared by <c>pack folder</c>, <c>pack exfat</c> and <c>batch</c>.</summary>
internal sealed class AmprCliOptions
{
    private readonly Option<string?> _libs = new("--ampr-libs") { Description = "Folder with AMPR Emu libSceAmpr.sprx (and optional libScePlayGo.sprx) to copy into fakelib/ of APR titles" };
    private readonly Option<bool> _title = new("--ampr-title") { Description = "With --ampr-libs, add the libraries even without sce_sys/playgo-chunk.dat" };
    private readonly Option<bool> _noIndex = new("--no-ampr-index") { Description = "Do not generate ampr_emu.index even when fakelib/libSceAmpr.sprx is present" };
    private readonly Option<bool> _skip = new("--ampr-skip-regen-if-exists") { Description = "If AMPR generation is enabled, validate and skip regen when a valid index exists" };
    private readonly Option<bool> _force = new("--ampr-force-regen") { Description = "Force AMPR index regeneration even when an existing index is present" };

    public AmprCliOptions(Command command)
    {
        command.Options.Add(_noIndex);
        command.Options.Add(_skip);
        command.Options.Add(_force);
        command.Options.Add(_libs);
        command.Options.Add(_title);
    }

    public AmprOptions Read(ParseResult parse) => new()
    {
        LibsDir = parse.GetValue(_libs) is { Length: > 0 } libs ? Path.GetFullPath(PathRules.ExpandUser(libs)) : null,
        ForceAprTitle = parse.GetValue(_title),
        Index = !parse.GetValue(_noIndex),
        SkipRegenIfExists = parse.GetValue(_skip),
        ForceRegen = parse.GetValue(_force),
    };
}
