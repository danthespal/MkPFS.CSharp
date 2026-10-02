using System.Text;
using MkPFS.Core.Diagnostics;

namespace MkPFS.Cli.Output;

/// <summary>
/// Console log matching Python <c>mkpfs.logging.log</c>: errors to stderr, everything else to
/// stdout, optional icon prefix, red/orange color on a terminal unless <c>MKPFS_NO_COLOR</c> is set.
/// </summary>
public sealed class ConsoleLog : IMkPFSLog
{
    private const string Reset = "\u001b[0m";
    private const string Red = "\u001b[31m";
    private const string Orange = "\u001b[38;5;208m";

    private readonly TextWriter _stdout;
    private readonly TextWriter _stderr;
    private readonly bool _useColor;
    private readonly bool _utf8;

    /// <summary>Create a log over the given writers.</summary>
    /// <param name="stdout">Writer for info and warning messages.</param>
    /// <param name="stderr">Writer for errors.</param>
    /// <param name="useColor">Emit ANSI colors.</param>
    /// <param name="utf8">Use UTF-8 icon glyphs instead of ASCII words.</param>
    public ConsoleLog(TextWriter stdout, TextWriter stderr, bool useColor, bool utf8)
    {
        _stdout = stdout;
        _stderr = stderr;
        _useColor = useColor;
        _utf8 = utf8;
    }

    /// <summary>Log over the process console with environment-based color and icon detection.</summary>
    /// <returns>Configured console log.</returns>
    public static ConsoleLog CreateDefault()
    {
        bool tty = !Console.IsOutputRedirected || !Console.IsErrorRedirected;
        bool useColor = tty && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MKPFS_NO_COLOR"));
        return new ConsoleLog(Console.Out, Console.Error, useColor, SupportsUtf8(Console.OutputEncoding));
    }

    /// <summary>
    /// Whether icons may use UTF-8 glyphs: false when <c>MKPFS_NO_UTF8</c> is set or the output
    /// encoding is not UTF-8 (Python <c>supports_utf8</c>).
    /// </summary>
    /// <param name="outputEncoding">Console output encoding.</param>
    /// <returns><see langword="true"/> when glyphs are safe to print.</returns>
    public static bool SupportsUtf8(Encoding? outputEncoding)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MKPFS_NO_UTF8")))
        {
            return false;
        }

        return outputEncoding is not null && outputEncoding.CodePage == Encoding.UTF8.CodePage;
    }

    /// <summary>Glyph or ASCII word for an icon (Python <c>icon</c>).</summary>
    /// <param name="icon">Icon.</param>
    /// <param name="utf8">Use glyphs.</param>
    /// <returns>Icon text, empty for <see cref="LogIcon.None"/>.</returns>
    public static string IconText(LogIcon icon, bool utf8) => (icon, utf8) switch
    {
        (LogIcon.Info, true) => "ℹ️",
        (LogIcon.Ok, true) => "✅",
        (LogIcon.Warning, true) => "⚠️",
        (LogIcon.Error, true) => "❌",
        (LogIcon.File, true) => "\U0001F4C4",
        (LogIcon.Success, true) => "\U0001F389",
        (LogIcon.Info, false) => "INFO",
        (LogIcon.Ok, false) => "OK",
        (LogIcon.Warning, false) => "WARN",
        (LogIcon.Error, false) => "ERROR",
        (LogIcon.File, false) => "FILE",
        (LogIcon.Success, false) => "SUCCESS",
        _ => string.Empty,
    };

    /// <inheritdoc />
    public void Log(LogLevel level, string message, LogIcon icon = LogIcon.None)
    {
        string prefix = icon == LogIcon.None ? string.Empty : IconText(icon, _utf8) + " ";
        string text = prefix + message;
        string color = !_useColor ? string.Empty
            : level >= LogLevel.Error ? Red
            : level >= LogLevel.Warning ? Orange
            : string.Empty;
        string line = color.Length > 0 ? color + text + Reset : text;
        (level >= LogLevel.Error ? _stderr : _stdout).WriteLine(line);
    }
}
