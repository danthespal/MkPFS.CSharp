using System.Globalization;
using MkPFS.Core.Diagnostics;

namespace MkPFS.Cli.Output;

/// <summary>Writers and output settings shared by all commands (lets tests capture output).</summary>
public sealed class CliContext
{
    /// <summary>Create a context.</summary>
    /// <param name="stdout">Standard output.</param>
    /// <param name="stderr">Standard error.</param>
    /// <param name="useColor">ANSI colors for warnings and errors.</param>
    /// <param name="utf8">UTF-8 icon glyphs.</param>
    /// <param name="progress">Show progress bars on stderr.</param>
    public CliContext(TextWriter stdout, TextWriter stderr, bool useColor, bool utf8, bool progress)
    {
        Out = stdout;
        Err = stderr;
        Log = new ConsoleLog(stdout, stderr, useColor, utf8);
        ProgressEnabled = progress;
    }

    /// <summary>Standard output.</summary>
    public TextWriter Out { get; }

    /// <summary>Standard error.</summary>
    public TextWriter Err { get; }

    /// <summary>Log with Python <c>info</c>/<c>warning</c>/<c>error</c> routing.</summary>
    public ConsoleLog Log { get; }

    /// <summary>Progress bars enabled.</summary>
    public bool ProgressEnabled { get; }

    /// <summary>Context over the process console.</summary>
    /// <returns>Default context.</returns>
    public static CliContext CreateDefault()
    {
        bool tty = !Console.IsOutputRedirected || !Console.IsErrorRedirected;
        bool color = tty && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MKPFS_NO_COLOR"));
        return new CliContext(Console.Out, Console.Error, color, ConsoleLog.SupportsUtf8(Console.OutputEncoding), progress: true);
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
    public IProgressSink? CreateProgress(bool enabled = true) => ProgressEnabled && enabled ? new TerminalProgress(Err) : null;

    /// <summary>Python <c>f"{value:,}"</c>.</summary>
    /// <param name="value">Number.</param>
    /// <returns>Number with comma thousands separators.</returns>
    public static string Thousands(long value) => value.ToString("#,0", CultureInfo.InvariantCulture);
}
