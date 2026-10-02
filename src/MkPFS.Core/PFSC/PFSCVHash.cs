using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MkPFS.Core.PFSC;

/// <summary>Nested image kind recorded in a <c>.vhash</c> header (GC <c>PFS_NESTED_*</c>).</summary>
public enum PFSCNestedType
{
    Unknown = 0,
    PFS = 1,
    Exfat = 2,
}

/// <summary>Result of looking up a sidecar for an image (GC <c>pfs_vhash_mode_t</c>).</summary>
public enum PFSCVHashMode
{
    /// <summary>No <c>.vhash</c> next to the image.</summary>
    Missing,

    /// <summary>File exists but is unreadable, too short, or not a v1 SHA-256 sidecar.</summary>
    Invalid,

    /// <summary>Valid sidecar describing a different image (size, block count or nested name differ).</summary>
    Stale,

    /// <summary>Sidecar matches the image and can be used.</summary>
    Used,
}

/// <summary>Identity of the nested image a sidecar describes.</summary>
/// <param name="LogicalSize">PFSC block-rounded logical size.</param>
/// <param name="NestedSize">Real nested file size (inode logical size).</param>
/// <param name="BlockCount">Number of PFSC blocks.</param>
/// <param name="NestedName">Nested file name, for example <c>PPSA00001.exfat</c>.</param>
/// <param name="NestedType">Nested image kind.</param>
public readonly record struct PFSCVHashIdentity(long LogicalSize, long NestedSize, long BlockCount, string NestedName, PFSCNestedType NestedType)
{
    /// <summary>Nested type from a file name, same rules as GC <c>nested_type_from_name</c>.</summary>
    /// <param name="name">Nested file name.</param>
    /// <returns>Detected type.</returns>
    public static PFSCNestedType TypeFromName(string name)
    {
        if (string.Equals(name, "pfs_image.dat", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".ffpfs", StringComparison.OrdinalIgnoreCase))
        {
            return PFSCNestedType.PFS;
        }

        return name.EndsWith(".exfat", StringComparison.OrdinalIgnoreCase) ? PFSCNestedType.Exfat : PFSCNestedType.Unknown;
    }
}

/// <summary>
/// PS5 Game Compressor validation sidecar <c>&lt;image&gt;.vhash</c> (<c>PFSCVHS1</c> v1): a 4 KiB header and
/// one SHA-256 per PFSC block over the unpadded raw block (the last block stops at the nested size).
/// Game Compressor uses it to validate a mounted image without decompressing it.
/// </summary>
public static class PFSCVHash
{
    /// <summary>Sidecar header size.</summary>
    public const int HeaderSize = 4096;

    /// <summary>Hash size (SHA-256).</summary>
    public const int HashSize = 32;

    private const uint Version = 1;
    private const uint AlgoSha256 = 1;
    private const long BlockSize = 65536;
    private const int NestedNameOffset = 128;
    private const int NestedNameSize = 256;
    private static readonly byte[] Magic = "PFSCVHS1"u8.ToArray();

    /// <summary>Sidecar path for an image (<c>image + ".vhash"</c>).</summary>
    /// <param name="imagePath">Image path.</param>
    /// <returns>Sidecar path.</returns>
    public static string SidecarPath(string imagePath) => imagePath + ".vhash";

