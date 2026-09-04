namespace ConversationManager.Models;

/// <summary>
/// Which part of a conversation a query matched. The order is the ranking order: a hit in the
/// title outranks one in a prompt, which outranks one buried in command output. Without that
/// ordering a common word like "migrator" returns every conversation that ever built the repo.
/// </summary>
public enum SearchLayer
{
    Title,
    SessionId,
    Branch,
    Folder,
    Prompt,

    /// <summary>Claude's replies. Only read when the scope is widened.</summary>
    Assistant,

    /// <summary>Tool input and output - file dumps, build logs. 97% of the bytes on disk.</summary>
    Tool,
}

/// <summary>How exactly a term sat inside the text it matched.</summary>
public enum MatchKind
{
    Exact,
    Prefix,
    WordStart,
    Substring,

    /// <summary>Characters in order but not adjacent, so "168dup" finds "168987-Duplex".</summary>
    Fuzzy,
}

/// <summary>One term found in one layer of one conversation.</summary>
public sealed class FieldHit
{
    public required SearchLayer Layer { get; init; }
    public required MatchKind Kind { get; init; }
    public required string Term { get; init; }

    /// <summary>Text to show, already trimmed around the match.</summary>
    public string Snippet { get; init; } = "";

    public int SnippetMatchStart { get; init; }
    public int SnippetMatchLength { get; init; }

    /// <summary>How many separate places in this layer matched.</summary>
    public int Count { get; init; } = 1;

    public int Score => Scoring.LayerWeight(Layer) + Scoring.KindBonus(Kind) +
                        Math.Min(Count - 1, 5) * 8;
}

/// <summary>The weights that decide result order, in one place so tests can pin them.</summary>
public static class Scoring
{
    public static int LayerWeight(SearchLayer layer) => layer switch
    {
        SearchLayer.Title => 1000,
        SearchLayer.SessionId => 950,
        SearchLayer.Branch => 900,
        SearchLayer.Folder => 650,
        SearchLayer.Prompt => 600,
        SearchLayer.Assistant => 250,
        SearchLayer.Tool => 90,
        _ => 0,
    };

    public static int KindBonus(MatchKind kind) => kind switch
    {
        MatchKind.Exact => 300,
        MatchKind.Prefix => 200,
        MatchKind.WordStart => 160,
        MatchKind.Substring => 100,
        MatchKind.Fuzzy => 40,
        _ => 0,
    };

    /// <summary>
    /// Layers whose text is short and name-like, where a fuzzy match is a help rather than a
    /// flood. Prose is excluded: a subsequence search over 60KB of prompts matches everything.
    /// </summary>
    public static bool AllowsFuzzy(SearchLayer layer) =>
        layer is SearchLayer.Title or SearchLayer.Branch or SearchLayer.Folder or SearchLayer.SessionId;

    /// <summary>Layers read from the in-memory index, with no file access.</summary>
    public static bool IsShallow(SearchLayer layer) =>
        layer is not (SearchLayer.Assistant or SearchLayer.Tool);
}

/// <summary>A conversation that matched, with the evidence for why it ranked where it did.</summary>
public sealed class ConversationMatch
{
    public required Conversation Conversation { get; init; }

    /// <summary>Best hit per layer, strongest first.</summary>
    public required IReadOnlyList<FieldHit> Hits { get; init; }

    public required int Score { get; init; }

    public FieldHit Best => Hits[0];

    /// <summary>
    /// True when nothing but Claude's replies or command output matched. These are kept out of
    /// the main list: a term that only appears in a build log says nothing about what the
    /// conversation was for.
    /// </summary>
    public bool IsDeepOnly => Hits.All(h => !Scoring.IsShallow(h.Layer));
}
