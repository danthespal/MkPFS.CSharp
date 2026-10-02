using MkPFS.Core.IO;

namespace MkPFS.Core.PFS;

/// <summary>
/// Files a verification compares an image against: a source directory, or one file exposed under the
/// image's inner name (replaces Python's temporary staging folder for <c>verify --source-file</c>).
/// </summary>
public sealed class SourceTree
{
    private SourceTree(string description, IReadOnlyDictionary<string, string> files, bool valid)
    {
        Description = description;
        Files = files;
        IsValid = valid;
    }

    /// <summary>Path shown in messages.</summary>
    public string Description { get; }

    /// <summary>Relative POSIX path → absolute file path.</summary>
    public IReadOnlyDictionary<string, string> Files { get; }

    /// <summary>False when the source directory does not exist.</summary>
    public bool IsValid { get; }

    /// <summary>All files under <paramref name="directory"/>, skipping OS metadata (Python <c>validate_source_paths</c>).</summary>
    /// <param name="directory">Source directory.</param>
    /// <returns>Source tree.</returns>
    public static SourceTree FromDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return new SourceTree(directory, new Dictionary<string, string>(), valid: false);
        }

        Dictionary<string, string> files = new(StringComparer.Ordinal);
        foreach (string path in SourceScanner.GatherFiles(directory))
        {
            files[Path.GetRelativePath(directory, path).Replace('\\', '/')] = path;
        }

        return new SourceTree(directory, files, valid: true);
    }

    /// <summary>One file exposed as <paramref name="innerName"/>.</summary>
    /// <param name="file">Source file.</param>
    /// <param name="innerName">Name inside the image.</param>
    /// <returns>Source tree.</returns>
    public static SourceTree FromSingleFile(string file, string innerName) =>
        new(file, new Dictionary<string, string>(StringComparer.Ordinal) { [innerName] = file }, valid: true);
}
