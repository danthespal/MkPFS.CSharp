using System.Buffers.Binary;
using System.Text;

namespace MkPFS.Core.Exfat;

/// <summary>Volume geometry from the main boot sector (Python <c>ExfatGeometry</c>).</summary>
/// <param name="BytesPerSector">Sector size.</param>
/// <param name="SectorsPerCluster">Cluster size in sectors.</param>
/// <param name="FatOffsetSectors">First FAT offset.</param>
/// <param name="ClusterHeapOffsetSectors">Cluster heap offset.</param>
/// <param name="ClusterCount">Clusters in the heap.</param>
/// <param name="RootDirCluster">Root directory first cluster.</param>
/// <param name="VolumeSerial">Volume serial number.</param>
public readonly record struct ExfatGeometry(
    int BytesPerSector,
    long SectorsPerCluster,
    uint FatOffsetSectors,
    uint ClusterHeapOffsetSectors,
    uint ClusterCount,
    uint RootDirCluster,
    uint VolumeSerial)
{
    /// <summary>Cluster size in bytes.</summary>
    public long ClusterSize => BytesPerSector * SectorsPerCluster;
}

/// <summary>File or directory entry (Python <c>ExfatEntry</c>).</summary>
public sealed class ExfatEntry
{
    /// <summary>Base name.</summary>
    public required string Name { get; init; }

    /// <summary>POSIX path relative to the volume root.</summary>
    public required string RelPath { get; init; }

    /// <summary>Directory entry.</summary>
    public required bool IsDir { get; init; }

    /// <summary>First data cluster (0 when empty).</summary>
    public required uint FirstCluster { get; init; }

    /// <summary>Data length in bytes.</summary>
    public required ulong Length { get; init; }

    /// <summary>Contiguous allocation (no FAT chain).</summary>
    public required bool NoFatChain { get; init; }

    /// <summary>Children of a directory.</summary>
    public List<ExfatEntry> Children { get; init; } = [];
}

/// <summary>Read-only exFAT parser over a seekable stream (port of Python <c>mkpfs/exfat.py</c>).</summary>
public sealed class ExfatReader
{
    /// <summary>exFAT file system name at boot sector offset 3.</summary>
    public static readonly byte[] Signature = "EXFAT   "u8.ToArray();

    private const byte EndOfDirectory = 0x00;
    private const byte EntryFile = 0x85;
    private const byte EntryStreamExtension = 0xC0;
    private const byte EntryFileName = 0xC1;
    private const ushort AttrDirectory = 0x10;
    private const byte FlagNoFatChain = 0x02;
    private const uint FatEndOfChain = 0xFFFFFFFF;
    private readonly Stream _stream;

    /// <summary>Parse the boot sector of <paramref name="stream"/> (not owned).</summary>
    /// <param name="stream">Seekable volume stream.</param>
    public ExfatReader(Stream stream)
    {
        _stream = stream;
        Geometry = ParseBootSector();
    }

    /// <summary>Volume geometry.</summary>
    public ExfatGeometry Geometry { get; }

    /// <summary>Directory tree under the volume root, in on-disk order.</summary>
    /// <returns>Root entries.</returns>
    public List<ExfatEntry> RootEntries() => WalkDirectory(Geometry.RootDirCluster, noFatChain: false, length: 0, relDir: string.Empty);

    /// <summary>Every file (not directory), depth first, sorted by lowercase path per level (Python <c>iter_files</c>).</summary>
    /// <returns>File entries.</returns>
    public IEnumerable<ExfatEntry> EnumerateFiles()
    {
        IEnumerable<ExfatEntry> Walk(List<ExfatEntry> nodes)
        {
            foreach (ExfatEntry node in SortByLowerPath(nodes))
            {
                if (node.IsDir)
                {
                    foreach (ExfatEntry child in Walk(node.Children))
                    {
                        yield return child;
                    }
                }
                else
                {
                    yield return node;
                }
            }
        }

        return Walk(RootEntries());
    }

    /// <summary>Sort entries by lowercase relative path (stable).</summary>
    /// <param name="nodes">Entries.</param>
    /// <returns>Sorted entries.</returns>
    public static IEnumerable<ExfatEntry> SortByLowerPath(IEnumerable<ExfatEntry> nodes) =>
        nodes.OrderBy(n => n.RelPath.ToLowerInvariant(), StringComparer.Ordinal);

    /// <summary>Copy a file's bytes to <paramref name="destination"/> (Python <c>read_file</c>).</summary>
    /// <param name="entry">File entry.</param>
    /// <param name="destination">Output stream.</param>
    /// <param name="progress">Called with each chunk's length.</param>
    /// <returns>Bytes copied.</returns>
    public long CopyFile(ExfatEntry entry, Stream destination, Action<int>? progress = null)
    {
        long copied = 0;
        foreach (ReadOnlyMemory<byte> chunk in ReadFile(entry))
        {
            destination.Write(chunk.Span);
            copied += chunk.Length;
            progress?.Invoke(chunk.Length);
        }

        return copied;
    }

