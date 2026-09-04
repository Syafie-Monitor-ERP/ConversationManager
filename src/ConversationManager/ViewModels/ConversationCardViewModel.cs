using ConversationManager.Models;
using ConversationManager.Platform;

namespace ConversationManager.ViewModels;

/// <summary>
/// Asks for a set of conversations to be deleted. Raised by a card for its own conversation and
/// by a group heading for all of its own; <paramref name="what"/> names the set so the
/// confirmation can say what is about to go.
/// </summary>
public delegate Task DeleteRequest(IReadOnlyList<Conversation> conversations, string what);

/// <summary>
/// One conversation as it appears on screen: what it was called, where and when it happened, why
/// it matched, and the things you can do with it.
/// </summary>
public sealed class ConversationCardViewModel : ObservableObject
{
    private readonly Action<Conversation> _preview;
    private readonly DeleteRequest _delete;
    private readonly DateTimeOffset _now;
    private string _message = "";
    private bool _messageIsError;

    public ConversationCardViewModel(
        Conversation conversation,
        DateTimeOffset now,
        Action<Conversation> preview,
        DeleteRequest delete,
        ConversationMatch? match = null)
    {
        Conversation = conversation;
        Match = match;
        _now = now;
        _preview = preview;
        _delete = delete;

        ResumeCommand = new RelayCommand(Resume, () => conversation.CanResume);
        PreviewCommand = new RelayCommand(() => _preview(Conversation), () => conversation.HasTranscript);
        ExplorerCommand = new RelayCommand(() => Report(Launcher.OpenFolder(conversation.Cwd)));
        TerminalCommand = new RelayCommand(() => Report(Launcher.OpenTerminal(conversation.Cwd)));
        CopyIdCommand = new RelayCommand(() =>
            Report(Launcher.Copy(conversation.SessionId, "Session id")));
        CopyResumeCommand = new RelayCommand(() =>
            Report(Launcher.Copy($"claude --resume {conversation.SessionId}", "Command")));
        DeleteCommand = new AsyncRelayCommand(() => _delete(new[] { Conversation }, DeleteLabel));
    }

    public Conversation Conversation { get; }

    /// <summary>Why this card is on screen, or null in the overview.</summary>
    public ConversationMatch? Match { get; }

