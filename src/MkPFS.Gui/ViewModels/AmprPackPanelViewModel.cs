using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using MkPFS.Gui.Jobs;
using MkPFS.Gui.Localization;

namespace MkPFS.Gui.ViewModels;

/// <summary>
/// AMPR Packs page: runs <c>mkpfs ampr</c> (game, pack, verify, unpack, list, inspect, runtime-config, remove-sources).
/// Only the fields the selected action uses are shown.
/// </summary>
/// <param name="accent">Accent color.</param>
/// <param name="job">Job runner, or <see langword="null"/> for the UI-thread runner.</param>
public sealed partial class AmprPackPanelViewModel(Color accent, JobRunner? job = null) : PanelViewModel("ap_title", "ap_subtitle", accent, job)
{
    private static readonly Choice[] ActionChoices =
    [
        new("game", "ap_act_game"),
        new("pack", "ap_act_pack"),
        new("verify", "ap_act_verify"),
        new("unpack", "ap_act_unpack"),
        new("list", "ap_act_list"),
        new("inspect", "ap_act_inspect"),
        new("runtime-config", "ap_act_runtime"),
        new("remove-sources", "ap_act_remove"),
    ];

    /// <summary>Actions, in menu order; values are the CLI subcommands.</summary>
    public IReadOnlyList<Choice> Actions => ActionChoices;

    /// <summary>Selected action.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRoot), nameof(ShowPackPaths), nameof(ShowOutput), nameof(ShowManifest), nameof(ShowConfig))]
    [NotifyPropertyChangedFor(nameof(ShowPackOptions), nameof(ShowOverwrite), nameof(ShowJson), nameof(ShowConfirm))]
    [NotifyPropertyChangedFor(nameof(ShowGameOptions), nameof(ShowAllowMissing), nameof(ShowExfatPath), nameof(ShowPackNote))]
    [NotifyPropertyChangedFor(nameof(ShowRulesMissing), nameof(ShowRulesConfig), nameof(ShowRulesTraces), nameof(ShowNoTraces))]
    public partial Choice Action { get; set; } = ActionChoices[0];

    /// <summary><c>--fakelib</c> (game): folder with AMPR Emu and other libraries to add.</summary>
    [ObservableProperty]
    public partial string Libs { get; set; } = string.Empty;

    /// <summary>Verify the packs and loose files after a game build (<c>--skip-verify</c> when off).</summary>
    [ObservableProperty]
    public partial bool VerifyGame { get; set; } = true;

    /// <summary>Also build an exFAT image of the game folder (<c>--exfat</c>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowExfatPath))]
    public partial bool Exfat { get; set; }

    /// <summary>exFAT image path; empty for <c>&lt;titleId&gt;.exfat</c> next to the output folder.</summary>
    [ObservableProperty]
    public partial string ExfatPath { get; set; } = string.Empty;

    /// <summary><c>--exfat-free-space</c>: free space inside the image, e.g. <c>2GiB</c>; empty for none.</summary>
    [ObservableProperty]
    public partial string ExfatFree { get; set; } = string.Empty;

    /// <summary><c>--root</c>: the game's <c>/app0</c> folder.</summary>
    [ObservableProperty]
    public partial string Root { get; set; } = string.Empty;

    /// <summary><c>--ampr-index</c>; defaults to <c>&lt;root&gt;/ampr_emu.index</c>.</summary>
    [ObservableProperty]
    public partial string AmprIndex { get; set; } = string.Empty;

    /// <summary><c>--output</c>.</summary>
    [ObservableProperty]
    public partial string Output { get; set; } = string.Empty;

    /// <summary><c>--index</c>: the pack manifest.</summary>
    [ObservableProperty]
    public partial string Manifest { get; set; } = string.Empty;

    /// <summary><c>--config</c>: the game's TOML profile; game and pack need it or a trace folder, runtime-config needs it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRulesMissing), nameof(ShowRulesConfig), nameof(ShowRulesTraces), nameof(ShowNoTraces))]
    public partial string Config { get; set; } = string.Empty;

    /// <summary><c>--traces</c> (game, pack): folder with APR traces of the debug emulator build.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRulesMissing), nameof(ShowRulesConfig), nameof(ShowRulesTraces), nameof(ShowNoTraces))]
    [NotifyPropertyChangedFor(nameof(RulesTracesText))]
    public partial string Traces { get; set; } = string.Empty;

    /// <summary><c>--pack-untraced-types</c>: also pack untraced files of the traced file types.</summary>
    [ObservableProperty]
    public partial bool UntracedTypes { get; set; }

