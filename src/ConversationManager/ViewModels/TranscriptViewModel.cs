using System.Collections.ObjectModel;
using ConversationManager.Models;
using ConversationManager.Platform;
using ConversationManager.Services;

namespace ConversationManager.ViewModels;

/// <summary>One turn as shown in the preview, with the find-box match picked out.</summary>
public sealed class TurnViewModel : ObservableObject
{
    private int _matchStart = -1;
    private int _matchLength;

    public TurnViewModel(TranscriptTurn turn)
    {
        Turn = turn;
    }

    public TranscriptTurn Turn { get; }

    public string Text => Turn.Text;

    public string RoleLabel => Turn.Role switch
    {
        TurnRole.You => "you",
        TurnRole.Claude => "claude",
        TurnRole.Thinking => "thinking",
        _ => Turn.ToolName?.ToLowerInvariant() ?? "tool",
    };

    public bool IsYou => Turn.Role == TurnRole.You;
    public bool IsTool => Turn.Role == TurnRole.Tool;
    public bool IsThinking => Turn.Role == TurnRole.Thinking;

    public string TimeText => Turn.When == default ? "" : Turn.When.ToString("HH:mm");

    public string? Result => Turn.Result;
    public bool HasResult => Turn.HasResult;

    public int MatchStart
    {
        get => _matchStart;
        private set => Set(ref _matchStart, value);
    }

    public int MatchLength
    {
        get => _matchLength;
        private set => Set(ref _matchLength, value);
    }

    public bool IsMatch => _matchLength > 0;

    /// <summary>Points the highlight at the first occurrence of a term, or clears it.</summary>
    public bool Locate(string? term)
    {
        if (string.IsNullOrEmpty(term))
        {
            MatchStart = -1;
            MatchLength = 0;
            OnPropertyChanged(nameof(IsMatch));
            return false;
        }

        var idx = Text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        MatchStart = idx;
        MatchLength = idx >= 0 ? term.Length : 0;
        OnPropertyChanged(nameof(IsMatch));
        return idx >= 0;
    }
}

/// <summary>
/// The read-only view of one conversation: what was said, what was run, and what came back -
/// enough to recover the context of a piece of work without resuming the session.
/// </summary>
public sealed class TranscriptViewModel : ObservableObject
{
    private readonly List<TurnViewModel> _all = new();
    private string _findText = "";
    private bool _onlyMatches;
    private bool _isLoading;
    private string _statusText = "";
    private string _message = "";
    private bool _messageIsError;

    public TranscriptViewModel(Conversation conversation)
    {
        Conversation = conversation;
        Turns = new ObservableCollection<TurnViewModel>();

        ResumeCommand = new RelayCommand(
            () => Report(Launcher.Resume(conversation.SessionId, conversation.Cwd)),
            () => conversation.CanResume);
        ExplorerCommand = new RelayCommand(() => Report(Launcher.OpenFolder(conversation.Cwd)));
        CopyIdCommand = new RelayCommand(() => Report(Launcher.Copy(conversation.SessionId, "Session id")));
        ClearFindCommand = new RelayCommand(() => FindText = "");
    }

    public Conversation Conversation { get; }

    public ObservableCollection<TurnViewModel> Turns { get; }

    public RelayCommand ResumeCommand { get; }
    public RelayCommand ExplorerCommand { get; }
    public RelayCommand CopyIdCommand { get; }
    public RelayCommand ClearFindCommand { get; }

    public string Title => Conversation.DisplayTitle;

    public string SubtitleText
    {
        get
        {
            var parts = new List<string>();
            if (Conversation.PrimaryBranch is { } branch) parts.Add(branch);
            if (!string.IsNullOrWhiteSpace(Conversation.Cwd)) parts.Add(Conversation.Cwd);
            parts.Add(Conversation.Start.ToString("ddd d MMM yyyy  HH:mm"));
            return string.Join("   ·   ", parts);
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => Set(ref _isLoading, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string FindText
    {
        get => _findText;
        set
        {
            if (!Set(ref _findText, value)) return;
            ApplyFind();
        }
    }

    public bool OnlyMatches
    {
        get => _onlyMatches;
        set
        {
            if (!Set(ref _onlyMatches, value)) return;
            ApplyFind();
        }
    }

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

    public async Task LoadAsync(string? initialFind = null)
    {
        if (Conversation.TranscriptPath is null)
        {
            // Prompt history is all that is left of this one; show it rather than an empty window.
            foreach (var prompt in Conversation.Prompts)
                _all.Add(new TurnViewModel(new TranscriptTurn
                {
                    Role = TurnRole.You,
                    Text = prompt.Text,
                    When = prompt.When,
                }));

            StatusText = "Transcript expired - showing prompt history only";
            FindText = initialFind ?? "";
            ApplyFind();
            return;
        }

        IsLoading = true;
        StatusText = "Reading transcript...";
        try
        {
            var turns = await TranscriptReader.ReadAsync(Conversation.TranscriptPath);
            _all.Clear();
            foreach (var turn in turns) _all.Add(new TurnViewModel(turn));

            var said = _all.Count(t => t.Turn.Role is TurnRole.You or TurnRole.Claude);
            var tools = _all.Count(t => t.IsTool);
            StatusText = $"{said} messages   ·   {tools} tool call{(tools == 1 ? "" : "s")}";

            if (!string.IsNullOrWhiteSpace(initialFind)) _findText = initialFind!;
            OnPropertyChanged(nameof(FindText));
            ApplyFind();
        }
        catch (Exception ex)
        {
            StatusText = $"Could not read the transcript: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplyFind()
    {
        var term = _findText.Trim();
        var matches = 0;

        foreach (var turn in _all)
            if (turn.Locate(term)) matches++;

        Turns.Clear();
        foreach (var turn in _all)
        {
            if (_onlyMatches && term.Length > 0 && !turn.IsMatch) continue;
            Turns.Add(turn);
        }

        if (term.Length > 0)
            StatusText = matches == 0
                ? $"\"{term}\" is not in this conversation"
                : $"{matches} turn{(matches == 1 ? "" : "s")} mention \"{term}\"";
    }

    private void Report(Launcher.Outcome outcome)
    {
        Message = outcome.Message;
        MessageIsError = outcome.IsError;
        OnPropertyChanged(nameof(HasMessage));
    }
}
