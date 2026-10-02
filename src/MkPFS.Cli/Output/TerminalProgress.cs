using System.Globalization;
using MkPFS.Core.Diagnostics;
using MkPFS.Core.Util;

namespace MkPFS.Cli.Output;

/// <summary>
/// Single-line terminal progress bar on stderr, same format as Python <c>Progress.step</c>:
/// <c>[####----] 50% compress @ 1.95 MB/s ETA 3s</c>.
/// </summary>
public sealed class TerminalProgress : IProgressSink
{
    private readonly TextWriter _writer;
    private readonly TimeProvider _time;
    private readonly int _width;
    private readonly Dictionary<string, PhaseState> _phases = new(StringComparer.Ordinal);

    /// <summary>Create a progress bar.</summary>
    /// <param name="writer">Target writer, normally stderr.</param>
    /// <param name="time">Clock used for speed and ETA.</param>
    /// <param name="width">Bar width in characters.</param>
    public TerminalProgress(TextWriter writer, TimeProvider? time = null, int width = 32)
    {
        _writer = writer;
        _time = time ?? TimeProvider.System;
        _width = width;
    }

    /// <inheritdoc />
    public void Step(string phase, long done, long total, long bytesProcessed)
    {
        total = Math.Max(total, 1);
        done = Math.Clamp(done, 0, total);

        if (!_phases.TryGetValue(phase, out PhaseState? state))
        {
            state = new PhaseState(_time.GetTimestamp());
            _phases[phase] = state;
        }

        if (bytesProcessed > 0)
        {
            state.Bytes = bytesProcessed;
        }

        double ratio = (double)done / total;
        int fill = (int)(_width * ratio);
        string bar = new string('#', fill) + new string('-', _width - fill);
        int pct = (int)(ratio * 100);

        double elapsed = _time.GetElapsedTime(state.StartTimestamp).TotalSeconds;
        string speedText = string.Empty;
        string etaText = string.Empty;
        if (elapsed > 0.1 && done > 0)
        {
            double speed;
            double etaSeconds = 0;
            if (bytesProcessed > 0)
            {
                speed = state.Bytes / elapsed;
                speedText = $" @ {Sizes.HumanReadable((long)speed)}/s";
                if (done < total && speed > 0)
                {
                    double remainingBytes = ((double)state.Bytes / done) * (total - done);
                    etaSeconds = remainingBytes / speed;
                }
            }
            else
            {
                speed = done / elapsed;
                speedText = string.Create(CultureInfo.InvariantCulture, $" {speed:F1} items/s");
                if (done < total && speed > 0)
                {
                    etaSeconds = (total - done) / speed;
                }
            }

            if (done < total)
            {
                etaText = etaSeconds < 3600
                    ? string.Create(CultureInfo.InvariantCulture, $" ETA {(long)etaSeconds}s")
                    : string.Create(CultureInfo.InvariantCulture, $" ETA {etaSeconds / 60:F1}m");
            }
        }

        string line = string.Create(CultureInfo.InvariantCulture, $"[{bar}] {pct,3}% {phase}{speedText}{etaText}");
        int padding = Math.Max(0, state.LastLength - line.Length);
        _writer.Write("\r" + line + new string(' ', padding));
        state.LastLength = line.Length;
        if (done >= total)
        {
            _writer.Write("\n");
            _phases.Remove(phase);
        }

        _writer.Flush();
    }

    /// <inheritdoc />
    public void Status(string message)
    {
        _writer.Write(message + "\n");
        _writer.Flush();
    }

    private sealed class PhaseState(long startTimestamp)
    {
        public long StartTimestamp { get; } = startTimestamp;

        public long Bytes { get; set; }

        public int LastLength { get; set; }
    }
}