    /// <summary><c>--workers</c>; empty for the configuration's value.</summary>
    [ObservableProperty]
    public partial string Workers { get; set; } = string.Empty;

    /// <summary><c>--self-contained</c>.</summary>
    [ObservableProperty]
    public partial bool SelfContained { get; set; }

    /// <summary><c>--allow-missing</c>.</summary>
    [ObservableProperty]
    public partial bool AllowMissing { get; set; }

    /// <summary><c>--overwrite</c> (unpack).</summary>
    [ObservableProperty]
    public partial bool Overwrite { get; set; }

    /// <summary><c>--json</c> (list).</summary>
    [ObservableProperty]
    public partial bool Json { get; set; }

    /// <summary><c>--confirm</c> (remove-sources): delete instead of printing the plan.</summary>
    [ObservableProperty]
    public partial bool Confirm { get; set; }

    /// <summary>Game folder field (pack, verify, remove-sources).</summary>
    public bool ShowRoot => Action.Value is "game" or "pack" or "verify" or "remove-sources";

    /// <summary>AMPR index field (pack).</summary>
    public bool ShowPackPaths => Action.Value == "pack";

    /// <summary>Output folder field (game, pack, unpack).</summary>
    public bool ShowOutput => Action.Value is "game" or "pack" or "unpack";

    /// <summary>Pack manifest field (every action but game and pack).</summary>
    public bool ShowManifest => Action.Value is not ("game" or "pack");

    /// <summary>Configuration field (game, pack, runtime-config).</summary>
    public bool ShowConfig => Action.Value is "game" or "pack" or "runtime-config";

    /// <summary>Workers and self-contained (game, pack).</summary>
    public bool ShowPackOptions => Action.Value is "game" or "pack";

    /// <summary>Allow-missing (pack).</summary>
    public bool ShowAllowMissing => Action.Value == "pack";

    /// <summary>Note that pack writes only the pack set (pack).</summary>
    public bool ShowPackNote => Action.Value == "pack";

    /// <summary>Library folder, verification and exFAT image (game).</summary>
    public bool ShowGameOptions => Action.Value == "game";

    /// <summary>exFAT image path (game with an image).</summary>
    public bool ShowExfatPath => ShowGameOptions && Exfat;

    // Trace runs found in the trace folder, or null when the field is empty.
    private int? _traceRuns;

    /// <summary>The trace folder has runs: the rules come from the traces.</summary>
    public bool ShowRulesTraces => ShowPackOptions && Config.Trim().Length == 0 && _traceRuns > 0;

    /// <summary>The trace folder has no runs.</summary>
    public bool ShowNoTraces => ShowPackOptions && Config.Trim().Length == 0 && _traceRuns == 0;

    /// <summary>How many trace runs were found.</summary>
    public string RulesTracesText => Localizer.Instance.Format("ap_rules_traces", _traceRuns ?? 0);

    /// <summary>Neither a TOML profile nor a trace folder is chosen yet (game, pack).</summary>
    public bool ShowRulesMissing => ShowPackOptions && Config.Trim().Length == 0 && Traces.Trim().Length == 0;

    /// <summary>The user's TOML file decides what is packed.</summary>
    public bool ShowRulesConfig => ShowPackOptions && Config.Trim().Length > 0 && Traces.Trim().Length == 0;

    /// <summary>Overwrite (unpack).</summary>
    public bool ShowOverwrite => Action.Value == "unpack";

    /// <summary>JSON output (list).</summary>
    public bool ShowJson => Action.Value == "list";

    /// <summary>Delete sources (remove-sources).</summary>
    public bool ShowConfirm => Action.Value == "remove-sources";

    /// <inheritdoc />
    public override object Form => this;

