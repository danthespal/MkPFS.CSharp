using System.Globalization;
using MkPFS.Core.Diagnostics;
using MkPFS.Core.Util;

namespace MkPFS.Cli.Output;

/// <summary>
/// Single-line terminal progress bar on stderr, same format as Python <c>Progress.step</c>:
/// <c>[####----] 50% compress @ 1.95 MB/s ETA 3s</c>. The line is redrawn when the percentage changes, at most
/// every half second otherwise, and once when a phase completes; builders report on every write, which would
/// otherwise flush the terminal hundreds of thousands of times.
/// </summary>
public sealed class TerminalProgress : IProgressSink
{
    private static readonly TimeSpan RedrawInterval = TimeSpan.FromMilliseconds(500);

    private readonly TextWriter _writer;
    private readonly TimeProvider _time;
    private readonly int _width;
    private readonly Dictionary<string, PhaseState> _phases = new(StringComparer.Ordinal);
    private readonly HashSet<string> _finished = new(StringComparer.Ordinal);

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

        // A completed phase stays complete until it starts over below 100%.
        if (_finished.Contains(phase))
        {
            if (done >= total)
            {
                return;
            }

            _finished.Remove(phase);
        }

        bool first = !_phases.TryGetValue(phase, out PhaseState? state);
        if (state is null)
        {
            state = new PhaseState(_time.GetTimestamp());
            _phases[phase] = state;
        }

        if (bytesProcessed > 0)
        {
            state.Bytes = bytesProcessed;
        }

        double ratio = (double)done / total;
        int pct = (int)(ratio * 100);
        long now = _time.GetTimestamp();
        if (!first && done < total && pct == state.LastPercent && _time.GetElapsedTime(state.LastDrawn, now) < RedrawInterval)
        {
            return;
        }

        state.LastPercent = pct;
        state.LastDrawn = now;
        int fill = (int)(_width * ratio);
        string bar = new string('#', fill) + new string('-', _width - fill);

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
            _finished.Add(phase);
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

        public long LastDrawn { get; set; } = startTimestamp;

        public int LastPercent { get; set; } = -1;

        public long Bytes { get; set; }

        public int LastLength { get; set; }
    }
}
