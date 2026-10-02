using System.Text;
using MkPFS.Cli;
using MkPFS.Cli.Output;
using MkPFS.Core.Diagnostics;

namespace MkPFS.Tests.Cli;

/// <summary>Port of Python <c>test_logging.py</c> and <c>test_pbar.py</c>.</summary>
public sealed class OutputTests
{
    [Fact]
    public void SupportsUtf8_is_false_when_env_override_is_set()
    {
        string? previous = Environment.GetEnvironmentVariable("MKPFS_NO_UTF8");
        try
        {
            Environment.SetEnvironmentVariable("MKPFS_NO_UTF8", "1");
            Assert.False(ConsoleLog.SupportsUtf8(Encoding.UTF8));
            Environment.SetEnvironmentVariable("MKPFS_NO_UTF8", null);
            Assert.True(ConsoleLog.SupportsUtf8(Encoding.UTF8));
            Assert.False(ConsoleLog.SupportsUtf8(Encoding.Latin1));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MKPFS_NO_UTF8", previous);
        }
    }

    [Fact]
    public void Icons_have_ascii_and_utf8_forms()
    {
        Assert.Equal("WARN", ConsoleLog.IconText(LogIcon.Warning, utf8: false));
        Assert.Equal("⚠️", ConsoleLog.IconText(LogIcon.Warning, utf8: true));
        Assert.Equal(string.Empty, ConsoleLog.IconText(LogIcon.None, utf8: true));
    }

    [Fact]
    public void Log_sends_info_to_stdout_and_errors_to_stderr()
    {
        StringWriter stdout = new();
        StringWriter stderr = new();
        ConsoleLog log = new(stdout, stderr, useColor: false, utf8: false);

        log.Info("hello", LogIcon.Ok);
        log.Warning("careful");
        log.Error("boom", LogIcon.Error);

        Assert.Equal($"OK hello{Environment.NewLine}careful{Environment.NewLine}", stdout.ToString());
        Assert.Equal($"ERROR boom{Environment.NewLine}", stderr.ToString());
    }

    [Fact]
    public void Log_colors_warnings_and_errors_when_enabled()
    {
        StringWriter stdout = new();
        StringWriter stderr = new();
        ConsoleLog log = new(stdout, stderr, useColor: true, utf8: false);

        log.Warning("w");
        log.Error("e");
        log.Info("i");

        Assert.StartsWith("\u001b[38;5;208mw\u001b[0m", stdout.ToString(), StringComparison.Ordinal);
        Assert.EndsWith($"i{Environment.NewLine}", stdout.ToString(), StringComparison.Ordinal);
        Assert.Equal($"\u001b[31me\u001b[0m{Environment.NewLine}", stderr.ToString());
    }

    [Fact]
    public void Progress_reports_percentage_speed_and_eta()
    {
        StringWriter writer = new();
        ManualTime time = new();
        TerminalProgress progress = new(writer, time);

        progress.Step("compress", 0, 100, 0);
        time.Advance(TimeSpan.FromSeconds(1));
        progress.Step("compress", 50, 100, 50 * 1024 * 1024);

        string output = writer.ToString();
        Assert.Contains("[--------------------------------]   0% compress", output, StringComparison.Ordinal);
        Assert.Contains("[################----------------]  50% compress @ 50.00 MB/s ETA 1s", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Progress_reports_items_per_second_without_bytes_and_ends_line_when_done()
    {
        StringWriter writer = new();
        ManualTime time = new();
        TerminalProgress progress = new(writer, time);

        progress.Step("verify", 0, 4, 0);
        time.Advance(TimeSpan.FromSeconds(2));
        progress.Step("verify", 4, 4, 0);

        Assert.EndsWith("[################################] 100% verify 2.0 items/s\n", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Progress_clamps_out_of_range_values()
    {
        StringWriter writer = new();
        TerminalProgress progress = new(writer, new ManualTime());

        progress.Step("write", 500, 0, 0);

        Assert.Contains("100% write", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Progress_status_writes_a_line()
    {
        StringWriter writer = new();
        new TerminalProgress(writer).Status("Writing image");
        Assert.Equal("Writing image\n", writer.ToString());
    }

    [Fact]
    public void Report_extension_clamps_before_calling_sink()
    {
        RecordingSink sink = new();
        sink.Report("x", -5, 0);
        sink.Report("x", 20, 10, 7);
        Assert.Equal([("x", 0L, 1L, 0L), ("x", 10L, 10L, 7L)], sink.Steps);
    }

    [Fact]
    public void Title_has_python_header_shape()
    {
        Assert.StartsWith("MkPFS.C# ", MkPFSCli.Title, StringComparison.Ordinal);
        Assert.EndsWith(" - https://github.com/danthespal/MkPFS.CSharp", MkPFSCli.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("+", MkPFSCli.Version, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2, "verify")] // missing argument
    [InlineData(2, "inspect", "x.ffpfs", "--bogus")] // unknown option
    [InlineData(2, "pack", "folder", "a", "b", "--compress", "--no-compress")] // mutually exclusive
    [InlineData(1, "verify", "missing.ffpfs")] // runtime error
    public void Usage_errors_exit_with_2_like_argparse(int expected, params string[] args)
    {
        CliContext ctx = new(TextWriter.Null, TextWriter.Null, useColor: false, utf8: false, progress: false);
        Assert.Equal(expected, MkPFSCli.Run(args, ctx));
    }

    private sealed class RecordingSink : IProgressSink
    {
        public List<(string Phase, long Done, long Total, long Bytes)> Steps { get; } = [];

        public void Step(string phase, long done, long total, long bytesProcessed) =>
            Steps.Add((phase, done, total, bytesProcessed));

        public void Status(string message)
        {
        }
    }
}
