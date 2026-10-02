using System.Globalization;
using System.Text;

namespace MkPFS.Repair;

/// <summary>
/// GC <c>bad_blocks.tsv</c>: a header line, then one row per block
/// (<c>block, stored_len, stored_offset, first_diff, software_fnv, mounted_fnv</c>).
/// </summary>
public static class BadBlockList
{
    /// <summary>GC header line.</summary>
    public const string Header = "block\tstored_len\tstored_offset\tfirst_diff\tsoftware_fnv\tmounted_fnv";

    /// <summary>
    /// Read block indexes like GC <c>repair_load_bad_blocks_tsv</c>: the leading decimal number of each line,
    /// lines without one (the header) are skipped.
    /// </summary>
    /// <param name="path">TSV path.</param>
    /// <param name="blockCount">Image block count.</param>
    /// <returns>Distinct block indexes in file order.</returns>
    /// <exception cref="InvalidDataException">An index is out of range or the list is empty.</exception>
    public static List<long> Read(string path, long blockCount)
    {
        List<long> blocks = [];
        HashSet<long> seen = [];
        foreach (string line in File.ReadLines(path))
        {
            string trimmed = line.TrimStart(' ', '\t');
            int digits = 0;
            while (digits < trimmed.Length && char.IsAsciiDigit(trimmed[digits]))
            {
                digits++;
            }

            if (digits == 0)
            {
                continue;
            }

            if (!long.TryParse(trimmed.AsSpan(0, digits), NumberStyles.None, CultureInfo.InvariantCulture, out long block) || block >= blockCount)
            {
                throw new InvalidDataException($"invalid repair bad block list: block {trimmed[..digits]} is outside the image ({blockCount} blocks)");
            }

            if (seen.Add(block))
            {
                blocks.Add(block);
            }
        }

        return blocks.Count > 0 ? blocks : throw new InvalidDataException("repair bad block list is empty");
    }

    /// <summary>
    /// Write marked blocks in GC format. Offline repair has no mounted copy, so <c>first_diff</c> is -1 and
    /// <c>mounted_fnv</c> is <c>-</c>; GC only reads the first column back.
    /// </summary>
    /// <param name="path">Output path.</param>
    /// <param name="image">Image before repair.</param>
    /// <param name="blocks">Marked blocks, ascending.</param>
    /// <param name="decodedFnv">FNV-1a 64 of each marked block's decoded bytes.</param>
    public static void Write(string path, PFSCImage image, IReadOnlyList<long> blocks, IReadOnlyList<ulong> decodedFnv)
    {
        StringBuilder text = new();
        text.Append(Header).Append('\n');
        for (int i = 0; i < blocks.Count; i++)
        {
            long block = blocks[i];
            text.Append(CultureInfo.InvariantCulture, $"{block}\t{image.StoredLength(block)}\t{image.Offsets[block]}\t-1\t{decodedFnv[i]:x16}\t-\n");
        }

        File.WriteAllText(path, text.ToString());
    }
}
