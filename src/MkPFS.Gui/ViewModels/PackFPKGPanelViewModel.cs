using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using MkPFS.Core.Metadata;
using MkPFS.Gui.Jobs;
using MkPFS.Gui.Localization;

namespace MkPFS.Gui.ViewModels;

/// <summary>Pack FPKG page: <c>mkpfs pack fpkg</c> (PS5 fake-signed debug package).</summary>
/// <param name="accent">Accent color.</param>
/// <param name="job">Job runner, or <see langword="null"/> for the UI-thread runner.</param>
public sealed partial class PackFPKGPanelViewModel : PanelViewModel
{
    private static readonly Choice[] CompressionChoices = [new("auto", "fpk_comp_auto"), new("fast", "fpk_comp_fast"), new("stored", "fpk_comp_stored")];
    private static readonly Choice[] DrmChoices = [.. PS5ParamJson.DrmTypes.Select(d => new Choice(d, null, d))];

    /// <summary>Create the page.</summary>
    /// <param name="accent">Accent color.</param>
    /// <param name="job">Job runner, or <see langword="null"/> for the UI-thread runner.</param>
    public PackFPKGPanelViewModel(Color accent, JobRunner? job = null)
        : base("fpk_title", "fpk_subtitle", accent, job)
    {
        CpuChoices = [AutoCpuChoice(), .. Enumerable.Range(1, MaxCpu).Select(n => new Choice(n.ToString(CultureInfo.InvariantCulture), null, n.ToString(CultureInfo.InvariantCulture)))];
        CpuChoice = CpuChoices[0];
        Localizer.Instance.PropertyChanged += (_, _) =>
        {
            // The Auto label carries the core count, so it is rebuilt for the new language.
            bool auto = CpuChoice.Value == "0";
            CpuChoices[0] = AutoCpuChoice();
            if (auto)
            {
                CpuChoice = CpuChoices[0];
            }
        };
    }

    /// <summary>
    /// Logical processors of this machine: what Auto uses, and the most the picker offers (Kraken waits on memory, so
    /// simultaneous multithreading pays, unlike zlib).
    /// </summary>
    public int MaxCpu { get; } = Environment.ProcessorCount;

    /// <summary>What Auto passes: every logical processor, fewer when memory is short.</summary>
    public int AutoCpu { get; } = MkPFS.Core.Compression.Kraken.PS5KrakenChunk.MaxParallelism;

    /// <summary>The CPU picker: Auto (every logical processor), then 1 to <see cref="MaxCpu"/>.</summary>
    public ObservableCollection<Choice> CpuChoices { get; }

    /// <summary><c>--cpu-count</c>: Kraken compression workers.</summary>
    [ObservableProperty]
    public partial Choice CpuChoice { get; set; }

    /// <summary>Cover and details of the source.</summary>
    public MetadataPreviewViewModel Metadata { get; } = new();

    /// <summary>Application folder.</summary>
    [ObservableProperty]
    public partial string Source { get; set; } = string.Empty;

    /// <summary>Package path; suggested from the source's content id.</summary>
    [ObservableProperty]
    public partial string Output { get; set; } = string.Empty;

    /// <summary><c>--content-id</c>; empty takes param.json's.</summary>
    [ObservableProperty]
    public partial string ContentId { get; set; } = string.Empty;

    /// <summary><c>--passcode</c>; empty is 32 zeros.</summary>
    [ObservableProperty]
    public partial string Passcode { get; set; } = string.Empty;

    /// <summary>Compression modes.</summary>
    public IReadOnlyList<Choice> Compressions => CompressionChoices;

    /// <summary><c>--compression</c>.</summary>
    [ObservableProperty]
    public partial Choice Compression { get; set; } = CompressionChoices[0];

    /// <summary><c>--verify</c>.</summary>
    [ObservableProperty]
    public partial bool VerifyAfter { get; set; } = true;

    /// <summary><c>--dry-run</c>.</summary>
    [ObservableProperty]
    public partial bool DryRun { get; set; }

