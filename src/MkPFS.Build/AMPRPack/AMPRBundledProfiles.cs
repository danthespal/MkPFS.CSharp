using System.Text;
using System.Text.Json;
using MkPFS.Core.AMPR;
using MkPFS.Core.Util;

namespace MkPFS.Build.AMPRPack;

/// <summary>A pack profile tested on the PS5 and shipped with MkPFS (<c>AMPRPack/Profiles</c>).</summary>
/// <param name="Id">Short name for <c>--profile</c>.</param>
/// <param name="Label">Game or engine name.</param>
/// <param name="Tested">The game and version it was tested on.</param>
/// <param name="Source">Where it was posted.</param>
/// <param name="TitleIds">Title IDs it was made for.</param>
/// <param name="Names">Lower-case letters and digits found in the game's title name.</param>
/// <param name="Engine">Engine layout it also suits (<see cref="AMPRBundledProfiles.DetectEngine"/>), or <see langword="null"/>.</param>
/// <param name="FileName">Resource file name.</param>
public sealed record AMPRBundledProfile(
    string Id, string Label, string Tested, string Source, IReadOnlyList<string> TitleIds, IReadOnlyList<string> Names, string? Engine, string FileName)
{
    /// <summary>The profile TOML, unchanged.</summary>
    public string Toml
    {
        get
        {
            using Stream stream = typeof(AMPRBundledProfile).Assembly.GetManifestResourceStream("MkPFS.Build.AMPRPack.Profiles." + FileName)
                ?? throw new InvalidOperationException($"missing bundled profile {FileName}");
            using StreamReader reader = new(stream, new UTF8Encoding(false));
            return reader.ReadToEnd();
        }
    }

    /// <summary>Parse <see cref="Toml"/>.</summary>
    /// <returns>Configuration.</returns>
    public AMPRPackConfig Load() => AMPRProfiler.ToConfig(Toml);
}

/// <summary>How <see cref="AMPRBundledProfiles.Match"/> chose a profile.</summary>
public enum AMPRBundledMatchKind
{
    /// <summary>The game's Title ID is one the profile was made for.</summary>
    TitleId,

    /// <summary>The game's title name contains one of the profile's names (another region or edition).</summary>
    Name,

    /// <summary>The game's files have the layout of the profile's engine; only tested on one game of that engine.</summary>
    Engine,
}

/// <summary>A bundled profile chosen for a game folder.</summary>
/// <param name="Profile">Profile.</param>
/// <param name="Kind">Why it was chosen.</param>
public sealed record AMPRBundledMatch(AMPRBundledProfile Profile, AMPRBundledMatchKind Kind);

/// <summary>
/// The community profiles tested on the PS5 (MkPFS.CSharp issues #2 to #5), and the choice of one for a game: by
/// Title ID or title name first, then by engine file layout (like LZ4 Compression 1.4.0 <c>bundled_profile_match</c>).
/// </summary>
public static class AMPRBundledProfiles
{
    /// <summary>Every bundled profile, in match order.</summary>
    public static IReadOnlyList<AMPRBundledProfile> All { get; } =
    [
        new("astrobot", "ASTRO BOT", "ASTRO BOT (PPSA21567, 01.019.000)", "https://github.com/danthespal/MkPFS.CSharp/issues/5",
            ["PPSA21567"], ["astrobot"], null, "astrobot.toml"),
        new("ufc6", "EA Sports UFC 6", "EA SPORTS UFC 6 (PPSA23566, 01.004.000)", "https://github.com/danthespal/MkPFS.CSharp/issues/2",
            ["PPSA23566"], ["ufc6"], null, "ufc6-PPSA23566.toml"),
        new("insomniac", "Insomniac engine", "Marvel's Wolverine (PPSA03671, 01.001.005)", "https://github.com/danthespal/MkPFS.CSharp/issues/4",
            ["PPSA03671"], ["wolverine"], "insomniac", "insomniac.toml"),
        new("unity", "Unity engine", "God of War Sons of Sparta (PPSA28997, 01.008.001)", "https://github.com/danthespal/MkPFS.CSharp/issues/3",
            ["PPSA28997"], ["sonsofsparta"], "unity", "unity.toml"),
    ];

