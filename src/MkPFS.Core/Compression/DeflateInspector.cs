namespace MkPFS.Core.Compression;

/// <summary>Structure of one zlib stream as seen by <see cref="DeflateInspector"/>.</summary>
/// <param name="Valid">Stream parsed to its final block without errors.</param>
/// <param name="Error">Parse error, or <see langword="null"/>.</param>
/// <param name="OutputLength">Decoded length in bytes.</param>
/// <param name="Blocks">Deflate blocks.</param>
/// <param name="StoredBlocks">Stored (type 0) blocks.</param>
/// <param name="FixedBlocks">Fixed-Huffman (type 1) blocks.</param>
/// <param name="DynamicBlocks">Dynamic-Huffman (type 2) blocks.</param>
/// <param name="MaxDistance">Largest back-reference distance.</param>
/// <param name="FarDistanceCount">Back-references longer than <see cref="DeflateInspector.ZlibMaxDistance"/>.</param>
/// <param name="MaxCodeLength">Longest literal/length or distance code in dynamic blocks (bits).</param>
public sealed record DeflateStreamReport(
    bool Valid,
    string? Error,
    int OutputLength,
    int Blocks,
    int StoredBlocks,
    int FixedBlocks,
    int DynamicBlocks,
    int MaxDistance,
    int FarDistanceCount,
    int MaxCodeLength)
{
    /// <summary>
    /// The stream uses back-references zlib never emits (ISA-L does). PS5 decoded such blocks wrongly in the
    /// bad-block investigation, so they are treated as unsafe for PFSC.
    /// </summary>
    public bool HasFarDistance => FarDistanceCount > 0;
}

/// <summary>
/// Walks a zlib stream (RFC 1950/1951) without materializing output and reports features that matter for
/// PS5 PFSC compatibility. Decoding follows Mark Adler's <c>puff.c</c>.
/// </summary>
public static class DeflateInspector
{
    /// <summary>Largest distance zlib's deflate emits: 32 KiB window minus <c>MIN_LOOKAHEAD</c> (262).</summary>
    public const int ZlibMaxDistance = 32768 - 262;

    private const int MaxBits = 15;
    private const int MaxLCodes = 286;
    private const int MaxDCodes = 30;
    private const int FixLCodes = 288;

