using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MkPFS.Gui.Localization;

namespace MkPFS.Gui.ViewModels;

/// <summary>A picker entry with a localized or fixed label.</summary>
public sealed partial class Choice : ObservableObject
{
    private readonly string? _labelKey;
    private readonly string _label;

    /// <summary>Create an entry.</summary>
    /// <param name="value">Value passed to the CLI or used as a key.</param>
    /// <param name="labelKey">String key, or <see langword="null"/> to show <paramref name="label"/>.</param>
    /// <param name="label">Fixed label when <paramref name="labelKey"/> is <see langword="null"/>.</param>
    public Choice(string value, string? labelKey, string label = "")
    {
        Value = value;
        _labelKey = labelKey;
        _label = label;
        if (labelKey is not null)
        {
            Localizer.Instance.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Label));
        }
    }

    /// <summary>Value.</summary>
    public string Value { get; }

    /// <summary>Text shown in the picker.</summary>
    public string Label => _labelKey is null ? _label : Localizer.Instance[_labelKey];

    /// <inheritdoc />
    public override string ToString() => Label;
}

/// <summary>
/// Compression tuning shared by the packing pages: presets over the zlib level and worker count, plus the
/// block size and the per-block keep rules. Only values that differ from the CLI defaults become arguments.
/// </summary>
public sealed partial class CompressionSettingsViewModel : ObservableObject
{
    /// <summary>zlib level used by the CLI (and Game Compressor) when none is given.</summary>
    public const int DefaultLevel = 7;

    private bool _applyingPreset;

    /// <summary>Create the settings.</summary>
    /// <param name="allowAutoFit">Offer "auto-fit" block sizing (not accepted by <c>batch</c>).</param>
    /// <param name="offerSkipExecutables">Show "skip compression of executables" (folder sources).</param>
    public CompressionSettingsViewModel(bool allowAutoFit, bool offerSkipExecutables)
    {
        OfferSkipExecutables = offerSkipExecutables;
        Presets = [new("fast", "ct_preset_fast"), new("balanced", "ct_preset_balanced"), new("max", "ct_preset_max"), new("low_ram", "ct_preset_low_ram"), new("custom", "ct_preset_custom")];
        List<Choice> sizes = [new("auto", "ct_block_auto")];
        if (allowAutoFit)
        {
            sizes.Add(new("auto-fit", "ct_block_fit"));
        }

        for (int size = 4096; size <= 2 * 1024 * 1024; size *= 2)
        {
            sizes.Add(new(size.ToString(CultureInfo.InvariantCulture), null, size >= 1024 * 1024 ? $"{size / (1024 * 1024)} MiB" : $"{size / 1024} KiB"));
        }

        BlockSizes = sizes;
        CpuChoices = [AutoCpuChoice(), .. Enumerable.Range(1, MaxCpu).Select(n => new Choice(n.ToString(CultureInfo.InvariantCulture), null, n.ToString(CultureInfo.InvariantCulture)))];
        Preset = Presets[1];
        BlockSize = BlockSizes[0];
        Localizer.Instance.PropertyChanged += (_, _) =>
        {
            // The Auto label carries the core count, so it is rebuilt for the new language.
            CpuChoices[0] = AutoCpuChoice();
            OnPropertyChanged(nameof(CpuChoice));
        };
    }

    private Choice AutoCpuChoice() => new("0", null, Localizer.Instance.Format("ct_cpu_auto", MaxCpu));

    /// <summary>Presets in picker order.</summary>
    public IReadOnlyList<Choice> Presets { get; }

    /// <summary>Block sizes in picker order.</summary>
    public IReadOnlyList<Choice> BlockSizes { get; }

    /// <summary>Show the executables option.</summary>
    public bool OfferSkipExecutables { get; }

    /// <summary>CPU cores of this machine: what Auto uses, and the most the picker offers.</summary>
    public int MaxCpu { get; } = Environment.ProcessorCount;

    /// <summary>The CPU core picker: Auto (every core of this machine), then 1 to <see cref="MaxCpu"/>.</summary>
    public System.Collections.ObjectModel.ObservableCollection<Choice> CpuChoices { get; }

