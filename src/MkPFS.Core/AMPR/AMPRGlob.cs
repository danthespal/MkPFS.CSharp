using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace MkPFS.Core.AMPR;

/// <summary>
/// Python <c>fnmatch.fnmatchcase</c> as used by ampr_pack (<c>glob_matches</c>): case-sensitive, <c>*</c> and
/// <c>?</c> also match <c>/</c>, <c>**</c> is the same as <c>*</c>, <c>[...]</c>/<c>[!...]</c> sets, no escaping.
/// Patterns are translated like CPython 3.11 <c>fnmatch._translate</c> and compiled to .NET regexes.
/// </summary>
public static class AMPRGlob
{
    // Lists at least this long are compiled once into a PatternSet; shorter ones (often built per call) are matched directly.
    private const int PatternSetThreshold = 16;

    private static readonly ConcurrentDictionary<string, Regex> Cache = new(StringComparer.Ordinal);
    private static readonly ConditionalWeakTable<IReadOnlyCollection<string>, PatternSet> Sets = new();

    /// <summary>Python <c>glob_matches</c>: normalize separators, strip leading <c>/</c>, match any pattern.</summary>
    /// <param name="path">Relative path.</param>
    /// <param name="patterns">Patterns.</param>
    /// <returns><see langword="true"/> when any pattern matches.</returns>
    public static bool Matches(string path, IEnumerable<string> patterns)
    {
        string normalized = Normalize(path);
        if (patterns is IReadOnlyCollection<string> { Count: >= PatternSetThreshold } collection)
        {
            // Trace-derived include_from lists hold tens of thousands of exact paths; matching each one as a regex
            // per file is quadratic. Recompile if the list changed size since it was cached.
            PatternSet set = Sets.GetValue(collection, static c => new PatternSet(c));
            if (set.Count != collection.Count)
            {
                set = new PatternSet(collection);
                Sets.AddOrUpdate(collection, set);
            }

            return set.Matches(normalized);
        }

        return patterns.Any(pattern => FnMatchCase(normalized, Normalize(pattern)));
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    /// <summary>Python <c>fnmatch.fnmatchcase(name, pattern)</c>.</summary>
    /// <param name="name">Name.</param>
    /// <param name="pattern">Pattern.</param>
    /// <returns><see langword="true"/> when the whole name matches.</returns>
    public static bool FnMatchCase(string name, string pattern) =>
        Cache.GetOrAdd(pattern, static p => new Regex(Translate(p), RegexOptions.Singleline | RegexOptions.CultureInvariant)).IsMatch(name);

    /// <summary>Translate a pattern to an anchored .NET regex (CPython 3.11 <c>fnmatch._translate</c>).</summary>
    /// <param name="pattern">Pattern.</param>
    /// <returns>Regex source.</returns>
    public static string Translate(string pattern)
    {
        StringBuilder res = new("^");
        bool lastWasStar = false;
        int i = 0;
        int n = pattern.Length;
        while (i < n)
        {
            char c = pattern[i++];
            if (c == '*')
            {
                // Consecutive stars compress into one.
                if (!lastWasStar)
                {
                    res.Append(".*");
                    lastWasStar = true;
                }

                continue;
            }

            lastWasStar = false;
            if (c == '?')
            {
                res.Append('.');
            }
            else if (c == '[')
            {
                i = TranslateSet(pattern, i, res);
            }
            else
            {
                res.Append(Regex.Escape(c.ToString()));
            }
        }

        return res.Append(@"\z").ToString();
    }

    // i is just past '['. Appends the set (or a literal '[') and returns the index to continue at.
    private static int TranslateSet(string pat, int i, StringBuilder res)
    {
        int n = pat.Length;
        int j = i;
        if (j < n && pat[j] == '!')
        {
            j++;
        }

        if (j < n && pat[j] == ']')
        {
            j++;
        }

        while (j < n && pat[j] != ']')
        {
            j++;
        }

        if (j >= n)
        {
            res.Append(@"\[");
            return i;
        }

        string stuff;
        if (!pat.AsSpan(i, j - i).Contains('-'))
        {
            stuff = pat[i..j].Replace(@"\", @"\\", StringComparison.Ordinal);
        }
        else
        {
            List<string> chunks = [];
            int k = pat[i] == '!' ? i + 2 : i + 1;
            while (true)
            {
                k = k < j ? pat.IndexOf('-', k, j - k) : -1;
                if (k < 0)
                {
                    break;
                }

                chunks.Add(pat[i..k]);
                i = k + 1;
                k += 3;
            }

            string chunk = pat[i..j];
            if (chunk.Length > 0)
            {
                chunks.Add(chunk);
            }
            else
            {
                chunks[^1] += "-";
            }

            // Remove empty ranges (invalid in a regex).
            for (int m = chunks.Count - 1; m > 0; m--)
            {
                if (chunks[m - 1][^1] > chunks[m][0])
                {
                    chunks[m - 1] = chunks[m - 1][..^1] + chunks[m][1..];
                    chunks.RemoveAt(m);
                }
            }

            // Escape backslashes and hyphens that do not form ranges.
            stuff = string.Join('-', chunks.Select(s => s.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("-", @"\-", StringComparison.Ordinal)));
        }

        // Python escapes set operations (&&, ~~, ||); .NET treats "-[" as class subtraction, so '[' is escaped too.
        stuff = Regex.Replace(stuff, @"([&~|\[])", @"\$1");
        if (stuff.Length == 0)
        {
            res.Append("(?!)");
        }
        else if (stuff == "!")
        {
            res.Append('.');
        }
        else
        {
            if (stuff[0] == '!')
            {
                stuff = "^" + stuff[1..];
            }
            else if (stuff[0] is '^')
            {
                stuff = @"\" + stuff;
            }

            // A leading ']' is literal in Python; escape it for .NET.
            if (stuff[0] == ']' || stuff.StartsWith("^]", StringComparison.Ordinal))
            {
                stuff = stuff[0] == ']' ? @"\" + stuff : @"^\" + stuff[1..];
            }

            res.Append('[').Append(stuff).Append(']');
        }

        return j + 1;
    }

    // A pattern without '*', '?' or '[' translates to an anchored, fully escaped regex, so fnmatchcase on it is plain
    // ordinal equality; those go into a hash set and only real globs are matched as regexes.
    private sealed class PatternSet
    {
        private readonly HashSet<string> _literals = new(StringComparer.Ordinal);
        private readonly List<string> _globs = [];

        public PatternSet(IReadOnlyCollection<string> patterns)
        {
            Count = patterns.Count;
            foreach (string pattern in patterns)
            {
                string normalized = Normalize(pattern);
                if (normalized.AsSpan().IndexOfAny('*', '?', '[') < 0)
                {
                    _literals.Add(normalized);
                }
                else
                {
                    _globs.Add(normalized);
                }
            }
        }

        public int Count { get; }

        public bool Matches(string normalizedPath) =>
            _literals.Contains(normalizedPath) || _globs.Exists(glob => FnMatchCase(normalizedPath, glob));
    }
}
