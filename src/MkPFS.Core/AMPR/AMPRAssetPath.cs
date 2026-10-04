using System.Text;
using MkPFS.Core.Util;

namespace MkPFS.Core.AMPR;

/// <summary>
/// <c>/app0</c> path rules shared by the pack manifest and AMPRIDX3 (ampr_pack_format.py:170-240).
/// Unlike <c>MkPFS.Build.AmprIndex.PathHash</c> (lower-cased code points), the pack manifest hashes the
/// UTF-8 bytes and folds only ASCII <c>A..Z</c>.
/// </summary>
public static class AMPRAssetPath
{
    /// <summary>FNV-1a 64 over bytes; 0 becomes 1 (Python <c>fnv1a64</c>).</summary>
    /// <param name="data">Bytes.</param>
    /// <returns>Hash.</returns>
    public static ulong Fnv1a64(ReadOnlySpan<byte> data)
    {
        ulong value = 0xCBF29CE484222325UL;
        foreach (byte b in data)
        {
            value ^= b;
            value = unchecked(value * 0x100000001B3UL);
        }

        return value == 0 ? 1 : value;
    }

    /// <summary>UTF-8 bytes with <c>\</c> -> <c>/</c> and ASCII <c>A..Z</c> folded (Python <c>ascii_fold_path_bytes</c>).</summary>
    /// <param name="path">Path.</param>
    /// <returns>Folded bytes.</returns>
    public static byte[] AsciiFoldPathBytes(string path)
    {
        byte[] raw = Encoding.UTF8.GetBytes(path.Replace('\\', '/'));
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] is >= 0x41 and <= 0x5A)
            {
                raw[i] += 0x20;
            }
        }

        return raw;
    }

    /// <summary>Manifest path hash of the canonical form of <paramref name="path"/> (Python <c>asset_path_hash</c>).</summary>
    /// <param name="path">Path.</param>
    /// <returns>Hash.</returns>
    public static ulong Hash(string path) => Fnv1a64(AsciiFoldPathBytes(Canonical(path)));

    /// <summary>
    /// Canonical absolute <c>/app0</c> path: <c>/</c> separators, no empty or <c>.</c> components, <c>..</c>
    /// resolved. The spelling is kept; only the <c>/app0</c> prefix check ignores case (Python <c>canonical_asset_path</c>).
    /// </summary>
    /// <param name="path">Path, absolute or relative to <c>/</c>.</param>
    /// <returns>Canonical path.</returns>
    /// <exception cref="ArgumentException">The path escapes the root, contains NUL, or is outside <c>/app0</c>.</exception>
    public static string Canonical(string path)
    {
        path = path.Replace('\\', '/');
        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        List<string> parts = [];
        foreach (string component in path.Split('/'))
        {
            if (component.Length == 0 || component == ".")
            {
                continue;
            }

            if (component == "..")
            {
                if (parts.Count == 0)
                {
                    throw new ArgumentException($"path escapes root: {path}");
                }

                parts.RemoveAt(parts.Count - 1);
                continue;
            }

            if (component.Contains('\0', StringComparison.Ordinal))
            {
                throw new ArgumentException("NUL in path");
            }

            parts.Add(component);
        }

        string normalized = "/" + string.Join('/', parts);
        if (normalized.Equals("/app0", StringComparison.OrdinalIgnoreCase))
        {
            return "/app0";
        }

        return normalized.StartsWith("/app0/", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : throw new ArgumentException($"asset is outside /app0: {path}");
    }

    /// <summary>Path relative to <c>/app0</c>, empty for <c>/app0</c> itself (Python <c>asset_relative_path</c>).</summary>
    /// <param name="path">Path.</param>
    /// <returns>Relative path with <c>/</c> separators.</returns>
    public static string Relative(string path)
    {
        string canonical = Canonical(path);
        return canonical == "/app0" ? string.Empty : canonical[6..];
    }

    /// <summary>
    /// Join a relative <c>/</c> path to <paramref name="root"/> after rejecting empty, <c>.</c>, <c>..</c>,
    /// absolute and backslash paths, and paths whose parent leaves the root (Python <c>safe_output_path</c>).
    /// </summary>
    /// <param name="root">Output root.</param>
    /// <param name="relative">Relative path.</param>
    /// <returns>The joined path.</returns>
    /// <exception cref="ArgumentException">The path is unsafe.</exception>
    public static string SafeOutputPath(string root, string relative)
    {
        if (relative.Length == 0
            || relative.Contains('\\', StringComparison.Ordinal)
            || relative.StartsWith('/')
            || relative.EndsWith('/')
            || relative.Split('/').Any(component => component is "" or "." or ".."))
        {
            throw new ArgumentException($"unsafe output path: {PythonText.Repr(relative)}");
        }

        string candidate = Path.Combine([root, .. relative.Split('/')]);
        string rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string parentFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetDirectoryName(Path.GetFullPath(candidate))!));
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string rootPrefix = Path.EndsInDirectorySeparator(rootFull) ? rootFull : rootFull + Path.DirectorySeparatorChar;
        bool inside = parentFull.Equals(rootFull, comparison) || parentFull.StartsWith(rootPrefix, comparison);
        return inside ? candidate : throw new ArgumentException($"output path escapes root: {PythonText.Repr(relative)}");
    }
}
