using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;
using MkPFS.Core.Diagnostics;
using MkPFS.Core.PFSC;

namespace MkPFS.Repair;

/// <summary>Writes a <see cref="RepairPlan"/> into an image (GC <c>apply_repair</c> and <c>copy_replace_repair</c>).</summary>
internal static class RepairApplier
{
    private const int CopyBufferSize = 8 << 20;

    /// <summary>Image size after repair: everything up to the payload plus the payload in whole blocks.</summary>
    public static long FinalSize(PFSCImage image, long storedSize) =>
        image.FileStart + (Math.Max(1, (storedSize + PFSCImage.BlockSize - 1) / PFSCImage.BlockSize) * PFSCImage.BlockSize);

    /// <summary>
    /// Rewrite the image in place, walking from the last block so data only moves toward the end.
    /// An interruption leaves the image unusable; callers prefer <see cref="CopyReplace"/> when space allows.
    /// </summary>
    /// <returns>Bytes written to the payload.</returns>
    public static long ApplyInPlace(PFSCImage image, RepairPlan plan, IProgressSink? progress, CancellationToken cancellationToken)
    {
        if (!plan.SupportsInPlace(image))
        {
            throw new InvalidOperationException("in-place repair cannot move blocks toward the start of the image");
        }

        SafeFileHandle handle = image.Stream.SafeFileHandle;
        long finalSize = FinalSize(image, plan.NewStoredSize);
        if (finalSize > RandomAccess.GetLength(handle))
        {
            RandomAccess.SetLength(handle, finalSize);
        }

        byte[] buffer = new byte[CopyBufferSize];
        long moved = 0;
        using RepairPlan.BlockEncoder encoder = new();
        for (long remaining = image.BlockCount; remaining > 0;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long i = remaining - 1;
            long oldStart = image.Offsets[i];
            long newStart = plan.NewOffsets[i];
            if (plan.Marked[i])
            {
                // Blocks after i already sit at or past old_end(i), so block i's old bytes are still intact.
                ReadOnlySpan<byte> bytes = encoder.Encode(image, i, plan.Recompress);
                CheckLength(plan, i, bytes.Length);
                RandomAccess.Write(handle, bytes, image.FileStart + newStart);
                moved += bytes.Length;
                remaining--;
            }
            else
            {
                long delta = newStart - oldStart;
                long runStart = i;
                while (runStart > 0 && !plan.Marked[runStart - 1] &&
                       plan.NewOffsets[runStart - 1] - image.Offsets[runStart - 1] == delta)
                {
                    runStart--;
                }

                long size = image.Offsets[remaining] - image.Offsets[runStart];
                if (delta != 0)
                {
                    CopyBackward(handle, image.FileStart + image.Offsets[runStart], image.FileStart + plan.NewOffsets[runStart], size, buffer);
                    moved += size;
                }

                remaining = runStart;
            }

            progress?.Report("repair", image.BlockCount - remaining, image.BlockCount, moved);
        }

        // Clear whatever old payload bytes remain past the new end, so in-place and copy results are identical.
        ZeroRange(handle, image.FileStart + plan.NewStoredSize, finalSize - (image.FileStart + plan.NewStoredSize));
        RandomAccess.SetLength(handle, finalSize);
        WriteMetadata(handle, image, plan);
        image.Stream.Flush(flushToDisk: true);
        return moved;
    }

