// Ported from LibProsperoPkg (SvenGDK, GPL-3.0-or-later) Util/ProsperoCrc32C.cs at commit 748eabf
// for byte parity with its packages (FPKG plan F4b). See NOTICE.
using MkPFS.Core.Compression.Kraken;
// LibProsperoPkg - A library for building and inspecting PS5 packages.
// Copyright (C) 2026 SvenGDK
//
// CRC-32C (Castagnoli) reducer for the PS5 sce_suppl config/{content-id}/playgo-chunk.crc file.
// The reducer processes the *finalized mount image* in 64KiB blocks: each block's CRC-32C is
// appended as a little-endian uint32, in block order (e.g. 17 dwords over a 0x110000 mount image,
// 90 dwords over a 0x5A0000 mount image).
//
// The variant is the standard ("reflected") CRC-32C used by iSCSI/SCTP/ext4/Btrfs and exposed by
// the SSE4.2 CRC32 instruction: polynomial 0x1EDC6F41 (reflected 0x82F63B78), initial value
// 0xFFFFFFFF, input/output reflected, final XOR 0xFFFFFFFF. Known-answer: CRC32C("123456789")
// == 0xE3069283.

#nullable enable
using System;

namespace MkPFS.Build.FPKG.Prospero;

/// <summary>
/// Standard reflected CRC-32C (Castagnoli, reflected polynomial 0x82F63B78, init/xorout
/// 0xFFFFFFFF). This is the exact reducer used to build
/// <c>playgo-chunk.crc</c>; see the file header for the decoded layout and validation.
/// </summary>
public static class ProsperoCrc32C
{
    /// <summary>The reflected CRC-32C generator polynomial (0x1EDC6F41 reflected).</summary>
    public const uint ReflectedPolynomial = 0x82F63B78u;

    /// <summary>
    /// Continues a running CRC-32C over <paramref name="data"/>. Pass <see cref="uint.MaxValue"/>
    /// as the initial <paramref name="crc"/> for a fresh checksum; the returned value is the
    /// *internal* running register (NOT yet finalized). Finalize with <c>~result</c> or use
    /// <see cref="Compute"/> for one-shot use.
    /// </summary>
    public static uint Update(uint crc, ReadOnlySpan<byte> data) =>
        // The register is the complement of a finalized CRC, so the SSE4.2 / ARMv8 path in Core gives the same value
        // about ten times faster than the table.
        ~MkPFS.Core.Util.Crc32C.Update(~crc, data);

    /// <summary>Computes the finalized standard CRC-32C of <paramref name="data"/>.</summary>
    public static uint Compute(ReadOnlySpan<byte> data) => ~Update(uint.MaxValue, data);

}
