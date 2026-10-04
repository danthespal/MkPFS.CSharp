using System.Buffers.Binary;
using System.Text;

namespace MkPFS.Build.AMPRPack;

/// <summary>One AMPRIDX3 row as the trace tools see it (Python <c>IndexEntry</c>).</summary>
/// <param name="FileId">Row number plus one.</param>
/// <param name="Path">Indexed path (<c>/app0/...</c>); invalid UTF-8 is replaced.</param>
/// <param name="Size">File size.</param>
/// <param name="MTime">Modification time.</param>
public sealed record AMPRTraceIndexEntry(int FileId, string Path, long Size, long MTime);

/// <summary>APR command fields the profiler uses (Python <c>decode_command</c> keeps more).</summary>
/// <param name="Name">Command name, as ampr_emu's parser names it.</param>
/// <param name="Dwords">Command length in 32-bit words.</param>
/// <param name="FileId">File id (<c>AprReadFile</c>).</param>
/// <param name="FileOffset">File offset (reads that carry one).</param>
/// <param name="Length">Read length (reads).</param>
public readonly record struct AMPRCommand(string Name, int Dwords, long FileId = 0, long FileOffset = 0, long Length = 0);

/// <summary>
/// AMPR Emu's APR submit journal (<c>ampr_commands.bin</c>, <c>AMPRCMD1</c>), written by the debug emulator build:
/// record header, command decoder and the lenient AMPRIDX3 reader of ampr_emu <c>tools/parse_ampr_command_log.py</c>.
/// </summary>
public static class AMPRCommandLog
{
    /// <summary>Record magic.</summary>
    public static ReadOnlySpan<byte> Magic => "AMPRCMD1"u8;

    /// <summary>Supported record version.</summary>
    public const int Version = 1;

    /// <summary>Size of the fixed record header (<c>&lt;8sHHIQQQQQQIIIIIIII</c>).</summary>
    public const int HeaderSize = 96;

    /// <summary>Submit domain of APR commands (2 is AMM).</summary>
    public const int DomainApr = 1;

    /// <summary>Fixed part of one journal record.</summary>
    /// <param name="Version">Record version.</param>
    /// <param name="HeaderBytes">Header size, including any extension.</param>
    /// <param name="RecordBytes">Header plus payload.</param>
    /// <param name="Sequence">Submit sequence, from 1.</param>
    /// <param name="MonotonicNs">Monotonic time of the submit.</param>
    /// <param name="PayloadHash">FNV-1a of the payload (<see cref="Fnv1a64"/>).</param>
    /// <param name="PayloadBytes">Payload size.</param>
    /// <param name="CommandCount">Commands in the payload, or 0 when unknown.</param>
    /// <param name="Priority">Submit priority (the gather/scatter state key).</param>
    /// <param name="Domain">1 APR, 2 AMM.</param>
    public readonly record struct Header(
        int Version, int HeaderBytes, long RecordBytes, ulong Sequence, ulong MonotonicNs, ulong PayloadHash, long PayloadBytes,
        long CommandCount, long Priority, long Domain);

    /// <summary>Parse a record header; the magic is checked by the caller.</summary>
    /// <param name="raw">At least <see cref="HeaderSize"/> bytes.</param>
    /// <returns>Header fields.</returns>
    public static Header ReadHeader(ReadOnlySpan<byte> raw) => new(
        BinaryPrimitives.ReadUInt16LittleEndian(raw[8..]),
        BinaryPrimitives.ReadUInt16LittleEndian(raw[10..]),
        BinaryPrimitives.ReadUInt32LittleEndian(raw[12..]),
        BinaryPrimitives.ReadUInt64LittleEndian(raw[16..]),
        BinaryPrimitives.ReadUInt64LittleEndian(raw[32..]),
        BinaryPrimitives.ReadUInt64LittleEndian(raw[56..]),
        BinaryPrimitives.ReadUInt32LittleEndian(raw[64..]),
        BinaryPrimitives.ReadUInt32LittleEndian(raw[72..]),
        BinaryPrimitives.ReadUInt32LittleEndian(raw[76..]),
        BinaryPrimitives.ReadUInt32LittleEndian(raw[80..]));

