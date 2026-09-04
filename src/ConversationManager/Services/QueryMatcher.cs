using ConversationManager.Models;

namespace ConversationManager.Services;

/// <summary>
/// Finding one term inside one piece of text, and saying how good the match was. Shared by the
/// in-memory search and the on-disk deep scan so both rank the same way.
/// </summary>
public static class QueryMatcher
{
    private static readonly char[] TermSeparators = { ' ', '\t', '\n', '\r' };

    /// <summary>Splits what the user typed into terms. All of them have to match, somewhere.</summary>
    public static string[] Terms(string query) =>
        (query ?? "").Split(
            TermSeparators,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Locates <paramref name="term"/> in <paramref name="text"/>. Returns null when absent.
    /// Fuzzy matching is only attempted when the layer allows it.
    /// </summary>
    public static (MatchKind Kind, int Start, int Length)? Find(string? text, string term, SearchLayer layer)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(term)) return null;

        if (text.Equals(term, StringComparison.OrdinalIgnoreCase))
            return (MatchKind.Exact, 0, text.Length);

        var idx = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        if (idx == 0) return (MatchKind.Prefix, 0, term.Length);
        if (idx > 0)
        {
            // A hit at a word boundary is what the user meant far more often than one in the
            // middle of a longer token, so the two are ranked apart.
            var kind = IsWordBoundary(text, idx) ? MatchKind.WordStart : MatchKind.Substring;
            return (kind, idx, term.Length);
        }

        if (Scoring.AllowsFuzzy(layer) && IsSubsequence(text, term))
            return (MatchKind.Fuzzy, 0, Math.Min(text.Length, term.Length));

        return null;
    }

    private static bool IsWordBoundary(string text, int idx)
    {
        if (idx == 0) return true;
        var prev = text[idx - 1];
        return !char.IsLetterOrDigit(prev);
    }

    /// <summary>Fuzzy fallback so "168dup" still finds "168987-Duplex-setting-is-not".</summary>
    public static bool IsSubsequence(string haystack, string needle)
    {
        var h = 0;
        foreach (var c in needle)
        {
            var found = false;
            while (h < haystack.Length)
            {
                if (char.ToLowerInvariant(haystack[h]) == char.ToLowerInvariant(c))
                {
                    h++;
                    found = true;
                    break;
                }
                h++;
            }
            if (!found) return false;
        }
        return true;
    }
}
