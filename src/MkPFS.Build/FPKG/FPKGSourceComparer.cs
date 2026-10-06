using System.Security.Cryptography;
using MkPFS.Core.PFS.PS5;
using MkPFS.Core.PKG;

namespace MkPFS.Build.FPKG;

/// <summary>Result of <see cref="FPKGSourceComparer.Compare"/>.</summary>
/// <param name="Errors">Missing or different files and entries.</param>
/// <param name="Warnings">Files only in the package, and source problems.</param>
/// <param name="FilesCompared">Inner files compared.</param>
/// <param name="EntriesCompared">CNT entries compared.</param>
public sealed record FPKGComparison(List<string> Errors, List<string> Warnings, int FilesCompared, int EntriesCompared);

/// <summary>
/// Compares a package with the package view of a source folder (<see cref="FPKGSource"/>): the inner
/// files byte for byte (modules as fake-signed), and the CNT entries the source supplies. Generated DDS
/// entries are only checked for presence (encoders differ) and a different param.json is a warning.
/// </summary>
public static class FPKGSourceComparer
{
    /// <summary>Compare.</summary>
    /// <param name="package">Opened package with a readable inner image.</param>
    /// <param name="source">Prepared source view.</param>
    /// <returns>Differences.</returns>
    public static FPKGComparison Compare(PS5Package package, FPKGSource source)
    {
        List<string> errors = [];
        List<string> warnings = [.. source.Errors.Select(e => "source: " + e)];
        Dictionary<string, PS5InnerFile> files = package.Inner.ReadTree()
            .Where(f => f.Size >= 0)
            .ToDictionary(f => f.Path, StringComparer.Ordinal);

        // Pair the files in source order, hash the pairs in parallel (each worker decoding through its own stream),
        // then report in source order.
        int filesCompared = 0;
        List<FPKGInput> inputs = [.. source.InnerFiles];
        PS5InnerFile?[] pairs = new PS5InnerFile?[inputs.Count];
        for (int i = 0; i < inputs.Count; i++)
        {
            if (files.Remove(inputs[i].Path, out PS5InnerFile? actual))
            {
                pairs[i] = actual;
                filesCompared++;
            }
        }

        bool[] same = new bool[inputs.Count];
        Parallel.For(0, inputs.Count, package.OpenInnerStream, (i, _, stored) =>
        {
            if (pairs[i] is { } actual)
            {
                same[i] = actual.Size == inputs[i].Size && Hash(inputs[i]).AsSpan().SequenceEqual(Hash(package.Inner, actual, stored));
            }

            return stored;
        }, stored => stored.Dispose());

        for (int i = 0; i < inputs.Count; i++)
        {
            FPKGInput expected = inputs[i];
            if (pairs[i] is null)
            {
                errors.Add($"missing in package: {expected.Path}");
            }
            else if (!same[i])
            {
                errors.Add($"content differs: {expected.Path}{(expected.Origin == FPKGInputOrigin.FakeSigned ? " (compared as fake SELF)" : string.Empty)}");
            }
        }

        warnings.AddRange(files.Keys.Order(StringComparer.Ordinal).Select(p => $"only in package: {p}"));

        int entriesCompared = 0;
        foreach (FPKGInput expected in source.Entries)
        {
            CNTEntry? entry = package.Package.Entries.FirstOrDefault(e => e.Name == expected.Path);
            if (entry is null)
            {
                errors.Add($"missing CNT entry: {expected.Path}");
                continue;
            }

            entriesCompared++;
            if (expected.Origin == FPKGInputOrigin.Generated)
            {
                continue;
            }

            if (!SHA256.HashData(package.Package.ReadEntry(entry)).AsSpan().SequenceEqual(Hash(expected)))
            {
                // Publishing Tools rewrites param.json (pubtools, attributePub, padded fields).
                (expected.Path == "param.json" ? warnings : errors).Add($"CNT entry differs: {expected.Path}");
            }
        }

        return new FPKGComparison(errors, warnings, filesCompared, entriesCompared);
    }

    private static byte[] Hash(FPKGInput input)
    {
        using Stream stream = input.Open();
        return SHA256.HashData(stream);
    }

    private static byte[] Hash(PS5InnerImage inner, PS5InnerFile file, Stream stored)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using HashingStream sink = new(hash);
        inner.CopyFile(file, sink, stored);
        return hash.GetHashAndReset();
    }

    private sealed class HashingStream(IncrementalHash hash) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => hash.AppendData(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) => hash.AppendData(buffer);

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
