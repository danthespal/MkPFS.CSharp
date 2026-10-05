using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MkPFS.Cli.Commands;
using MkPFS.Cli.Output;
using MkPFS.Core.Compression;
using MkPFS.Gui.Controls;
using MkPFS.Gui.Jobs;
using MkPFS.Gui.Localization;
using MkPFS.Repair;

namespace MkPFS.Gui.ViewModels;

/// <summary>
/// Repair page (new; not in the Python GUI): <c>mkpfs repair</c> with Scan and Repair, plus a block map of
/// the scanned image.
/// </summary>
/// <param name="accent">Accent color.</param>
/// <param name="job">Job runner, or <see langword="null"/> for the UI-thread runner.</param>
public sealed partial class RepairPanelViewModel(Color accent, JobRunner? job = null) : PanelViewModel("r_title", "r_subtitle", accent, job)
{
    /// <summary>Repair modes (<c>--mode</c>).</summary>
    public IReadOnlyList<string> Modes { get; } = ["auto", "in-place", "copy"];

    /// <summary>Cover and details of the image.</summary>
    public MetadataPreviewViewModel Metadata { get; } = new();

    /// <summary>Image to repair.</summary>
    [ObservableProperty]
    public partial string Image { get; set; } = string.Empty;

    /// <summary><c>--bad-blocks</c>, a Game Compressor <c>bad_blocks.tsv</c>.</summary>
    [ObservableProperty]
    public partial string BadBlocks { get; set; } = string.Empty;

    /// <summary><c>--report-dir</c>.</summary>
    [ObservableProperty]
    public partial string ReportDir { get; set; } = string.Empty;

    /// <summary><c>--mode</c>.</summary>
    [ObservableProperty]
    public partial string Mode { get; set; } = "auto";

    /// <summary><c>--recompress</c>.</summary>
    [ObservableProperty]
    public partial bool Recompress { get; set; }

    /// <summary>Outer slack cleanup (off adds <c>--no-slack-cleanup</c>).</summary>
    [ObservableProperty]
    public partial bool CleanSlack { get; set; } = true;

    /// <summary>Block map of the last scan, or <see langword="null"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMap), nameof(MapSummary))]
    public partial PFSCBlockMap? Map { get; private set; }

    /// <summary>A block map is shown.</summary>
    public bool HasMap => Map is not null;

    /// <summary>Block, compressed, and risky counts of <see cref="Map"/>.</summary>
    public string MapSummary => Map is null ? string.Empty : Localizer.Instance.Format(
        "r_map_summary",
        Thousands(Map.BlockCount),
        Thousands(Enumerable.Range(0, (int)Map.BlockCount).LongCount(i => !Map.IsRaw(i))),
        Thousands(Map.Risky.LongCount(r => r)));

    /// <summary>Blocks per map cell, set by the view from the map layout.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScaleText))]
    public partial long BlocksPerCell { get; set; } = 1;

    /// <summary>"N blocks per cell" when cells group blocks.</summary>
    public string ScaleText => BlocksPerCell > 1 ? Localizer.Instance.Format("r_map_scale", Thousands(BlocksPerCell)) : string.Empty;

    /// <summary>Selected cell's blocks, or <see langword="null"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailsTitle), nameof(Details))]
    public partial BlockRange? Selection { get; set; }

    /// <summary>"Block N" or "Blocks A–B".</summary>
    public string DetailsTitle => Selection is not { } range ? Localizer.Instance["r_detail_none"]
        : range.First == range.Last ? Localizer.Instance.Format("r_block", Thousands(range.First))
        : Localizer.Instance.Format("r_blocks", Thousands(range.First), Thousands(range.Last));

    /// <summary>Detail lines for the selection: block counts by state, then the worst block's offset, size, and distance.</summary>
    public IReadOnlyList<string> Details => Map is { } map && Selection is { } range ? Describe(map, range) : [];

    /// <inheritdoc />
    public override object Form => this;

    /// <summary>Scan only (<c>--scan</c>): log the report and draw the block map.</summary>
    /// <returns>Completes with the job.</returns>
    [RelayCommand(CanExecute = nameof(CanScan))]
    public async Task ScanAsync()
    {
        Job.Clear();
        IReadOnlyList<string>? args = Arguments(scan: true, out string? error);
        if (args is null)
        {
            Job.Append(new LogLine(error ?? string.Empty, LogTone.Error));
            return;
        }

        await ExecuteAsync(args).ConfigureAwait(true);
    }