    /// <summary>Build the 4 KiB header (GC <c>fill_header</c>).</summary>
    /// <param name="identity">Image identity.</param>
    /// <returns>Header bytes.</returns>
    public static byte[] BuildHeader(PFSCVHashIdentity identity)
    {
        byte[] header = new byte[HeaderSize];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), Version);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), HeaderSize);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(16), BlockSize);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(24), identity.LogicalSize);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(32), identity.NestedSize);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(40), identity.BlockCount);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(48), (uint)identity.NestedType);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(52), AlgoSha256);

        // snprintf semantics: at most 255 bytes plus the terminating NUL.
        byte[] name = Encoding.UTF8.GetBytes(identity.NestedName);
        name.AsSpan(0, Math.Min(name.Length, NestedNameSize - 1)).CopyTo(header.AsSpan(NestedNameOffset));
        return header;
    }

    /// <summary>Write a complete sidecar to a temporary file and move it into place.</summary>
    /// <param name="path">Sidecar path.</param>
    /// <param name="identity">Image identity.</param>
    /// <param name="hashes">Concatenated SHA-256 hashes, <c>BlockCount × 32</c> bytes.</param>
    public static void Write(string path, PFSCVHashIdentity identity, ReadOnlySpan<byte> hashes)
    {
        if (hashes.Length != checked(identity.BlockCount * HashSize))
        {
            throw new ArgumentException($"expected {identity.BlockCount * HashSize} hash bytes, got {hashes.Length}", nameof(hashes));
        }

        string temp = path + ".tmp";
        using (FileStream stream = new(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(BuildHeader(identity));
            stream.Write(hashes);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Check a sidecar against an image identity (GC <c>pfs_vhash_reader_open_for_image</c>).</summary>
    /// <param name="path">Sidecar path.</param>
    /// <param name="identity">Expected identity.</param>
    /// <returns>Lookup mode.</returns>
    public static PFSCVHashMode Probe(string path, PFSCVHashIdentity identity)
    {
        if (!File.Exists(path))
        {
            return PFSCVHashMode.Missing;
        }

        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length < HeaderSize + (identity.BlockCount * HashSize))
            {
                return PFSCVHashMode.Invalid;
            }

            byte[] header = new byte[HeaderSize];
            stream.ReadExactly(header);
            return MatchHeader(header, identity);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PFSCVHashMode.Invalid;
        }
    }

    /// <summary>Read the hash of block <paramref name="index"/>.</summary>
    /// <param name="path">Sidecar path (already probed as <see cref="PFSCVHashMode.Used"/>).</param>
    /// <param name="index">Block index.</param>
    /// <returns>32-byte SHA-256.</returns>
    public static byte[] ReadHash(string path, long index)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        stream.Seek(HeaderSize + (index * HashSize), SeekOrigin.Begin);
        byte[] hash = new byte[HashSize];
        stream.ReadExactly(hash);
        return hash;
    }

    /// <summary>SHA-256 of one raw block, as stored in the sidecar.</summary>
    /// <param name="rawBlock">Unpadded block bytes.</param>
    /// <returns>Hash.</returns>
    public static byte[] HashBlock(ReadOnlySpan<byte> rawBlock) => SHA256.HashData(rawBlock);

    private static PFSCVHashMode MatchHeader(ReadOnlySpan<byte> header, PFSCVHashIdentity identity)
    {
        if (!header[..8].SequenceEqual(Magic) ||
            BinaryPrimitives.ReadUInt32LittleEndian(header[8..]) != Version ||
            BinaryPrimitives.ReadUInt32LittleEndian(header[12..]) != HeaderSize ||
            BinaryPrimitives.ReadInt64LittleEndian(header[16..]) != BlockSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(header[52..]) != AlgoSha256)
        {
            return PFSCVHashMode.Invalid;
        }

        // strncmp over the 256-byte field: compare up to the first NUL on both sides.
        ReadOnlySpan<byte> storedName = header.Slice(NestedNameOffset, NestedNameSize);
        int nul = storedName.IndexOf((byte)0);
        storedName = nul >= 0 ? storedName[..nul] : storedName;
        byte[] expectedName = Encoding.UTF8.GetBytes(identity.NestedName);
        ReadOnlySpan<byte> expected = expectedName.AsSpan(0, Math.Min(expectedName.Length, NestedNameSize));

        bool same = BinaryPrimitives.ReadInt64LittleEndian(header[24..]) == identity.LogicalSize &&
            BinaryPrimitives.ReadInt64LittleEndian(header[32..]) == identity.NestedSize &&
            BinaryPrimitives.ReadInt64LittleEndian(header[40..]) == identity.BlockCount &&
            BinaryPrimitives.ReadUInt32LittleEndian(header[48..]) == (uint)identity.NestedType &&
            storedName.SequenceEqual(expected);
        return same ? PFSCVHashMode.Used : PFSCVHashMode.Stale;
    }
}
