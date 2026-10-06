using System.Globalization;

namespace MkPFS.Core.PKG;

/// <summary>
/// CNT entry ids and the <c>sce_sys</c>-relative names they carry (LibProsperoPkg
/// <c>ProsperoCntEntryNames</c> plus the PS5 ids its builder and Publishing Tools add:
/// param.json, pic2, the PlayGo tables and playgo-scenario.json).
/// </summary>
public static class CNTEntryNames
{
    /// <summary>Entry-digest table.</summary>
    public const uint Digests = 0x0001;

    /// <summary>Entry keys.</summary>
    public const uint EntryKeys = 0x0010;

    /// <summary>Image key.</summary>
    public const uint ImageKey = 0x0020;

    /// <summary>General digests.</summary>
    public const uint GeneralDigests = 0x0080;

    /// <summary>Metas.</summary>
    public const uint Metas = 0x0100;

    /// <summary>Entry-name table.</summary>
    public const uint EntryNames = 0x0200;

    /// <summary>Outer-block digest table (unnamed).</summary>
    public const uint ImageDigests = 0x040A;

    /// <summary><c>param.sfo</c> (PS4; refused by the PS5 launch service).</summary>
    public const uint ParamSfo = 0x1000;

    /// <summary><c>param.json</c>.</summary>
    public const uint ParamJson = 0x2000;

    /// <summary>Name → id for every named entry.</summary>
    public static IReadOnlyDictionary<string, uint> NameToId { get; } = Build();

    /// <summary>Id → name.</summary>
    public static IReadOnlyDictionary<uint, string> IdToName { get; } = NameToId.ToDictionary(kv => kv.Value, kv => kv.Key);

    /// <summary>Presentation PNGs and the DDS entry generated from each.</summary>
    public static IReadOnlyList<(string Png, string Dds)> DdsMedia { get; } =
    [
        ("icon0.png", "icon0.dds"),
        ("pic0.png", "pic0.dds"),
        ("pic1.png", "pic1.dds"),
        ("pic2.png", "pic2.dds"),
    ];

    private static Dictionary<string, uint> Build()
    {
        Dictionary<string, uint> map = new(StringComparer.Ordinal)
        {
            ["license.dat"] = 0x0400,
            ["license.info"] = 0x0401,
            ["nptitle.dat"] = 0x0402,
            ["npbind.dat"] = 0x0403,
            ["selfinfo.dat"] = 0x0404,
            ["imageinfo.dat"] = 0x0406,
            ["target-deltainfo.dat"] = 0x0407,
            ["origin-deltainfo.dat"] = 0x0408,
            ["psreserved.dat"] = 0x0409,
            ["param.sfo"] = ParamSfo,
            ["playgo-chunk.dat"] = 0x1001,
            ["playgo-chunk.sha"] = 0x1002,
            ["playgo-manifest.xml"] = 0x1003,
            ["pronunciation.xml"] = 0x1004,
            ["pronunciation.sig"] = 0x1005,
            ["pic1.png"] = 0x1006,
            ["pubtoolinfo.dat"] = 0x1007,
            ["app/playgo-chunk.dat"] = 0x1008,
            ["app/playgo-chunk.sha"] = 0x1009,
            ["app/playgo-manifest.xml"] = 0x100A,
            ["shareparam.json"] = 0x100B,
            ["shareoverlayimage.png"] = 0x100C,
            ["save_data.png"] = 0x100D,
            ["shareprivacyguardimage.png"] = 0x100E,
            ["icon0.png"] = 0x1200,
            ["pic0.png"] = 0x1220,
            ["snd0.at9"] = 0x1240,
            ["changeinfo/changeinfo.xml"] = 0x1260,
            ["icon0.dds"] = 0x1280,
            ["pic0.dds"] = 0x12A0,
            ["pic1.dds"] = 0x12C0,
            ["param.json"] = ParamJson,
            ["playgo-hash-table.dat"] = 0x2010,
            ["playgo-ficm.dat"] = 0x2011,
            ["pic2.png"] = 0x2040,
            ["pic2.dds"] = 0x2060,
            ["playgo-scenario.json"] = 0x3000,
        };

        static string Two(int i) => i.ToString("D2", CultureInfo.InvariantCulture);
        for (int i = 0; i < 31; i++)
        {
            map[$"icon0_{Two(i)}.png"] = 0x1201 + (uint)i;
            map[$"icon0_{Two(i)}.dds"] = 0x1281 + (uint)i;
            map[$"pic1_{Two(i)}.png"] = 0x1241 + (uint)i;
            map[$"pic1_{Two(i)}.dds"] = 0x12C1 + (uint)i;
            map[$"changeinfo/changeinfo_{Two(i)}.xml"] = 0x1261 + (uint)i;
            if (i < 10)
            {
                map[$"keymap_rp/0{Two(i + 1)}.png"] = 0x1600 + (uint)i;
            }

            for (int j = 0; j < 10; j++)
            {
                map[$"keymap_rp/{Two(i)}/0{Two(j + 1)}.png"] = 0x1610 + (uint)(16 * i) + (uint)j;
            }
        }

        for (int i = 0; i < 100; i++)
        {
            map[$"trophy/trophy{Two(i)}.trp"] = 0x1400 + (uint)i;
        }

        return map;
    }
}
