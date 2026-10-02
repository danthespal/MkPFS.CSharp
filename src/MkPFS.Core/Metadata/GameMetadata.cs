using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MkPFS.Core.Exfat;
using MkPFS.Core.PFS;

namespace MkPFS.Core.Metadata;

/// <summary>Metadata shown when a game file or folder is selected (Python <c>GameMetadata</c>).</summary>
public sealed class GameMetadata
{
    /// <summary>Placeholder for unknown values.</summary>
    public const string Dash = "-";

    /// <summary>Selected path.</summary>
    public required string FilePath { get; init; }

    /// <summary>File or folder name.</summary>
    public required string FileName { get; init; }

    /// <summary>Size in bytes (sum of files for a folder).</summary>
    public long FileSize { get; set; }

    /// <summary>Game title.</summary>
    public string GameTitle { get; set; } = string.Empty;

    /// <summary>Content ID.</summary>
    public string ContentId { get; set; } = Dash;

    /// <summary>Title ID.</summary>
    public string TitleId { get; set; } = Dash;

    /// <summary>Package type label (for example <c>PKG</c>, <c>FFPFSC (4 inodes)</c>).</summary>
    public string PackageType { get; set; } = Dash;

    /// <summary>Version.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Region from the Content ID prefix.</summary>
    public string Region { get; set; } = string.Empty;

    /// <summary><c>sce_sys/icon0.png</c> bytes.</summary>
    public byte[]? IconBytes { get; set; }

    /// <summary><c>fakelib/libSceAmpr.sprx</c> present (APR emulation build).</summary>
    public bool HasAprEmu { get; set; }

    /// <summary>Read error, if any.</summary>
    public string Error { get; set; } = string.Empty;

    /// <summary>Size for display.</summary>
    public string SizeDisplay => GameMetadataReader.FormatBytes(FileSize);

    /// <summary>APR-EMU state for display.</summary>
    public string AprEmuDisplay => HasAprEmu ? "APR-EMU" : "No";
}

