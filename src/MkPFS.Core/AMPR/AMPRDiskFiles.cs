namespace MkPFS.Core.AMPR;

/// <summary>
/// Files the PS5 reads outside the game process, where AMPR Emu cannot serve packed files, so they must stay on
/// disk: empty files (PlayGo language placeholders), PlayGo chunk markers, and the Sucker Punch boot files Ghost of
/// Yotei (PPSA26344 02.002.000) needs to get past a black screen. Not in ampr_pack.py; port of LZ4 Compression 1.4.3
/// <c>ampr_pack_keep_on_disk</c> (theghostonline).
/// </summary>
public static class AMPRDiskFiles
{
    /// <summary>Globs (<see cref="AMPRGlob"/> syntax) of non-empty files that stay on disk.</summary>
    public static IReadOnlyList<string> Patterns { get; } =
    [
        "cache_ps5/pgc_*_dummy_file",
        "cache_ps5/playgo.pgm",
        "cache_ps5/load_menu_catalog.log",
        "cache_ps5/all_shaders.texmeshman",
        "cache_ps5/game.sprig.packman",
        "cache_ps5/game.sprig.texmeshman",
        "cache_ps5/pulse.sprig.texmeshman",
        "sounds/scream/_generated/buss_config.bus",
        "sounds/scream/_generated/distance_model.dml",
        "sounds/scream/_generated/group_mix.gmx",
    ];

    /// <summary>Whether a file must stay on disk instead of only in the packs.</summary>
    /// <param name="relative">Path relative to <c>/app0</c>.</param>
    /// <param name="size">Logical size in bytes.</param>
    /// <returns><see langword="true"/> for empty files and <see cref="Patterns"/> matches.</returns>
    public static bool MustStayOnDisk(string relative, ulong size) => size == 0 || AMPRGlob.Matches(relative, Patterns);
}