    /// <inheritdoc />
    protected internal override IReadOnlyList<string>? BuildArguments(out string? error)
    {
        // Files may have been copied into the folders since they were picked.
        RefreshDetection();
        string root = Root.Trim();
        string manifest = Manifest.Trim();
        string output = Output.Trim();
        string config = Config.Trim();
        string traces = Traces.Trim();
        error = Action.Value switch
        {
            "game" or "pack" when config.Length > 0 && traces.Length > 0 => Localizer.Instance["ap_err_traces_config"],
            "game" when root.Length == 0 || output.Length == 0 => Localizer.Instance["ap_err_pack"],
            "pack" when root.Length == 0 || output.Length == 0 => Localizer.Instance["ap_err_pack"],
            "game" or "pack" when config.Length == 0 && traces.Length == 0 => Localizer.Instance["ap_err_rules"],
            "game" or "pack" when Workers.Trim().Length > 0 && !(int.TryParse(Workers.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n is >= 1 and <= 256)
                => Localizer.Instance["ap_err_workers"],
            "verify" or "list" or "inspect" when manifest.Length == 0 => Localizer.Instance["ap_err_manifest"],
            "unpack" when manifest.Length == 0 || output.Length == 0 => Localizer.Instance["ap_err_unpack"],
            "runtime-config" when manifest.Length == 0 || config.Length == 0 => Localizer.Instance["ap_err_runtime"],
            "remove-sources" when manifest.Length == 0 || root.Length == 0 => Localizer.Instance["ap_err_remove"],
            _ => null,
        };
        if (error is not null)
        {
            return null;
        }

        List<string> args = ["ampr", Action.Value];
        switch (Action.Value)
        {
            case "game":
                args.AddRange(["--root", root, "--output", output]);
                AddOption(args, "--fakelib", Libs);
                AddOption(args, "--config", config);
                AddOption(args, "--traces", traces);
                AddFlag(args, traces.Length > 0 && UntracedTypes, "--pack-untraced-types");
                AddOption(args, "--workers", Workers);
                AddFlag(args, SelfContained, "--self-contained");
                AddFlag(args, !VerifyGame, "--skip-verify");
                if (Exfat)
                {
                    // An existing folder receives <titleId>.exfat: by default the folder that holds the output.
                    args.AddRange(["--exfat", ExfatPath.Trim() is { Length: > 0 } image ? image : Path.GetDirectoryName(Path.GetFullPath(output)) ?? output]);
                    AddOption(args, "--exfat-free-space", ExfatFree);
                }

                break;
            case "pack":
                args.AddRange(["--root", root, "--ampr-index", AmprIndex.Trim() is { Length: > 0 } index ? index : Path.Combine(root, "ampr_emu.index"), "--output", output]);
                AddOption(args, "--config", config);
                AddOption(args, "--traces", traces);
                AddFlag(args, traces.Length > 0 && UntracedTypes, "--pack-untraced-types");
                AddOption(args, "--workers", Workers);
                AddFlag(args, SelfContained, "--self-contained");
                AddFlag(args, AllowMissing, "--allow-missing");
                break;
            case "verify":
                args.AddRange(["--index", manifest]);
                AddOption(args, "--root", root);
                break;
            case "unpack":
                args.AddRange(["--index", manifest, "--output", output]);
                AddFlag(args, Overwrite, "--overwrite");
                break;
            case "list":
                args.AddRange(["--index", manifest]);
                AddFlag(args, Json, "--json");
                break;
            case "inspect":
                args.AddRange(["--index", manifest]);
                break;
            case "runtime-config":
                args.AddRange(["--index", manifest, "--config", config]);
                break;
            default:
                args.AddRange(["--index", manifest, "--root", root]);
                AddFlag(args, Confirm, "--confirm");
                break;
        }

        return args;
    }

    /// <summary>Count the trace runs in the chosen folder (each needs ampr_commands.bin and ampr_emu.index).</summary>
    partial void OnTracesChanged(string value) => CountTraceRuns(value);

    /// <summary>Look at the trace folder again and update the rules lines.</summary>
    public void RefreshDetection()
    {
        CountTraceRuns(Traces);
        OnPropertyChanged(nameof(ShowRulesTraces));
        OnPropertyChanged(nameof(ShowNoTraces));
        OnPropertyChanged(nameof(RulesTracesText));
    }

    private void CountTraceRuns(string value)
    {
        string folder = value.Trim();
        if (folder.Length == 0)
        {
            _traceRuns = null;
            return;
        }

        try
        {
            _traceRuns = Directory.Exists(folder) ? Build.AMPRPack.AMPRProfiler.DiscoverTracePairs(Path.GetFullPath(folder)).Count : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Core.AMPR.AMPRPackException)
        {
            _traceRuns = 0;
        }
    }

    /// <summary>The manifest a pack run writes, so the other actions can use it right away.</summary>
    partial void OnOutputChanged(string value)
    {
        if (Manifest.Length == 0 && value.Trim().Length > 0 && Action.Value is "game" or "pack")
        {
            Manifest = Path.Combine(value.Trim(), Build.AMPRPack.AMPRPackConfig.DefaultIndexName);
        }
    }
}
