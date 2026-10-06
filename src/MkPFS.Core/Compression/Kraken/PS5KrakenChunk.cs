namespace MkPFS.Core.Compression.Kraken;

/// <summary>A Kraken-compressed chunk of a PS5 inner image.</summary>
/// <param name="Payload">Stored bytes (one or two sub-chunks).</param>
/// <param name="FirstLength">Stored length of the first sub-chunk (the whole payload when there is one).</param>
/// <param name="SecondLength">Stored length of the second sub-chunk, 0 when there is one.</param>
/// <param name="Kind">naps kind of the first sub-chunk: 2, or 3 for sub (delta) literals.</param>
/// <param name="SecondKind">naps kind of the second sub-chunk, 0 when there is one.</param>
public sealed record PS5KrakenChunk(byte[] Payload, int FirstLength, int SecondLength, int Kind, int SecondKind)
{
    /// <summary>Uncompressed size of one sub-chunk; a larger chunk (up to 256 KiB) has two.</summary>
    public const int SubChunkSize = 0x20000;

    /// <summary>
    /// Default number of chunks encoded at once: every logical processor, since the encoder waits on memory and
    /// gains from simultaneous multithreading. Each holds about 20 MiB of working arrays plus its batch share, so the
    /// count is also capped at one worker per 64 MiB of half the memory available to the process.
    /// </summary>
    public static int MaxParallelism =>
        (int)Math.Clamp(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 2 / (64L << 20), 1, Environment.ProcessorCount);

    /// <summary>Release the encoder's pooled working arrays (after a build).</summary>
    public static void ReleaseScratch() => KrakenScratch.Trim();

    /// <summary>Decoder flags for a chunk with the given naps kinds (both sub-chunks newLZ).</summary>
    /// <param name="kind">First sub-chunk kind.</param>
    /// <param name="secondKind">Second sub-chunk kind.</param>
    /// <returns>Flags for <c>KrakenDecoder.DecodeBlock</c>.</returns>
    public static int DecoderFlags(int kind, int secondKind) => 0x22 | (kind & 1) | ((secondKind & 1) << 4);

    /// <summary>
    /// Compress <paramref name="data"/> (at most 256 KiB) with MkPFS's Kraken encoder. Succeeds only when every
    /// sub-chunk is newLZ, the payload is smaller than the data, and it decodes back to the data under the flags
    /// its naps kinds give, so a chunk is never stored in a form a reader cannot describe.
    /// </summary>
    /// <param name="data">Chunk.</param>
    /// <param name="chunk">The compressed chunk.</param>
    /// <returns>Whether compressing pays.</returns>
    public static bool TryEncode(ReadOnlySpan<byte> data, out PS5KrakenChunk? chunk) => TryEncode(data, fast: false, out chunk);

    /// <summary>As <see cref="TryEncode(ReadOnlySpan{byte}, out PS5KrakenChunk?)"/>, optionally without the windowed optimal parse.</summary>
    /// <param name="data">Chunk.</param>
    /// <param name="fast">Parse greedily only: about twice as fast, a few percent larger.</param>
    /// <param name="chunk">The compressed chunk.</param>
    /// <returns>Whether compressing pays.</returns>
    public static bool TryEncode(ReadOnlySpan<byte> data, bool fast, out PS5KrakenChunk? chunk)
    {
        chunk = null;
        if (data.Length is 0 or > 2 * SubChunkSize)
        {
            return false;
        }

        using KrakenScratch.Lease lease = KrakenScratch.Borrow();
        OodleKrakenEncoder.SkipWindowedOptimal = fast;
        EncodedBlock? block;
        try
        {
            block = OodleKrakenEncoder.EncodeBlock(data, useHuffmanArrays: true);
        }
        finally
        {
            OodleKrakenEncoder.SkipWindowedOptimal = false;
        }

        if (block is not { } encoded
            || encoded.Payload.Length >= data.Length
            || encoded.Chunk0Form != KrakenSubChunkForm.Lz || (encoded.MultiChunk && encoded.Chunk1Form != KrakenSubChunkForm.Lz))
        {
            return false;
        }

        // Literal mode 0 is sub (delta) literals: kind 3.
        int kind = encoded.Chunk0LitMode == 0 ? 3 : 2;
        int secondKind = encoded.MultiChunk ? (encoded.Chunk1LitMode == 0 ? 3 : 2) : 0;
        int first = encoded.MultiChunk ? encoded.FirstChunkCompSize : encoded.Payload.Length;
        byte[] check = KrakenScratch.Rent<byte>(KrakenScratch.Slot.DecodeCheck, data.Length);
        if (KrakenDecoder.DecodeBlock(encoded.Payload, DecoderFlags(kind, secondKind), encoded.MultiChunk ? first : 0, check) != KrakenDecodeStatus.Success
            || !data.SequenceEqual(check))
        {
            return false;
        }

        chunk = new PS5KrakenChunk(encoded.Payload, first, encoded.Payload.Length - (encoded.MultiChunk ? first : encoded.Payload.Length), kind, secondKind);
        return true;
    }
}
