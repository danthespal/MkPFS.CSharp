using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using MkPFS.Core.Util;
using MkPFS.Gui.Jobs;
using MkPFS.Gui.Localization;

namespace MkPFS.Gui.ViewModels;

/// <summary>Verify page (Python <c>VerifyPanel</c>): <c>mkpfs verify</c>.</summary>
/// <param name="accent">Accent color.</param>
/// <param name="job">Job runner, or <see langword="null"/> for the UI-thread runner.</param>
public sealed partial class VerifyPanelViewModel(Color accent, JobRunner? job = null) : PanelViewModel("v_title", "v_subtitle", accent, job)
{
    /// <summary>Cover and details of the image.</summary>
    public MetadataPreviewViewModel Metadata { get; } = new();

    /// <summary>Image to verify.</summary>
    [ObservableProperty]
    public partial string Image { get; set; } = string.Empty;

    /// <summary><c>--source-dir</c>, optional payload comparison.</summary>
    [ObservableProperty]
    public partial string SourceDir { get; set; } = string.Empty;

    /// <summary><c>--source-file</c>, optional comparison for a single-file image.</summary>
    [ObservableProperty]
    public partial string SourceFile { get; set; } = string.Empty;

    /// <summary><c>--require-game-files</c>: the PS5 game-file checklist.</summary>
    [ObservableProperty]
    public partial bool RequireGameFiles { get; set; }

    /// <summary>Image formats (<c>--format</c>).</summary>
    public IReadOnlyList<Choice> ImageFormats => ImageFormatChoices;

    /// <summary><c>--format</c>; auto-detect adds nothing.</summary>
    [ObservableProperty]
    public partial Choice ImageFormat { get; set; } = ImageFormatChoices[0];

    /// <summary><c>--expect-crc32</c>.</summary>
    [ObservableProperty]
    public partial string Crc32 { get; set; } = string.Empty;

    /// <summary><c>--expect-manifest-sha256</c>.</summary>
    [ObservableProperty]
    public partial string Sha256 { get; set; } = string.Empty;

    /// <summary><c>--ekpfs-key</c>.</summary>
    [ObservableProperty]
    public partial string Ekpfs { get; set; } = string.Empty;

    /// <summary><c>--new-crypt</c>.</summary>
    [ObservableProperty]
    public partial bool NewCrypt { get; set; }

    /// <inheritdoc />
    public override object Form => this;

    /// <inheritdoc />
    protected internal override IReadOnlyList<string>? BuildArguments(out string? error)
    {
        string image = Image.Trim();
        error = image.Length == 0 ? Localizer.Instance["v_err"]
            : SourceDir.Trim().Length > 0 && SourceFile.Trim().Length > 0 ? Localizer.Instance["v_err_sources"]
            : null;
        if (error is not null)
        {
            return null;
        }

        List<string> args = ["verify", image];
        AddOption(args, "--source-dir", SourceDir);
        AddOption(args, "--source-file", SourceFile);
        AddOption(args, "--expect-crc32", Crc32);
        AddOption(args, "--expect-manifest-sha256", Sha256);
        AddOption(args, "--ekpfs-key", Ekpfs);
        AddFlag(args, NewCrypt, "--new-crypt");
        AddFormat(args, ImageFormat);
        AddFlag(args, RequireGameFiles, "--require-game-files");
        return args;
    }

    partial void OnImageChanged(string value) => _ = Metadata.LoadAsync(value);
}

/// <summary>Inspect page (Python <c>InspectPanel</c>): <c>mkpfs inspect</c>.</summary>
/// <param name="accent">Accent color.</param>
/// <param name="job">Job runner, or <see langword="null"/> for the UI-thread runner.</param>
public sealed partial class InspectPanelViewModel(Color accent, JobRunner? job = null) : PanelViewModel("i_title", "i_subtitle", accent, job)
{
    /// <summary>Output formats (Python <c>OptionRow</c> values).</summary>
    public IReadOnlyList<string> Formats { get; } = ["text", "json"];

    /// <summary>Cover and details of the image.</summary>
    public MetadataPreviewViewModel Metadata { get; } = new();

    /// <summary>Image to inspect.</summary>
    [ObservableProperty]
    public partial string Image { get; set; } = string.Empty;

    /// <summary><c>--format</c>.</summary>
    [ObservableProperty]
    public partial string Format { get; set; } = "text";

    /// <summary><c>--ekpfs-key</c>.</summary>
    [ObservableProperty]
    public partial string Ekpfs { get; set; } = string.Empty;

    /// <summary><c>--new-crypt</c>.</summary>
    [ObservableProperty]
    public partial bool NewCrypt { get; set; }

    /// <inheritdoc />
    public override object Form => this;

    /// <inheritdoc />
    protected internal override IReadOnlyList<string>? BuildArguments(out string? error)
    {
        string image = Image.Trim();
        error = image.Length == 0 ? Localizer.Instance["i_err"] : null;
        if (error is not null)
        {
            return null;
        }

        List<string> args = ["inspect", image, "--format", Format];
        AddOption(args, "--ekpfs-key", Ekpfs);
        AddFlag(args, NewCrypt, "--new-crypt");
        return args;
    }

    partial void OnImageChanged(string value) => _ = Metadata.LoadAsync(value);
}

/// <summary>Tree page (Python <c>TreePanel</c>): <c>mkpfs tree</c> on an image or a source folder.</summary>
/// <param name="accent">Accent color.</param>
/// <param name="job">Job runner, or <see langword="null"/> for the UI-thread runner.</param>
public sealed partial class TreePanelViewModel(Color accent, JobRunner? job = null) : PanelViewModel("t_title", "t_subtitle", accent, job)
{
    /// <summary>Picker filters for the image (Python <c>filetypes</c>).</summary>
    public const string ImageFilters = "Game images:*.ffpfs *.ffpfsc *.exfat|PFS image:*.ffpfs *.ffpfsc|exFAT image:*.exfat|All files:*.*";

