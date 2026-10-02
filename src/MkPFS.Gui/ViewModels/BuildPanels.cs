using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using MkPFS.Gui.Jobs;
using MkPFS.Gui.Localization;

namespace MkPFS.Gui.ViewModels;

/// <summary>Pack Folder page (Python <c>PackFolderPanel</c>): <c>mkpfs pack folder</c>.</summary>
/// <param name="accent">Accent color.</param>
/// <param name="job">Job runner, or <see langword="null"/> for the UI-thread runner.</param>
public sealed partial class PackFolderPanelViewModel(Color accent, JobRunner? job = null) : PanelViewModel("pf_title", "pf_subtitle", accent, job)
{
    /// <summary>Cover and details of the source.</summary>
    public MetadataPreviewViewModel Metadata { get; } = new();

    /// <summary>Source folder.</summary>
    [ObservableProperty]
    public partial string Source { get; set; } = string.Empty;

    /// <summary>Output image.</summary>
    [ObservableProperty]
    public partial string Output { get; set; } = string.Empty;

    /// <summary>PFSC compression (off adds <c>--no-compress</c>).</summary>
    [ObservableProperty]
    public partial bool Compress { get; set; } = true;

    /// <summary><c>--signed</c>.</summary>
    [ObservableProperty]
    public partial bool Signed { get; set; }

    /// <summary><c>--verify</c>.</summary>
    [ObservableProperty]
    public partial bool VerifyAfter { get; set; }

    /// <summary><c>--dry-run</c>.</summary>
    [ObservableProperty]
    public partial bool DryRun { get; set; }

    /// <summary><c>--temp-folder</c>, optional.</summary>
    [ObservableProperty]
    public partial string TempFolder { get; set; } = string.Empty;

    /// <inheritdoc />
    public override object Form => this;

    /// <inheritdoc />
    protected internal override IReadOnlyList<string>? BuildArguments(out string? error)
    {
        string source = Source.Trim();
        string output = Output.Trim();
        error = source.Length == 0 ? Localizer.Instance["pf_err_src"] : output.Length == 0 ? Localizer.Instance["pf_err_out"] : null;
        if (error is not null)
        {
            return null;
        }

        List<string> args = ["pack", "folder", source, output];
        AddFlag(args, !Compress, "--no-compress");
        AddFlag(args, Signed, "--signed");
        AddFlag(args, VerifyAfter, "--verify");
        AddFlag(args, DryRun, "--dry-run");
        AddOption(args, "--temp-folder", TempFolder);
        return args;
    }

    // Python _on_src_changed: preview the source and suggest <parent>/<sanitized name>.ffpfsc.
    partial void OnSourceChanged(string value)
    {
        _ = Metadata.LoadAsync(value);
        Output = SuggestOutput(value, Output, Directory.Exists, Path.GetFileName, ".ffpfsc");
    }
}

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
        return args;
    }

    partial void OnSourceChanged(string value)
    {
        _ = Metadata.LoadAsync(value);
        Output = SuggestOutput(value, Output, Directory.Exists, Path.GetFileName, ".exfat");
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
        return args;
    }

    partial void OnSourceChanged(string value)
    {
        _ = Metadata.LoadAsync(value);
        Output = SuggestOutput(value, Output, File.Exists, Path.GetFileNameWithoutExtension, ".ffpfsc");
    }
}

/// <summary>Batch page (Python <c>BatchPanel</c>): <c>mkpfs batch</c>.</summary>
/// <param name="accent">Accent color.</param>
/// <param name="job">Job runner, or <see langword="null"/> for the UI-thread runner.</param>
public sealed partial class BatchPanelViewModel(Color accent, JobRunner? job = null) : PanelViewModel("bt_title", "bt_subtitle", accent, job)
{
    /// <summary>Items the batch would convert.</summary>
    public BatchQueueViewModel Queue { get; } = new(new Avalonia.Media.Immutable.ImmutableSolidColorBrush(accent));

    /// <summary>Source folder.</summary>
    [ObservableProperty]
    public partial string Source { get; set; } = string.Empty;

    /// <summary>Output folder.</summary>
    [ObservableProperty]
    public partial string Output { get; set; } = string.Empty;

    /// <summary>PFSC compression (off adds <c>--no-compress</c>).</summary>
    [ObservableProperty]
    public partial bool Compress { get; set; } = true;

    /// <summary><c>--overwrite</c>.</summary>
    [ObservableProperty]
    public partial bool Overwrite { get; set; }

    /// <summary><c>--dry-run</c>.</summary>
    [ObservableProperty]
    public partial bool DryRun { get; set; }

    /// <summary><c>--verify</c>.</summary>
    [ObservableProperty]
    public partial bool VerifyAfter { get; set; }

    /// <inheritdoc />
    public override object Form => this;

    /// <inheritdoc />
    protected internal override IReadOnlyList<string>? BuildArguments(out string? error)
    {
        string source = Source.Trim();
        string output = Output.Trim();
        error = source.Length == 0 ? Localizer.Instance["bt_err_src"] : output.Length == 0 ? Localizer.Instance["bt_err_out"] : null;
        if (error is not null)
        {
            return null;
        }

        List<string> args = ["batch", source, output];
        AddFlag(args, !Compress, "--no-compress");
        AddFlag(args, Overwrite, "--overwrite");
        AddFlag(args, DryRun, "--dry-run");
        AddFlag(args, VerifyAfter, "--verify");
        return args;
    }

    // Python: preview the queue; the output folder defaults to the source folder.
    partial void OnSourceChanged(string value)
    {
        _ = Queue.LoadAsync(value);
        if (Output.Trim().Length == 0 && value.Trim() is { Length: > 0 } source && Directory.Exists(source))
        {
            Output = source;
        }
    }
}
