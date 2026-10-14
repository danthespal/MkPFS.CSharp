using System.CommandLine;
using MkPFS.Build.AMPRPack;
using MkPFS.Build.PFS;
using MkPFS.Cli.Output;
using MkPFS.Core.AMPR;
using MkPFS.Core.Diagnostics;

namespace MkPFS.Cli.Commands;

/// <summary>
/// <c>ampr compress</c>: the PS5 LZ4 Game Compressor workflow (rules generated from the folder structure, then the
/// same playable folder as <c>ampr game</c>). Not in ampr_pack.py; errors use its <c>error: ...</c> / exit 2 style.
/// </summary>
internal static class AmprCompressCommand
{
    public static Command Create(CliContext ctx)
    {
        Option<string> root = new("--root") { Description = "unpacked game folder (/app0) with eboot.bin and sce_sys/param.json; only read", Required = true };
        Option<string> output = new("--output") { Description = "new or empty folder for the playable game", Required = true };
        Option<string?> fakelib = new("--fakelib") { Description = "folder with AMPR Emu 0.4.2.1+ libSceAmpr.sprx and other fakelib libraries to add" };
        Option<int> level = new("--level")
        {
            Description = $"compression level 1..12: up to {AMPRUniversalProfile.FastLevelLimit} uses LZ4 fast, higher LZ4 HC (default: {AMPRUniversalProfile.DefaultLevel})",
            DefaultValueFactory = _ => AMPRUniversalProfile.DefaultLevel,
        };
        Option<int?> workers = AmprCommand.WorkersOption();
        Option<string?> exfat = new("--exfat") { Description = "also build an exFAT image of the output (file, or folder for <titleId>.exfat)" };
        Option<string?> exfatFree = new("--exfat-free-space") { Description = "free space to leave inside the exFAT image, e.g. 2GiB (default: 0)" };
        Option<bool> skipVerify = new("--skip-verify") { Description = "do not verify the packs and loose files after the build" };
        Option<bool> noProgress = new("--no-progress") { Description = "suppress progress on stderr" };
        Command command = new("compress", "LZ4 Game Compressor: build a playable folder with rules generated from the game's folder structure (not in ampr_pack.py)")
        {
            root, output, fakelib, level, workers, exfat, exfatFree, skipVerify, noProgress,
        };
        command.SetAction(parse => AmprCommand.Run(ctx, () =>
        {
            ctx.Log.Warning(AmprCommand.ExperimentalWarning);
            int chosen = parse.GetValue(level);
            if (chosen is < 1 or > 12)
            {
                throw new AMPRPackException("--level must be between 1 and 12");
            }

            long? count = parse.GetValue(workers) is { } value ? AmprCommand.Workers(value) : null;

            // The generic rules are untested per game; a tested profile is the safer choice when one fits.
            string rootPath = AmprCommand.Resolve(ctx, parse.GetValue(root)!);
            if (AMPRBundledProfiles.Match(rootPath) is { } match)
            {
                ctx.Log.Info($"A profile tested on the PS5 fits this game ({match.Profile.Label}, {AmprCommand.BundledMatchText(match.Kind)}): "
                    + $"mkpfs ampr game --profile {match.Profile.Id} uses it instead of the generic rules");
            }

            string? image = parse.GetValue(exfat);
            AMPRLZ4CompressorOptions options = new()
            {
                Level = chosen,
                Workers = count,
                LibsDir = parse.GetValue(fakelib) is { } libs ? AmprCommand.Resolve(ctx, libs) : null,
                Verify = !parse.GetValue(skipVerify),
                ExfatImage = image is null ? null : AmprCommand.Resolve(ctx, image),
                ExfatFreeBytes = parse.GetValue(exfatFree) is { } free
                    ? image is not null ? AMPRSize.Parse(free) : throw new AMPRPackException("--exfat-free-space needs --exfat")
                    : 0,
            };
            bool progress = !parse.GetValue(noProgress) && ctx.ProgressEnabled;
            try
            {
                AMPRLZ4Compressor.Build(
                    rootPath,
                    AmprCommand.Resolve(ctx, parse.GetValue(output)!),
                    options,
                    ctx.Log,
                    progress ? AmprCommand.ProgressReporter(ctx) : null,
                    ctx.CreateProgress(progress));
            }
            catch (BuildException ex)
            {
                throw new AMPRPackException(ex.Message);
            }
        }));
        return command;
    }
}
