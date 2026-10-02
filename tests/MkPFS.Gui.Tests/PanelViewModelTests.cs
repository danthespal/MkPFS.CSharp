using System.Text.Json;
using Avalonia.Media;
using MkPFS.Gui.Jobs;
using MkPFS.Gui.ViewModels;

namespace MkPFS.Gui.Tests;

[Collection(GuiCollection.Name)]
public sealed class PanelViewModelTests
{
    private sealed class EchoPanel(JobRunner job, string[]? args) : PanelViewModel("v_title", "v_subtitle", Colors.Lime, job)
    {
        public override object Form => this;

        protected internal override IReadOnlyList<string>? BuildArguments(out string? error)
        {
            error = args is null ? "✗ Image path is required." : null;
            return args;
        }
    }

    [Fact]
    public async Task Run_logs_validation_errors_without_starting_a_job()
    {
        EchoPanel panel = new(new JobRunner(action => action()), args: null);

        await panel.RunCommand.ExecuteAsync(null);

        LogLine line = Assert.Single(panel.Job.Lines);
        Assert.Equal(new LogLine("✗ Image path is required.", LogTone.Error), line);
    }

    [Fact]
    public async Task Run_starts_the_cli_and_the_run_label_follows_the_job()
    {
        EchoPanel panel = new(new JobRunner(action => action()), ["--version"]);
        List<string> labels = [];
        panel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PanelViewModel.RunLabel))
            {
                labels.Add(panel.RunLabel);
            }
        };

        await panel.RunCommand.ExecuteAsync(null);

        Assert.Equal("$ mkpfs --version", panel.Job.Lines[0].Text);
        Assert.Equal("✓ Completed successfully.", panel.Job.Lines[^1].Text);
        Assert.Equal(["Running…", "Run"], labels);
        Assert.Equal("Verify", panel.Title);
    }

    [Fact]
    public void Export_writes_text_or_python_style_json()
    {
        using TempDir dir = new();
        EchoPanel panel = new(new JobRunner(action => action()), null);
        panel.Job.Append(new LogLine("first", LogTone.Normal));
        panel.Job.Append(new LogLine("✓ Completed successfully.", LogTone.Success));

        string text = Path.Combine(dir.Path, "log.txt");
        string json = Path.Combine(dir.Path, "log.json");
        panel.ExportLog(text);
        panel.ExportLog(json);

        Assert.Equal("first\n✓ Completed successfully.\n", File.ReadAllText(text));
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(json));
        Assert.Equal(["first", "✓ Completed successfully."], doc.RootElement.GetProperty("log").EnumerateArray().Select(e => e.GetString()));
    }
}
