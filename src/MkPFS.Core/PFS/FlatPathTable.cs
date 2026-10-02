using System.Text;

namespace MkPFS.Core.PFS;

/// <summary>flat_path_table helpers (port of Python <c>fpt_hash</c>, <c>pfs.py:2451</c>).</summary>
public static class FlatPathTable
{
    /// <summary>Value bit marking a directory entry.</summary>
    public const uint DirectoryBit = 0x20000000;

    /// <summary>Value bit marking a collision-resolver offset.</summary>
    public const uint CollisionBit = 0x80000000;

    /// <summary>Mask for the inode number in a value.</summary>
    public const uint InodeMask = 0x1FFFFFFF;

    /// <summary>
    /// <c>h = (codepoint(c) + 31 * h) mod 2^32</c> over the path, upper-casing each code point first when
    /// <paramref name="caseInsensitive"/>. PFS names are ASCII, where this equals Python <c>str.upper</c>.
    /// </summary>
    /// <param name="path">Path such as <c>/sce_sys/param.json</c>.</param>
    /// <param name="caseInsensitive">Fold to upper case.</param>
    /// <returns>32-bit hash.</returns>
    public static uint Hash(string path, bool caseInsensitive = true)
    {
        uint h = 0;
        foreach (Rune rune in path.EnumerateRunes())
        {
            Rune value = caseInsensitive ? Rune.ToUpperInvariant(rune) : rune;
            h = unchecked((uint)value.Value + (31 * h));
        }

        return h;
    }
}
