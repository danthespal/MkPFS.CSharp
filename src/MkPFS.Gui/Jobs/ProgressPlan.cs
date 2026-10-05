namespace MkPFS.Gui.Jobs;

/// <summary>One phase of a run and its share of the progress bar.</summary>
/// <param name="Name">Phase name as the command reports it (<c>compress</c>, <c>verify</c>, ...).</param>
/// <param name="Weight">Relative share of the whole run.</param>
public readonly record struct ProgressPhase(string Name, double Weight);

/// <summary>
/// The phases a command reports, in order, so one bar can show the progress of the whole run instead of starting
/// over at every phase. Weights follow how much data each phase moves.
/// </summary>
public static class ProgressPlan
{
    /// <summary>The phases <c>mkpfs</c> reports for <paramref name="args"/>.</summary>
    /// <param name="args">CLI arguments.</param>
    /// <returns>Phases in order, or <see langword="null"/> when the command reports one phase or none.</returns>
    public static IReadOnlyList<ProgressPhase>? For(IReadOnlyList<string> args)
    {
        bool Has(string flag) => args.Contains(flag);
        string command = args.Count > 0 ? args[0] : string.Empty;
        string sub = args.Count > 1 ? args[1] : string.Empty;
        List<ProgressPhase> phases = (command, sub) switch
        {
            // Compressing reads and deflates the image; verifying decodes it, comparing reads both again.
            ("pack", "file") when Has("--verify") => [new("compress", 3), new("verify", 1), new("compare", 1)],
            ("verify", _) when Has("--source-file") || Has("--source-dir") => [new("verify", 1), new("compare", 1)],
            ("repair", _) when !Has("--scan") => [new("scan", 1), new("repair", 1), new("verify", 1)],
            // Packing compresses every packed byte with LZ4 HC; the image only copies the output.
            ("ampr", "game") when Has("--exfat") => [new("pack", 4), new("exfat", 1)],
            _ => [],
        };
        return phases.Count > 1 ? phases : null;
    }

    /// <summary>Overall progress, 0 to 1, when <paramref name="phase"/> is <paramref name="ratio"/> done.</summary>
    /// <param name="plan">Phases of the run.</param>
    /// <param name="phase">Current phase.</param>
    /// <param name="ratio">Progress of the current phase, 0 to 1.</param>
    /// <returns>Overall progress, or <see langword="null"/> for a phase outside the plan.</returns>
    public static double? Overall(IReadOnlyList<ProgressPhase> plan, string phase, double ratio)
    {
        int index = Index(plan, phase);
        if (index < 0)
        {
            return null;
        }

        double total = plan.Sum(p => p.Weight);
        double before = plan.Take(index).Sum(p => p.Weight);
        return total > 0 ? Math.Clamp((before + (plan[index].Weight * Math.Clamp(ratio, 0, 1))) / total, 0, 1) : 0;
    }

    /// <summary>Position of <paramref name="phase"/> in <paramref name="plan"/>, or -1.</summary>
    /// <param name="plan">Phases.</param>
    /// <param name="phase">Phase name.</param>
    /// <returns>Index.</returns>
    public static int Index(IReadOnlyList<ProgressPhase> plan, string phase)
    {
        for (int i = 0; i < plan.Count; i++)
        {
            if (plan[i].Name == phase)
            {
                return i;
            }
        }

        return -1;
    }
}
