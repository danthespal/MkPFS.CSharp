using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using MkPFS.Core.Util;

namespace MkPFS.Cli.Commands;

/// <summary>
/// CPU core options: <c>auto</c> (or 0) uses every logical processor but <see cref="CpuCores.Reserved"/>, and a
/// count is capped at that (<see cref="CpuCores"/>).
/// </summary>
internal static class CpuOption
{
    /// <summary>The rule, for option descriptions.</summary>
    internal static string Rule =>
        $"auto or 0 = every logical processor but {CpuCores.Reserved} ({CpuCores.Max} here), which also caps larger counts";

    /// <summary>An option that is Auto (0) when left out.</summary>
    /// <param name="name">Option name.</param>
    /// <param name="what">What the cores do, for the description.</param>
    /// <returns>Option.</returns>
    public static Option<int> Create(string name, string what) => new(name)
    {
        Description = $"{what}: {Rule} (default: auto)",
        HelpName = "auto|N",
        CustomParser = result => Parse(result) ?? 0,
    };

    /// <summary>An option that is <see langword="null"/> when left out, so a configuration value can apply.</summary>
    /// <param name="name">Option name.</param>
    /// <param name="what">What the cores do, for the description.</param>
    /// <param name="aliases">Other names.</param>
    /// <returns>Option.</returns>
    public static Option<int?> CreateOptional(string name, string what, params string[] aliases) => new(name, aliases)
    {
        Description = $"{what}: {Rule}",
        HelpName = "auto|N",
        CustomParser = Parse,
    };

    private static int? Parse(ArgumentResult result)
    {
        if (result.Tokens.Count == 0)
        {
            return null;
        }

        string text = result.Tokens[0].Value;
        if (text.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value))
        {
            return value;
        }

        string option = result.Parent is OptionResult parent ? parent.Option.Name : "the CPU count";
        result.AddError($"{option} must be auto or a whole number, not '{text}'");
        return null;
    }
}