    /// <summary>The profile with this id (case-insensitive), or <see langword="null"/>.</summary>
    /// <param name="id">Profile id.</param>
    /// <returns>Profile.</returns>
    public static AMPRBundledProfile? Find(string id) => All.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The bundled profile for a game folder, or <see langword="null"/> when none fits.</summary>
    /// <param name="root">Game <c>/app0</c> folder.</param>
    /// <returns>Profile and why it was chosen.</returns>
    public static AMPRBundledMatch? Match(string root)
    {
        string? titleId = GameParams.TitleIdFromSource(root);
        List<string> names = TitleNames(root);
        foreach (AMPRBundledProfile profile in All)
        {
            if (titleId is not null && profile.TitleIds.Contains(titleId, StringComparer.OrdinalIgnoreCase))
            {
                return new AMPRBundledMatch(profile, AMPRBundledMatchKind.TitleId);
            }

            if (profile.Names.Any(token => names.Any(name => name.Contains(token, StringComparison.Ordinal))))
            {
                return new AMPRBundledMatch(profile, AMPRBundledMatchKind.Name);
            }
        }

        string? engine = DetectEngine(root);
        AMPRBundledProfile? byEngine = engine is null ? null : All.FirstOrDefault(p => p.Engine == engine);
        return byEngine is null ? null : new AMPRBundledMatch(byEngine, AMPRBundledMatchKind.Engine);
    }

    /// <summary>
    /// The engine a bundled profile covers, from the file layout: Insomniac keeps <c>toc</c>, <c>dag</c> and a
    /// <c>d/</c> folder at the top; Unity has <c>StreamingAssets</c> next to its <c>Data</c> files, at the top or
    /// under <c>Media/</c>.
    /// </summary>
    /// <param name="root">Game <c>/app0</c> folder.</param>
    /// <returns><c>insomniac</c>, <c>unity</c> or <see langword="null"/>.</returns>
    public static string? DetectEngine(string root)
    {
        if (File.Exists(Path.Combine(root, "toc")) && File.Exists(Path.Combine(root, "dag")) && Directory.Exists(Path.Combine(root, "d")))
        {
            return "insomniac";
        }

        string[] bases = [root, Path.Combine(root, "Media")];
        string[] markers = ["Data/globalgamemanagers", "Data/data.unity3d", "Data/Managed/Metadata/global-metadata.dat"];
        bool streamingAssets = bases.Any(b => Directory.Exists(Path.Combine(b, "StreamingAssets")));
        return streamingAssets && bases.Any(b => markers.Any(m => File.Exists(Path.Combine([b, .. m.Split('/')]))))
            ? "unity"
            : null;
    }

    // Every localized title name in sce_sys/param.json, reduced to lower-case letters and digits.
    private static List<string> TitleNames(string root)
    {
        string path = Path.Combine(root, "sce_sys", "param.json");
        List<string> names = [];
        if (!File.Exists(path))
        {
            return names;
        }

        try
        {
            using JsonDocument document = GameParams.ReadParamJson(path);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("localizedParameters", out JsonElement localized)
                && localized.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty language in localized.EnumerateObject())
                {
                    if (language.Value.ValueKind == JsonValueKind.Object
                        && language.Value.TryGetProperty("titleName", out JsonElement title)
                        && title.ValueKind == JsonValueKind.String)
                    {
                        names.Add(string.Concat(title.GetString()!.Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant)));
                    }
                }
            }
        }
        catch (InvalidDataException)
        {
            // An unreadable param.json only loses the name match; the Title ID reader reports nothing either.
        }

        return names;
    }
}