    /// <summary>Yield a file's bytes cluster by cluster; each chunk is valid until the next iteration.</summary>
    /// <param name="entry">File entry.</param>
    /// <returns>Chunks whose total length equals <see cref="ExfatEntry.Length"/>.</returns>
    /// <exception cref="InvalidDataException">The entry is a directory or its data is truncated.</exception>
    public IEnumerable<ReadOnlyMemory<byte>> ReadFile(ExfatEntry entry)
    {
        if (entry.IsDir)
        {
            throw new InvalidDataException($"not a file: {entry.RelPath}");
        }

        long clusterSize = Geometry.ClusterSize;
        if (entry.Length > (ulong)long.MaxValue)
        {
            throw new InvalidDataException($"file '{entry.RelPath}' has an invalid length {entry.Length}");
        }

        long remaining = (long)entry.Length;
        byte[] buffer = new byte[(int)Math.Min(clusterSize, Math.Max(remaining, 1))];
        foreach (uint cluster in Clusters(entry.FirstCluster, entry.NoFatChain, entry.Length))
        {
            if (remaining <= 0)
            {
                break;
            }

            int want = (int)Math.Min(clusterSize, remaining);
            SeekCluster(cluster);
            int got = _stream.ReadAtLeast(buffer.AsSpan(0, want), want, throwOnEndOfStream: false);
            remaining -= got;
            yield return buffer.AsMemory(0, got);
            if (got < want)
            {
                break;
            }
        }

        if (remaining > 0)
        {
            throw new InvalidDataException($"file '{entry.RelPath}' truncated: {remaining} bytes short");
        }
    }

    private ExfatGeometry ParseBootSector()
    {
        byte[] vbr = new byte[512];
        _stream.Seek(0, SeekOrigin.Begin);
        if (_stream.ReadAtLeast(vbr, vbr.Length, throwOnEndOfStream: false) < vbr.Length)
        {
            throw new InvalidDataException("source too small for an exFAT boot sector");
        }

        if (!vbr.AsSpan(3, 8).SequenceEqual(Signature))
        {
            throw new InvalidDataException("missing exFAT file system signature");
        }

        if (BinaryPrimitives.ReadUInt16LittleEndian(vbr.AsSpan(0x1FE)) != 0xAA55)
        {
            throw new InvalidDataException("missing boot signature 0xAA55");
        }

        int sectorShift = vbr[0x6C];
        int clusterShift = vbr[0x6D];
        if (sectorShift is < 9 or > 12)
        {
            throw new InvalidDataException($"unsupported bytes-per-sector shift {sectorShift}");
        }

        // exFAT limits clusters to 32 MiB (shift sum <= 25).
        if (sectorShift + clusterShift > 25)
        {
            throw new InvalidDataException($"unsupported sectors-per-cluster shift {clusterShift}");
        }

        return new ExfatGeometry(
            1 << sectorShift,
            1L << clusterShift,
            BinaryPrimitives.ReadUInt32LittleEndian(vbr.AsSpan(0x50)),
            BinaryPrimitives.ReadUInt32LittleEndian(vbr.AsSpan(0x58)),
            BinaryPrimitives.ReadUInt32LittleEndian(vbr.AsSpan(0x5C)),
            BinaryPrimitives.ReadUInt32LittleEndian(vbr.AsSpan(0x60)),
            BinaryPrimitives.ReadUInt32LittleEndian(vbr.AsSpan(0x64)));
    }

    private long ClusterOffset(uint cluster) =>
        (Geometry.ClusterHeapOffsetSectors + ((long)cluster - 2) * Geometry.SectorsPerCluster) * Geometry.BytesPerSector;

    /// <summary>Seek to a cluster, rejecting clusters that lie outside the volume data.</summary>
    private void SeekCluster(uint cluster)
    {
        long offset = ClusterOffset(cluster);
        if (offset < 0 || offset >= _stream.Length)
        {
            throw new InvalidDataException($"cluster {cluster} is outside the volume");
        }

        _stream.Seek(offset, SeekOrigin.Begin);
    }

