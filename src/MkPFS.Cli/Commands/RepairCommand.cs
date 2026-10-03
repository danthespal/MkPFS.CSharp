using System.CommandLine;
using MkPFS.Cli.Output;
using MkPFS.Core.PFSC;
using MkPFS.Core.Util;
using MkPFS.Repair;

namespace MkPFS.Cli.Commands;

/// <summary><c>repair</c>: offline PFSC block repair for single-file <c>.ffpfsc</c> images (PS5 Game Compressor port).</summary>
internal static class RepairCommand
{
    /// <summary>Exit code of <c>--scan</c> when blocks need repair (errors use 1, usage errors 2).</summary>
    public const int ExitRepairNeeded = 3;

    public static Command Create(CliContext ctx)
    {
        Argument<string> image = new("image_file") { Description = "Path to the .ffpfsc image" };
        Option<bool> scan = new("--scan") { Description = "Only scan and report; do not modify the image" };
        Option<string?> badBlocks = new("--bad-blocks") { Description = "Repair the blocks listed in a Game Compressor bad_blocks.tsv instead of the risky blocks found by the scan" };
        Option<bool> recompress = new("--recompress") { Description = "Re-encode repaired blocks with zlib level 7 instead of storing them raw" };
        Option<string> mode = new("--mode") { Description = "auto: copy-replace when free space is at least 1.2x the image, else in-place", DefaultValueFactory = _ => "auto" };
        mode.AcceptOnlyFromAmong("auto", "in-place", "copy");
        Option<string?> reportDir = new("--report-dir") { Description = "Write summary.json and bad_blocks.tsv to this folder" };
        Option<bool> noSlack = new("--no-slack-cleanup") { Description = "Do not zero unused bytes in the outer PFS wrapper" };
        Option<int> cpuCount = new("--cpu-count") { Description = "Worker threads (0 = auto min(16, max(1, cpu_count() - 1)))", DefaultValueFactory = _ => 0 };
        Option<bool> noProgress = new("--no-progress") { Description = "Disable progress output" };
        Command command = new("repair", "Find and fix PFSC blocks the PS5 may decode wrongly (single-file .ffpfsc)")
        {
            image, scan, badBlocks, recompress, mode, reportDir, noSlack, cpuCount, noProgress,
        };
        command.SetAction(parse => Execute(
            ctx,
            parse.GetValue(image)!,
            Options(ctx, parse.GetValue(scan), parse.GetValue(badBlocks), parse.GetValue(recompress), parse.GetValue(mode)!,
                parse.GetValue(reportDir), !parse.GetValue(noSlack), parse.GetValue(cpuCount), !parse.GetValue(noProgress))));
        return command;
    }

    /// <summary>Repair options from command-line values (also used by the GUI Repair page).</summary>
    internal static PFSCRepairOptions Options(CliContext ctx, bool scanOnly, string? badBlocks, bool recompress, string mode, string? reportDir, bool cleanSlack, int cpuCount, bool progress) => new()
    {
        ScanOnly = scanOnly,
        BadBlocksPath = badBlocks is { } list ? Path.GetFullPath(PathRules.ExpandUser(list)) : null,
        Recompress = recompress,
        Mode = mode switch { "in-place" => RepairMode.InPlace, "copy" => RepairMode.Copy, _ => RepairMode.Auto },
        ReportDirectory = reportDir is { } dir ? Path.GetFullPath(PathRules.ExpandUser(dir)) : null,
        CleanSlack = cleanSlack,
        Workers = PFSCEncoder.ResolveWorkerCount(cpuCount),
        Progress = ctx.CreateProgress(progress),
    };

    /// <summary>Run the repair and print its report; the GUI passes <paramref name="onResult"/> to draw the block map.</summary>
    /// <returns>Exit code: 0, 1 on failure, or <see cref="ExitRepairNeeded"/> for a scan that found blocks to repair.</returns>
    internal static int Execute(CliContext ctx, string imagePath, PFSCRepairOptions options, CancellationToken cancellationToken = default, Action<PFSCRepairResult>? onResult = null)
    {
        string path = Path.GetFullPath(PathRules.ExpandUser(imagePath));
        if (!File.Exists(path))
        {
            ctx.Error($"Image not found: {path}");
            return 1;
        }

        ctx.VersionHeader();
        ctx.Info($"Image:   {path}");
        PFSCRepairResult result;
        try
        {
            result = PFSCRepair.Run(path, options, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            ctx.Error($"Repair failed: {ex.Message}");
            return 1;
        }

        onResult?.Invoke(result);
        Print(ctx, result);
        return result.Status switch
        {
            RepairStatus.Failed => 1,
            RepairStatus.RepairNeeded => ExitRepairNeeded,
            _ => 0,
        };
    }

    private static void Print(CliContext ctx, PFSCRepairResult result)
    {
        ctx.Info($"Nested:  {result.NestedName} ({CliContext.Thousands(result.NestedSize)} bytes, {CliContext.Thousands(result.BlockCount)} blocks, {CliContext.Thousands(result.CompressedBlocks)} compressed)");
        ctx.Info($"vhash:   {PFSCRepair.HashModeName(result.HashMode)}");
        ctx.Info($"Risky:   {CliContext.Thousands(result.RiskyBlocks)} block(s)");
        if (result.Selection == "bad-blocks")
        {
            ctx.Info($"Listed:  {CliContext.Thousands(result.MarkedBlocks.Count)} block(s) from --bad-blocks");
        }

        switch (result.Status)
        {
            case RepairStatus.Failed:
                ctx.Error($"Repair failed: {result.Error}");
                break;
            case RepairStatus.Noop:
                ctx.Info("No blocks need repair.");
                break;
            case RepairStatus.RepairNeeded:
                ctx.Warning($"{CliContext.Thousands(result.MarkedBlocks.Count)} block(s) need repair: {Preview(result.MarkedBlocks)}");
                ctx.Info($"Stored size would change from {CliContext.Thousands(result.OldStoredSize)} to {CliContext.Thousands(result.NewStoredSize)} bytes.");
                break;
            case RepairStatus.Repaired:
                string how = result.Recompress ? "recompressed" : "stored raw";
                ctx.Info($"Repaired {CliContext.Thousands(result.MarkedBlocks.Count)} block(s) ({how}, {PFSCRepair.ModeName(result.AppliedMode!.Value)}): {Preview(result.MarkedBlocks)}");
                ctx.Info($"Stored size: {CliContext.Thousands(result.OldStoredSize)} -> {CliContext.Thousands(result.NewStoredSize)} bytes");
                ctx.Info($"Verified {CliContext.Thousands(result.PostVerifyBlocks)} block(s): decoded content unchanged.");
                break;
        }

        if (result.Slack is { } slack)
        {
            ctx.Info(slack.Applicable
                ? $"Outer slack: {CliContext.Thousands(slack.FixedBytes)} non-zero byte(s) {(result.Status == RepairStatus.RepairNeeded ? "to clear" : "cleared")}"
                : $"Outer slack: skipped ({slack.Reason})");
        }
    }

    private static string Preview(IReadOnlyList<long> blocks) =>
        string.Join(", ", blocks.Take(10)) + (blocks.Count > 10 ? $", ... (+{blocks.Count - 10})" : "");
}
