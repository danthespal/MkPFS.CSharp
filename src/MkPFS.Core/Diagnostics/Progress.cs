namespace MkPFS.Core.Diagnostics;

/// <summary>
/// Receives build/verify progress. Replaces Python <c>Progress</c> plus its GUI listener hook:
/// the CLI renders a terminal bar, the GUI forwards events to its view model.
/// </summary>
public interface IProgressSink
{
    /// <summary>Report progress for a named phase (for example <c>compress</c>).</summary>
    /// <param name="phase">Phase name.</param>
    /// <param name="done">Completed units, already clamped to 0..<paramref name="total"/>.</param>
    /// <param name="total">Total units, at least 1.</param>
    /// <param name="bytesProcessed">Bytes processed so far, or 0 when units are items.</param>
    void Step(string phase, long done, long total, long bytesProcessed);

    /// <summary>Report a status line.</summary>
    /// <param name="message">Text.</param>
    void Status(string message);
}

/// <summary>Helpers shared by every <see cref="IProgressSink"/> caller.</summary>
public static class ProgressExtensions
{
    /// <summary>
    /// Clamp and forward a step, like Python <c>Progress.step</c>: total ≥ 1 and
    /// 0 ≤ done ≤ total, so sinks never see ratios above 1.
    /// </summary>
    /// <param name="sink">Target sink.</param>
    /// <param name="phase">Phase name.</param>
    /// <param name="done">Completed units.</param>
    /// <param name="total">Total units.</param>
    /// <param name="bytesProcessed">Bytes processed, or 0.</param>
    public static void Report(this IProgressSink sink, string phase, long done, long total, long bytesProcessed = 0)
    {
        long safeTotal = Math.Max(total, 1);
        long safeDone = Math.Clamp(done, 0, safeTotal);
        sink.Step(phase, safeDone, safeTotal, bytesProcessed);
    }
}

/// <summary>Progress sink that discards everything.</summary>
public sealed class NullProgress : IProgressSink
{
    /// <summary>Shared instance.</summary>
    public static readonly NullProgress Instance = new();

    private NullProgress()
    {
    }

    /// <inheritdoc />
    public void Step(string phase, long done, long total, long bytesProcessed)
    {
    }

    /// <inheritdoc />
    public void Status(string message)
    {
    }
}
