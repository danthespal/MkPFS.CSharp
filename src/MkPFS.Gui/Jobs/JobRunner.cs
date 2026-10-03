using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MkPFS.Cli;
using MkPFS.Cli.Output;
using MkPFS.Core.Diagnostics;
using MkPFS.Gui.Localization;

namespace MkPFS.Gui.Jobs;

/// <summary>How a job ended.</summary>
public enum JobOutcome
{
    /// <summary>Exit code 0.</summary>
    Succeeded,

    /// <summary>Non-zero exit code or an unexpected exception.</summary>
    Failed,

    /// <summary>Stopped by <see cref="JobRunner.Cancel"/>.</summary>
    Cancelled,
}

/// <summary>Log line color (Python GUI log tags).</summary>
public enum LogTone
{
    /// <summary>Plain output.</summary>
    Normal,

    /// <summary>Echoed command line.</summary>
    Muted,

    /// <summary>Success line.</summary>
    Success,

    /// <summary>Warning line.</summary>
    Warning,

    /// <summary>Error line.</summary>
    Error,
}

/// <summary>One line of the output log.</summary>
/// <param name="Text">Text without the line break.</param>
/// <param name="Tone">Color.</param>
public sealed record LogLine(string Text, LogTone Tone);

/// <summary>What a job body gets: output writers, a progress sink, and the cancellation token.</summary>
public sealed class JobContext
{
    internal JobContext(TextWriter output, IProgressSink progress, CancellationToken token)
    {
        Out = output;
        Progress = progress;
        Token = token;
    }

    /// <summary>Line-buffered writer into the log (stdout and stderr share it, as in the Python GUI).</summary>
    public TextWriter Out { get; }

    /// <summary>Progress sink; throws <see cref="OperationCanceledException"/> once the job is cancelled.</summary>
    public IProgressSink Progress { get; }

    /// <summary>Cancellation token.</summary>
    public CancellationToken Token { get; }

    /// <summary>
    /// CLI context over this job (Python <c>_run_mkpfs</c>): plain-text icons, progress into the GUI sink,
    /// and "y" for every overwrite prompt.
    /// </summary>
    /// <returns>Context for <see cref="MkPFSCli.Run(string[], CliContext)"/>.</returns>
    public CliContext CreateCliContext() =>
        new(Out, Out, useColor: false, utf8: false, progress: true, stdin: new YesReader(Out), progressSink: Progress);

    // Answers prompts with "y" and echoes it, so the prompt line reads like a terminal session.
    private sealed class YesReader(TextWriter echo) : TextReader
    {
        public override string ReadLine()
        {
            echo.WriteLine("y");
            return "y";
        }
    }
}

/// <summary>
/// Runs one operation at a time on a background thread and mirrors its output and progress into
/// bindable state (Python <c>BasePanel</c> worker, log queue, and progress listener).
/// </summary>
public sealed partial class JobRunner : ObservableObject
{
    private readonly Action<Action> _post;
    private readonly Lock _gate = new();
    private readonly List<LogLine> _pendingLines = [];
    private CancellationTokenSource? _cancel;
    private TaskCompletionSource? _idle;
    private bool _flushQueued;
    private bool _progressDirty;
    private string _phase = string.Empty;
    private string _label = string.Empty;
    private long _done;
    private long _total;

    /// <summary>Create a runner that applies updates on the UI thread.</summary>
    public JobRunner()
        : this(action => Dispatcher.UIThread.Post(action))
    {
    }

    /// <summary>Create a runner with a custom UI scheduler (tests pass a synchronous one).</summary>
    /// <param name="post">Schedules an action on the UI thread.</param>
    public JobRunner(Action<Action> post)
    {
        _post = post;
    }

    /// <summary>Output log.</summary>
    public ObservableCollection<LogLine> Lines { get; } = [];

    /// <summary>A job is running.</summary>
    [ObservableProperty]
    public partial bool IsRunning { get; private set; }

    /// <summary>Progress of the current phase, 0 to 1.</summary>
    [ObservableProperty]
    public partial double Progress { get; private set; }

    /// <summary>No step reported yet in the running job.</summary>
    [ObservableProperty]
    public partial bool IsIndeterminate { get; private set; }

    /// <summary>Current phase or status line.</summary>
    [ObservableProperty]
    public partial string PhaseText { get; private set; } = string.Empty;

