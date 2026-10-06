using System.Collections.Concurrent;

namespace MkPFS.Core.Compression.Kraken;

/// <summary>
/// Reuse of the encoder's large working arrays (match table, suffix-trie nodes, parser arrivals; about 20 MiB per
/// 128 KiB chunk). An encode borrows one set of arrays from a shared pool for its duration (<see cref="Borrow"/>), so
/// memory follows the number of concurrent encodes, not the number of chunks, and <see cref="Trim"/> releases the
/// pool after a build. An array of a slot is handed back cleared when the same length is asked for again, so a
/// rented array is indistinguishable from a fresh one; chunks are almost always 128 or 256 KiB, so lengths repeat.
/// </summary>
internal static class KrakenScratch
{
    /// <summary>Slots in use.</summary>
    internal enum Slot
    {
        MatchTable,
        MatchBlock,
        DecodeCheck,
        MatchHead,
        MatchPrev,
        DpHead,
        DpPrev,
        Arrivals,
        TrieHash,
        TrieNodePos,
        TrieNodeParent,
        TrieNodeFollow,
        TrieNodeDepth,
        TrieChildStart,
        TrieChildCount,
        TrieChildCapacity,
        TrieChildChar,
        TrieChildNode,
        CtmfTable,
        Count,
    }

    private static readonly ConcurrentBag<Array?[]> Pool = [];

    [ThreadStatic]
    private static Array?[]? _current;

    /// <summary>Borrow a set of arrays for the encodes on this thread until the result is disposed.</summary>
    /// <returns>Lease.</returns>
    public static Lease Borrow()
    {
        Array?[]? previous = _current;
        _current = Pool.TryTake(out Array?[]? set) ? set : new Array?[(int)Slot.Count];
        return new Lease(previous);
    }

    /// <summary>Drop every pooled array.</summary>
    public static void Trim() => Pool.Clear();

    /// <summary>A zeroed array of exactly <paramref name="length"/> elements, reused within the borrowed set.</summary>
    /// <typeparam name="T">Element type.</typeparam>
    /// <param name="slot">Use site.</param>
    /// <param name="length">Length.</param>
    /// <returns>Array.</returns>
    public static T[] Rent<T>(Slot slot, int length)
    {
        Array?[]? arrays = _current;
        if (arrays is null)
        {
            return new T[length];
        }

        if (arrays[(int)slot] is T[] cached && cached.Length == length)
        {
            Array.Clear(cached);
            return cached;
        }

        T[] fresh = new T[length];
        arrays[(int)slot] = fresh;
        return fresh;
    }

    /// <summary>Returns the borrowed set to the pool.</summary>
    internal readonly struct Lease(Array?[]? previous) : IDisposable
    {
        public void Dispose()
        {
            if (_current is { } set)
            {
                Pool.Add(set);
            }

            _current = previous;
        }
    }
}
