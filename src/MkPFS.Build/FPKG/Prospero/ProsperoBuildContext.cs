using System.Reflection;
using MkPFS.Core.Crypto;

namespace MkPFS.Build.FPKG.Prospero;

/// <summary>
/// Inputs the ported LibProsperoPkg code takes from the environment: its random outer seed, the RSA padding
/// randomness and the wall clock. They replace the oracle runner's hooks (<c>tools/oracle-fpkg/OracleHooks.cs</c>)
/// at the same sites, so equal inputs give the oracle's golden bytes. Thread-local; set by
/// <see cref="FPKGBuilder"/> around one build.
/// </summary>
internal static class ProsperoBuildContext
{
    [ThreadStatic]
    private static byte[]? _seed;

    /// <summary>16-byte outer PFS seed, also the RSA padding seed.</summary>
    public static byte[] Seed
    {
        get => _seed ?? throw new InvalidOperationException("no package build is in progress");
        set => _seed = value;
    }



    /// <summary>
    /// Test hook: maps each RSA ciphertext the build produces to the one written. The padding is random by
    /// design, so the parity tests swap in PS5PkgTool's ciphertexts to compare the rest byte for byte.
    /// </summary>
    [field: ThreadStatic]
    public static Func<byte[], byte[]>? RSAOutput { get; set; }

    /// <summary>Apply <see cref="RSAOutput"/>.</summary>
    /// <param name="ciphertext">Ciphertext.</param>
    /// <returns>Ciphertext to write.</returns>
    public static byte[] RSA(byte[] ciphertext) => RSAOutput?.Invoke(ciphertext) ?? ciphertext;

}

/// <summary>Public RSA moduli (MkPFS.Build resources; LibProsperoPkg <c>Keys/Data</c>).</summary>
internal static class ProsperoKeys
{
    private static readonly byte[] Passcode = Load("passcode_moduli.bin");
    private static readonly byte[] MountImage = Load("mount_image_modulus.bin");

    /// <summary>Always true: the moduli are embedded.</summary>
    public static bool IsAvailable => true;

    /// <summary>Seven RSA-3072 moduli for CNT entry 0x0010.</summary>
    public static ReadOnlySpan<byte> PasscodeKey => Passcode;

    /// <summary>RSA-3072 modulus for CNT entry 0x0020.</summary>
    public static ReadOnlySpan<byte> MountImageKey => MountImage;

    /// <summary>PKG metadata RSA-3072 modulus (header seal).</summary>
    public static byte[] MetadataModulus { get; } = Load("pkg_meta_modulus.bin");

    private static byte[] Load(string name)
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MkPFS.Build.FPKG." + name)
            ?? throw new InvalidOperationException($"embedded resource {name} is missing");
        using MemoryStream copy = new();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}

/// <summary>
/// The part of LibProsperoPkg's <c>ProsperoPkgSigner</c> the package build uses: the CNT+0x1000 seal.
/// MkPFS ships only the public metadata modulus, so the seal is encrypted with deterministic padding
/// (<see cref="RSAKeyWrap"/>), as the oracle runner's hook does.
/// </summary>
internal static class ProsperoPkgSigner
{
    /// <summary>Seal size (RSA-3072).</summary>
    public const int SignatureSize = 384;

    /// <summary>Encrypt the SHA3-256 header digest with the metadata key.</summary>
    /// <param name="sha3Digest">Digest.</param>
    /// <returns>384 bytes.</returns>
    public static byte[] EncryptHeaderDigest(byte[] sha3Digest) => ProsperoBuildContext.RSA(
        RSAKeyWrap.Encrypt(ProsperoKeys.MetadataModulus, [0x01, 0x00, 0x01], sha3Digest, ProsperoBuildContext.Seed));

    /// <summary>EKPFS of a content id and passcode.</summary>
    /// <param name="contentId">Content id.</param>
    /// <param name="passcode">Passcode.</param>
    /// <returns>32 bytes.</returns>
    public static byte[] ComputeEkpfs(string contentId, string passcode) => PS5Keys.DeriveEkpfs(contentId, passcode);
}