    /// <summary>The selected entry of <see cref="CpuChoices"/>, kept in step with <see cref="CpuCount"/>.</summary>
    public Choice CpuChoice
    {
        get => CpuChoices[Math.Clamp(Whole(CpuCount, 0), 0, MaxCpu)];
        set
        {
            if (value is not null)
            {
                CpuCount = int.Parse(value.Value, CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>Selected preset; "custom" once a value no longer matches one.</summary>
    [ObservableProperty]
    public partial Choice Preset { get; set; }

    /// <summary><c>--compression-level</c> (0 to 9).</summary>
    [ObservableProperty]
    public partial decimal? Level { get; set; } = DefaultLevel;

    /// <summary><c>--cpu-count</c>; 0 is Auto, which uses every core of this machine.</summary>
    [ObservableProperty]
    public partial decimal? CpuCount { get; set; } = 0;

    /// <summary><c>--block-size</c>.</summary>
    [ObservableProperty]
    public partial Choice BlockSize { get; set; }

    /// <summary><c>--threshold-gain</c> (percent).</summary>
    [ObservableProperty]
    public partial decimal? ThresholdGain { get; set; } = 0;

    /// <summary><c>--max-compressed-ratio</c> (percent).</summary>
    [ObservableProperty]
    public partial decimal? MaxRatio { get; set; } = 100;

    /// <summary><c>--min-compress-size</c> in bytes; empty for the CLI default (the block size).</summary>
    [ObservableProperty]
    public partial string MinCompressSize { get; set; } = string.Empty;

    /// <summary><c>--skip-executable-compression</c>.</summary>
    [ObservableProperty]
    public partial bool SkipExecutables { get; set; }

    /// <summary>Append the non-default settings to <paramref name="args"/>.</summary>
    /// <param name="args">CLI arguments.</param>
    /// <param name="compress">Compression is on; the zlib settings only apply then.</param>
    /// <param name="error">Message for the log when a value is invalid.</param>
    /// <returns><see langword="false"/> with <paramref name="error"/> set when a value is invalid.</returns>
    public bool AppendTo(List<string> args, bool compress, out string? error)
    {
        error = null;
        long minSize = 0;
        if (compress && MinCompressSize.Trim() is { Length: > 0 } text && (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out minSize) || minSize > int.MaxValue))
        {
            error = Localizer.Instance["ct_err_min_size"];
            return false;
        }

        if (compress)
        {
            Add(args, "--compression-level", Whole(Level, DefaultLevel), DefaultLevel);
            // The CLI's own default leaves a core free and stops at 16; Auto here means every core.
            int cpu = Whole(CpuCount, 0);
            args.Add("--cpu-count");
            args.Add((cpu > 0 ? cpu : MaxCpu).ToString(CultureInfo.InvariantCulture));
            Add(args, "--threshold-gain", Whole(ThresholdGain, 0), 0);
            Add(args, "--max-compressed-ratio", Whole(MaxRatio, 100), 100);
            if (MinCompressSize.Trim().Length > 0)
            {
                args.Add("--min-compress-size");
                args.Add(minSize.ToString(CultureInfo.InvariantCulture));
            }

            if (OfferSkipExecutables && SkipExecutables)
            {
                args.Add("--skip-executable-compression");
            }
        }

        if (BlockSize.Value != "auto")
        {
            args.Add("--block-size");
            args.Add(BlockSize.Value);
        }

        return true;
    }

    partial void OnPresetChanged(Choice value)
    {
        (int Level, int Cpu)? settings = value.Value switch
        {
            "fast" => (1, 0),
            "balanced" => (DefaultLevel, 0),
            "max" => (9, 0),
            "low_ram" => (DefaultLevel, 1), // one worker holds one block in flight
            _ => null,
        };
        if (settings is not { } preset)
        {
            return;
        }

        _applyingPreset = true;
        Level = preset.Level;
        CpuCount = preset.Cpu;
        _applyingPreset = false;
    }

    partial void OnLevelChanged(decimal? value) => MatchPreset();

    partial void OnCpuCountChanged(decimal? value)
    {
        OnPropertyChanged(nameof(CpuChoice));
        MatchPreset();
    }

    // A hand-edited level or core count selects the preset it equals, or Custom.
    private void MatchPreset()
    {
        if (_applyingPreset)
        {
            return;
        }

        string key = (Whole(Level, DefaultLevel), Whole(CpuCount, 0)) switch
        {
            (1, 0) => "fast",
            (DefaultLevel, 0) => "balanced",
            (9, 0) => "max",
            (DefaultLevel, 1) => "low_ram",
            _ => "custom",
        };
        Preset = Presets.First(p => p.Value == key);
    }

    private static int Whole(decimal? value, int fallback) => value is { } v ? (int)decimal.Truncate(v) : fallback;

    private static void Add(List<string> args, string option, int value, int defaultValue)
    {
        if (value != defaultValue)
        {
            args.Add(option);
            args.Add(value.ToString(CultureInfo.InvariantCulture));
        }
    }
}
