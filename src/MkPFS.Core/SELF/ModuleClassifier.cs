namespace MkPFS.Core.SELF;

/// <summary>What an executable module is, for a debug-mode console.</summary>
public enum ModuleKind
{
    /// <summary>Not an ELF or SELF.</summary>
    NotExecutable,

    /// <summary>Plain ELF; <c>pack fpkg</c> fake-signs it.</summary>
    RawELF,

    /// <summary>Fake-authority SELF (0x31 prefix); runs on a debug-mode console.</summary>
    FakeSELF,

    /// <summary>Sony-signed SELF (0x45 prefix); does not run from a debug package.</summary>
    GenuineSELF,

    /// <summary>SELF whose authority is unknown or unreadable.</summary>
    UnknownSELF,
}

/// <summary>Module classification (LibProsperoPkg <c>ProsperoLaunchReadiness.InspectModule</c>).</summary>
public static class ModuleClassifier
{
    /// <summary>Bytes needed to classify any module (header plus segment table and extended info).</summary>
    public const int HeaderWindow = 0x200000;

    private const ulong AuthorityMask = 0xFF00000000000000;

    /// <summary>Classify a module from its leading bytes.</summary>
    /// <param name="data">At least the first <see cref="HeaderWindow"/> bytes (or the whole file).</param>
    /// <returns>Kind and authority id (0 when not a SELF or unreadable).</returns>
    public static (ModuleKind Kind, ulong Authority) Classify(ReadOnlySpan<byte> data)
    {
        if (ELFHeader.IsELF(data))
        {
            return (ModuleKind.RawELF, 0);
        }

        if (!SELFFile.IsSELF(data))
        {
            return (ModuleKind.NotExecutable, 0);
        }

        SELFImage image;
        try
        {
            image = SELFFile.Parse(data);
        }
        catch (InvalidDataException)
        {
            return (ModuleKind.UnknownSELF, 0);
        }

        ulong authority = image.AuthorityId ?? 0;
        return (authority & AuthorityMask) switch
        {
            SELFFile.FakeAuthorityPrefix => (ModuleKind.FakeSELF, authority),
            SELFFile.GenuineAuthorityPrefix => (ModuleKind.GenuineSELF, authority),
            _ => (ModuleKind.UnknownSELF, authority),
        };
    }

    /// <summary>
    /// Whether a source file is fake-signed by <c>pack fpkg</c>: <c>eboot.bin</c> and <c>*.elf</c>,
    /// <c>*.prx</c>, <c>*.sprx</c> that are plain ELF64 files (LibProsperoPkg <c>IsFakeSignCandidate</c>).
    /// </summary>
    /// <param name="relativePath">Path from the package root (<c>/</c> separators).</param>
    /// <returns>True when the name qualifies.</returns>
    public static bool IsModuleName(string relativePath)
    {
        string name = relativePath[(relativePath.LastIndexOf('/') + 1)..];
        return name.Equals("eboot.bin", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".elf", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".prx", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".sprx", StringComparison.OrdinalIgnoreCase);
    }
}
