using System.Buffers.Binary;
using System.Text;

namespace MkPFS.Core.AMPR;

/// <summary>One AMPRIDX3 row as the pack tools see it.</summary>
/// <param name="Path">Canonical <c>/app0</c> path.</param>
/// <param name="Size">File size.</param>
/// <param name="MTime">Modification time, Unix seconds.</param>
public readonly record struct AMPRIndexEntry(string Path, ulong Size, long MTime);

/// <summary>
/// Strict AMPRIDX3 (<c>ampr_emu.index</c>) reader used by the pack tools (Python <c>read_ampridx3</c> in
/// ampr_pack_format.py). Row <c>n</c> is file id <c>n + 1</c>. The index writer lives in
/// <c>MkPFS.Build.AmprIndex</c>.
/// </summary>
public static class AMPRIndexReader
{
    private const int HeaderSize = 48;
    private const int EntrySize = 24;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Read an index file.</summary>
    /// <param name="path">Index path.</param>
    /// <returns>Rows in record order.</returns>
    /// <exception cref="InvalidDataException">The index is malformed.</exception>
    public static List<AMPRIndexEntry> Read(string path) => Parse(File.ReadAllBytes(path));

    /// <summary>Parse index bytes.</summary>
    /// <param name="data">Index bytes.</param>
    /// <returns>Rows in record order.</returns>
    /// <exception cref="InvalidDataException">The index is malformed.</exception>
    public static List<AMPRIndexEntry> Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize)
        {
            throw new InvalidDataException("AMPRIDX3 is truncated");
        }

        if (!data[..8].SequenceEqual("AMPRIDX3"u8) || BinaryPrimitives.ReadUInt32LittleEndian(data[8..]) != 3)
        {
            throw new InvalidDataException("unsupported AMPR index");
        }

        uint entrySize = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        ulong entryCount = BinaryPrimitives.ReadUInt64LittleEndian(data[16..]);
        ulong pathBytes = BinaryPrimitives.ReadUInt64LittleEndian(data[24..]);
        ulong hashOffset = BinaryPrimitives.ReadUInt64LittleEndian(data[32..]);
        uint hashSlotSize = BinaryPrimitives.ReadUInt32LittleEndian(data[40..]);
        uint hashSlotCount = BinaryPrimitives.ReadUInt32LittleEndian(data[44..]);
        if (entrySize != EntrySize)
        {
            throw new InvalidDataException("unexpected AMPRIDX3 entry size");
        }

        UInt128 pathOffset = HeaderSize + ((UInt128)entryCount * entrySize);
        if (pathOffset + pathBytes > (UInt128)data.Length)
        {
            throw new InvalidDataException("AMPRIDX3 records/path blob are truncated");
        }

        if (hashOffset < pathOffset + pathBytes || hashOffset > (ulong)data.Length)
        {
            throw new InvalidDataException("invalid AMPRIDX3 hash offset");
        }

        if (hashSlotCount != 0 && hashSlotSize == 0)
        {
            throw new InvalidDataException("invalid AMPRIDX3 hash table");
        }

        List<AMPRIndexEntry> result = new((int)entryCount);
        for (int index = 0; index < (int)entryCount; index++)
        {
            ReadOnlySpan<byte> record = data.Slice(HeaderSize + (index * EntrySize), EntrySize);
            ulong pathAt = BinaryPrimitives.ReadUInt32LittleEndian(record);
            ulong pathLength = BinaryPrimitives.ReadUInt32LittleEndian(record[4..]);
            ulong size = BinaryPrimitives.ReadUInt64LittleEndian(record[8..]);
            long mtime = BinaryPrimitives.ReadInt64LittleEndian(record[16..]);
            if (pathAt + pathLength >= pathBytes)
            {
                throw new InvalidDataException($"AMPRIDX3 path {index} is out of range");
            }

            int start = (int)pathOffset + (int)pathAt;
            int end = start + (int)pathLength;
            if (data[end] != 0)
            {
                throw new InvalidDataException($"AMPRIDX3 path {index} is not NUL terminated");
            }

            string decoded;
            try
            {
                decoded = StrictUtf8.GetString(data[start..end]);
            }
            catch (DecoderFallbackException exc)
            {
                throw new InvalidDataException($"AMPRIDX3 path {index} is not UTF-8", exc);
            }

            try
            {
                result.Add(new AMPRIndexEntry(AMPRAssetPath.Canonical(decoded), size, mtime));
            }
            catch (ArgumentException exc)
            {
                throw new InvalidDataException(exc.Message, exc);
            }
        }

        return result;
    }
}