    /// <summary>FNV-1a 64 of a payload, never 0 (Python <c>fnv1a64</c>).</summary>
    /// <param name="data">Payload.</param>
    /// <returns>Hash.</returns>
    public static ulong Fnv1a64(ReadOnlySpan<byte> data)
    {
        ulong hash = 1469598103934665603UL;
        foreach (byte b in data)
        {
            hash = (hash ^ b) * 1099511628211UL;
        }

        return hash == 0 ? 1 : hash;
    }

    /// <summary>
    /// Read an AMPRIDX3 file the way the trace tools do (Python <c>load_index</c>): bounds are checked, but paths that
    /// are not UTF-8 are decoded with replacement characters and the hash table is not used.
    /// </summary>
    /// <param name="path">Index file.</param>
    /// <returns>Rows by file id.</returns>
    /// <exception cref="InvalidDataException">The index is malformed.</exception>
    public static Dictionary<int, AMPRTraceIndexEntry> LoadIndex(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        if (data.Length < 48)
        {
            throw new InvalidDataException($"index is too small: {path}");
        }

        ReadOnlySpan<byte> span = data;
        if (!span[..8].SequenceEqual("AMPRIDX3"u8))
        {
            throw new InvalidDataException($"unsupported index magic {PythonBytesRepr(span[..8])}; expected AMPRIDX3");
        }

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);
        uint entrySize = BinaryPrimitives.ReadUInt32LittleEndian(span[12..]);
        ulong entryCount = BinaryPrimitives.ReadUInt64LittleEndian(span[16..]);
        ulong pathBytes = BinaryPrimitives.ReadUInt64LittleEndian(span[24..]);
        ulong hashOffset = BinaryPrimitives.ReadUInt64LittleEndian(span[32..]);
        uint hashSlotSize = BinaryPrimitives.ReadUInt32LittleEndian(span[40..]);
        uint hashSlotCount = BinaryPrimitives.ReadUInt32LittleEndian(span[44..]);
        if (version != 3)
        {
            throw new InvalidDataException($"unsupported AMPRIDX3 version {version}; expected 3");
        }

        if (entrySize != 24)
        {
            throw new InvalidDataException($"unsupported AMPRIDX3 entry size {entrySize}; expected 24");
        }

        UInt128 pathStart = 48 + ((UInt128)entryCount * entrySize);
        UInt128 pathEnd = pathStart + pathBytes;
        if (pathEnd > (UInt128)data.Length)
        {
            throw new InvalidDataException("AMPRIDX3 path blob exceeds file size");
        }

        if (hashOffset < pathEnd || hashOffset > (ulong)data.Length)
        {
            throw new InvalidDataException("AMPRIDX3 hash offset is invalid");
        }

        if (hashSlotSize != 16)
        {
            throw new InvalidDataException($"unsupported AMPRIDX3 hash slot size {hashSlotSize}");
        }

        if (hashOffset + ((UInt128)hashSlotCount * hashSlotSize) > (UInt128)data.Length)
        {
            throw new InvalidDataException("AMPRIDX3 hash table exceeds file size");
        }

        ReadOnlySpan<byte> blob = span[(int)pathStart..(int)pathEnd];
        Dictionary<int, AMPRTraceIndexEntry> entries = new((int)entryCount);
        for (int index = 0; index < (int)entryCount; index++)
        {
            ReadOnlySpan<byte> record = span.Slice(48 + (index * 24), 24);
            uint pathOff = BinaryPrimitives.ReadUInt32LittleEndian(record);
            uint pathLen = BinaryPrimitives.ReadUInt32LittleEndian(record[4..]);
            ulong size = BinaryPrimitives.ReadUInt64LittleEndian(record[8..]);
            long mtime = BinaryPrimitives.ReadInt64LittleEndian(record[16..]);
            if ((ulong)pathOff + pathLen > (ulong)blob.Length)
            {
                throw new InvalidDataException($"AMPRIDX3 entry {index} path is out of bounds");
            }

            string decoded = Encoding.UTF8.GetString(blob.Slice((int)pathOff, (int)pathLen));
            entries[index + 1] = new AMPRTraceIndexEntry(index + 1, decoded, (long)size, mtime);
        }

