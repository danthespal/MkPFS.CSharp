using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using MkPFS.Gui.Jobs;
using MkPFS.Gui.Localization;

namespace MkPFS.Gui.ViewModels;

/// <summary>
/// AMPR Packs page: runs <c>mkpfs ampr</c> (pack, verify, unpack, list, inspect, runtime-config, remove-sources).
/// Only the fields the selected action uses are shown.
/// </summary>
/// <param name="accent">Accent color.</param>
/// <param name="job">Job runner, or <see langword="null"/> for the UI-thread runner.</param>
public sealed partial class AmprPackPanelViewModel(Color accent, JobRunner? job = null) : PanelViewModel("ap_title", "ap_subtitle", accent, job)
{
    private static readonly Choice[] ActionChoices =
    [
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
    public partial Choice Action { get; set; } = ActionChoices[0];

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

    /// <summary><c>--config</c>; empty packs with <c>--preset default</c>, and runtime-config requires it.</summary>
    [ObservableProperty]
    public partial string Config { get; set; } = string.Empty;

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
    public bool ShowRoot => Action.Value is "pack" or "verify" or "remove-sources";

    /// <summary>AMPR index field (pack).</summary>
    public bool ShowPackPaths => Action.Value == "pack";

    /// <summary>Output folder field (pack, unpack).</summary>
    public bool ShowOutput => Action.Value is "pack" or "unpack";

    /// <summary>Pack manifest field (every action but pack).</summary>
    public bool ShowManifest => Action.Value != "pack";

    /// <summary>Configuration field (pack, runtime-config).</summary>
    public bool ShowConfig => Action.Value is "pack" or "runtime-config";

    /// <summary>Workers, self-contained and allow-missing (pack).</summary>
    public bool ShowPackOptions => Action.Value == "pack";

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
        string root = Root.Trim();
        string manifest = Manifest.Trim();
        string output = Output.Trim();
        string config = Config.Trim();
        error = Action.Value switch
        {
            "pack" when root.Length == 0 || output.Length == 0 => Localizer.Instance["ap_err_pack"],
            "pack" when Workers.Trim().Length > 0 && !(int.TryParse(Workers.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n is >= 1 and <= 256)
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
            case "pack":
                args.AddRange(["--root", root, "--ampr-index", AmprIndex.Trim() is { Length: > 0 } index ? index : Path.Combine(root, "ampr_emu.index"), "--output", output]);
                // Without a TOML every file would stay loose; the page uses the built-in rules instead.
                args.AddRange(config.Length > 0 ? ["--config", config] : ["--preset", "default"]);
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

    /// <summary>The manifest a pack run writes, so the other actions can use it right away.</summary>
    partial void OnOutputChanged(string value)
    {
        if (Manifest.Length == 0 && value.Trim().Length > 0 && Action.Value == "pack")
        {
            Manifest = Path.Combine(value.Trim(), Build.AMPRPack.AMPRPackConfig.DefaultIndexName);
        }
    }
}
