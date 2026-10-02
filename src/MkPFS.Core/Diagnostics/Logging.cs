namespace MkPFS.Core.Diagnostics;

/// <summary>Message severity (Python <c>logging</c> levels used by MkPFS).</summary>
public enum LogLevel
{
    Debug = 10,
    Info = 20,
    Warning = 30,
    Error = 40,
}

/// <summary>Semantic icon prefixed to a message (Python <c>mkpfs.logging.icon</c> names).</summary>
public enum LogIcon
{
    None,
    Info,
    Ok,
    Warning,
    Error,
    File,
    Success,
}

/// <summary>Message sink for library code. Core never writes to the console directly.</summary>
public interface IMkPFSLog
{
    /// <summary>Emit one message.</summary>
    /// <param name="level">Severity.</param>
    /// <param name="message">Text.</param>
    /// <param name="icon">Optional icon.</param>
    void Log(LogLevel level, string message, LogIcon icon = LogIcon.None);
}

/// <summary>Convenience wrappers matching Python <c>info</c>/<c>warning</c>/<c>error</c>.</summary>
public static class MkPFSLogExtensions
{
    /// <summary>Informational message.</summary>
    public static void Info(this IMkPFSLog log, string message, LogIcon icon = LogIcon.None) =>
        log.Log(LogLevel.Info, message, icon);

    /// <summary>Warning message.</summary>
    public static void Warning(this IMkPFSLog log, string message, LogIcon icon = LogIcon.None) =>
        log.Log(LogLevel.Warning, message, icon);

    /// <summary>Error message.</summary>
    public static void Error(this IMkPFSLog log, string message, LogIcon icon = LogIcon.None) =>
        log.Log(LogLevel.Error, message, icon);
}

/// <summary>Log that discards everything.</summary>
public sealed class NullLog : IMkPFSLog
{
    /// <summary>Shared instance.</summary>
    public static readonly NullLog Instance = new();

    private NullLog()
    {
    }

    /// <inheritdoc />
    public void Log(LogLevel level, string message, LogIcon icon = LogIcon.None)
    {
    }
}