    /// <summary>The whole log as text (Export log).</summary>
    public string LogText => string.Join(Environment.NewLine, Lines.Select(l => l.Text));

    /// <summary>Run <c>mkpfs</c> in-process with <paramref name="args"/>, echoing the command line first.</summary>
    /// <param name="args">CLI arguments.</param>
    /// <returns>Outcome.</returns>
    public Task<JobOutcome> RunCliAsync(IReadOnlyList<string> args) => RunAsync(job =>
    {
        Echo(args);
        return MkPFSCli.Run([.. args], job.CreateCliContext());
    });

    /// <summary>Log the equivalent <c>mkpfs</c> command line (callable from the job thread).</summary>
    /// <param name="args">CLI arguments.</param>
    public void Echo(IReadOnlyList<string> args) => Enqueue(new LogLine("$ mkpfs " + string.Join(' ', args.Select(Quote)), LogTone.Muted));

    /// <summary>Run <paramref name="work"/> on a background thread; its return value is the exit code.</summary>
    /// <param name="work">Job body.</param>
    /// <returns>Outcome, once the final lines are in <see cref="Lines"/>.</returns>
    /// <exception cref="InvalidOperationException">A job is already running.</exception>
    public async Task<JobOutcome> RunAsync(Func<JobContext, int> work)
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("A job is already running.");
        }

        using CancellationTokenSource cancel = new();
        _cancel = cancel;
        TaskCompletionSource idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _idle = idle;
        _phase = string.Empty;
        _label = string.Empty;
        IsRunning = true;
        IsIndeterminate = true;
        Progress = 0;
        PhaseText = string.Empty;

        LineWriter output = new(this);
        JobContext context = new(output, new Sink(this, cancel.Token), cancel.Token);
        (int exitCode, Exception? error) = await Task.Run(() =>
        {
            try
            {
                return (work(context), (Exception?)null);
            }
            catch (Exception ex)
            {
                return (1, ex);
            }
            finally
            {
                output.Complete();
            }
        }).ConfigureAwait(true);

        JobOutcome outcome = cancel.IsCancellationRequested ? JobOutcome.Cancelled
            : error is null && exitCode == 0 ? JobOutcome.Succeeded
            : JobOutcome.Failed;
        if (outcome == JobOutcome.Failed && error is not null)
        {
            Enqueue(new LogLine(Localizer.Instance.Format("err_unexpected", error.Message), LogTone.Error));
        }

        if (outcome == JobOutcome.Succeeded && _phase.Length > 0)
        {
            Enqueue(PhaseDone());
        }

        Enqueue(new LogLine(string.Empty, LogTone.Normal));
        Enqueue(outcome switch
        {
            JobOutcome.Succeeded => new LogLine(Localizer.Instance["ok"], LogTone.Success),
            JobOutcome.Cancelled => new LogLine(Localizer.Instance["cancelled"], LogTone.Error),
            _ => new LogLine(Localizer.Instance.Format("err_process", exitCode), LogTone.Error),
        });

        // Apply everything still queued now, so callers see the final state when the task completes.
        Flush();
        _cancel = null;
        IsRunning = false;
        IsIndeterminate = false;
        if (outcome == JobOutcome.Succeeded)
        {
            // Python freezes the bar at 100% and ticks the last label.
            Progress = 1;
            PhaseText = "✓ " + (_label.Length > 0 ? _label : Localizer.Instance["ok"]);
        }
        else
        {
            Progress = 0;
            PhaseText = string.Empty;
        }

        idle.TrySetResult();
        return outcome;
    }

    /// <summary>Ask the running job to stop at its next progress report.</summary>
    public void Cancel() => _cancel?.Cancel();

    /// <summary>Completes when no job is running (at once when idle).</summary>
    /// <returns>Task that ends with the current job.</returns>
    public Task WhenIdle() => _idle?.Task ?? Task.CompletedTask;

    /// <summary>Add a line from the UI thread (form validation errors).</summary>
    /// <param name="line">Line.</param>
    public void Append(LogLine line) => Lines.Add(line);

    /// <summary>Empty the log.</summary>
    public void Clear() => Lines.Clear();

    /// <summary>Python GUI line tags: success, error, and warning prefixes.</summary>
    /// <param name="line">Output line.</param>
    /// <returns>Tone.</returns>
    internal static LogTone ToneOf(string line)
    {
        string lower = line.ToLowerInvariant();
        if (lower.StartsWith('✓') || lower.StartsWith("ok ", StringComparison.Ordinal) || lower.StartsWith("success", StringComparison.Ordinal)
            || lower.StartsWith("done:", StringComparison.Ordinal) || lower.StartsWith("complete:", StringComparison.Ordinal))
        {
            return LogTone.Success;
        }

        if (lower.StartsWith("error", StringComparison.Ordinal) || lower.StartsWith('✗'))
        {
            return LogTone.Error;
        }

        return lower.StartsWith("warn", StringComparison.Ordinal) ? LogTone.Warning : LogTone.Normal;
    }

    private static string Quote(string arg) => arg.Length == 0 || arg.Contains(' ', StringComparison.Ordinal) ? $"\"{arg}\"" : arg;

    // Worker side: record the line and make sure one flush is queued on the UI thread.
    private void Enqueue(LogLine line)
    {
        lock (_gate)
        {
            _pendingLines.Add(line);
            QueueFlush();
        }
    }

    private void QueueFlush()
    {
        if (!_flushQueued)
        {
            _flushQueued = true;
            _post(Flush);
        }
    }

    private LogLine PhaseDone()
    {
        int percent = _total > 0 ? (int)(_done * 100 / _total) : 100;
        return new LogLine($"✓ {_phase}: {percent}%", LogTone.Success);
    }

    // UI side: apply queued lines and the latest progress snapshot.
    private void Flush()
    {
        LogLine[] lines;
        bool progressDirty;
        double ratio;
        string label;
        lock (_gate)
        {
            lines = [.. _pendingLines];
            _pendingLines.Clear();
            progressDirty = _progressDirty;
            _progressDirty = false;
            _flushQueued = false;
            ratio = _total > 0 ? Math.Clamp((double)_done / _total, 0, 1) : 0;
            label = _label;
        }

        foreach (LogLine line in lines)
        {
            Lines.Add(line);
        }

        if (progressDirty && IsRunning)
        {
            PhaseText = label;
            Progress = ratio;
        }
    }

    private void OnStep(string phase, long done, long total)
    {
        lock (_gate)
        {
            // Python logs "✓ <phase>: N%" whenever the phase changes.
            if (phase != _phase && _phase.Length > 0)
            {
                _pendingLines.Add(PhaseDone());
            }

            if (_phase.Length == 0)
            {
                _post(() => IsIndeterminate = false);
            }

            _phase = phase;
            _label = phase;
            _done = done;
            _total = total;
            _progressDirty = true;
            QueueFlush();
        }
    }

    private void OnStatus(string message)
    {
        lock (_gate)
        {
            _label = message.Trim();
            _progressDirty = true;
            QueueFlush();
        }
    }

    private sealed class Sink(JobRunner runner, CancellationToken token) : IProgressSink
    {
        public void Step(string phase, long done, long total, long bytesProcessed)
        {
            token.ThrowIfCancellationRequested();
            runner.OnStep(phase, done, total);
        }

        public void Status(string message)
        {
            token.ThrowIfCancellationRequested();
            runner.OnStatus(message);
        }
    }

    // Splits writes into lines; a carriage return overwrites the line like a terminal, blank lines are dropped.
    private sealed class LineWriter(JobRunner runner) : TextWriter
    {
        private readonly StringBuilder _line = new();
        private readonly Lock _lock = new();
        private bool _carriageReturn;

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            lock (_lock)
            {
                if (value == '\n')
                {
                    _carriageReturn = false;
                    Emit();
                }
                else if (value == '\r')
                {
                    // Wait for the next character: "\r\n" ends the line, anything else overwrites it.
                    _carriageReturn = true;
                }
                else
                {
                    if (_carriageReturn)
                    {
                        _carriageReturn = false;
                        _line.Clear();
                    }

                    _line.Append(value);
                }
            }
        }

        public override void Write(string? value)
        {
            if (value is null)
            {
                return;
            }

            lock (_lock)
            {
                foreach (char c in value)
                {
                    Write(c);
                }
            }
        }

        // Flush keeps a partial line (a prompt waits for its answer on the same line); Complete ends it.
        public void Complete()
        {
            lock (_lock)
            {
                Emit();
            }
        }

        private void Emit()
        {
            string text = _line.ToString().TrimEnd();
            _line.Clear();
            if (text.Length > 0)
            {
                runner.Enqueue(new LogLine(text, ToneOf(text)));
            }
        }
    }
}