    private uint FatNext(uint cluster)
    {
        byte[] entry = new byte[4];
        long offset = (Geometry.FatOffsetSectors * (long)Geometry.BytesPerSector) + (cluster * 4L);
        if (offset + 4 > _stream.Length)
        {
            throw new InvalidDataException($"FAT entry for cluster {cluster} is outside the volume");
        }

        _stream.Seek(offset, SeekOrigin.Begin);
        if (_stream.ReadAtLeast(entry, 4, throwOnEndOfStream: false) < 4)
        {
            throw new InvalidDataException($"FAT entry for cluster {cluster} is outside the volume");
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(entry);
    }

    /// <summary>Clusters of an allocation; length 0 with a FAT chain means "follow to end of chain".</summary>
    private IEnumerable<uint> Clusters(uint first, bool noFatChain, ulong length)
    {
        if (first < 2)
        {
            yield break;
        }

        long clusterSize = Geometry.ClusterSize;
        if (noFatChain)
        {
            if (length == 0)
            {
                yield break;
            }

            ulong count = (length / (ulong)clusterSize) + (length % (ulong)clusterSize != 0 ? 1UL : 0UL);

            // A contiguous run must fit inside the cluster heap (Python does not check and would scan forever).
            if (count > Geometry.ClusterCount || first - 2UL + count > Geometry.ClusterCount)
            {
                throw new InvalidDataException($"allocation at cluster {first} ({length} bytes) exceeds the volume");
            }

            for (ulong i = 0; i < count; i++)
            {
                yield return (uint)(first + i);
            }

            yield break;
        }

        long limit = length > 0 ? (long)Math.Min((length / (ulong)clusterSize) + 1, long.MaxValue) : -1;
        if (length > 0 && length % (ulong)clusterSize == 0)
        {
            limit--;
        }
        uint cluster = first;
        long seen = 0;
        while (cluster >= 2 && cluster < FatEndOfChain)
        {
            yield return cluster;
            seen++;
            if (limit > 0 && seen >= limit)
            {
                yield break;
            }

            if (seen > Geometry.ClusterCount + 2L)
            {
                throw new InvalidDataException("cluster chain exceeds volume size (loop?)");
            }

            cluster = FatNext(cluster);
        }
    }

    private List<byte[]> DirectoryEntries(uint first, bool noFatChain, ulong length)
    {
        List<byte[]> entries = [];
        byte[] data = new byte[Geometry.ClusterSize];
        foreach (uint cluster in Clusters(first, noFatChain, length))
        {
            SeekCluster(cluster);
            int got = _stream.ReadAtLeast(data, data.Length, throwOnEndOfStream: false);
            for (int offset = 0; offset < got; offset += 32)
            {
                if (offset + 32 > got || data[offset] == EndOfDirectory)
                {
                    return entries;
                }

                entries.Add(data.AsSpan(offset, 32).ToArray());
            }
        }

        return entries;
    }

    private List<ExfatEntry> WalkDirectory(uint first, bool noFatChain, ulong length, string relDir)
    {
        List<ExfatEntry> result = [];
        List<byte[]> pending = DirectoryEntries(first, noFatChain, length);
        int index = 0;
        while (index < pending.Count)
        {
            byte[] entry = pending[index];
            if (entry[0] != EntryFile)
            {
                index++;
                continue;
            }

            // A File entry is followed by SecondaryCount entries: one Stream Extension, then File Name entries.
            int secondaryCount = entry[1];
            ushort attributes = BinaryPrimitives.ReadUInt16LittleEndian(entry.AsSpan(0x04));
            List<byte[]> secondaries = [.. pending.Skip(index + 1).Take(secondaryCount)];
            index += 1 + secondaryCount;
            if (secondaries.Count < secondaryCount || secondaries.Count == 0 || secondaries[0][0] != EntryStreamExtension)
            {
                continue;
            }

            byte[] stream = secondaries[0];
            bool childNoFat = (stream[1] & FlagNoFatChain) != 0;
            int nameLength = stream[3];
            uint childCluster = BinaryPrimitives.ReadUInt32LittleEndian(stream.AsSpan(0x14));
            ulong dataLength = BinaryPrimitives.ReadUInt64LittleEndian(stream.AsSpan(0x18));

            using MemoryStream nameUnits = new();
            foreach (byte[] secondary in secondaries.Skip(1))
            {
                if (secondary[0] == EntryFileName)
                {
                    nameUnits.Write(secondary, 2, 30);
                }
            }

            string decoded = Encoding.Unicode.GetString(nameUnits.GetBuffer(), 0, (int)nameUnits.Length);
            string name = decoded.Length > nameLength ? decoded[..nameLength] : decoded;
            bool isDir = (attributes & AttrDirectory) != 0;
            string relPath = relDir.Length > 0 ? $"{relDir}/{name}" : name;
            result.Add(new ExfatEntry
            {
                Name = name,
                RelPath = relPath,
                IsDir = isDir,
                FirstCluster = childCluster,
                Length = dataLength,
                NoFatChain = childNoFat,
                Children = isDir ? WalkDirectory(childCluster, childNoFat, dataLength, relPath) : [],
            });
        }

        return result;
    }
}
