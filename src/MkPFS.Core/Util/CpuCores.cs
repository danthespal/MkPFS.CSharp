namespace MkPFS.Core.Util;

/// <summary>
/// How many logical processors a command may use: every one but <see cref="Reserved"/>, which stay free so Windows
/// and other programs keep responding. Auto uses that many, and larger requests are capped at it. The count never
/// changes an image, package or pack set, only how fast it is built.
/// </summary>
public static class CpuCores
{
    /// <summary>Logical processors left for the system and other programs.</summary>
    public const int Reserved = 2;

    /// <summary>Auto and the most any command uses: logical processors minus <see cref="Reserved"/>, at least 1.</summary>
    public static int Max => Math.Max(1, Environment.ProcessorCount - Reserved);

    /// <summary>The count to use for a request.</summary>
    /// <param name="requested">0 (or less) for Auto, otherwise the wanted count.</param>
    /// <returns><see cref="Max"/> for Auto, otherwise <paramref name="requested"/> capped at <see cref="Max"/>.</returns>
    public static int Resolve(long requested) => requested <= 0 ? Max : (int)Math.Min(requested, Max);

    /// <summary>Parallel loop options limited to <see cref="Max"/>, for work that has no core count option.</summary>
    public static ParallelOptions ParallelOptions => new() { MaxDegreeOfParallelism = Max };
}
