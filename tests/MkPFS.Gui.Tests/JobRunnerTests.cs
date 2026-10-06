using MkPFS.Core.Diagnostics;
using MkPFS.Gui.Jobs;

namespace MkPFS.Gui.Tests;

[Collection(GuiCollection.Name)]
public sealed class JobRunnerTests
{
    // Applies updates on the calling thread; tests have no UI dispatcher.
    private static JobRunner Runner() => new(action => action());

    private static string[] Texts(JobRunner runner) => [.. runner.Lines.Select(l => l.Text)];

    [Fact]
    public async Task Output_is_split_into_tagged_lines_like_the_python_streamer()
    {
        JobRunner runner = Runner();

        JobOutcome outcome = await runner.RunAsync(job =>
        {
            job.Out.Write("plain\nWARN careful\r\nERROR broken\n\n");
            job.Out.Write("progress 10%\rprogress done\n");
            job.Out.Write("✓ finished");
            return 0;
        });

        Assert.Equal(JobOutcome.Succeeded, outcome);
        Assert.Equal(["plain", "WARN careful", "ERROR broken", "progress done", "✓ finished", "", "✓ Completed successfully."], Texts(runner));
        Assert.Equal(
            [LogTone.Normal, LogTone.Warning, LogTone.Error, LogTone.Normal, LogTone.Success, LogTone.Normal, LogTone.Success],
            runner.Lines.Select(l => l.Tone));
        Assert.False(runner.IsRunning);
    }

    [Theory]
    [InlineData(0.005, 60, null)]
    [InlineData(0.5, 2, null)]
    [InlineData(1.0, 60, null)]
    [InlineData(0.5, 38, "38s")]
    [InlineData(0.25, 84, "4m 12s")]
    [InlineData(0.1, 434, "1h 05m")]
    public void Time_left_waits_for_a_settled_rate_and_reads_naturally(double ratio, int seconds, string? expected) =>
        Assert.Equal(expected, JobRunner.TimeLeft(ratio, TimeSpan.FromSeconds(seconds)));

    [Fact]
    public async Task The_phase_text_carries_the_time_left()
    {
        ManualClock clock = new();
        JobRunner runner = new(action => action(), clock);
        string? midway = null;
        await runner.RunAsync(job =>
        {
            job.Progress.Report("compress", 0, 100);
            clock.Advance(TimeSpan.FromSeconds(30));
            job.Progress.Report("compress", 25, 100);
            midway = runner.PhaseText;
            return 0;
        });

        Assert.Equal("compress · 1m 30s left", midway);
        Assert.Equal("✓ compress", runner.PhaseText);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }

    [Fact]
    public async Task Phase_changes_log_the_finished_phase_and_the_bar_ends_full()
    {
        JobRunner runner = Runner();

        JobOutcome outcome = await runner.RunAsync(job =>
        {
            job.Progress.Report("scan", 1, 2);
            job.Progress.Report("compress", 10, 10);
            job.Progress.Status("Writing header");
            return 0;
        });

        Assert.Equal(JobOutcome.Succeeded, outcome);
        Assert.Equal(["✓ scan: 50%", "✓ compress: 100%", "", "✓ Completed successfully."], Texts(runner));
        Assert.Equal(1, runner.Progress);
        Assert.Equal("✓ Writing header", runner.PhaseText);
        Assert.False(runner.IsIndeterminate);
    }

