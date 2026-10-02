using System.ComponentModel;
using System.Text.Json;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MkPFS.Core.Util;
using MkPFS.Gui.Jobs;
using MkPFS.Gui.Localization;

namespace MkPFS.Gui.ViewModels;

/// <summary>
/// One operation page (Python <c>BasePanel</c>): localized header, an options form supplied by the
/// subclass, and the shared progress bar, output log, Run and Cancel.
/// </summary>
public abstract partial class PanelViewModel : ObservableObject
{
    private readonly string _titleKey;
    private readonly string _subtitleKey;

    /// <summary>Create a panel.</summary>
    /// <param name="titleKey">Title string key.</param>
    /// <param name="subtitleKey">Subtitle string key.</param>
    /// <param name="accent">Panel accent color (Python <c>_PANEL_ACCENT</c>).</param>
    /// <param name="job">Job runner; a UI-thread runner when <see langword="null"/>.</param>
    protected PanelViewModel(string titleKey, string subtitleKey, Color accent, JobRunner? job = null)
    {
        _titleKey = titleKey;
        _subtitleKey = subtitleKey;
        Accent = new ImmutableSolidColorBrush(accent);
        Job = job ?? new JobRunner();
        Job.PropertyChanged += OnJobChanged;
        Localizer.Instance.PropertyChanged += (_, _) => OnLanguageChanged();
    }

    /// <summary>Accent brush for the header and progress bar.</summary>
    public IBrush Accent { get; }

    /// <summary>Runs the operation and holds its log.</summary>
    public JobRunner Job { get; }

    /// <summary>Panel title.</summary>
    public string Title => Localizer.Instance[_titleKey];

    /// <summary>Panel subtitle.</summary>
    public string Subtitle => Localizer.Instance[_subtitleKey];

    /// <summary>Run button text ("Running…" while busy, as in Python).</summary>
    public string RunLabel => Localizer.Instance[Job.IsRunning ? "running" : "run"];

    /// <summary>Options form content: the panel itself for real panels (resolved by a data template), or text.</summary>
    public abstract object Form { get; }

    /// <summary>
    /// Validate the form and build the <c>mkpfs</c> arguments (Python <c>_run_command</c>).
    /// </summary>
    /// <param name="error">Message for the log when the form is incomplete.</param>
    /// <returns>Arguments, or <see langword="null"/> with <paramref name="error"/> set.</returns>
    protected internal abstract IReadOnlyList<string>? BuildArguments(out string? error);

    /// <summary>Write the log to <paramref name="path"/>: <c>{"log": [...]}</c> for <c>.json</c>, plain text otherwise.</summary>
    /// <param name="path">Target file.</param>
    public void ExportLog(string path)
    {
        string[] lines = [.. Job.Lines.Select(l => l.Text)];
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new ExportedLog(lines), GuiJsonContext.Default.ExportedLog));
        }
        else
        {
            File.WriteAllText(path, string.Join('\n', lines).Trim() + "\n");
        }
    }

    /// <summary>Append <paramref name="flag"/> when <paramref name="enabled"/>.</summary>
    /// <param name="args">Argument list.</param>
    /// <param name="enabled">Condition.</param>
    /// <param name="flag">Flag.</param>
    protected static void AddFlag(List<string> args, bool enabled, string flag)
    {
        if (enabled)
        {
            args.Add(flag);
        }
    }

    /// <summary>Append <paramref name="option"/> and the trimmed <paramref name="value"/> when it is not empty.</summary>
    /// <param name="args">Argument list.</param>
    /// <param name="option">Option name.</param>
    /// <param name="value">Option value.</param>
    protected static void AddOption(List<string> args, string option, string value)
    {
        if (value.Trim() is { Length: > 0 } trimmed)
        {
            args.Add(option);
            args.Add(trimmed);
        }
    }

    /// <summary>
    /// Python <c>_on_src_changed</c>: while the output is empty and the source exists, suggest
    /// <c>&lt;parent&gt;/&lt;sanitized name&gt;&lt;extension&gt;</c>; otherwise keep the output as typed.
    /// </summary>
    /// <param name="source">Source path.</param>
    /// <param name="output">Current output path.</param>
    /// <param name="exists">Source existence check (folder or file).</param>
    /// <param name="nameOf">Base name of the source (folder name or file stem).</param>
    /// <param name="extension">Output extension.</param>
    /// <returns>New output path.</returns>
    protected static string SuggestOutput(string source, string output, Func<string, bool> exists, Func<string, string> nameOf, string extension)
    {
        string trimmed = Path.TrimEndingDirectorySeparator(source.Trim());
        if (output.Trim().Length > 0 || trimmed.Length == 0 || !exists(trimmed))
        {
            return output;
        }

        string parent = Path.GetDirectoryName(trimmed) ?? trimmed;
        return Path.Combine(parent, NameRules.UiSanitizeBasename(nameOf(trimmed)) + extension);
    }

    /// <summary>Raise change notifications for every localized property.</summary>
    protected virtual void OnLanguageChanged() => OnPropertyChanged(string.Empty);

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        // Python _on_run clears the log before every run.
        Job.Clear();
        IReadOnlyList<string>? args = BuildArguments(out string? error);
        if (args is null)
        {
            Job.Append(new LogLine(error ?? string.Empty, LogTone.Error));
            return;
        }

        await ExecuteAsync(args).ConfigureAwait(true);
    }

    /// <summary>Run the validated arguments; the default runs <c>mkpfs</c> in-process.</summary>
    /// <param name="args">CLI arguments.</param>
    /// <returns>Outcome.</returns>
    protected virtual Task<JobOutcome> ExecuteAsync(IReadOnlyList<string> args) => Job.RunCliAsync(args);

    /// <summary>Called when a job starts or ends (update extra commands).</summary>
    protected virtual void OnRunningChanged()
    {
    }

    private bool CanRun() => !Job.IsRunning;

    [RelayCommand]
    private void Cancel() => Job.Cancel();

    private void OnJobChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(JobRunner.IsRunning))
        {
            OnPropertyChanged(nameof(RunLabel));
            RunCommand.NotifyCanExecuteChanged();
            OnRunningChanged();
        }
    }
}
