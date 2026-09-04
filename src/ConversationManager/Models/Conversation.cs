using System.IO;

namespace ConversationManager.Models;

/// <summary>Where a conversation's facts came from, which decides what can be done with it.</summary>
public enum ConversationSource
{
    /// <summary>A .jsonl transcript under projects\ - full detail, resumable, previewable.</summary>
    Transcript,

    /// <summary>
    /// Only the prompt history in history.jsonl survives. Claude Code prunes transcripts long
    /// before it prunes that file, so these are the oldest conversations: still findable by what
    /// was typed, but with no branch, no reply text, and nothing left to resume.
    /// </summary>
    HistoryOnly,
}

/// <summary>One thing the user typed, and when.</summary>
public sealed record Prompt(DateTimeOffset When, string Text);

/// <summary>
/// A past Claude Code session, reduced to the facts worth searching: what it was called, which
/// branch and folder it worked in, when it ran, and everything the user typed.
///
/// Deliberately excludes the bulk of a transcript - assistant prose and tool output are 97% of
/// the bytes on disk, so keeping them in memory would cost hundreds of MB to answer questions
/// that the title, branch and prompts already answer. <see cref="Services.DeepSearcher"/> goes
/// back to the file when the user actually asks for that.
/// </summary>
public sealed class Conversation
{
    public required string SessionId { get; init; }

    /// <summary>Path to the .jsonl, or null when only prompt history survives.</summary>
    public string? TranscriptPath { get; init; }

    public ConversationSource Source =>
        TranscriptPath is null ? ConversationSource.HistoryOnly : ConversationSource.Transcript;

    /// <summary>The working folder the session spent most of its messages in.</summary>
    public string Cwd { get; init; } = "";

    /// <summary>Every folder seen, most used first - a session can wander into subfolders.</summary>
    public IReadOnlyList<string> Cwds { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Real branch names, most used first. "HEAD" (detached, or not a repo at all) is dropped
    /// during parsing rather than shown, because it names nothing the user would search for.
    /// </summary>
    public IReadOnlyList<string> Branches { get; init; } = Array.Empty<string>();

    /// <summary>Claude Code's own generated title for the session, when it wrote one.</summary>
    public string? Title { get; init; }

    public DateTimeOffset Start { get; init; }
    public DateTimeOffset End { get; init; }

    /// <summary>User plus assistant messages - a rough size for the conversation.</summary>
    public int MessageCount { get; init; }

    /// <summary>Bytes of transcript on disk; 0 for history-only.</summary>
    public long Bytes { get; init; }

    public IReadOnlyList<Prompt> Prompts { get; init; } = Array.Empty<Prompt>();

    // ---- derived ----------------------------------------------------------------------

    public bool HasTranscript => TranscriptPath is not null;

    /// <summary>Only a real transcript can be handed back to `claude --resume`.</summary>
    public bool CanResume => HasTranscript && Directory.Exists(Cwd);

    public TimeSpan Duration => End > Start ? End - Start : TimeSpan.Zero;

    public string? PrimaryBranch => Branches.Count > 0 ? Branches[0] : null;

    public string FolderName => FolderNameOf(Cwd);

    /// <summary>The title if there is one, else the opening prompt, else the session id.</summary>
    public string DisplayTitle
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Title)) return Title!;

            var opening = FirstDescriptivePrompt();
            if (opening is not null) return TextSummary.OneLine(opening, 90);

            return ShortId;
        }
    }

    /// <summary>
    /// The first prompt that describes something, for conversations with no generated title -
    /// which is every conversation recovered from prompt history alone.
    ///
    /// Sessions often open with a pasted link or a bare path, and using that as the card's name
    /// leaves a wall of URLs where the titles should be. A later sentence names the work better.
    /// </summary>
    private string? FirstDescriptivePrompt()
    {
        string? fallback = null;

        foreach (var prompt in Prompts)
        {
            var text = TextSummary.OneLine(prompt.Text, int.MaxValue);
            if (text.Length == 0) continue;

            fallback ??= text;
            if (!IsBareReference(text)) return text;
        }

        return fallback;
    }

    /// <summary>A lone URL or path: something pasted in, not something said.</summary>
    private static bool IsBareReference(string text)
    {
        if (text.Contains(' ')) return false;
        return text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ||
               text.Contains('\\') ||
               text.StartsWith('/');
    }

    public string ShortId => SessionId.Length >= 8 ? SessionId[..8] : SessionId;

    public static string FolderNameOf(string path)
    {
        var trimmed = (path ?? "").TrimEnd('\\', '/');
        if (trimmed.Length == 0) return "(unknown folder)";
        var idx = trimmed.LastIndexOfAny(new[] { '\\', '/' });
        return idx >= 0 && idx < trimmed.Length - 1 ? trimmed[(idx + 1)..] : trimmed;
    }
}
