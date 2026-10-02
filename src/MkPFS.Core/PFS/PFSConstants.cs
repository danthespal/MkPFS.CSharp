namespace MkPFS.Core.PFS;

/// <summary>On-disk constants for PFS and PFSC images (port of Python <c>mkpfs/consts.py</c>).</summary>
public static class PFSConstants
{
    // Header identity
    public const long PFSMagic = 20130315;
    public const long PFSVersionPS4 = 1;
    public const long PFSVersionPS5 = 2;
    public const long PFSVersion = PFSVersionPS5;

    // Header mode bits
    public const ushort PFSModeSigned = 0x1;
    public const ushort PFSMode64BitInodes = 0x2;
    public const ushort PFSModeEncrypted = 0x4;
    public const ushort PFSModeCaseInsensitive = 0x8;

    // Inode mode bits
    public const ushort InodeModeOtherRead = 0x001;
    public const ushort InodeModeOtherWrite = 0x002;
    public const ushort InodeModeOtherExec = 0x004;
    public const ushort InodeModeGroupRead = 0x008;
    public const ushort InodeModeGroupWrite = 0x010;
    public const ushort InodeModeGroupExec = 0x020;
    public const ushort InodeModeUserRead = 0x040;
    public const ushort InodeModeUserWrite = 0x080;
    public const ushort InodeModeUserExec = 0x100;
    public const ushort InodeModeDir = 0x4000;
    public const ushort InodeModeFile = 0x8000;
    public const ushort InodeModeAnyWrite = InodeModeOtherWrite | InodeModeGroupWrite | InodeModeUserWrite;

    /// <summary>r-x for owner, group and other (0x16D).</summary>
    public const ushort InodeRxOnly =
        InodeModeOtherRead | InodeModeOtherExec |
        InodeModeGroupRead | InodeModeGroupExec |
        InodeModeUserRead | InodeModeUserExec;

    // Inode flags
    public const uint InodeFlagCompressed = 0x1;
    public const uint InodeFlagReadOnly = 0x10;
    public const uint InodeFlagInternal = 0x20000;
    public const uint InodeFlagSignedExtra = 0x4 | 0x8;

    // Dirent types
    public const int DirentTypeFile = 2;
    public const int DirentTypeDirectory = 3;
    public const int DirentTypeDot = 4;
    public const int DirentTypeDotDot = 5;

    // Inode layouts
    public const int InodeD32Size = 0xA8;
    public const int InodeS32Size = 0x2C8;
    public const int InodeS64Size = 0x310;
    public const int MaxDirectBlocks = 12;
    public const int MaxIndirectBlocks = 5;
    public const int SigSize = 32;
    public const int SigEntryS32Size = SigSize + 4;
    public const int SigEntryS64Size = SigSize + 8;
    public const int SigEntrySize = SigEntryS32Size;

    // Crypto
    public const int XtsSectorSize = 0x1000;
    public const int EkpfsSize = 32;
    public const int SeedSize = 16;
    public const int HeaderDigestOffset = 0x380;
    public const int HeaderDigestSize = 0x5A0;

    // Integer limits used by layout validation
    public const uint UInt32Max = 0xFFFFFFFF;
    public const int Int32Max = 0x7FFFFFFF;

    // PFSC
    public const uint PFSCMagic = 0x43534650;
    public const uint PFSCUnk4 = 0;
    public const uint PFSCUnk8 = 6;
    public const int PFSCLogicalBlockSize = 0x10000;
    public const int PFSCHeaderSize = 0x30;
    public const int PFSCOffsetEntrySize = 0x8;
    public const int PFSCBlockOffsetsOffset = 0x400;
    public const int PFSCInitialDataOffset = 0x10000;
    public const int PFSCInitialOffsetTableCapacity = PFSCInitialDataOffset - PFSCBlockOffsetsOffset;

    private static readonly byte[] ZeroEkpfsBytes = new byte[EkpfsSize];
    private static readonly byte[] ZeroSeedBytes = new byte[SeedSize];

    /// <summary>Default all-zero EKPFS key (32 bytes).</summary>
    public static ReadOnlySpan<byte> ZeroEkpfs => ZeroEkpfsBytes;

    /// <summary>Default all-zero PFS seed (16 bytes).</summary>
    public static ReadOnlySpan<byte> ZeroPFSSeed => ZeroSeedBytes;
}
