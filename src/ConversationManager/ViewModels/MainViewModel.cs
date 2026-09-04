using System.Collections.ObjectModel;
using ConversationManager.Models;
using ConversationManager.Platform;
using ConversationManager.Services;

namespace ConversationManager.ViewModels;

/// <summary>
/// Holds the index, the current query, and the two ways results are shown: ranked hits while
/// searching, grouped cards when the box is empty.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    /// <summary>
    /// Below this many shallow hits, the deep scan is worth running unasked - the user clearly has
    /// not found what they wanted yet. Above it, re-reading every transcript on each keystroke
    /// buys nothing, so the noisy layers stay closed until the scope is widened by hand.
    /// </summary>
    private const int DeepScanThreshold = 20;

    /// <summary>Long enough that typing does not start a scan per character.</summary>
    private static readonly TimeSpan DeepScanDelay = TimeSpan.FromMilliseconds(450);

    /// <summary>
    /// Deleted transcripts go to the Recycle Bin, never straight off the disk. A conversation is
    /// often the only record of how something was worked out, and a mis-clicked group delete
    /// would otherwise take twenty of them with no way back.
    /// </summary>
    private const DeleteMode Removal = DeleteMode.Recycle;

    private readonly Action<Conversation> _preview;
    private CancellationTokenSource? _deepScan;

    private string _searchText = "";
    private string _statusText = "";
    private bool _isScanning;
    private bool _showDeep;
    private int _deepOnlyCount;
    private bool _deepScanRunning;

    private ConversationIndex _index = new();
    private List<ConversationMatch> _shallow = new();
    private List<ConversationMatch> _deepOnly = new();

    public MainViewModel(Action<Conversation> preview, AppConfig? config = null)
    {
        _preview = preview;
        Config = config ?? AppConfig.Load();

        Groups = new ObservableCollection<ConversationGroupViewModel>();
        Results = new ObservableCollection<ConversationCardViewModel>();

        ReloadCommand = new AsyncRelayCommand(ReloadAsync, () => !IsScanning);
        ClearSearchCommand = new RelayCommand(() => SearchText = "");
        ShowDeepCommand = new RelayCommand(() =>
        {
            _showDeep = true;
            RebuildResults();
        });
        DeleteResultsCommand = new AsyncRelayCommand(
            () => DeleteAsync(
                Results.Select(r => r.Conversation).ToList(),
                $"Everything found for “{_searchText.Trim()}”"),
            () => Results.Count > 0);

        SetScopeCommand = new RelayCommand(p =>
            SetScope(p is "everything" ? SearchScope.Everything : SearchScope.Prompts));
        SetGroupCommand = new RelayCommand(p => SetGroupMode(p switch
        {
            "folder" => GroupMode.Folder,
            "recent" => GroupMode.Recent,
            _ => GroupMode.Branch,
        }));
    }

    public AppConfig Config { get; }

    public ObservableCollection<ConversationGroupViewModel> Groups { get; }

    /// <summary>Ranked matches for the current query, best first.</summary>
    public ObservableCollection<ConversationCardViewModel> Results { get; }

    public AsyncRelayCommand ReloadCommand { get; }
    public RelayCommand ClearSearchCommand { get; }
    public RelayCommand ShowDeepCommand { get; }

    /// <summary>Deletes everything the current query turned up - the search box doubling as a
    /// way to pick a set, which is how anyone would use it once single deletes exist.</summary>
    public AsyncRelayCommand DeleteResultsCommand { get; }

    public bool CanDeleteResults => Results.Count > 0;

    public string DeleteResultsText => $"Delete all {Results.Count}";

    // ---- query -------------------------------------------------------------------------

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!Set(ref _searchText, value)) return;
            _showDeep = false;
            OnPropertyChanged(nameof(IsSearching));
            UpdateSearch();
        }
    }

    public bool IsSearching => !string.IsNullOrWhiteSpace(_searchText);

    // ---- the two switches ---------------------------------------------------------------
    //
    // Exposed as one flag per position rather than a selected index, because the segmented
    // buttons in the header each need to know whether they are the active one.

    public bool IsScopePrompts => Config.Scope == SearchScope.Prompts;

    public bool IsScopeEverything => Config.Scope == SearchScope.Everything;

    public RelayCommand SetScopeCommand { get; private set; } = null!;

    public bool IsGroupByBranch => Config.GroupMode == GroupMode.Branch;

    public bool IsGroupByFolder => Config.GroupMode == GroupMode.Folder;

    public bool IsGroupByRecent => Config.GroupMode == GroupMode.Recent;

    public RelayCommand SetGroupCommand { get; private set; } = null!;

    private void SetScope(SearchScope scope)
    {
        if (Config.Scope == scope) return;
        Config.Scope = scope;
        SaveConfig();
        OnPropertyChanged(nameof(IsScopePrompts));
        OnPropertyChanged(nameof(IsScopeEverything));
        UpdateSearch();
    }

    private void SetGroupMode(GroupMode mode)
    {
        if (Config.GroupMode == mode) return;
        Config.GroupMode = mode;
        SaveConfig();
        OnPropertyChanged(nameof(IsGroupByBranch));
        OnPropertyChanged(nameof(IsGroupByFolder));
        OnPropertyChanged(nameof(IsGroupByRecent));
        RebuildGroups();
    }

    // ---- state -------------------------------------------------------------------------

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (Set(ref _isScanning, value)) ReloadCommand.RaiseCanExecuteChanged();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string Problem => _index.Problem ?? "";

    public bool HasProblem => !string.IsNullOrEmpty(Problem);

    public string SearchSummary
    {
        get
        {
            if (!IsSearching) return "";

            var query = _searchText.Trim();
            if (Results.Count == 0)
            {
                if (_deepScanRunning) return $"Searching transcripts for \"{query}\"...";
                return _deepOnlyCount > 0
                    ? $"Nothing named \"{query}\" - but it appears in command output"
                    : $"No conversation matches \"{query}\"";
            }

            var n = Results.Count;
            var head = $"{n} conversation{(n == 1 ? "" : "s")}";

            var best = _shallow.Count > 0 ? _shallow[0] : null;
            var where = best?.Best.Layer switch
            {
                SearchLayer.Branch => " - best match is a branch name",
                SearchLayer.Title => " - best match is a title",
                SearchLayer.Prompt => " - best match is something you typed",
                _ => "",
            };

            return head + where;
        }
    }

    /// <summary>The footer offering the noisy layers, worded so its weakness is obvious.</summary>
    public string DeepSummary
    {
        get
        {
            if (_showDeep || _deepOnlyCount == 0) return "";
            var n = _deepOnlyCount;
            return n == 1
                ? "1 more conversation mentions it in replies or command output"
                : $"{n} more conversations mention it in replies or command output";
        }
    }

    public bool HasDeepSummary => !string.IsNullOrEmpty(DeepSummary);

    // ---- loading -----------------------------------------------------------------------

    public async Task ReloadAsync()
    {
        IsScanning = true;
        StatusText = "Reading conversations...";
        try
        {
            var store = new TranscriptStore(Config);
            _index = await store.LoadAsync();

            StatusText = Describe(_index);
            OnPropertyChanged(nameof(Problem));
            OnPropertyChanged(nameof(HasProblem));

            RebuildGroups();
            UpdateSearch();
        }
        catch (Exception ex)
        {
            StatusText = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    private static string Describe(ConversationIndex index)
    {
        var total = index.Conversations.Count;
        if (total == 0) return "No conversations found";

        var parts = new List<string>
        {
            $"{total} conversation{(total == 1 ? "" : "s")}",
            $"{index.TranscriptCount} with transcripts",
        };

        if (index.HistoryOnlyCount > 0)
            parts.Add($"{index.HistoryOnlyCount} prompts only");

        if (index.Oldest is { } oldest)
            parts.Add($"back to {oldest:d MMM yyyy}");

        parts.Add($"read in {index.Elapsed.TotalSeconds:0.0}s" +
                  (index.CacheHits > 0 ? $" ({index.CacheHits} cached)" : ""));

        return string.Join("   ·   ", parts);
    }

    // ---- searching ---------------------------------------------------------------------

    private void UpdateSearch()
    {
        _deepScan?.Cancel();
        _deepOnly = new List<ConversationMatch>();
        _deepOnlyCount = 0;
        _deepScanRunning = false;

        if (!IsSearching)
        {
            _shallow = new List<ConversationMatch>();
            RebuildResults();
            return;
        }

        _shallow = ConversationSearch.Search(_index.Conversations, _searchText);
        RebuildResults();

        if (Config.Scope == SearchScope.Everything || _shallow.Count < DeepScanThreshold)
            StartDeepScan(_searchText);
    }

    /// <summary>
    /// Reads the transcripts for the layers the index does not hold. Debounced and cancellable,
    /// because it is the one part of a search that touches disk.
    /// </summary>
    private void StartDeepScan(string query)
    {
        var cts = new CancellationTokenSource();
        _deepScan = cts;
        _deepScanRunning = true;
        OnPropertyChanged(nameof(SearchSummary));

        _ = RunDeepScanAsync(query, cts.Token);
    }

    private async Task RunDeepScanAsync(string query, CancellationToken token)
    {
        try
        {
            await Task.Delay(DeepScanDelay, token);

            var alreadyMatched = _shallow
                .Select(m => m.Conversation.SessionId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var hits = await DeepSearcher.ScanAsync(_index.Conversations, query, token);
            if (token.IsCancellationRequested) return;

            var deepOnly = new List<ConversationMatch>();
            foreach (var conversation in _index.Conversations)
            {
                if (!hits.TryGetValue(conversation.SessionId, out var layers)) continue;
                if (alreadyMatched.Contains(conversation.SessionId)) continue;

                deepOnly.Add(new ConversationMatch
                {
                    Conversation = conversation,
                    Hits = layers,
                    Score = layers[0].Score,
                });
            }

            _deepOnly = ConversationSearch.Order(deepOnly);
            _deepOnlyCount = _deepOnly.Count;
            _deepScanRunning = false;
            RebuildResults();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer query; the newer one owns the results now.
        }
        catch (Exception ex)
        {
            _deepScanRunning = false;
            StatusText = $"Deep search failed: {ex.Message}";
        }
    }

    private void RebuildResults()
    {
        Results.Clear();

        var now = DateTimeOffset.Now;

        foreach (var match in _shallow)
            Results.Add(new ConversationCardViewModel(
                match.Conversation, now, _preview, DeleteAsync, match));

        // In the wider scope the weak hits belong in the list; otherwise they wait behind a click.
        if (Config.Scope == SearchScope.Everything || _showDeep)
        {
            foreach (var match in _deepOnly)
                Results.Add(new ConversationCardViewModel(
                    match.Conversation, now, _preview, DeleteAsync, match));
        }

        OnPropertyChanged(nameof(SearchSummary));
        OnPropertyChanged(nameof(DeepSummary));
        OnPropertyChanged(nameof(HasDeepSummary));
        OnPropertyChanged(nameof(CanDeleteResults));
        OnPropertyChanged(nameof(DeleteResultsText));
        DeleteResultsCommand.RaiseCanExecuteChanged();
    }

    private void RebuildGroups()
    {
        Groups.Clear();
        var now = DateTimeOffset.Now;

        foreach (var group in ConversationGrouper.Group(_index.Conversations, Config.GroupMode, now))
        {
            Groups.Add(new ConversationGroupViewModel(
                group,
                group.Conversations.Select(c =>
                    new ConversationCardViewModel(c, now, _preview, DeleteAsync)),
                DeleteAsync,
                Config.GroupMode == GroupMode.Branch));
        }
    }

    // ---- deleting ------------------------------------------------------------------------

    /// <summary>
    /// The one destructive path in the app, shared by a card's Delete, a group heading's Delete
    /// all, and Delete all results. Everything asks first, everything reports afterwards, and
    /// nothing is removed from the screen that was not removed from disk.
    /// </summary>
    private async Task DeleteAsync(IReadOnlyList<Conversation> targets, string what)
    {
        if (targets.Count == 0 || IsScanning) return;

        var plan = DeletePlan.For(targets);
        if (!Dialogs.ConfirmDelete(plan, what, Removal)) return;

        IsScanning = true;
        StatusText = $"Deleting {plan.Count}...";
        try
        {
            // Off the UI thread: the Recycle Bin is a shell call per file, and a branch with
            // thirty sessions under it would otherwise freeze the window mid-click.
            var report = await Task.Run(() => ConversationDeleter.Delete(targets, Config, Removal));

            DropFromIndex(report.RemovedSessionIds);
            StatusText = report.Summary;
        }
        catch (Exception ex)
        {
            StatusText = $"Delete failed: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    /// <summary>
    /// Takes the deleted conversations out of the index and both views. A full rescan would do
    /// the same job and cost a second and the scroll position, for no new information: what
    /// changed on disk is exactly what was just deleted.
    /// </summary>
    private void DropFromIndex(IReadOnlyCollection<string> sessionIds)
    {
        if (sessionIds.Count == 0) return;

        var gone = sessionIds.ToHashSet(StringComparer.OrdinalIgnoreCase);

        _index = _index.Without(gone);
        _shallow = _shallow.Where(m => !gone.Contains(m.Conversation.SessionId)).ToList();
        _deepOnly = _deepOnly.Where(m => !gone.Contains(m.Conversation.SessionId)).ToList();
        _deepOnlyCount = _deepOnly.Count;

        RebuildResults();
        RebuildGroups();
    }

    private void SaveConfig()
    {
        try
        {
            Config.Save();
        }
        catch (Exception ex)
        {
            // Settings are a convenience; failing to persist one must not interrupt a search.
            System.Diagnostics.Debug.WriteLine($"config save failed: {ex.Message}");
        }
    }
}
