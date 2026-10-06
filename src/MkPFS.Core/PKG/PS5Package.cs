using MkPFS.Core.Crypto;
using MkPFS.Core.PFS;
using MkPFS.Core.PFS.PS5;

namespace MkPFS.Core.PKG;

/// <summary>
/// Read access to a PS5 finalized package: CNT entries, the outer PFS (verified block by block) and the
/// inner file system through <c>naps_pkg_layout.dat</c>.
/// </summary>
public sealed class PS5Package : IDisposable
{
    /// <summary>Name of the inner image in the outer PFS.</summary>
    public const string InnerImageName = "pfs_image.dat";

    private readonly Stream _innerStream;
    private readonly PFSInode _imageInode;

    private PS5Package(PKGFile pkg, PS5OuterImage outer, PFSInode imageInode, Stream innerStream, PS5InnerImage? inner, string? innerError, byte[] naps)
    {
        _imageInode = imageInode;
        Package = pkg;
        Outer = outer;
        _innerStream = innerStream;
        InnerOrNull = inner;
        InnerError = innerError;
        Naps = naps;
    }

    /// <summary>Container.</summary>
    public PKGFile Package { get; }

    /// <summary>Outer PFS.</summary>
    public PS5OuterImage Outer { get; }

    /// <summary>Inner image.</summary>
    /// <exception cref="InvalidDataException">The naps layout does not map the inner image (<see cref="InnerError"/>).</exception>
    public PS5InnerImage Inner => InnerOrNull ?? throw new InvalidDataException(InnerError);

    /// <summary>Inner image, or null when its naps layout does not map it.</summary>
    public PS5InnerImage? InnerOrNull { get; }

    /// <summary>Why the inner image could not be mapped, or null.</summary>
    public string? InnerError { get; }

    /// <summary>Raw <c>naps_pkg_layout.dat</c>.</summary>
    public byte[] Naps { get; }

    /// <summary>
    /// Open a package. Plaintext images need no key; an encrypted debug image opens with the package
    /// passcode (32 zeros by default) or an explicit EKPFS.
    /// </summary>
    /// <param name="path">Package path.</param>
    /// <param name="passcode">Passcode, or null for the all-zero default.</param>
    /// <param name="ekpfs">Explicit 32-byte image key (overrides the passcode).</param>
    /// <returns>Package.</returns>
    public static PS5Package Open(string path, string? passcode = null, byte[]? ekpfs = null)
    {
        PKGFile pkg = PKGFile.Open(path);
        Stream? innerStream = null;
        try
        {
            ekpfs ??= PS5Keys.DeriveEkpfs(pkg.CNT.ContentId, passcode ?? new string('0', PS5Keys.PasscodeLength));
            PS5OuterImage outer = PS5OuterImage.Open(pkg, ekpfs);
            PFSInode imageInode = outer.Files.GetValueOrDefault(InnerImageName)
                ?? throw new InvalidDataException($"outer image has no {InnerImageName}");
            PFSInode napsInode = outer.Files.GetValueOrDefault(NAPSLayout.FileName)
                ?? throw new InvalidDataException($"outer image has no {NAPSLayout.FileName}");
            byte[] naps = outer.ReadFile(napsInode);
            innerStream = outer.OpenFile(imageInode);

            // The CNT and outer image stay readable when the naps layout does not map the inner image, so
            // verify can still report on them.
            PS5InnerImage? inner = null;
            string? innerError = null;
            try
            {
                inner = PS5InnerImage.Open(innerStream, NAPSLayout.Parse(naps));
            }
            catch (InvalidDataException ex)
            {
                innerError = ex.Message;
            }

            return new PS5Package(pkg, outer, imageInode, innerStream, inner, innerError, naps);
        }
        catch
        {
            innerStream?.Dispose();
            pkg.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A new stream over the stored inner image, for one thread's <see cref="PS5InnerImage.DecodeChunk(int, Stream)"/>
    /// calls; package reads are positional, so such streams can be read in parallel.
    /// </summary>
    /// <returns>Stream; the caller disposes it.</returns>
    public Stream OpenInnerStream() => Outer.OpenFile(_imageInode);

    /// <inheritdoc />
    public void Dispose()
    {
        _innerStream.Dispose();
        Package.Dispose();
    }
}