    private static readonly short[] LengthBase = [3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258];
    private static readonly short[] LengthExtra = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0];
    private static readonly short[] DistBase = [1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577];
    private static readonly short[] DistExtra = [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13];
    private static readonly short[] CodeLengthOrder = [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];
    private static readonly Huffman FixedLengths;
    private static readonly Huffman FixedDistances;

    static DeflateInspector()
    {
        short[] lengths = new short[FixLCodes];
        for (int i = 0; i < 144; i++)
        {
            lengths[i] = 8;
        }

        for (int i = 144; i < 256; i++)
        {
            lengths[i] = 9;
        }

        for (int i = 256; i < 280; i++)
        {
            lengths[i] = 7;
        }

        for (int i = 280; i < FixLCodes; i++)
        {
            lengths[i] = 8;
        }

        FixedLengths = new Huffman(FixLCodes);
        FixedLengths.Build(lengths);
        short[] distances = new short[MaxDCodes];
        Array.Fill(distances, (short)5);
        FixedDistances = new Huffman(MaxDCodes);
        FixedDistances.Build(distances);
    }

    /// <summary>
    /// A stored PFSC block is risky when it uses back-references zlib never emits (ISA-L output, which the PS5
    /// decoded wrongly) or when this walker cannot parse it.
    /// </summary>
    /// <param name="stored">zlib stream of one 64 KiB block.</param>
    /// <returns><see langword="true"/> when the block should be rewritten (<c>mkpfs repair</c>).</returns>
    public static bool IsRiskyForPS5(ReadOnlySpan<byte> stored)
    {
        DeflateStreamReport report = InspectZlib(stored, 65536);
        return !report.Valid || report.HasFarDistance;
    }

    /// <summary>Inspect a complete zlib stream.</summary>
    /// <param name="stream">zlib bytes (2-byte header, deflate data, Adler-32 trailer).</param>
    /// <param name="maxOutput">Reject streams that decode to more than this many bytes.</param>
    /// <returns>Structure report; <see cref="DeflateStreamReport.Valid"/> is false on malformed input.</returns>
    public static DeflateStreamReport InspectZlib(ReadOnlySpan<byte> stream, int maxOutput = 65536)
    {
        State state = new();
        string? error = null;
        try
        {
            if (stream.Length < 2)
            {
                throw new InvalidDataException("missing zlib header");
            }

            int cmf = stream[0];
            int flg = stream[1];
            if ((cmf & 0x0F) != 8 || (cmf >> 4) > 7 || ((cmf << 8) | flg) % 31 != 0)
            {
                throw new InvalidDataException("invalid zlib header");
            }

            if ((flg & 0x20) != 0)
            {
                throw new InvalidDataException("preset dictionary not supported");
            }

            BitReader bits = new(stream[2..]);
            int last;
            do
            {
                last = bits.Bits(1);
                int type = bits.Bits(2);
                state.Blocks++;
                switch (type)
                {
                    case 0:
                        state.StoredBlocks++;
                        Stored(ref bits, state, maxOutput);
                        break;
                    case 1:
                        state.FixedBlocks++;
                        Codes(ref bits, state, FixedLengths, FixedDistances, maxOutput);
                        break;
                    case 2:
                        state.DynamicBlocks++;
                        Dynamic(ref bits, state, maxOutput);
                        break;
                    default:
                        throw new InvalidDataException("invalid block type");
                }
            }
            while (last == 0);

            if (bits.RemainingBytesAfterAlign() < 4)
            {
                throw new InvalidDataException("missing Adler-32 trailer");
            }
        }
        catch (InvalidDataException ex)
        {
            error = ex.Message;
        }

        return new DeflateStreamReport(
            error is null,
            error,
            state.Output,
            state.Blocks,
            state.StoredBlocks,
            state.FixedBlocks,
            state.DynamicBlocks,
            state.MaxDistance,
            state.FarDistances,
            state.MaxCodeLength);
    }

    private static void Stored(ref BitReader bits, State state, int maxOutput)
    {
        bits.AlignToByte();
        int length = bits.ByteU16();
        int complement = bits.ByteU16();
        if (length != (~complement & 0xFFFF))
        {
            throw new InvalidDataException("stored block length mismatch");
        }

        bits.SkipBytes(length);
        state.AddOutput(length, maxOutput);
    }

    private static void Codes(ref BitReader bits, State state, Huffman lengthCode, Huffman distCode, int maxOutput)
    {
        while (true)
        {
            int symbol = lengthCode.Decode(ref bits);
            if (symbol < 256)
            {
                state.AddOutput(1, maxOutput);
                continue;
            }

            if (symbol == 256)
            {
                return;
            }

            symbol -= 257;
            if (symbol >= 29)
            {
                throw new InvalidDataException("invalid length symbol");
            }

            int length = LengthBase[symbol] + bits.Bits(LengthExtra[symbol]);
            int distSymbol = distCode.Decode(ref bits);
            if (distSymbol >= 30)
            {
                throw new InvalidDataException("invalid distance symbol");
            }

            int distance = DistBase[distSymbol] + bits.Bits(DistExtra[distSymbol]);
            if (distance > state.Output)
            {
                throw new InvalidDataException("distance too far back");
            }

            state.MaxDistance = Math.Max(state.MaxDistance, distance);
            if (distance > ZlibMaxDistance)
            {
                state.FarDistances++;
            }

            state.AddOutput(length, maxOutput);
        }
    }

    private static void Dynamic(ref BitReader bits, State state, int maxOutput)
    {
        int nlen = bits.Bits(5) + 257;
        int ndist = bits.Bits(5) + 1;
        int ncode = bits.Bits(4) + 4;
        if (nlen > MaxLCodes || ndist > MaxDCodes)
        {
            throw new InvalidDataException("bad code counts");
        }

        short[] lengths = new short[MaxLCodes + MaxDCodes];
        for (int i = 0; i < ncode; i++)
        {
            lengths[CodeLengthOrder[i]] = (short)bits.Bits(3);
        }

        Huffman codeLengthCode = new(19);
        if (codeLengthCode.Build(lengths.AsSpan(0, 19)) != 0)
        {
            throw new InvalidDataException("incomplete code length code");
        }

        Array.Clear(lengths);
        int index = 0;
        while (index < nlen + ndist)
        {
            int symbol = codeLengthCode.Decode(ref bits);
            if (symbol < 16)
            {
                lengths[index++] = (short)symbol;
                continue;
            }

            short value = 0;
            int repeat;
            if (symbol == 16)
            {
                if (index == 0)
                {
                    throw new InvalidDataException("repeat with no first length");
                }

                value = lengths[index - 1];
                repeat = 3 + bits.Bits(2);
            }
            else if (symbol == 17)
            {
                repeat = 3 + bits.Bits(3);
            }
            else
            {
                repeat = 11 + bits.Bits(7);
            }

            if (index + repeat > nlen + ndist)
            {
                throw new InvalidDataException("too many lengths");
            }

            while (repeat-- > 0)
            {
                lengths[index++] = value;
            }
        }

        if (lengths[256] == 0)
        {
            throw new InvalidDataException("no end-of-block code");
        }

        for (int i = 0; i < nlen + ndist; i++)
        {
            state.MaxCodeLength = Math.Max(state.MaxCodeLength, lengths[i]);
        }

        Huffman lengthCode = new(MaxLCodes);
        int err = lengthCode.Build(lengths.AsSpan(0, nlen));
        if (err < 0 || (err > 0 && nlen != lengthCode.Count[0] + lengthCode.Count[1]))
        {
            throw new InvalidDataException("incomplete literal/length code");
        }

        Huffman distCode = new(MaxDCodes);
        err = distCode.Build(lengths.AsSpan(nlen, ndist));
        if (err < 0 || (err > 0 && ndist != distCode.Count[0] + distCode.Count[1]))
        {
            throw new InvalidDataException("incomplete distance code");
        }

        Codes(ref bits, state, lengthCode, distCode, maxOutput);
    }

    private sealed class State
    {
        public int Output;
        public int Blocks;
        public int StoredBlocks;
        public int FixedBlocks;
        public int DynamicBlocks;
        public int MaxDistance;
        public int FarDistances;
        public int MaxCodeLength;

        public void AddOutput(int count, int maxOutput)
        {
            Output += count;
            if (Output > maxOutput)
            {
                throw new InvalidDataException($"stream decodes to more than {maxOutput} bytes");
            }
        }
    }

    /// <summary>Canonical Huffman table in puff.c form (counts per length + symbols by code).</summary>
    private sealed class Huffman(int symbols)
    {
        public readonly short[] Count = new short[MaxBits + 1];
        public readonly short[] Symbol = new short[symbols];

        /// <summary>Returns 0 for a complete code, &gt;0 incomplete, &lt;0 over-subscribed.</summary>
        public int Build(ReadOnlySpan<short> lengths)
        {
            Array.Clear(Count);
            foreach (short length in lengths)
            {
                Count[length]++;
            }

            if (Count[0] == lengths.Length)
            {
                return 0;
            }

            int left = 1;
            for (int len = 1; len <= MaxBits; len++)
            {
                left <<= 1;
                left -= Count[len];
                if (left < 0)
                {
                    return left;
                }
            }

            Span<short> offsets = stackalloc short[MaxBits + 1];
            offsets[1] = 0;
            for (int len = 1; len < MaxBits; len++)
            {
                offsets[len + 1] = (short)(offsets[len] + Count[len]);
            }

            for (int symbol = 0; symbol < lengths.Length; symbol++)
            {
                if (lengths[symbol] != 0)
                {
                    Symbol[offsets[lengths[symbol]]++] = (short)symbol;
                }
            }

            return left;
        }

        public int Decode(ref BitReader bits)
        {
            int code = 0;
            int first = 0;
            int index = 0;
            for (int len = 1; len <= MaxBits; len++)
            {
                code |= bits.Bits(1);
                int count = Count[len];
                if (code - count < first)
                {
                    return Symbol[index + (code - first)];
                }

                index += count;
                first += count;
                first <<= 1;
                code <<= 1;
            }

            throw new InvalidDataException("ran out of codes");
        }
    }

    private ref struct BitReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _position;
        private int _bitBuffer;
        private int _bitCount;

        public int Bits(int need)
        {
            long value = _bitBuffer;
            while (_bitCount < need)
            {
                if (_position >= _data.Length)
                {
                    throw new InvalidDataException("unexpected end of stream");
                }

                value |= (long)_data[_position++] << _bitCount;
                _bitCount += 8;
            }

            _bitBuffer = (int)(value >> need);
            _bitCount -= need;
            return (int)(value & ((1L << need) - 1));
        }

        public void AlignToByte()
        {
            _bitBuffer = 0;
            _bitCount = 0;
        }

        public int ByteU16()
        {
            if (_position + 2 > _data.Length)
            {
                throw new InvalidDataException("unexpected end of stream");
            }

            int value = _data[_position] | (_data[_position + 1] << 8);
            _position += 2;
            return value;
        }

        public void SkipBytes(int count)
        {
            if (_position + count > _data.Length)
            {
                throw new InvalidDataException("unexpected end of stream");
            }

            _position += count;
        }

        public int RemainingBytesAfterAlign() => _data.Length - _position;
    }
}