    [Fact]
    public async Task With_a_plan_one_bar_covers_every_phase_and_never_moves_back()
    {
        JobRunner runner = Runner();
        List<double> seen = [];
        runner.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(JobRunner.Progress))
            {
                seen.Add(runner.Progress);
            }
        };
        List<string> labels = [];
        IReadOnlyList<ProgressPhase> plan = ProgressPlan.For(["pack", "file", "a.exfat", "a.ffpfsc", "--verify"])!;

        await runner.RunAsync(
            job =>
            {
                job.Progress.Report("compress", 30, 60);
                labels.Add(runner.PhaseText);
                job.Progress.Report("compress", 60, 60);
                job.Progress.Report("verify", 0, 10); // a new phase starts at 0%, the bar does not
                labels.Add(runner.PhaseText);
                job.Progress.Report("verify", 10, 10);
                job.Progress.Report("compare", 5, 10);
                return 0;
            },
            plan);

        // compress weighs 3 of 5, verify and compare 1 each.
        Assert.Equal([0.3, 0.6, 0.8, 0.9, 1], seen.Where(p => p > 0).Select(p => Math.Round(p, 3)).Distinct());
        Assert.Equal(["compress (1/3)", "verify (2/3)"], labels);
    }

    [Theory]
    [InlineData(new[] { "pack", "file", "a", "b", "--verify" }, "compress,verify,compare")]
    [InlineData(new[] { "pack", "file", "a", "b" }, "")]
    [InlineData(new[] { "pack", "exfat", "a" }, "")]
    [InlineData(new[] { "verify", "a.ffpfsc", "--source-file", "a.exfat" }, "verify,compare")]
    [InlineData(new[] { "verify", "a.ffpfsc" }, "")]
    [InlineData(new[] { "repair", "a.ffpfsc" }, "scan,repair,verify")]
    [InlineData(new[] { "repair", "a.ffpfsc", "--scan" }, "")]
    [InlineData(new[] { "ampr", "game", "--root", "g", "--output", "o", "--exfat", "." }, "pack,exfat")]
    [InlineData(new[] { "ampr", "game", "--root", "g", "--output", "o" }, "")]
    public void Plans_list_the_phases_each_command_reports(string[] args, string phases) =>
        Assert.Equal(phases, string.Join(',', ProgressPlan.For(args)?.Select(p => p.Name) ?? []));

    [Fact]
    public async Task Cancel_stops_the_job_at_its_next_progress_report()
    {
        JobRunner runner = Runner();
        using SemaphoreSlim started = new(0);
        int steps = 0;

        Task<JobOutcome> run = runner.RunAsync(job =>
        {
            for (long i = 0; ; i++)
            {
                job.Progress.Report("compress", i, long.MaxValue);
                steps++;
                if (i == 0)
                {
                    started.Release();
                }

                Thread.Sleep(5);
            }
        });
        Assert.True(await started.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        runner.Cancel();
        JobOutcome outcome = await run;

        Assert.Equal(JobOutcome.Cancelled, outcome);
        Assert.Equal("✗ Cancelled.", runner.Lines[^1].Text);
        Assert.Equal(0, runner.Progress);
        Assert.True(steps < 10_000);
    }

    [Fact]
    public async Task Failures_report_the_exit_code_or_the_exception()
    {
        JobRunner runner = Runner();

        Assert.Equal(JobOutcome.Failed, await runner.RunAsync(_ => 3));
        Assert.Equal("✗ Process exited with code 3.", runner.Lines[^1].Text);

        runner.Clear();
        Assert.Equal(JobOutcome.Failed, await runner.RunAsync(_ => throw new InvalidDataException("bad header")));
        Assert.Equal(["[ERROR] Unexpected: bad header", "", "✗ Process exited with code 1."], Texts(runner));
    }

    [Fact]
    public async Task Only_one_job_runs_at_a_time()
    {
        JobRunner runner = Runner();
        using ManualResetEventSlim release = new();

        Task<JobOutcome> first = runner.RunAsync(_ =>
        {
            release.Wait();
            return 0;
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(_ => 0));
        release.Set();
        Assert.Equal(JobOutcome.Succeeded, await first);
    }

    [Fact]
    public async Task Cli_runs_in_process_with_progress_and_auto_confirmed_prompts()
    {
        using TempDir dir = new();
        dir.File("src/sce_sys/param.json", """{"titleId": "PPSA01234"}""");
        dir.File("src/eboot.bin", string.Concat(Enumerable.Repeat("eboot ", 50_000)));
        string source = Path.Combine(dir.Path, "src");
        string exfat = Path.Combine(dir.Path, "data.exfat");
        string image = Path.Combine(dir.Path, "out.ffpfsc");
        JobRunner runner = Runner();

        Assert.Equal(JobOutcome.Succeeded, await runner.RunCliAsync(["pack", "exfat", source, exfat]));
        Assert.Equal($"$ mkpfs pack exfat {source} {exfat}", runner.Lines[0].Text);
        Assert.Equal(LogTone.Muted, runner.Lines[0].Tone);
        Assert.Contains(runner.Lines, l => l.Text.StartsWith("Successfully wrote", StringComparison.Ordinal));
        Assert.Contains(runner.Lines, l => l.Text.StartsWith("✓ ", StringComparison.Ordinal) && l.Text.EndsWith(": 100%", StringComparison.Ordinal));

        // The second pack finds the image and answers the overwrite prompt with "y".
        for (int pass = 0; pass < 2; pass++)
        {
            runner.Clear();
            Assert.Equal(JobOutcome.Succeeded, await runner.RunCliAsync(["pack", "file", exfat, image, "--cpu-count", "1", "--no-verify-structure"]));
        }

        Assert.True(runner.Lines.Any(l => l.Text == "Overwrite? [Y/n] y"), string.Join('\n', Texts(runner)));
        Assert.True(File.Exists(image));
    }
}