        return entries;
    }

    /// <summary>
    /// Decode the command at <paramref name="offset"/> (Python <c>decode_command</c>): its name and length for every
    /// command, and the file fields of APR reads.
    /// </summary>
    /// <param name="data">Submit payload.</param>
    /// <param name="offset">Byte offset of the command.</param>
    /// <returns>The command.</returns>
    /// <exception cref="InvalidDataException">The command is truncated or has an invalid length (Python
    /// <c>ValueError</c>, same message).</exception>
    public static AMPRCommand Decode(ReadOnlySpan<byte> data, int offset)
    {
        if ((offset & 3) != 0 || offset + 4 > data.Length)
        {
            throw new InvalidDataException("unaligned or truncated command");
        }

        uint w0 = U32(data, offset);
        uint opcode8 = w0 & 0xFF;
        uint opcode12 = w0 & 0xFFF;
        int dwords;
        if (opcode8 == 1)
        {
            dwords = (int)((w0 >> 8) & 0xF) + 1;
            return dwords is >= 2 and <= 4 && Need(data, offset, dwords)
                ? new AMPRCommand("WaitOnAddress", dwords)
                : throw new InvalidDataException("invalid WaitOnAddress length");
        }

        if (opcode8 == 2)
        {
            dwords = (int)((w0 >> 8) & 0xF) + 1;
            return dwords == 4 || !(dwords is >= 1 and <= 5 && Need(data, offset, dwords))
                ? throw new InvalidDataException("invalid WaitOnCounter length")
                : new AMPRCommand("WaitOnCounter", dwords);
        }

        if (opcode8 is 5 or 117)
        {
            dwords = (int)((w0 >> 8) & 3) + 1;
            if (!(dwords is >= 2 and <= 4 && Need(data, offset, dwords)))
            {
                throw new InvalidDataException("invalid WriteAddress length");
            }

            string name = (U32(data, offset + 4) & 7) switch
            {
                1 => "WriteAddressFromTimeCounter",
                2 => "WriteAddressFromCounter",
                3 => "WriteAddressFromCounterPair",
                _ => "WriteAddress",
            };
            return new AMPRCommand(name, dwords);
        }

        if (opcode8 is 6 or 118)
        {
            dwords = (int)((w0 >> 8) & 0xF) + 1;
            return dwords is >= 1 and <= 3 && Need(data, offset, dwords)
                ? new AMPRCommand("WriteCounter", dwords)
                : throw new InvalidDataException("invalid WriteCounter length");
        }

        if (opcode12 is 1032 or 1144)
        {
            return Need(data, offset, 5) ? new AMPRCommand("WriteKernelEventQueue", 5) : throw new InvalidDataException("truncated WriteKernelEventQueue");
        }

        if ((w0 & 0xFFFF000F) == 0x5452000F)
        {
            uint markerType = (w0 >> 12) & 0xF;
            int payloadDwords = (int)((w0 >> 8) & 0xF);
            dwords = payloadDwords + 1;
            if (!Need(data, offset, dwords))
            {
                throw new InvalidDataException("truncated NOP/marker packet");
            }

            if (w0 == 0x5452300F)
            {
                return new AMPRCommand("MarkerPop", 1);
            }

            if (markerType is 5 or 6 && payloadDwords < 1)
            {
                throw new InvalidDataException("marker with color has no color dword");
            }

            return new AMPRCommand(markerType switch
            {
                1 or 5 => "MarkerSet",
                2 or 6 => "MarkerPush",
                4 => "MarkerContinuation",
                _ => "Nop",
            }, dwords);
        }

        if (opcode8 == 40)
        {
            dwords = (int)((w0 >> 8) & 7) + 1;
            if (!(dwords is >= 5 and <= 6 && Need(data, offset, dwords)))
            {
                throw new InvalidDataException("invalid AprReadFile length");
            }

            uint w4 = U32(data, offset + 16);
            long fileOffset = ((w0 >> 12) & 0x3FFFF) | (w4 & 0xFFFC0000);
            if (dwords >= 6)
            {
                fileOffset |= (long)(U32(data, offset + 20) & 0xFF) << 32;
            }

            return new AMPRCommand("AprReadFile", dwords, U32(data, offset + 8) & 0x7FFFFFFF, fileOffset, U32(data, offset + 4) + 1L);
        }

        if (opcode8 == 41)
        {
            dwords = (int)((w0 >> 8) & 3) + 1;
            if (!(dwords is >= 2 and <= 3 && Need(data, offset, dwords)))
            {
                throw new InvalidDataException("invalid AprReadGather length");
            }

            long fileOffset = (w0 >> 12) & 0x3FFFF;
            if (dwords >= 3)
            {
                fileOffset |= (long)(U32(data, offset + 8) & 0x3FFFFF) << 18;
            }

            return new AMPRCommand("AprReadGather", dwords, FileOffset: fileOffset, Length: U32(data, offset + 4) + 1L);
        }

        if (opcode12 == 0x22A)
        {
            return Need(data, offset, 3)
                ? new AMPRCommand("AprReadScatter", 3, Length: U32(data, offset + 4) + 1L)
                : throw new InvalidDataException("truncated AprReadScatter");
        }

        if (opcode8 == 43)
        {
            dwords = (int)((w0 >> 8) & 7) + 1;
            if (!(dwords is >= 4 and <= 5 && Need(data, offset, dwords)))
            {
                throw new InvalidDataException("invalid AprReadGatherScatter length");
            }

            uint w3 = U32(data, offset + 12);
            long fileOffset = ((w0 >> 12) & 0x3FFFF) | (w3 & 0xFFFC0000);
            if (dwords >= 5)
            {
                fileOffset |= (long)(U32(data, offset + 16) & 0xFF) << 32;
            }

            return new AMPRCommand("AprReadGatherScatter", dwords, FileOffset: fileOffset, Length: U32(data, offset + 4) + 1L);
        }

        if (w0 == 47)
        {
            return new AMPRCommand("AprResetGatherScatterState", 1);
        }

        if (opcode12 == 557)
        {
            return Need(data, offset, 3) ? new AMPRCommand("AprMapBegin", 3) : throw new InvalidDataException("truncated AprMapBegin");
        }

        if (opcode12 == 813)
        {
            return Need(data, offset, 4) ? new AMPRCommand("AprMapDirectBegin", 4) : throw new InvalidDataException("truncated AprMapDirectBegin");
        }

        if (w0 == 46)
        {
            return new AMPRCommand("AprMapEnd", 1);
        }

        // AMM commands (Python decode_amm); a truncated one falls through to Unknown.
        dwords = opcode12 switch
        {
            0x221 => 3,
            0x321 => 4,
            0x325 => 4,
            0x425 => 5,
            0x222 or 0x228 => 3,
            0x323 or 0x324 or 0x327 => 4,
            0x423 or 0x424 => 5,
            0x326 => 4,
            0x426 => 5,
            _ => 0,
        };
        if (dwords > 0 && Need(data, offset, dwords))
        {
            return new AMPRCommand("Amm", dwords);
        }

        int guessed = (int)((w0 >> 8) & 0xF) + 1;
        return new AMPRCommand("Unknown", Need(data, offset, guessed) ? guessed : 1);
    }

    private static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);

    private static bool Need(ReadOnlySpan<byte> data, int offset, int dwords) =>
        dwords > 0 && offset >= 0 && offset + ((long)dwords * 4) <= data.Length;

    // Python repr(bytes): b'...' with printable ASCII kept, \t \n \r and other bytes as \xNN.
    private static string PythonBytesRepr(ReadOnlySpan<byte> value)
    {
        bool hasSingle = value.Contains((byte)'\'');
        bool hasDouble = value.Contains((byte)'"');
        char quote = hasSingle && !hasDouble ? '"' : '\'';
        StringBuilder builder = new("b");
        builder.Append(quote);
        foreach (byte b in value)
        {
            _ = b switch
            {
                (byte)'\\' => builder.Append(@"\\"),
                (byte)'\t' => builder.Append(@"\t"),
                (byte)'\n' => builder.Append(@"\n"),
                (byte)'\r' => builder.Append(@"\r"),
                _ when b == quote => builder.Append('\\').Append(quote),
                >= 0x20 and < 0x7F => builder.Append((char)b),
                _ => builder.Append(@"\x").Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)),
            };
        }

        return builder.Append(quote).ToString();
    }
}
