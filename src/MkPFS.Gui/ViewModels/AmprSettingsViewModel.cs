using CommunityToolkit.Mvvm.ComponentModel;
using MkPFS.Build;

namespace MkPFS.Gui.ViewModels;

/// <summary>
/// APR Emu options shared by Pack Folder, Pack exFAT and Batch: the AMPR Emu library folder and the
/// <c>ampr_emu.index</c> switches. Only values that differ from the CLI defaults become arguments.
/// </summary>
public sealed partial class AmprSettingsViewModel : ObservableObject
{
    /// <summary><c>--ampr-libs</c>, optional.</summary>
    [ObservableProperty]
    public partial string LibsDir { get; set; } = string.Empty;

    /// <summary><c>--ampr-title</c>: add the libraries without <c>sce_sys/playgo-chunk.dat</c>.</summary>
    [ObservableProperty]
    public partial bool ForceAprTitle { get; set; }

    /// <summary>Generate <c>ampr_emu.index</c> (off adds <c>--no-ampr-index</c>).</summary>
    [ObservableProperty]
    public partial bool GenerateIndex { get; set; } = true;

    /// <summary><c>--ampr-skip-regen-if-exists</c>.</summary>
    [ObservableProperty]
    public partial bool KeepValidIndex { get; set; }

    /// <summary><c>--ampr-force-regen</c>.</summary>
    [ObservableProperty]
    public partial bool ForceRegen { get; set; }

    /// <summary>The source already holds an <c>ampr_emu.index</c>.</summary>
    [ObservableProperty]
    public partial bool HasExistingIndex { get; set; }

    // KeepValidIndex was ticked by NoteSource, not by the user, so a source without an index clears it again.
    private bool _autoKeep;

    /// <summary>
    /// Record whether the chosen source already has an index. An existing index is kept while it still matches the
    /// files, so it is not replaced by default; the user's own choice of <see cref="KeepValidIndex"/> wins.
    /// </summary>
    /// <param name="hasIndex">The source (or, for Batch, one of its folders) holds <c>ampr_emu.index</c>.</param>
    public void NoteSource(bool hasIndex)
    {
        HasExistingIndex = hasIndex;
        if (hasIndex && !KeepValidIndex)
        {
            KeepValidIndex = true;
            _autoKeep = true;
        }
        else if (!hasIndex && _autoKeep)
        {
            KeepValidIndex = false;
        }
    }

    /// <summary><see langword="true"/> when <paramref name="folder"/> holds <c>ampr_emu.index</c>.</summary>
    /// <param name="folder">Game folder, possibly empty or invalid.</param>
    /// <returns>Whether the index exists.</returns>
    public static bool HasIndex(string folder) =>
        folder.Trim() is { Length: > 0 } path && File.Exists(Path.Combine(path, AmprIndex.IndexName));

    partial void OnKeepValidIndexChanged(bool value) => _autoKeep = false;

    /// <summary>Append the APR Emu arguments.</summary>
    /// <param name="args">Arguments.</param>
    public void AppendTo(List<string> args)
    {
        if (LibsDir.Trim() is { Length: > 0 } libs)
        {
            args.Add("--ampr-libs");
            args.Add(libs);
            if (ForceAprTitle)
            {
                args.Add("--ampr-title");
            }
        }

        if (!GenerateIndex)
        {
            args.Add("--no-ampr-index");
            return;
        }

        if (KeepValidIndex)
        {
            args.Add("--ampr-skip-regen-if-exists");
        }

        if (ForceRegen)
        {
            args.Add("--ampr-force-regen");
        }
    }
}