    /// <summary>Cover and details of the image or folder.</summary>
    public MetadataPreviewViewModel Metadata { get; } = new();

    /// <summary>Image or folder.</summary>
    [ObservableProperty]
    public partial string Image { get; set; } = string.Empty;

    /// <summary><c>--ekpfs-key</c>.</summary>
    [ObservableProperty]
    public partial string Ekpfs { get; set; } = string.Empty;

    /// <summary><c>--new-crypt</c>.</summary>
    [ObservableProperty]
    public partial bool NewCrypt { get; set; }

    /// <summary><c>--deep</c>: list the files inside a wrapped exFAT (on by default, like Python).</summary>
    [ObservableProperty]
    public partial bool Deep { get; set; } = true;

    /// <summary>Image formats (<c>--format</c>).</summary>
    public IReadOnlyList<Choice> ImageFormats => ImageFormatChoices;

    /// <summary><c>--format</c>; auto-detect adds nothing.</summary>
    [ObservableProperty]
    public partial Choice ImageFormat { get; set; } = ImageFormatChoices[0];

    /// <inheritdoc />
    public override object Form => this;

    /// <inheritdoc />
    protected internal override IReadOnlyList<string>? BuildArguments(out string? error)
    {
        string image = Image.Trim();
        error = image.Length == 0 ? Localizer.Instance["t_err"] : null;
        if (error is not null)
        {
            return null;
        }

        // Python: every input except a bare exFAT image also lists the inner exFAT (--deep).
        List<string> args = ["tree", image];
        AddFlag(args, Deep && !image.EndsWith(".exfat", StringComparison.OrdinalIgnoreCase), "--deep");
        AddOption(args, "--ekpfs-key", Ekpfs);
        AddFlag(args, NewCrypt, "--new-crypt");
        AddFormat(args, ImageFormat);
        return args;
    }

    partial void OnImageChanged(string value) => _ = Metadata.LoadAsync(value);
}

/// <summary>Unpack page (Python <c>UnpackPanel</c>): <c>mkpfs unpack</c>.</summary>
/// <param name="accent">Accent color.</param>
/// <param name="job">Job runner, or <see langword="null"/> for the UI-thread runner.</param>
public sealed partial class UnpackPanelViewModel(Color accent, JobRunner? job = null) : PanelViewModel("u_title", "u_subtitle", accent, job)
{
    /// <summary>Cover and details of the image.</summary>
    public MetadataPreviewViewModel Metadata { get; } = new();

    /// <summary>Image to extract.</summary>
    [ObservableProperty]
    public partial string Image { get; set; } = string.Empty;

    /// <summary>Destination folder.</summary>
    [ObservableProperty]
    public partial string Output { get; set; } = string.Empty;

    /// <summary><c>--overwrite</c>.</summary>
    [ObservableProperty]
    public partial bool Overwrite { get; set; }

    /// <summary><c>--ekpfs-key</c>.</summary>
    [ObservableProperty]
    public partial string Ekpfs { get; set; } = string.Empty;

    /// <summary><c>--new-crypt</c>.</summary>
    [ObservableProperty]
    public partial bool NewCrypt { get; set; }

    /// <summary><c>--deep</c>: extract the files inside a wrapped exFAT.</summary>
    [ObservableProperty]
    public partial bool Deep { get; set; }

    /// <summary><c>--only</c> paths inside the wrapped exFAT, separated by <c>;</c> (used with <see cref="Deep"/>).</summary>
    [ObservableProperty]
    public partial string Only { get; set; } = string.Empty;

    /// <summary>Image formats (<c>--format</c>).</summary>
    public IReadOnlyList<Choice> ImageFormats => ImageFormatChoices;

    /// <summary><c>--format</c>; auto-detect adds nothing.</summary>
    [ObservableProperty]
    public partial Choice ImageFormat { get; set; } = ImageFormatChoices[0];

    /// <inheritdoc />
    public override object Form => this;

    /// <inheritdoc />
    protected internal override IReadOnlyList<string>? BuildArguments(out string? error)
    {
        string image = Image.Trim();
        string output = Output.Trim();
        error = image.Length == 0 || output.Length == 0 ? Localizer.Instance["u_err"] : null;
        if (error is not null)
        {
            return null;
        }

        // Python: an existing folder without --overwrite gets a subfolder named after the image
        // (Desktop -> Desktop/GRIS), so extraction never collides with the folder's content.
        if (Directory.Exists(output) && !Overwrite)
        {
            output = Path.Combine(output, NameRules.UiSanitizeBasename(Path.GetFileNameWithoutExtension(image)));
            Job.Append(new LogLine(Localizer.Instance.Format("u_auto_subdir", output), LogTone.Muted));
        }

        List<string> args = ["unpack", image, output];
        AddFlag(args, Overwrite, "--overwrite");
        AddOption(args, "--ekpfs-key", Ekpfs);
        AddFlag(args, NewCrypt, "--new-crypt");
        AddFlag(args, Deep, "--deep");
        if (Deep)
        {
            foreach (string path in Only.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                args.Add("--only");
                args.Add(path);
            }
        }

        AddFormat(args, ImageFormat);
        return args;
    }

    // Python _on_image_changed: preview the image and suggest <parent>/<sanitized stem>.
    partial void OnImageChanged(string value)
    {
        _ = Metadata.LoadAsync(value);
        Output = SuggestOutput(value, Output, File.Exists, Path.GetFileNameWithoutExtension, string.Empty);
    }
}
