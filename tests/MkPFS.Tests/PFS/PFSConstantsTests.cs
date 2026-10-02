using System.Numerics;
using MkPFS.Core.PFS;

namespace MkPFS.Tests.PFS;

/// <summary>Port of Python <c>tests/mkpfs/test_consts.py</c>.</summary>
public sealed class PFSConstantsTests
{
    [Fact]
    public void Header_identity_matches_reference_values()
    {
        Assert.Equal(20130315, PFSConstants.PFSMagic);
        Assert.Equal(1, PFSConstants.PFSVersionPS4);
        Assert.Equal(2, PFSConstants.PFSVersionPS5);
        Assert.Equal(PFSConstants.PFSVersionPS5, PFSConstants.PFSVersion);
        Assert.True(PFSConstants.PFSVersionPS5 > PFSConstants.PFSVersionPS4);
    }

    [Theory]
    [InlineData(PFSConstants.PFSModeSigned, 0x1)]
    [InlineData(PFSConstants.PFSMode64BitInodes, 0x2)]
    [InlineData(PFSConstants.PFSModeEncrypted, 0x4)]
    [InlineData(PFSConstants.PFSModeCaseInsensitive, 0x8)]
    public void Mode_flags_have_reference_values_and_are_single_bits(ushort flag, int expected)
    {
        Assert.Equal(expected, flag);
        Assert.True(BitOperations.IsPow2(flag));
    }

    [Theory]
    [InlineData(PFSConstants.InodeModeOtherRead, 0x001)]
    [InlineData(PFSConstants.InodeModeOtherWrite, 0x002)]
    [InlineData(PFSConstants.InodeModeOtherExec, 0x004)]
    [InlineData(PFSConstants.InodeModeGroupRead, 0x008)]
    [InlineData(PFSConstants.InodeModeGroupWrite, 0x010)]
    [InlineData(PFSConstants.InodeModeGroupExec, 0x020)]
    [InlineData(PFSConstants.InodeModeUserRead, 0x040)]
    [InlineData(PFSConstants.InodeModeUserWrite, 0x080)]
    [InlineData(PFSConstants.InodeModeUserExec, 0x100)]
    [InlineData(PFSConstants.InodeModeDir, 0x4000)]
    [InlineData(PFSConstants.InodeModeFile, 0x8000)]
    public void Inode_mode_bits_have_reference_values(ushort flag, int expected) => Assert.Equal(expected, flag);

    [Fact]
    public void Composite_inode_modes_are_correct()
    {
        Assert.Equal(0x092, PFSConstants.InodeModeAnyWrite);
        Assert.Equal(0x16D, PFSConstants.InodeRxOnly);
        Assert.Equal(0, PFSConstants.InodeRxOnly & PFSConstants.InodeModeAnyWrite);
        Assert.Equal(0x1FF, PFSConstants.InodeRxOnly | PFSConstants.InodeModeAnyWrite);
        Assert.NotEqual(PFSConstants.InodeModeDir, PFSConstants.InodeModeFile);
    }

    [Fact]
    public void Inode_flags_have_reference_values_and_do_not_overlap()
    {
        Assert.Equal(0x1u, PFSConstants.InodeFlagCompressed);
        Assert.Equal(0x10u, PFSConstants.InodeFlagReadOnly);
        Assert.Equal(0x20000u, PFSConstants.InodeFlagInternal);
        Assert.Equal(0xCu, PFSConstants.InodeFlagSignedExtra);
        uint[] flags = [PFSConstants.InodeFlagCompressed, PFSConstants.InodeFlagReadOnly, PFSConstants.InodeFlagInternal, PFSConstants.InodeFlagSignedExtra];
        for (int i = 0; i < flags.Length; i++)
        {
            for (int j = i + 1; j < flags.Length; j++)
            {
                Assert.Equal(0u, flags[i] & flags[j]);
            }
        }
    }

    [Fact]
    public void Dirent_types_are_sequential_from_2()
    {
        Assert.Equal(2, PFSConstants.DirentTypeFile);
        Assert.Equal(3, PFSConstants.DirentTypeDirectory);
        Assert.Equal(4, PFSConstants.DirentTypeDot);
        Assert.Equal(5, PFSConstants.DirentTypeDotDot);
    }

    [Fact]
    public void Inode_layout_sizes_match_reference()
    {
        Assert.Equal(168, PFSConstants.InodeD32Size);
        Assert.Equal(712, PFSConstants.InodeS32Size);
        Assert.Equal(784, PFSConstants.InodeS64Size);
        Assert.Equal(12, PFSConstants.MaxDirectBlocks);
        Assert.Equal(5, PFSConstants.MaxIndirectBlocks);
        Assert.Equal(32, PFSConstants.SigSize);
        Assert.Equal(36, PFSConstants.SigEntryS32Size);
        Assert.Equal(40, PFSConstants.SigEntryS64Size);
        Assert.Equal(PFSConstants.SigEntryS32Size, PFSConstants.SigEntrySize);

        // D32: fixed header (100 bytes) + 12 direct + 5 indirect i32 pointers.
        Assert.Equal(100 + (17 * 4), PFSConstants.InodeD32Size);
    }

    [Fact]
    public void Crypto_and_limit_constants_match_reference()
    {
        Assert.Equal(0x1000, PFSConstants.XtsSectorSize);
        Assert.Equal(0xFFFFFFFFu, PFSConstants.UInt32Max);
        Assert.Equal(int.MaxValue, PFSConstants.Int32Max);
        Assert.Equal(0x380, PFSConstants.HeaderDigestOffset);
        Assert.Equal(0x5A0, PFSConstants.HeaderDigestSize);
        Assert.Equal(32, PFSConstants.ZeroEkpfs.Length);
        Assert.Equal(16, PFSConstants.ZeroPFSSeed.Length);
        Assert.True(PFSConstants.ZeroEkpfs.IndexOfAnyExcept((byte)0) < 0);
        Assert.True(PFSConstants.ZeroPFSSeed.IndexOfAnyExcept((byte)0) < 0);
    }

    [Fact]
    public void PFSC_constants_match_reference()
    {
        Assert.Equal(0x43534650u, PFSConstants.PFSCMagic);
        Assert.Equal("PFSC"u8.ToArray(), BitConverter.GetBytes(PFSConstants.PFSCMagic));
        Assert.Equal(0u, PFSConstants.PFSCUnk4);
        Assert.Equal(6u, PFSConstants.PFSCUnk8);
        Assert.Equal(0x10000, PFSConstants.PFSCLogicalBlockSize);
        Assert.Equal(0x30, PFSConstants.PFSCHeaderSize);
        Assert.Equal(8, PFSConstants.PFSCOffsetEntrySize);
        Assert.Equal(0x400, PFSConstants.PFSCBlockOffsetsOffset);
        Assert.Equal(0x10000, PFSConstants.PFSCInitialDataOffset);
        Assert.Equal(0xFC00, PFSConstants.PFSCInitialOffsetTableCapacity);
        Assert.True(PFSConstants.PFSCHeaderSize <= PFSConstants.PFSCBlockOffsetsOffset);
    }
}