    public RelayCommand ResumeCommand { get; }
    public RelayCommand PreviewCommand { get; }
    public RelayCommand ExplorerCommand { get; }
    public RelayCommand TerminalCommand { get; }
    public RelayCommand CopyIdCommand { get; }
    public RelayCommand CopyResumeCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }

    /// <summary>Which conversation the confirmation is about - a title alone is not enough when
    /// three sessions on the same branch were all named after the same bug.</summary>
    private string DeleteLabel =>
        $"{Title}{Environment.NewLine}{FolderName}  ·  {AgeText}  ·  {SizeText}";

    // ---- identity ---------------------------------------------------------------------

    public string Title => Conversation.DisplayTitle;

    public string ShortId => Conversation.ShortId;

    public bool CanResume => Conversation.CanResume;

    public bool HasTranscript => Conversation.HasTranscript;

    /// <summary>
    /// Says out loud when a conversation is only half there. Claude Code prunes transcripts long
    /// before it prunes prompt history, so these are findable but not resumable - and a user
    /// clicking Resume on one deserves to know that before the click, not after.
    /// </summary>
    public string SourceNote => Conversation.HasTranscript
        ? ""
        : "transcript expired · prompts only";

    public bool IsHistoryOnly => !Conversation.HasTranscript;

    // ---- where and when ---------------------------------------------------------------

    public string AgeText => TimeText.Relative(Conversation.End, _now);

    public string WhenText
    {
        get
        {
            var start = Conversation.Start;
            var end = Conversation.End;
            if (start == default) return AgeText;

            var duration = TimeText.Duration(Conversation.Duration);
            return start.Date == end.Date
                ? $"{start:ddd d MMM  HH:mm}–{end:HH:mm}  ·  {duration}"
                : $"{start:ddd d MMM HH:mm} → {end:ddd d MMM HH:mm}  ·  {duration}";
        }
    }

    public bool HasBranch => Conversation.Branches.Count > 0;

    public string BranchText => Conversation.PrimaryBranch ?? ConversationGrouperLabels.NoBranch;

    /// <summary>A session that changed branches says so rather than hiding the second one.</summary>
    public string ExtraBranchText => Conversation.Branches.Count > 1
        ? $"+{Conversation.Branches.Count - 1} more"
        : "";

    public bool HasExtraBranches => Conversation.Branches.Count > 1;

    public string BranchTooltip => Conversation.Branches.Count == 0
        ? "No git branch recorded for this conversation"
        : string.Join("\n", Conversation.Branches);

    public string FolderName => Conversation.FolderName;

    public string FolderPath => string.IsNullOrWhiteSpace(Conversation.Cwd)
        ? "(folder unknown)"
        : Conversation.Cwd;

    public string SizeText
    {
        get
        {
            if (!Conversation.HasTranscript)
            {
                var n = Conversation.Prompts.Count;
                return n == 1 ? "1 prompt" : $"{n} prompts";
            }

            var msgs = Conversation.MessageCount;
            var size = Conversation.Bytes >= 1024 * 1024
                ? $"{Conversation.Bytes / 1024.0 / 1024.0:0.#} MB"
                : $"{Math.Max(1, Conversation.Bytes / 1024)} KB";
            return $"{msgs} msg{(msgs == 1 ? "" : "s")}  ·  {size}";
        }
    }

    // ---- why it matched ---------------------------------------------------------------

    public bool HasSnippet => !string.IsNullOrEmpty(SnippetText);

    /// <summary>The best hit, or the opening prompt when nothing is being searched for.</summary>
    public string SnippetText => Match?.Best.Snippet ?? OpeningPrompt;

    public int SnippetMatchStart => Match?.Best.SnippetMatchStart ?? 0;

    public int SnippetMatchLength => Match?.Best.SnippetMatchLength ?? 0;

    public string MatchLabel => Match is null
        ? (Conversation.Prompts.Count > 0 ? "opened with" : "")
        : LayerLabel(Match.Best.Layer);

    /// <summary>True when the only evidence is Claude's prose or command output.</summary>
    public bool IsDeepMatch => Match?.IsDeepOnly ?? false;

    /// <summary>"also in 3 prompts" - the hits beyond the one being shown.</summary>
    public string OtherHitsText
    {
        get
        {
            if (Match is null) return "";

            var parts = new List<string>();
            var best = Match.Best;

            if (best.Count > 1)
                parts.Add($"{best.Count} places in {LayerNoun(best.Layer)}");

            foreach (var hit in Match.Hits.Skip(1))
                parts.Add($"also {LayerLabel(hit.Layer)}");

            return parts.Count == 0 ? "" : string.Join("   ·   ", parts.Take(3));
        }
    }

    private string OpeningPrompt => Conversation.Prompts.Count == 0
        ? ""
        : TextSummary.OneLine(Conversation.Prompts[0].Text, 150);

    private static string LayerLabel(SearchLayer layer) => layer switch
    {
        SearchLayer.Title => "in the title",
        SearchLayer.SessionId => "session id",
        SearchLayer.Branch => "in the branch name",
        SearchLayer.Folder => "in the folder path",
        SearchLayer.Prompt => "you said",
        SearchLayer.Assistant => "Claude said",
        SearchLayer.Tool => "in command output",
        _ => "",
    };

    private static string LayerNoun(SearchLayer layer) => layer switch
    {
        SearchLayer.Prompt => "your prompts",
        SearchLayer.Assistant => "Claude's replies",
        SearchLayer.Tool => "command output",
        _ => "this conversation",
    };

    // ---- action feedback ---------------------------------------------------------------

    public string Message
    {
        get => _message;
        private set => Set(ref _message, value);
    }

    public bool MessageIsError
    {
        get => _messageIsError;
        private set => Set(ref _messageIsError, value);
    }

    public bool HasMessage => !string.IsNullOrEmpty(_message);

    private void Resume()
    {
        Report(Launcher.Resume(Conversation.SessionId, Conversation.Cwd));
    }

    private void Report(Launcher.Outcome outcome)
    {
        Message = outcome.Message;
        MessageIsError = outcome.IsError;
        OnPropertyChanged(nameof(HasMessage));
    }
}

/// <summary>Labels shared between the card and the grouping, kept in one place.</summary>
public static class ConversationGrouperLabels
{
    public const string NoBranch = "no branch";
}
