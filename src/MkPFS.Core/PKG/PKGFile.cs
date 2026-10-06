using System.Text;

namespace MkPFS.Core.PKG;

/// <summary>
/// A PS5 package: a finalized image (<c>\x7FFIH</c> + outer PFS + embedded CNT + SI archive) or a bare
/// metadata container (<c>\x7FCNT</c>). Read-only; owns the stream.
/// </summary>
public sealed class PKGFile : IDisposable
{
    private readonly Stream _stream;

    private PKGFile(Stream stream, FIHHeader? fih, long cntOffset, CNTHeader cnt, IReadOnlyList<CNTEntry> entries)
    {
        _stream = stream;
        FIH = fih;
        CNTOffset = cntOffset;
        CNT = cnt;
        Entries = entries;
    }

    /// <summary>Finalized-image header, or null for a bare CNT.</summary>
    public FIHHeader? FIH { get; }

    /// <summary>Absolute offset of the CNT container.</summary>
    public long CNTOffset { get; }

    /// <summary>Container header.</summary>
    public CNTHeader CNT { get; }

    /// <summary>Entry table in on-disk order.</summary>
    public IReadOnlyList<CNTEntry> Entries { get; }

    /// <summary>Package length.</summary>
    public long Length => _stream.Length;

    /// <summary>Underlying stream (positioned reads only).</summary>
    internal Stream Stream => _stream;

    /// <summary>Whether the file starts with a FIH or CNT magic.</summary>
    /// <param name="path">File path.</param>
    /// <returns>True for a PS5 package.</returns>
    public static bool IsPackage(string path)
    {
        Span<byte> head = stackalloc byte[4];
        using FileStream fs = File.OpenRead(path);
        return fs.Length >= 4 && fs.ReadAtLeast(head, 4, throwOnEndOfStream: false) == 4
            && (FIHHeader.IsFIH(head) || CNTHeader.IsCNT(head));
    }

    /// <summary>Open a package.</summary>
    /// <param name="path">File path.</param>
    /// <returns>Package.</returns>
    public static PKGFile Open(string path) =>
        Open(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess));

    /// <summary>Open a package from a seekable stream (taken over).</summary>
    /// <param name="stream">Package stream.</param>
    /// <returns>Package.</returns>
    /// <exception cref="InvalidDataException">Malformed package.</exception>
    public static PKGFile Open(Stream stream)
    {
        try
        {
            byte[] head = ReadAt(stream, 0, 0x100);
            FIHHeader? fih = null;
            long cntOffset = 0;
            if (FIHHeader.IsFIH(head))
            {
                fih = FIHHeader.Parse(head);
                cntOffset = fih.CNTOffset;
            }

            byte[] header = ReadAt(stream, cntOffset, 0x100);
            CNTHeader cnt = CNTHeader.Parse(header);
            byte[] table = ReadAt(stream, cntOffset + cnt.EntryTableOffset, checked(cnt.EntryCount * CNTHeader.EntrySize));
            List<CNTEntry> raw = [];
            for (int i = 0; i < cnt.EntryCount; i++)
            {
                raw.Add(CNTEntry.Parse(table.AsSpan(i * CNTHeader.EntrySize, CNTHeader.EntrySize)));
            }

            // Names live in entry 0x0200 as NUL-terminated strings addressed by NameOffset.
            CNTEntry? namesEntry = raw.Find(e => e.Id == CNTEntry.EntryNamesId);
            byte[] names = namesEntry is null ? [] : ReadAt(stream, cntOffset + namesEntry.DataOffset, namesEntry.DataSize);
            List<CNTEntry> entries = raw.ConvertAll(e => e with { Name = NameAt(names, e.NameOffset) });
            return new PKGFile(stream, fih, cntOffset, cnt, entries);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Find an entry by id.</summary>
    /// <param name="id">Entry id.</param>
    /// <returns>Entry or null.</returns>
    public CNTEntry? FindEntry(uint id) => Entries.FirstOrDefault(e => e.Id == id);

    /// <summary>Read an entry payload (encrypted entries are returned as stored).</summary>
    /// <param name="entry">Entry.</param>
    /// <returns>Payload bytes.</returns>
    public byte[] ReadEntry(CNTEntry entry) => ReadAt(_stream, CNTOffset + entry.DataOffset, entry.DataSize);

    /// <summary>Read bytes at an absolute offset; safe to call from several threads at once.</summary>
    /// <param name="offset">Offset.</param>
    /// <param name="buffer">Destination, filled completely.</param>
    public void Read(long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset + buffer.Length > _stream.Length)
        {
            throw new InvalidDataException($"read of {buffer.Length} bytes at 0x{offset:X} is outside the package");
        }

        // A file reads by position (no shared cursor), so parallel checks need no lock; other streams take one.
        if (_stream is FileStream file)
        {
            while (!buffer.IsEmpty)
            {
                int n = RandomAccess.Read(file.SafeFileHandle, buffer, offset);
                if (n == 0)
                {
                    throw new EndOfStreamException($"package ends before 0x{offset:X}");
                }

                buffer = buffer[n..];
                offset += n;
            }

            return;
        }

        lock (_stream)
        {
            _stream.Position = offset;
            _stream.ReadExactly(buffer);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _stream.Dispose();

    private static string NameAt(byte[] names, uint offset)
    {
        if (offset == 0 || offset >= names.Length)
        {
            return string.Empty;
        }

        int end = Array.IndexOf(names, (byte)0, (int)offset);
        return Encoding.UTF8.GetString(names, (int)offset, (end < 0 ? names.Length : end) - (int)offset);
    }

    // Sizes come from the package, so anything outside the file (or beyond an array) is a malformed package.
    private static byte[] ReadAt(Stream stream, long offset, long size)
    {
        if (offset < 0 || size < 0 || size > Array.MaxLength || offset > stream.Length - size)
        {
            throw new InvalidDataException($"package truncated: need {size} bytes at 0x{offset:X}");
        }

        byte[] buffer = new byte[size];
        stream.Position = offset;
        stream.ReadExactly(buffer);
        return buffer;
    }
}
