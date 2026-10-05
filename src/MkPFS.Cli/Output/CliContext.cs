using System.Globalization;
using MkPFS.Core.Diagnostics;

namespace MkPFS.Cli.Output;

/// <summary>Writers and output settings shared by all commands (lets tests capture output).</summary>
public sealed class CliContext
{
    private readonly IProgressSink? _progressSink;

    /// <summary>Create a context.</summary>
    /// <param name="stdout">Standard output.</param>
    /// <param name="stderr">Standard error.</param>
    /// <param name="useColor">ANSI colors for warnings and errors.</param>
    /// <param name="utf8">UTF-8 icon glyphs.</param>
    /// <param name="progress">Show progress bars on stderr.</param>
    /// <param name="stdin">Standard input for prompts; end of input when <see langword="null"/>.</param>
    /// <param name="progressSink">Receives progress instead of the terminal bar (the GUI view model).</param>
    public CliContext(TextWriter stdout, TextWriter stderr, bool useColor, bool utf8, bool progress, TextReader? stdin = null, IProgressSink? progressSink = null)
    {
        _progressSink = progressSink;
        In = stdin ?? TextReader.Null;
        Out = stdout;
        Err = stderr;
        Log = new ConsoleLog(stdout, stderr, useColor, utf8);
        ProgressEnabled = progress;
    }

    /// <summary>Standard input (overwrite prompts).</summary>
    public TextReader In { get; }

    /// <summary>Standard output.</summary>
    public TextWriter Out { get; }

    /// <summary>Standard error.</summary>
    public TextWriter Err { get; }

    /// <summary>Log with Python <c>info</c>/<c>warning</c>/<c>error</c> routing.</summary>
    public ConsoleLog Log { get; }

    /// <summary>Progress bars enabled.</summary>
    public bool ProgressEnabled { get; }

    /// <summary>Progress sink supplied by the host (the GUI), or <see langword="null"/> on a terminal.</summary>
    public IProgressSink? ExternalProgressSink => _progressSink;

    /// <summary>
    /// Directory that relative path arguments resolve against (the process directory by default). Commands that
    /// echo a path as typed, like Python does, resolve it here for file access.
    /// </summary>
    public string WorkingDirectory { get; init; } = Directory.GetCurrentDirectory();

    /// <summary>Context over the process console.</summary>
    /// <returns>Default context.</returns>
    public static CliContext CreateDefault()
    {
        bool tty = !Console.IsOutputRedirected || !Console.IsErrorRedirected;
        bool color = tty && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MKPFS_NO_COLOR"));
        return new CliContext(Console.Out, Console.Error, color, ConsoleLog.SupportsUtf8(Console.OutputEncoding), progress: true, Console.In);
    }

    /// <summary>Informational line (stdout).</summary>
    /// <param name="message">Text.</param>
    public void Info(string message) => Log.Info(message);

    /// <summary>Warning line with the warning icon (stdout).</summary>
    /// <param name="message">Text.</param>
    public void Warning(string message) => Log.Warning(message, LogIcon.Warning);

    /// <summary>Error line with the error icon (stderr).</summary>
    /// <param name="message">Text.</param>
    public void Error(string message) => Log.Error(message, LogIcon.Error);

    /// <summary>Three-line banner (Python <c>print_version_header</c>).</summary>
    public void VersionHeader()
    {
        Info(new string('=', 70));
        Info(MkPFSCli.Title);
        Info(new string('=', 70));
    }

    /// <summary>Progress sink on stderr, or <see langword="null"/> when disabled.</summary>
    /// <param name="enabled">Extra switch (for example <c>--no-progress</c>).</param>
    /// <returns>Sink or <see langword="null"/>.</returns>
    public IProgressSink? CreateProgress(bool enabled = true) => ProgressEnabled && enabled ? _progressSink ?? new TerminalProgress(Err) : null;

    /// <summary>Python <c>f"{value:,}"</c>.</summary>
    /// <param name="value">Number.</param>
    /// <returns>Number with comma thousands separators.</returns>
    public static string Thousands(long value) => value.ToString("#,0", CultureInfo.InvariantCulture);
}