    /// <summary><c>--temp-folder</c>; empty stages the inner image next to the package.</summary>
    [ObservableProperty]
    public partial string TempFolder { get; set; } = string.Empty;

    /// <summary><c>--verbose</c>.</summary>
    [ObservableProperty]
    public partial bool Verbose { get; set; }

    /// <summary><c>--no-fake-sign</c>.</summary>
    [ObservableProperty]
    public partial bool NoFakeSign { get; set; }

    /// <summary><c>--seed</c>, 32 hex digits; empty derives it.</summary>
    [ObservableProperty]
    public partial string Seed { get; set; } = string.Empty;

    /// <summary><c>--title</c> of a generated param.json.</summary>
    [ObservableProperty]
    public partial string AppTitle { get; set; } = string.Empty;

    /// <summary><c>--app-version</c> of a generated param.json.</summary>
    [ObservableProperty]
    public partial string AppVersion { get; set; } = string.Empty;

    /// <summary>DRM types of a generated param.json.</summary>
    public IReadOnlyList<Choice> DrmTypes => DrmChoices;

    /// <summary><c>--drm-type</c> of a generated param.json.</summary>
    [ObservableProperty]
    public partial Choice DrmType { get; set; } = DrmChoices[0];

    /// <inheritdoc />
    public override object Form => this;

    /// <inheritdoc />
    protected internal override IReadOnlyList<string>? BuildArguments(out string? error)
    {
        string source = Source.Trim();
        error = source.Length == 0 ? Localizer.Instance["fpk_err_src"] : null;
        if (error is not null)
        {
            return null;
        }

        List<string> args = ["pack", "fpkg", source];
        if (Output.Trim() is { Length: > 0 } output)
        {
            args.Add(output);
        }

        AddOption(args, "--content-id", ContentId);
        AddOption(args, "--passcode", Passcode);
        args.Add("--compression");
        args.Add(Compression.Value);
        int cpu = int.Parse(CpuChoice.Value, CultureInfo.InvariantCulture);
        args.Add("--cpu-count");
        args.Add((cpu > 0 ? cpu : AutoCpu).ToString(CultureInfo.InvariantCulture));
        AddFlag(args, VerifyAfter && !DryRun, "--verify");
        AddFlag(args, DryRun, "--dry-run");
        AddFlag(args, NoFakeSign, "--no-fake-sign");
        AddOption(args, "--temp-folder", TempFolder);
        AddFlag(args, Verbose, "--verbose");
        AddOption(args, "--seed", Seed);
        AddOption(args, "--title", AppTitle);
        AddOption(args, "--app-version", AppVersion);
        if (DrmType.Value != DrmChoices[0].Value)
        {
            args.Add("--drm-type");
            args.Add(DrmType.Value);
        }

        return args;
    }

    private Choice AutoCpuChoice() => new("0", null, Localizer.Instance.Format("ct_cpu_auto", AutoCpu));

    partial void OnSourceChanged(string value)
    {
        _ = Metadata.LoadAsync(value);
        Output = SuggestOutput(value, Output, Directory.Exists, PackageName, ".pkg");
    }

    // The CLI's default name, <content-id>-A<version>-V0100, when param.json gives a content id; else the folder name.
    private static string PackageName(string folder)
    {
        string param = Path.Combine(folder, "sce_sys", "param.json");
        byte[]? json;
        try
        {
            json = File.Exists(param) ? File.ReadAllBytes(param) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A locked or unreadable param.json only loses the suggestion; the CLI reports it on Run.
            json = null;
        }

        string? id = json is null ? null : PS5ParamJson.ReadString(json, "contentId");
        if (!PS5ParamJson.IsContentId(id))
        {
            return Path.GetFileName(folder);
        }

        string version = PS5ParamJson.ReadString(json!, "masterVersion") ?? "01.00";
        return $"{id}-A{version.Replace(".", string.Empty, StringComparison.Ordinal)}-V0100";
    }
}
