using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using MkPFS.Gui.Jobs;
using MkPFS.Gui.Localization;

namespace MkPFS.Gui.ViewModels;

/// <summary>Pack exFAT page (Python <c>ExfatPanel</c>): <c>mkpfs pack exfat</c>.</summary>
/// <param name="accent">Accent color.</param>
/// <param name="job">Job runner, or <see langword="null"/> for the UI-thread runner.</param>
public sealed partial class PackExfatPanelViewModel(Color accent, JobRunner? job = null) : PanelViewModel("exf_title", "exf_subtitle", accent, job)
{
    /// <summary>Cover and details of the source.</summary>
    public MetadataPreviewViewModel Metadata { get; } = new();

    /// <summary>Source folder.</summary>
    [ObservableProperty]
    public partial string Source { get; set; } = string.Empty;

    /// <summary>Output image; the CLI default when empty.</summary>
    [ObservableProperty]
    public partial string Output { get; set; } = string.Empty;

    /// <summary><c>--overwrite</c>.</summary>
    [ObservableProperty]
    public partial bool Overwrite { get; set; }

    /// <summary>APR Emu libraries and index.</summary>
    public AmprSettingsViewModel Ampr { get; } = new();

    private static readonly Choice[] ClusterChoices = [new("auto", "adv_cluster_auto"), .. Enumerable.Range(12, 14).Select(bits => SizeChoice(1 << bits))];

    /// <summary>Cluster sizes in picker order: auto, then 4 KiB to 32 MiB.</summary>
    public IReadOnlyList<Choice> ClusterSizes => ClusterChoices;

    /// <summary><c>--cluster-size</c>.</summary>
    [ObservableProperty]
    public partial Choice ClusterSize { get; set; } = ClusterChoices[0];

    /// <summary><c>--verbose</c>.</summary>
    [ObservableProperty]
    public partial bool Verbose { get; set; }

    /// <inheritdoc />
    public override object Form => this;

    /// <inheritdoc />
    protected internal override IReadOnlyList<string>? BuildArguments(out string? error)
    {
        string source = Source.Trim();
        error = source.Length == 0 ? Localizer.Instance["exf_err_src"] : null;
        if (error is not null)
        {
            return null;
        }

        List<string> args = ["pack", "exfat", source];
        if (Output.Trim() is { Length: > 0 } output)
        {
            args.Add(output);
        }

        AddFlag(args, Overwrite, "--overwrite");
        if (ClusterSize is { Value: not "auto" } cluster)
        {
            args.Add("--cluster-size");
            args.Add(cluster.Value);
        }

        AddFlag(args, Verbose, "--verbose");
        Ampr.AppendTo(args);
        return args;
    }

    private static Choice SizeChoice(int size) =>
        new(size.ToString(CultureInfo.InvariantCulture), null, size >= 1024 * 1024 ? $"{size / (1024 * 1024)} MiB" : $"{size / 1024} KiB");

    partial void OnSourceChanged(string value)
    {
        _ = Metadata.LoadAsync(value);
        Output = SuggestOutput(value, Output, Directory.Exists, Path.GetFileName, ".exfat");
        Ampr.NoteSource(AmprSettingsViewModel.HasIndex(value));
    }
}

/// <summary>Pack File page (Python <c>PackFilePanel</c>): <c>mkpfs pack file</c>.</summary>
/// <param name="accent">Accent color.</param>
/// <param name="job">Job runner, or <see langword="null"/> for the UI-thread runner.</param>
public sealed partial class PackFilePanelViewModel(Color accent, JobRunner? job = null) : PanelViewModel("pkf_title", "pkf_subtitle", accent, job)
{
    /// <summary>Picker filters for the source (Python <c>filetypes</c>).</summary>
    public const string SourceFilters =
        "Game images:*.exfat *.ffpkg *.ffpfs *.ffpfsc|PFS image:*.ffpfs *.ffpfsc|exFAT image:*.exfat|FFPKG image:*.ffpkg|All files:*.*";

    /// <summary>Cover and details of the source.</summary>
    public MetadataPreviewViewModel Metadata { get; } = new();

    /// <summary>Compression tuning.</summary>
    public CompressionSettingsViewModel Compression { get; } = new(allowAutoFit: true, offerSkipExecutables: false);

    /// <summary>Source file.</summary>
    [ObservableProperty]
    public partial string Source { get; set; } = string.Empty;

    /// <summary>Output image.</summary>
    [ObservableProperty]
    public partial string Output { get; set; } = string.Empty;

    /// <summary>PFSC compression (off adds <c>--no-compress</c>).</summary>
    [ObservableProperty]
    public partial bool Compress { get; set; } = true;

    /// <summary><c>--temp-folder</c>, optional.</summary>
    [ObservableProperty]
    public partial string TempFolder { get; set; } = string.Empty;

    /// <summary>Advanced PFS options.</summary>
    public PFSOptionsViewModel PFS { get; } = new(offerInodeBits: true, offerNewCrypt: false);

    /// <summary><c>--signed</c>.</summary>
    [ObservableProperty]
    public partial bool Signed { get; set; }

    /// <summary><c>--verify</c>.</summary>
    [ObservableProperty]
    public partial bool VerifyAfter { get; set; }

    /// <summary><c>--dry-run</c>.</summary>
    [ObservableProperty]
    public partial bool DryRun { get; set; }

    /// <summary><c>--skip-verification</c> (not with <see cref="VerifyAfter"/>).</summary>
    [ObservableProperty]
    public partial bool SkipVerification { get; set; }

    /// <summary><c>--no-adjust-output-file-extension</c>.</summary>
    [ObservableProperty]
    public partial bool KeepExtension { get; set; }

    /// <summary><c>--use-spool</c>.</summary>
    [ObservableProperty]
    public partial bool UseSpool { get; set; }

    /// <summary>Keep the source file name inside the image (<c>--no-rename-inner-image</c>).</summary>
    [ObservableProperty]
    public partial bool KeepInnerName { get; set; }

    /// <inheritdoc />
    public override object Form => this;

    /// <inheritdoc />
    protected internal override IReadOnlyList<string>? BuildArguments(out string? error)
    {
        string source = Source.Trim();
        string output = Output.Trim();
        error = source.Length == 0 || output.Length == 0 ? Localizer.Instance["pkf_err"] : null;
        if (error is not null)
        {
            return null;
        }

        List<string> args = ["pack", "file", source, output];
        AddFlag(args, !Compress, "--no-compress");
        AddOption(args, "--temp-folder", TempFolder);
        AddFlag(args, Signed, "--signed");
        AddFlag(args, VerifyAfter, "--verify");
        AddFlag(args, DryRun, "--dry-run");
        AddFlag(args, SkipVerification && !VerifyAfter, "--skip-verification");
        AddFlag(args, KeepExtension, "--no-adjust-output-file-extension");
        AddFlag(args, UseSpool, "--use-spool");
        AddFlag(args, KeepInnerName, "--no-rename-inner-image");
        PFS.AppendTo(args);
        return Compression.AppendTo(args, Compress, out error) ? args : null;
    }

    partial void OnSourceChanged(string value)
    {
        _ = Metadata.LoadAsync(value);
        Output = SuggestOutput(value, Output, File.Exists, Path.GetFileNameWithoutExtension, ".ffpfsc");
    }
}