    /// <inheritdoc />
    protected internal override IReadOnlyList<string>? BuildArguments(out string? error) => Arguments(scan: false, out error);

    /// <inheritdoc />
    protected override async Task<JobOutcome> ExecuteAsync(IReadOnlyList<string> args)
    {
        // Same code path as the CLI command, so the log matches; the callback hands over the scan's map.
        bool scan = args.Contains("--scan");
        string image = Image.Trim();
        string? badBlocks = OrNull(BadBlocks);
        string? reportDir = OrNull(ReportDir);
        (string mode, bool recompress, bool cleanSlack) = (Mode, Recompress, CleanSlack);
        PFSCRepairResult? result = null;
        JobOutcome outcome = await Job.RunAsync(job =>
        {
            Job.Echo(args);
            CliContext ctx = job.CreateCliContext();
            PFSCRepairOptions options = RepairCommand.Options(ctx, scan, badBlocks, recompress, mode, reportDir, cleanSlack, cpuCount: 0, progress: true);
            int code = RepairCommand.Execute(ctx, image, options, job.Token, r => result = r);
            return scan && code == RepairCommand.ExitRepairNeeded ? 0 : code; // "repair needed" is a scan result, not a failure
        }, ProgressPlan.For(args)).ConfigureAwait(true);

        // After a repair the scanned map is out of date; scan again to see the new state.
        Selection = null;
        Map = result is { Status: not RepairStatus.Repaired } ? result.Map : null;
        return outcome;
    }

    /// <inheritdoc />
    protected override void OnRunningChanged() => ScanCommand.NotifyCanExecuteChanged();

    private bool CanScan() => !Job.IsRunning;

    private IReadOnlyList<string>? Arguments(bool scan, out string? error)
    {
        string image = Image.Trim();
        error = image.Length == 0 ? Localizer.Instance["r_err"] : null;
        if (error is not null)
        {
            return null;
        }

        List<string> args = ["repair", image];
        AddFlag(args, scan, "--scan");
        AddOption(args, "--bad-blocks", BadBlocks);
        AddFlag(args, Recompress, "--recompress");
        if (Mode != "auto")
        {
            args.Add("--mode");
            args.Add(Mode);
        }

        AddOption(args, "--report-dir", ReportDir);
        AddFlag(args, !CleanSlack, "--no-slack-cleanup");
        return args;
    }

    partial void OnImageChanged(string value)
    {
        _ = Metadata.LoadAsync(value);
        Map = null;
        Selection = null;
    }

    private static List<string> Describe(PFSCBlockMap map, BlockRange range)
    {
        Localizer tr = Localizer.Instance;
        List<string> lines = [];
        long[] counts = new long[4];
        long worst = range.First;
        for (long block = range.First; block <= range.Last; block++)
        {
            BlockState state = BlockMap.StateOf(map, block);
            counts[(int)state]++;
            if (state > BlockMap.StateOf(map, worst))
            {
                worst = block;
            }
        }

        if (range.First != range.Last)
        {
            lines.Add(string.Join(" · ", new[] { ("r_legend_zlib", BlockState.Zlib), ("r_legend_raw", BlockState.Raw), ("r_legend_risky", BlockState.Risky), ("r_legend_bad", BlockState.Undecodable) }
                .Where(p => counts[(int)p.Item2] > 0)
                .Select(p => $"{Thousands(counts[(int)p.Item2])} {tr[p.Item1]}")));
            lines.Add(tr.Format("r_block", Thousands(worst)) + ":");
        }

        BlockState worstState = BlockMap.StateOf(map, worst);
        string kind = tr[worstState switch
        {
            BlockState.Raw => "r_legend_raw",
            BlockState.Zlib => "r_legend_zlib",
            BlockState.Risky => "r_legend_risky",
            _ => "r_legend_bad",
        }];
        lines.Add($"{tr["r_detail_offset"]}: 0x{map.Offsets[worst]:X}");
        lines.Add($"{tr["r_detail_stored"]}: {Thousands(map.StoredLength(worst))} B ({kind})");
        if (!map.IsRaw(worst))
        {
            lines.Add($"{tr["r_detail_distance"]}: {Thousands(map.MaxDistances[worst])} ({tr.Format("r_detail_limit", Thousands(DeflateInspector.ZlibMaxDistance))})");
        }

        return lines;
    }

    private static string? OrNull(string value) => value.Trim() is { Length: > 0 } trimmed ? trimmed : null;

    private static string Thousands(long value) => value.ToString("#,0", CultureInfo.InvariantCulture);
}
