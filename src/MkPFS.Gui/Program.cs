using Avalonia;
using MkPFS.Cli;
using MkPFS.Gui.Localization;

namespace MkPFS.Gui;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args) =>
        args is ["--selftest"] ? SelfTest() : BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    // Also used by the XAML previewer.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();

    // Hidden CI check of a published build without opening a window: every string table is embedded and
    // the bundled native zlib loads (same check as `mkpfs selftest`).
    private static int SelfTest()
    {
        bool tables = Localizer.Languages.All(language =>
        {
            Localizer.Instance.Language = language;
            return Localizer.Instance["run"] != "run";
        });
        return tables && MkPFSCli.Run(["selftest"]) == 0 ? 0 : 1;
    }
}