/// <summary>
/// Best-effort metadata reader for PKG, FFPKG, exFAT, PFS images and source folders (port of Python
/// <c>mkpfs/game_metadata.py</c>). Malformed input yields fallback values, never an exception.
/// </summary>
public static partial class GameMetadataReader
{
    private const int MaxParamSize = 4 * 1024 * 1024;
    private const int MaxIconSize = 10 * 1024 * 1024;
    private const int PkgEntrySize = 0x20;
    private const uint PkgEntryParamSfo = 0x1000;
    private const uint PkgEntryIcon0Png = 0x1200;
    private const int FfpkgMagicOffset = 0xFFEC;
    private const int FfpkgDirOffset = 0x38000;
    private const int FfpkgScanLimit = 1_048_576;
    private const int FfpkgAprScanLimit = 8 * 1024 * 1024;
    private static readonly byte[] FfpkgMagic = [0x19, 0x01, 0x54, 0x19];
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] PngEnd = [0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82];

    [GeneratedRegex("(PPSA|CUSA|PUSA|PCSE|PCSB|PCAS|PCJS|BCUS|BCES|BLAS|BLJM|BCAS|BCJS|NPUB|NPEB|NPJB|NPAS|SLES|SLPS)[0-9]+", RegexOptions.IgnoreCase)]
    private static partial Regex TitleIdPattern();

    /// <summary>Read metadata from a package, image or source folder (Python <c>read_game_metadata</c>).</summary>
    /// <param name="path">File or folder.</param>
    /// <returns>Metadata; <see cref="GameMetadata.Error"/> is set when reading failed.</returns>
    public static GameMetadata Read(string path)
    {
        path = Util.PathRules.ExpandUser(path);
        GameMetadata meta = BaseMetadata(path);
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            meta.Error = $"Path not found: {path}";
            return meta;
        }

        if (Directory.Exists(path))
        {
            meta.PackageType = "FOLDER";
            FillFromSourceFolder(path, meta);
            return meta;
        }

        string suffix = Util.PathRules.Suffix(path).ToLowerInvariant();
        try
        {
            switch (suffix)
            {
                case ".pkg":
                    ReadPkg(path, meta);
                    break;
                case ".exfat":
                    meta.PackageType = "EXFAT";
                    using (FileStream stream = File.OpenRead(path))
                    {
                        FillFromExfat(new ExfatReader(stream), meta);
                    }

                    break;
                case ".ffpkg":
                    ReadFfpkg(path, meta);
                    break;
                case ".ffpfs" or ".ffpfsc":
                    ReadPfs(path, meta);
                    break;
                default:
                    meta.PackageType = suffix.Length > 1 ? suffix[1..].ToUpperInvariant() : GameMetadata.Dash;
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            meta.Error = ex.Message;
        }

        return meta;
    }

    /// <summary>Spectrum-style size text (Python <c>format_bytes</c>).</summary>
    /// <param name="size">Bytes.</param>
    /// <returns>Text, or <c>-</c> for zero.</returns>
    public static string FormatBytes(long size)
    {
        if (size <= 0)
        {
            return GameMetadata.Dash;
        }

        double value = size;
        foreach (string unit in new[] { "B", "KB", "MB", "GB", "TB" })
        {
            if (value < 1024.0 || unit == "TB")
            {
                return unit switch
                {
                    "B" => $"{(long)value} B",
                    "KB" => value.ToString("F0", CultureInfo.InvariantCulture) + " KB",
                    "MB" => value.ToString("F1", CultureInfo.InvariantCulture) + " MB",
                    _ => value.ToString("F2", CultureInfo.InvariantCulture) + " " + unit,
                };
            }

            value /= 1024.0;
        }

        return value.ToString("F2", CultureInfo.InvariantCulture) + " PB";
    }

    /// <summary>Region from a Content ID prefix (Python <c>detect_region_from_content_id</c>).</summary>
    /// <param name="contentId">Content ID.</param>
    /// <returns>Region or empty.</returns>
    public static string RegionFromContentId(string contentId)
    {
        if (string.IsNullOrEmpty(contentId) || contentId.Length < 2 || contentId == GameMetadata.Dash)
        {
            return string.Empty;
        }

        return contentId[..2].ToUpperInvariant() switch
        {
            "UP" => "USA",
            "EP" => "EUR",
            "JP" => "JPN",
            "HP" or "AP" => "ASIA",
            "KP" => "KOR",
            _ => string.Empty,
        };
    }

    /// <summary>Parse a <c>param.sfo</c> (Python <c>_parse_sfo</c>).</summary>
    /// <param name="data">SFO bytes.</param>
    /// <returns>Key to value; integers as decimal text.</returns>
    public static Dictionary<string, string> ParseSfo(ReadOnlySpan<byte> data)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        if (data.Length < 20 || !data[..4].SequenceEqual("\0PSF"u8))
        {
            return result;
        }

        uint keyTable = Le32(data, 0x08);
        uint dataTable = Le32(data, 0x0C);
        uint count = Le32(data, 0x10);
        for (long i = 0; i < count; i++)
        {
            long entry = 0x14 + (i * 16);
            if (entry + 16 > data.Length)
            {
                break;
            }

            ushort keyOffset = BinaryPrimitives.ReadUInt16LittleEndian(data[(int)entry..]);
            ushort format = BinaryPrimitives.ReadUInt16LittleEndian(data[((int)entry + 2)..]);
            uint length = Le32(data, (int)entry + 4);
            uint dataOffset = Le32(data, (int)entry + 12);
            long keyStart = keyTable + (long)keyOffset;
            if (keyStart >= data.Length)
            {
                continue;
            }

            int keyEnd = data[(int)keyStart..].IndexOf((byte)0);
            if (keyEnd < 0)
            {
                continue;
            }

            string key = AsciiIgnoringErrors(data.Slice((int)keyStart, keyEnd));
            long valueStart = (long)dataTable + dataOffset;
            if (valueStart >= data.Length)
            {
                continue;
            }

            int valueLength = (int)Math.Min(length, data.Length - valueStart);
            string value;
            if (format == 0x0404 && valueLength >= 4)
            {
                value = Le32(data, (int)valueStart).ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                ReadOnlySpan<byte> raw = data.Slice((int)valueStart, valueLength);
                int nul = raw.IndexOf((byte)0);
                value = Encoding.UTF8.GetString(nul >= 0 ? raw[..nul] : raw);
            }

            result[key] = value.TrimEnd('\0');
        }

        return result;
    }

    private static GameMetadata BaseMetadata(string path)
    {
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        string stem = Stem(name);
        Match match = TitleIdPattern().Match(stem);
        string titleId = match.Success ? match.Value.ToUpperInvariant() : stem;
        return new GameMetadata
        {
            FilePath = path,
            FileName = name,
            FileSize = PathSize(path),
            ContentId = titleId != stem ? titleId : GameMetadata.Dash,
            TitleId = titleId,
        };
    }

    // Python PurePath.stem: the name without its last suffix.
    private static string Stem(string name) => name[..(name.Length - Util.PathRules.Suffix(name).Length)];

    private static long PathSize(string path)
    {
        if (Directory.Exists(path))
        {
            long total = 0;
            foreach (string file in Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true }))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch (IOException)
                {
                    // Python suppresses OSError per file.
                }
            }

            return total;
        }

        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static void ReadPkg(string path, GameMetadata meta)
    {
        meta.PackageType = "PKG";
        using FileStream fh = File.OpenRead(path);
        if (!ReadAt(fh, 0, 4).AsSpan().SequenceEqual("\u007fCNT"u8))
        {
            meta.PackageType = "UNKNOWN";
            return;
        }

        string contentId = AsciiIgnoringErrors(ReadAt(fh, 0x40, 48)).TrimEnd('\0').Trim();
        if (contentId.Length > 0)
        {
            meta.ContentId = contentId;
            meta.TitleId = TitleIdFromContentId(contentId) ?? meta.TitleId;
            meta.Region = RegionFromContentId(contentId);
        }

        uint entryCount = ReadU32Be(fh, 0x10);
        uint entryTable = ReadU32Be(fh, 0x18);
        uint flags = ReadU32Be(fh, 0x04);
        if (entryCount == 0 || entryCount > 4096 || entryTable == 0)
        {
            meta.PackageType = PkgTypeFromFlags(flags);
            return;
        }

        (uint Offset, uint Size)? sfo = null;
        (uint Offset, uint Size)? icon = null;
        for (uint i = 0; i < entryCount; i++)
        {
            long entry = entryTable + ((long)i * PkgEntrySize);
            uint id = ReadU32Be(fh, entry);
            uint offset = ReadU32Be(fh, entry + 0x10);
            uint size = ReadU32Be(fh, entry + 0x14);
            if (id == PkgEntryParamSfo)
            {
                sfo = (offset, size);
            }
            else if (id == PkgEntryIcon0Png)
            {
                icon = (offset, size);
            }

            if (sfo is not null && icon is not null)
            {
                break;
            }
        }

        if (sfo is { Size: > 0 and <= MaxParamSize } s)
        {
            FillFromSfo(ParseSfo(ReadAt(fh, s.Offset, (int)s.Size)), meta, PkgTypeFromFlags(flags));
        }
        else
        {
            meta.PackageType = PkgTypeFromFlags(flags);
        }

        if (icon is { Size: > 0 and <= MaxIconSize } ic && ReadAt(fh, ic.Offset, (int)ic.Size) is { } png && IsPng(png))
        {
            meta.IconBytes = png;
        }
    }

    private static void ReadFfpkg(string path, GameMetadata meta)
    {
        meta.PackageType = "FFPKG";
        using FileStream fh = File.OpenRead(path);
        if (!ReadAt(fh, FfpkgMagicOffset, 4).AsSpan().SequenceEqual(FfpkgMagic))
        {
            meta.PackageType = "UNKNOWN";
            meta.ContentId = GameMetadata.Dash;
            return;
        }

        string? titleId = ScanFfpkgDirectory(fh) ?? ScanTitleId(fh, 0, (int)Math.Min(FfpkgScanLimit, meta.FileSize));
        if (titleId is not null)
        {
            meta.TitleId = titleId;
            meta.ContentId = titleId;
        }

        meta.IconBytes = ScanPng(fh, FfpkgDirOffset);
        byte[] head = ReadAt(fh, 0, (int)Math.Min(FfpkgAprScanLimit, meta.FileSize));
        meta.HasAprEmu = head.AsSpan().IndexOf("libSceAmpr.sprx"u8) >= 0 || head.AsSpan().IndexOf("libSceAmpr.SPRX"u8) >= 0;
    }

    private static void ReadPfs(string path, GameMetadata meta)
    {
        meta.PackageType = Util.PathRules.Suffix(path).Equals(".ffpfsc", StringComparison.OrdinalIgnoreCase) ? "FFPFSC" : "FFPFS";
        PFSInspection inspection = PFSInspector.Inspect(path, new PFSInspectOptions { VerifyPayloads = false, Checklist = ChecklistMode.Never });
        if (inspection.Header is not null)
        {
            meta.PackageType = $"{meta.PackageType} ({inspection.Inodes.Count} inodes)";
        }

        if (inspection.Header is not null && inspection.Inodes.Count > 0 && inspection.FileInodes.Count > 0)
        {
            meta.HasAprEmu = inspection.FileInodes.Keys.Any(name => name.Equals("fakelib/libsceampr.sprx", StringComparison.OrdinalIgnoreCase));
            using PFSImage image = PFSImage.Open(path);
            if (FindRelPath(inspection.FileInodes, "sce_sys/param.json") is long param &&
                inspection.Inodes[(int)param] is { LogicalSize: > 0 and <= MaxParamSize } paramInode)
            {
                FillFromParamJson(ReadInode(image, paramInode, MaxParamSize), meta);
            }

            if (FindRelPath(inspection.FileInodes, "sce_sys/icon0.png") is long icon &&
                inspection.Inodes[(int)icon] is { LogicalSize: > 0 and <= MaxIconSize } iconInode &&
                ReadInode(image, iconInode, MaxIconSize) is { } png && IsPng(png))
            {
                meta.IconBytes = png;
            }
        }

        if (meta.GameTitle.Length == 0 && meta.IconBytes is null && PFSExtractor.OpenInnerExfat(path, null, false) is { } inner)
        {
            using (inner.Image)
            {
                FillFromExfat(new ExfatReader(inner.View), meta);
            }
        }
    }

    private static void FillFromExfat(ExfatReader reader, GameMetadata meta)
    {
        Dictionary<string, ExfatEntry> files = new(StringComparer.Ordinal);
        foreach (ExfatEntry entry in reader.EnumerateFiles())
        {
            files[entry.RelPath.ToLowerInvariant()] = entry;
        }

        meta.HasAprEmu = files.ContainsKey("fakelib/libsceampr.sprx");
        if (files.TryGetValue("sce_sys/param.json", out ExfatEntry? param) && param.Length is > 0 and <= MaxParamSize)
        {
            FillFromParamJson(ReadExfat(reader, param), meta);
        }

        if (files.TryGetValue("sce_sys/icon0.png", out ExfatEntry? icon) && icon.Length is > 0 and <= MaxIconSize &&
            ReadExfat(reader, icon) is { } png && IsPng(png))
        {
            meta.IconBytes = png;
        }
    }

    private static void FillFromSourceFolder(string path, GameMetadata meta)
    {
        meta.HasAprEmu = File.Exists(Path.Combine(path, "fakelib", "libSceAmpr.sprx"));
        string paramJson = Path.Combine(path, "sce_sys", "param.json");
        if (File.Exists(paramJson))
        {
            try
            {
                if (new FileInfo(paramJson).Length is > 0 and <= MaxParamSize)
                {
                    FillFromParamJson(File.ReadAllBytes(paramJson), meta);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or UnauthorizedAccessException)
            {
                // Python suppresses OSError, ValueError and JSONDecodeError here.
            }
        }

        string paramSfo = Path.Combine(path, "sce_sys", "param.sfo");
        if (File.Exists(paramSfo) && meta.GameTitle.Length == 0)
        {
            try
            {
                if (new FileInfo(paramSfo).Length is > 0 and <= MaxParamSize)
                {
                    FillFromSfo(ParseSfo(File.ReadAllBytes(paramSfo)), meta, null);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        string icon = Path.Combine(path, "sce_sys", "icon0.png");
        if (File.Exists(icon))
        {
            try
            {
                if (new FileInfo(icon).Length is > 0 and <= MaxIconSize && File.ReadAllBytes(icon) is { } png && IsPng(png))
                {
                    meta.IconBytes = png;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    // Python _fill_from_param_json: UTF-8 with optional BOM; only an object is used.
    private static void FillFromParamJson(byte[] data, GameMetadata meta)
    {
        string text = Encoding.UTF8.GetString(data.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? data.AsSpan(3) : data);
        using JsonDocument document = JsonDocument.Parse(text);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        string contentId = StringValue(root, "contentId", "content_id");
        if (contentId.Length > 0)
        {
            meta.ContentId = contentId;
            meta.Region = RegionFromContentId(contentId);
        }

        string titleId = StringValue(root, "titleId", "title_id");
        if (titleId.Length > 0)
        {
            meta.TitleId = titleId;
        }

        string version = StringValue(root, "contentVersion", "masterVersion", "appVersion", "version");
        if (version.Length > 0)
        {
            meta.Version = version;
        }

        string title = ExtractGameTitle(root);
        if (title.Length > 0)
        {
            meta.GameTitle = title;
        }
    }

    private static void FillFromSfo(Dictionary<string, string> sfo, GameMetadata meta, string? defaultPackageType)
    {
        string category = sfo.GetValueOrDefault("CATEGORY", string.Empty);
        if (category.Length > 0)
        {
            meta.PackageType = PkgCategoryToType(category);
        }
        else if (!string.IsNullOrEmpty(defaultPackageType))
        {
            meta.PackageType = defaultPackageType;
        }

        string contentId = sfo.GetValueOrDefault("CONTENT_ID", string.Empty).Trim();
        if (contentId.Length > 0)
        {
            meta.ContentId = contentId;
            meta.TitleId = TitleIdFromContentId(contentId) ?? meta.TitleId;
            meta.Region = RegionFromContentId(contentId);
        }

        meta.TitleId = NonEmpty(sfo.GetValueOrDefault("TITLE_ID", meta.TitleId).Trim(), meta.TitleId);
        meta.Version = NonEmpty(sfo.GetValueOrDefault("APP_VER", meta.Version).Trim(), meta.Version);
        meta.GameTitle = NonEmpty(sfo.GetValueOrDefault("TITLE", meta.GameTitle).Trim(), meta.GameTitle);
    }

    private static string NonEmpty(string value, string fallback) => value.Length > 0 ? value : fallback;

    private static string ExtractGameTitle(JsonElement root)
    {
        foreach (string key in new[] { "title", "titleName", "localizedTitle", "name" })
        {
            if (root.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String && value.GetString()!.Trim().Length > 0)
            {
                return value.GetString()!.Trim();
            }
        }

        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object && StringValue(property.Value, "titleName", "title", "name") is { Length: > 0 } title)
            {
                return title;
            }
        }

        if (root.TryGetProperty("localizedParameters", out JsonElement localized) && localized.ValueKind == JsonValueKind.Object)
        {
            // Python takes the first locale in file order, usually ar-AE (oracle finding 16); prefer the declared
            // default language, then en-US, and only then the first locale.
            foreach (string language in new[] { StringValue(localized, "defaultLanguage"), "en-US" })
            {
                if (language.Length > 0 && localized.TryGetProperty(language, out JsonElement preferred) &&
                    preferred.ValueKind == JsonValueKind.Object && StringValue(preferred, "titleName", "title", "name") is { Length: > 0 } title)
                {
                    return title;
                }
            }

            foreach (JsonProperty property in localized.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object && StringValue(property.Value, "titleName", "title", "name") is { Length: > 0 } title)
                {
                    return title;
                }
            }
        }

        return string.Empty;
    }

    private static string StringValue(JsonElement source, params string[] keys)
    {
        foreach (string key in keys)
        {
            if (source.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String && value.GetString()!.Trim() is { Length: > 0 } text)
            {
                return text;
            }
        }

        return string.Empty;
    }

    private static byte[] ReadInode(PFSImage image, PFSInode inode, int limit)
    {
        using MemoryStream data = new();
        foreach (ReadOnlyMemory<byte> chunk in image.ReadLogicalChunks(inode))
        {
            if (data.Length + chunk.Length > limit)
            {
                throw new InvalidDataException("PFS metadata file exceeds safe read limit");
            }

            data.Write(chunk.Span);
        }

        return data.ToArray();
    }

    private static byte[] ReadExfat(ExfatReader reader, ExfatEntry entry)
    {
        using MemoryStream data = new();
        foreach (ReadOnlyMemory<byte> chunk in reader.ReadFile(entry))
        {
            data.Write(chunk.Span);
        }

        return data.ToArray();
    }

    private static long? FindRelPath(IReadOnlyDictionary<string, long> files, string wanted)
    {
        foreach ((string rel, long inode) in files)
        {
            if (rel.Equals(wanted, StringComparison.OrdinalIgnoreCase))
            {
                return inode;
            }
        }

        return null;
    }

    private static string? ScanFfpkgDirectory(FileStream fh)
    {
        byte[] data = ReadAt(fh, FfpkgDirOffset, 4096);
        int pos = 0;
        while (pos + 8 <= data.Length)
        {
            int recordLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos + 4));
            int nameLength = data[pos + 7];
            if (recordLength < 8 || pos + 8 + nameLength > data.Length)
            {
                break;
            }

            if (nameLength > 0 && TitleIdPattern().Match(AsciiIgnoringErrors(data.AsSpan(pos + 8, nameLength))) is { Success: true } match)
            {
                return match.Value.ToUpperInvariant();
            }

            pos += recordLength;
        }

        return null;
    }

    private static string? ScanTitleId(FileStream fh, long offset, int size)
    {
        byte[] data = ReadAt(fh, offset, size);
        char[] text = new char[data.Length];
        for (int i = 0; i < data.Length; i++)
        {
            text[i] = data[i] is >= 0x20 and <= 0x7E ? (char)data[i] : ' ';
        }

        Match match = TitleIdPattern().Match(new string(text));
        return match.Success ? match.Value.ToUpperInvariant() : null;
    }

    private static byte[]? ScanPng(FileStream fh, long offset)
    {
        const int chunk = 256 * 1024;
        for (long pos = offset; ; pos += chunk - PngSignature.Length)
        {
            byte[] data = ReadAt(fh, pos, chunk);
            if (data.Length < PngSignature.Length)
            {
                return null;
            }

            int index = data.AsSpan().IndexOf(PngSignature);
            if (index >= 0)
            {
                byte[] png = ReadAt(fh, pos + index, Math.Min(MaxIconSize, 3_145_728));
                if (!IsPng(png))
                {
                    return null;
                }

                int end = png.AsSpan().IndexOf(PngEnd);
                return end < 0 ? png : png[..(end + PngEnd.Length)];
            }

            if (data.Length < chunk)
            {
                return null;
            }
        }
    }

    private static string? TitleIdFromContentId(string contentId)
    {
        if (string.IsNullOrEmpty(contentId) || contentId == GameMetadata.Dash)
        {
            return null;
        }

        string[] parts = contentId.Split('-');
        if (parts.Length >= 2)
        {
            string middle = parts[1].Split('_', 2)[0];
            return middle.Length > 0 ? middle : null;
        }

        return contentId;
    }

    private static string PkgCategoryToType(string category) => category.ToLowerInvariant() switch
    {
        "ac" => "PS4AC",
        "bd" => "PS4BD",
        "gc" => "PS4GC",
        "gd" => "PS4GD",
        "gda" => "PS4GDA",
        "gdc" => "PS4GDC",
        "gdd" => "PS4GDD",
        "gde" => "PS4GDE",
        "gdk" => "PS4GDK",
        "gdl" => "PS4GDL",
        "gdo" => "PS4GDO",
        "gp" => "PS4GP",
        "gpc" => "PS4GPC",
        "sd" => "PS4SD",
        _ => category.ToUpperInvariant(),
    };

    private static string PkgTypeFromFlags(uint flags) => (flags & 0xFF) switch
    {
        0x01 => "PS4GD",
        0x02 => "PS4AC",
        0x04 => "PS4DX",
        0x08 => "PS4DA",
        0x10 => "PS4GP",
        _ => $"0x{flags:X8}",
    };

    private static byte[] ReadAt(FileStream fh, long offset, int size)
    {
        if (size <= 0 || offset >= fh.Length)
        {
            return [];
        }

        byte[] buffer = new byte[(int)Math.Min(size, fh.Length - offset)];
        fh.Seek(offset, SeekOrigin.Begin);
        fh.ReadExactly(buffer);
        return buffer;
    }

    private static uint ReadU32Be(FileStream fh, long offset)
    {
        byte[] data = ReadAt(fh, offset, 4);
        return data.Length == 4 ? BinaryPrimitives.ReadUInt32BigEndian(data) : 0;
    }

    private static uint Le32(ReadOnlySpan<byte> data, int offset) =>
        offset >= 0 && offset + 4 <= data.Length ? BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]) : 0;

    // Python bytes.decode("ascii", errors="ignore").
    private static string AsciiIgnoringErrors(ReadOnlySpan<byte> data)
    {
        StringBuilder text = new(data.Length);
        foreach (byte b in data)
        {
            if (b < 0x80)
            {
                text.Append((char)b);
            }
        }

        return text.ToString();
    }

    private static bool IsPng(byte[] data) => data.Length > 8 && data.AsSpan(0, 4).SequenceEqual(PngSignature.AsSpan(0, 4));
}
