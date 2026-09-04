using ConversationManager.Models;

namespace ConversationManager.Services;

/// <summary>
/// Ranks conversations against what the user typed, using only the in-memory index - titles,
/// branches, folders and prompts. No file access, so it can run on every keystroke.
///
/// Every term has to match somewhere (AND), because narrowing is the whole point: "duplex print"
/// should mean the conversation about both, not everything about either.
/// </summary>
public static class ConversationSearch
{
    /// <summary>A session id is only matched deliberately, never by a few stray hex characters.</summary>
    private const int MinSessionIdTerm = 6;

    public static List<ConversationMatch> Search(
        IEnumerable<Conversation> conversations, string query)
    {
        var terms = QueryMatcher.Terms(query);
        if (terms.Length == 0) return new List<ConversationMatch>();

        var matches = new List<ConversationMatch>();

        foreach (var conversation in conversations)
        {
            var match = Score(conversation, terms);
            if (match is not null) matches.Add(match);
        }

        return Order(matches);
    }

    /// <summary>Best-first, and for equal scores the more recent conversation wins.</summary>
    public static List<ConversationMatch> Order(IEnumerable<ConversationMatch> matches) =>
        matches
            .OrderByDescending(m => m.Score)
            .ThenByDescending(m => m.Conversation.End)
            .ThenBy(m => m.Conversation.DisplayTitle, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Null when any term is missing from every shallow field.</summary>
    public static ConversationMatch? Score(Conversation conversation, string[] terms)
    {
        var perTermBest = new List<FieldHit>(terms.Length);
        var byLayer = new Dictionary<SearchLayer, FieldHit>();

        foreach (var term in terms)
        {
            var hits = HitsFor(conversation, term);
            if (hits.Count == 0) return null;

            var best = hits.OrderByDescending(h => h.Score).First();
            perTermBest.Add(best);

            foreach (var hit in hits)
            {
                // One hit per layer is enough evidence; keep the strongest of each.
                if (!byLayer.TryGetValue(hit.Layer, out var existing) || hit.Score > existing.Score)
                    byLayer[hit.Layer] = hit;
            }
        }

        // Average, not sum: a two-word query should not outrank a one-word query on the same
        // conversation just for having more terms to add up.
        var score = (int)Math.Round(perTermBest.Average(h => h.Score));

        // A little credit for corroboration across different fields.
        score += Math.Min(byLayer.Count - 1, 3) * 25;

        return new ConversationMatch
        {
            Conversation = conversation,
            Hits = byLayer.Values.OrderByDescending(h => h.Score).ToList(),
            Score = score,
        };
    }

    /// <summary>Every shallow place one term appears in one conversation.</summary>
    private static List<FieldHit> HitsFor(Conversation c, string term)
    {
        var hits = new List<FieldHit>();

        Add(hits, c.Title, term, SearchLayer.Title);

        if (term.Length >= MinSessionIdTerm)
        {
            var found = QueryMatcher.Find(c.SessionId, term, SearchLayer.SessionId);
            // Fuzzy over a guid is noise, not a match.
            if (found is not null && found.Value.Kind != MatchKind.Fuzzy)
                hits.Add(Make(SearchLayer.SessionId, found.Value, c.SessionId, term));
        }

        foreach (var branch in c.Branches)
            Add(hits, branch, term, SearchLayer.Branch);

        // The folder name on its own matches "dev2"; the full path matches "src\Dev2\Monitor.Net".
        Add(hits, c.FolderName, term, SearchLayer.Folder);
        foreach (var cwd in c.Cwds)
            Add(hits, cwd, term, SearchLayer.Folder);

        var promptHits = 0;
        FieldHit? bestPrompt = null;
        foreach (var prompt in c.Prompts)
        {
            var found = QueryMatcher.Find(prompt.Text, term, SearchLayer.Prompt);
            if (found is null) continue;

            promptHits++;
            var hit = MakePromptHit(prompt.Text, found.Value, term);
            if (bestPrompt is null || hit.Score > bestPrompt.Score) bestPrompt = hit;
        }
        if (bestPrompt is not null)
        {
            hits.Add(new FieldHit
            {
                Layer = SearchLayer.Prompt,
                Kind = bestPrompt.Kind,
                Term = term,
                Snippet = bestPrompt.Snippet,
                SnippetMatchStart = bestPrompt.SnippetMatchStart,
                SnippetMatchLength = bestPrompt.SnippetMatchLength,
                Count = promptHits,
            });
        }

        return hits;
    }

    private static void Add(List<FieldHit> hits, string? text, string term, SearchLayer layer)
    {
        if (string.IsNullOrEmpty(text)) return;
        var found = QueryMatcher.Find(text, term, layer);
        if (found is null) return;

        // The same folder appears once per subfolder visited; only the best copy is evidence.
        var existing = hits.FindIndex(h => h.Layer == layer);
        var hit = Make(layer, found.Value, text!, term);
        if (existing < 0) hits.Add(hit);
        else if (hit.Score > hits[existing].Score) hits[existing] = hit;
    }

    private static FieldHit Make(
        SearchLayer layer, (MatchKind Kind, int Start, int Length) found, string text, string term) =>
        new()
        {
            Layer = layer,
            Kind = found.Kind,
            Term = term,
            Snippet = TextSummary.OneLine(text, 120),
            SnippetMatchStart = found.Kind == MatchKind.Fuzzy ? 0 : found.Start,
            SnippetMatchLength = found.Kind == MatchKind.Fuzzy ? 0 : found.Length,
        };

    private static FieldHit MakePromptHit(
        string text, (MatchKind Kind, int Start, int Length) found, string term)
    {
        // A prompt can be a paragraph, so show the words around the hit rather than the opening.
        var (snippet, start, length) = TextSummary.Snippet(text, found.Start, found.Length);
        return new FieldHit
        {
            Layer = SearchLayer.Prompt,
            Kind = found.Kind,
            Term = term,
            Snippet = snippet,
            SnippetMatchStart = start,
            SnippetMatchLength = length,
        };
    }
}
