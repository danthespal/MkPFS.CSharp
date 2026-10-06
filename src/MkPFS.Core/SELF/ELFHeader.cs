using System.Buffers.Binary;

namespace MkPFS.Core.SELF;

/// <summary>Which header fields <see cref="ELFHeader.NormalizeForModule"/> changed.</summary>
/// <param name="Machine">Machine set to x86-64.</param>
/// <param name="OsAbi">OS/ABI set from System V or GNU to FreeBSD.</param>
/// <param name="Type">Type set from none to executable.</param>
public readonly record struct ELFNormalization(bool Machine, bool OsAbi, bool Type)
{
    /// <summary>Any field changed.</summary>
    public bool Changed => Machine || OsAbi || Type;
}

/// <summary>64-bit little-endian ELF header helpers (LibProsperoPkg <c>ProsperoElfHeader</c>).</summary>
public static class ELFHeader
{
    /// <summary>ELF header size.</summary>
    public const int Size = 0x40;

    /// <summary>Program header size.</summary>
    public const int ProgramHeaderSize = 0x38;

    /// <summary>x86-64 machine id.</summary>
    public const ushort MachineX86_64 = 0x3E;

    private const byte OsAbiSystemV = 0;
    private const byte OsAbiGnu = 3;
    private const byte OsAbiFreeBSD = 9;
    private const ushort TypeNone = 0;
    private const ushort TypeExecutable = 2;

    /// <summary>Whether <paramref name="data"/> starts with an ELF header.</summary>
    /// <param name="data">File start.</param>
    /// <returns>True for an ELF.</returns>
    public static bool IsELF(ReadOnlySpan<byte> data) =>
        data.Length >= Size && data[0] == 0x7F && data[1] == (byte)'E' && data[2] == (byte)'L' && data[3] == (byte)'F';

    /// <summary>Whether the ELF is 64-bit little-endian.</summary>
    /// <param name="data">ELF bytes.</param>
    /// <returns>True for ELF64 LE.</returns>
    public static bool Is64LittleEndian(ReadOnlySpan<byte> data) => IsELF(data) && data[4] == 2 && data[5] == 1;

    /// <summary>ELF type (<c>e_type</c>).</summary>
    /// <param name="data">ELF bytes.</param>
    /// <returns>Type.</returns>
    public static ushort Type(ReadOnlySpan<byte> data) => BinaryPrimitives.ReadUInt16LittleEndian(data[0x10..]);

    /// <summary>ELF machine (<c>e_machine</c>).</summary>
    /// <param name="data">ELF bytes.</param>
    /// <returns>Machine.</returns>
    public static ushort Machine(ReadOnlySpan<byte> data) => BinaryPrimitives.ReadUInt16LittleEndian(data[0x12..]);

    /// <summary>
    /// The minimum header edits a module needs before fake-signing: machine to x86-64, OS/ABI System V or
    /// GNU to FreeBSD, type none to executable. Only the 0x40-byte header changes.
    /// </summary>
    /// <param name="elf">ELF64 LE bytes, edited in place.</param>
    /// <returns>Changed fields.</returns>
    /// <exception cref="ArgumentException">Not an ELF64 little-endian file.</exception>
    public static ELFNormalization NormalizeForModule(Span<byte> elf)
    {
        if (!Is64LittleEndian(elf))
        {
            throw new ArgumentException("only 64-bit little-endian ELF modules are supported", nameof(elf));
        }

        bool machine = Machine(elf) != MachineX86_64;
        if (machine)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(elf[0x12..], MachineX86_64);
        }

        bool osAbi = elf[7] is OsAbiSystemV or OsAbiGnu;
        if (osAbi)
        {
            elf[7] = OsAbiFreeBSD;
        }

        bool type = Type(elf) == TypeNone;
        if (type)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(elf[0x10..], TypeExecutable);
        }

        return new ELFNormalization(machine, osAbi, type);
    }
}