    /// <summary>Write the repaired image to <paramref name="tempPath"/>; the caller replaces the original.</summary>
    /// <returns>Bytes written to the payload.</returns>
    public static long CopyReplace(PFSCImage image, RepairPlan plan, string tempPath, IProgressSink? progress, CancellationToken cancellationToken)
    {
        using FileStream temp = new(tempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        SafeFileHandle source = image.Stream.SafeFileHandle;
        SafeFileHandle target = temp.SafeFileHandle;
        byte[] buffer = new byte[CopyBufferSize];
        CopyForward(source, 0, target, 0, image.FileStart + image.HeaderSize, buffer);

        long moved = 0;
        using RepairPlan.BlockEncoder encoder = new();
        for (long block = 0; block < image.BlockCount;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (plan.Marked[block])
            {
                ReadOnlySpan<byte> bytes = encoder.Encode(image, block, plan.Recompress);
                CheckLength(plan, block, bytes.Length);
                RandomAccess.Write(target, bytes, image.FileStart + plan.NewOffsets[block]);
                moved += bytes.Length;
                block++;
            }
            else
            {
                long runStart = block;
                while (block < image.BlockCount && !plan.Marked[block])
                {
                    block++;
                }

                long size = image.Offsets[block] - image.Offsets[runStart];
                CopyForward(source, image.FileStart + image.Offsets[runStart], target, image.FileStart + plan.NewOffsets[runStart], size, buffer);
                moved += size;
            }

            progress?.Report("repair", block, image.BlockCount, moved);
        }

        RandomAccess.SetLength(target, FinalSize(image, plan.NewStoredSize));
        WriteMetadata(target, image, plan);
        temp.Flush(flushToDisk: true);
        return moved;
    }

    /// <summary>
    /// Rewrite the PFSC header and offset table, then the outer header <c>final_ndblock</c> (0x38) and inode 3
    /// <c>size</c>, <c>size_compressed</c> and <c>blocks</c> (GC <c>update_outer_metadata_fd</c>).
    /// </summary>
    private static void WriteMetadata(SafeFileHandle handle, PFSCImage image, RepairPlan plan)
    {
        byte[] header = new byte[image.HeaderSize];
        PFSCHeader.ForBlocks(image.BlockCount).Write(header);
        for (long i = 0; i <= image.BlockCount; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan((int)(PFSCImage.OffsetTableOffset + (i * 8))), plan.NewOffsets[i]);
        }

        RandomAccess.Write(handle, header, image.FileStart);

        long finalBlocks = FinalSize(image, plan.NewStoredSize) / PFSCImage.BlockSize;
        long fileBlocks = finalBlocks - (image.FileStart / PFSCImage.BlockSize);
        byte[] raw = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(raw, finalBlocks);
        RandomAccess.Write(handle, raw, 0x38);
        BinaryPrimitives.WriteInt64LittleEndian(raw, plan.NewStoredSize);
        RandomAccess.Write(handle, raw, PFSCImage.NestedInodeOffset + 0x08);

        // The inode keeps the real nested size; the PFSC header keeps the block-rounded logical size.
        BinaryPrimitives.WriteInt64LittleEndian(raw, image.NestedSize);
        RandomAccess.Write(handle, raw, PFSCImage.NestedInodeOffset + 0x10);
        BinaryPrimitives.WriteUInt32LittleEndian(raw, checked((uint)fileBlocks));
        RandomAccess.Write(handle, raw.AsSpan(0, 4), PFSCImage.NestedInodeOffset + 0x60);
    }

    private static void CheckLength(RepairPlan plan, long block, int length)
    {
        if (length != plan.NewOffsets[block + 1] - plan.NewOffsets[block])
        {
            throw new InvalidOperationException($"block {block} re-encoded to {length} bytes, planned {plan.NewOffsets[block + 1] - plan.NewOffsets[block]}");
        }
    }

    // Copies from the end so an overlapping destination past the source never clobbers unread bytes.
    internal static void CopyBackward(SafeFileHandle handle, long source, long destination, long size, byte[] buffer)
    {
        for (long remaining = size; remaining > 0;)
        {
            int chunk = (int)Math.Min(buffer.Length, remaining);
            long position = remaining - chunk;
            ReadExact(handle, buffer.AsSpan(0, chunk), source + position);
            RandomAccess.Write(handle, buffer.AsSpan(0, chunk), destination + position);
            remaining = position;
        }
    }

    internal static void CopyForward(SafeFileHandle source, long sourceOffset, SafeFileHandle target, long targetOffset, long size, byte[] buffer)
    {
        for (long done = 0; done < size;)
        {
            int chunk = (int)Math.Min(buffer.Length, size - done);
            ReadExact(source, buffer.AsSpan(0, chunk), sourceOffset + done);
            RandomAccess.Write(target, buffer.AsSpan(0, chunk), targetOffset + done);
            done += chunk;
        }
    }

    internal static void ZeroRange(SafeFileHandle handle, long offset, long size)
    {
        byte[] zeros = new byte[(int)Math.Min(size, PFSCImage.BlockSize)];
        for (long done = 0; done < size;)
        {
            int chunk = (int)Math.Min(zeros.Length, size - done);
            RandomAccess.Write(handle, zeros.AsSpan(0, chunk), offset + done);
            done += chunk;
        }
    }

    private static void ReadExact(SafeFileHandle handle, Span<byte> buffer, long offset)
    {
        for (int got = 0; got < buffer.Length;)
        {
            int read = RandomAccess.Read(handle, buffer[got..], offset + got);
            if (read == 0)
            {
                throw new InvalidDataException($"image truncated at offset {offset + got}");
            }

            got += read;
        }
    }
}
