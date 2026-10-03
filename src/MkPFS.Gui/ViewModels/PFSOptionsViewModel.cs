using CommunityToolkit.Mvvm.ComponentModel;

namespace MkPFS.Gui.ViewModels;

/// <summary>
/// Advanced PFS options shared by the packing pages: profile, inode width, case sensitivity, encryption and
/// verbose output. Only values that differ from the CLI defaults become arguments.
/// </summary>
public sealed partial class PFSOptionsViewModel : ObservableObject
{
    /// <summary>Create the options.</summary>
    /// <param name="offerInodeBits">Show <c>--inode-bits</c> (not used by <c>batch</c>).</param>
    /// <param name="offerNewCrypt">Show <c>--new-crypt</c> (only <c>batch</c> has it).</param>
    public PFSOptionsViewModel(bool offerInodeBits, bool offerNewCrypt)
    {
        OfferInodeBits = offerInodeBits;
        OfferNewCrypt = offerNewCrypt;
        Version = Versions[0];
        InodeBits = InodeWidths[0];
    }

    /// <summary>PFS profiles in picker order.</summary>
    public IReadOnlyList<Choice> Versions { get; } = [new("PS5", null, "PS5"), new("PS4", null, "PS4")];

    /// <summary>Inode widths in picker order.</summary>
    public IReadOnlyList<Choice> InodeWidths { get; } = [new("32", null, "32-bit"), new("64", null, "64-bit")];

    /// <summary>Show the inode width picker.</summary>
    public bool OfferInodeBits { get; }

    /// <summary>Show the newCrypt option.</summary>
    public bool OfferNewCrypt { get; }

    /// <summary>The inode width applies (Pack Folder: raw PFS only).</summary>
    [ObservableProperty]
    public partial bool InodeBitsEnabled { get; set; } = true;

    /// <summary><c>--version</c>.</summary>
    [ObservableProperty]
    public partial Choice Version { get; set; }

    /// <summary><c>--inode-bits</c>.</summary>
    [ObservableProperty]
    public partial Choice InodeBits { get; set; }

    /// <summary><c>--case-sensitive</c>.</summary>
    [ObservableProperty]
    public partial bool CaseSensitive { get; set; }

    /// <summary><c>--encrypted</c>.</summary>
    [ObservableProperty]
    public partial bool Encrypted { get; set; }

    /// <summary><c>--ekpfs-key</c>, used only with <see cref="Encrypted"/>.</summary>
    [ObservableProperty]
    public partial string EkpfsKey { get; set; } = string.Empty;

    /// <summary><c>--new-crypt</c>, used only with <see cref="Encrypted"/>.</summary>
    [ObservableProperty]
    public partial bool NewCrypt { get; set; }

    /// <summary><c>--verbose</c>.</summary>
    [ObservableProperty]
    public partial bool Verbose { get; set; }

    /// <summary>Append the non-default options.</summary>
    /// <param name="args">Arguments.</param>
    public void AppendTo(List<string> args)
    {
        if (Version.Value != "PS5")
        {
            args.Add("--version");
            args.Add(Version.Value);
        }

        if (OfferInodeBits && InodeBitsEnabled && InodeBits.Value != "32")
        {
            args.Add("--inode-bits");
            args.Add(InodeBits.Value);
        }

        if (CaseSensitive)
        {
            args.Add("--case-sensitive");
        }

        if (Encrypted)
        {
            args.Add("--encrypted");
            if (EkpfsKey.Trim() is { Length: > 0 } key)
            {
                args.Add("--ekpfs-key");
                args.Add(key);
            }

            if (OfferNewCrypt && NewCrypt)
            {
                args.Add("--new-crypt");
            }
        }

        if (Verbose)
        {
            args.Add("--verbose");
        }
    }
}
