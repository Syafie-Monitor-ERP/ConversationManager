using System.IO;
using System.Collections.Concurrent;
using System.Diagnostics;
using ConversationManager.Models;

namespace ConversationManager.Services;

/// <summary>What one scan of the .claude directory found.</summary>
public sealed class ConversationIndex
{
    public IReadOnlyList<Conversation> Conversations { get; init; } = Array.Empty<Conversation>();

    /// <summary>Conversations with a transcript on disk: resumable and previewable.</summary>
    public int TranscriptCount => Conversations.Count(c => c.HasTranscript);

    /// <summary>Conversations only history.jsonl still remembers.</summary>
    public int HistoryOnlyCount => Conversations.Count(c => !c.HasTranscript);

    public long TranscriptBytes => Conversations.Sum(c => c.Bytes);

    public TimeSpan Elapsed { get; init; }

    /// <summary>Files that were reused from the cache rather than reparsed.</summary>
    public int CacheHits { get; init; }

    /// <summary>Set when the .claude directory is not where it was expected.</summary>
    public string? Problem { get; init; }

    public DateTimeOffset? Oldest => Conversations.Count == 0
        ? null
        : Conversations.Min(c => c.Start);

    /// <summary>
    /// The same scan with some conversations gone. What a delete leaves behind: the store on disk
    /// has changed in a way that is exactly known, so re-reading all of it to find that out would
    /// only cost the user a second and their scroll position.
    /// </summary>
    public ConversationIndex Without(IReadOnlySet<string> sessionIds) => new()
    {
        Conversations = Conversations.Where(c => !sessionIds.Contains(c.SessionId)).ToList(),
        Elapsed = Elapsed,
        CacheHits = CacheHits,
        Problem = Problem,
    };
}

/// <summary>
/// Builds the searchable index from the two places Claude Code records the past: the transcripts
/// under projects\, and the prompt history in history.jsonl. Neither is complete on its own -
/// transcripts have the detail, history has the reach - so both are read and merged by session id.
/// </summary>
public sealed class TranscriptStore
{
    private readonly AppConfig _config;
    private readonly IndexCache _cache;

    public TranscriptStore(AppConfig config, IndexCache? cache = null)
    {
        _config = config;
        _cache = cache ?? IndexCache.Load();
    }

    public Task<ConversationIndex> LoadAsync(CancellationToken token = default) =>
        Task.Run(() => Load(token), token);

    public ConversationIndex Load(CancellationToken token = default)
    {
        var clock = Stopwatch.StartNew();

        if (!Directory.Exists(_config.ClaudeHome))
        {
            return new ConversationIndex
            {
                Elapsed = clock.Elapsed,
                Problem = $"No .claude directory at {_config.ClaudeHome}. " +
                          "Set claudeHome in config.json next to the exe.",
            };
        }

        var (parsed, cacheHits) = LoadTranscripts(token);
        var conversations = MergeHistory(parsed, HistoryReader.Read(_config.HistoryFile));

        if (_config.MaxAgeDays > 0)
        {
            var cutoff = DateTimeOffset.Now.AddDays(-_config.MaxAgeDays);
            conversations = conversations.Where(c => c.End >= cutoff).ToList();
        }

        // Newest first is the useful default at every level of the UI.
        conversations = conversations.OrderByDescending(c => c.End).ToList();

        SaveCache(parsed);

        return new ConversationIndex
        {
            Conversations = conversations,
            Elapsed = clock.Elapsed,
            CacheHits = cacheHits,
            Problem = Directory.Exists(_config.ProjectsDir)
                ? null
                : $"No transcripts: {_config.ProjectsDir} does not exist",
        };
    }

    private (List<Conversation> Parsed, int CacheHits) LoadTranscripts(CancellationToken token)
    {
        if (!Directory.Exists(_config.ProjectsDir)) return (new List<Conversation>(), 0);

        var files = Directory.EnumerateFiles(_config.ProjectsDir, "*.jsonl", SearchOption.AllDirectories)
            .ToList();

        var results = new ConcurrentBag<Conversation>();
        var hits = 0;

        // Files are independent, and parsing is the only slow part of a scan.
        Parallel.ForEach(files, new ParallelOptions { CancellationToken = token }, file =>
        {
            FileInfo info;
            try
            {
                info = new FileInfo(file);
            }
            catch (Exception)
            {
                return;
            }

            var cached = _cache.Match(file, info.Length, info.LastWriteTimeUtc);
            if (cached is not null)
            {
                Interlocked.Increment(ref hits);
                results.Add(cached.ToConversation());
                return;
            }

            var parsed = TranscriptParser.ParseFile(file);
            if (parsed is not null) results.Add(parsed);
        });

        return (results.ToList(), hits);
    }

    /// <summary>
    /// Folds history.jsonl into the transcripts. Two jobs: give conversations whose transcript
    /// was pruned an entry of their own, and give surviving ones back any prompt that was
    /// compacted out of the transcript but is still in history.
    /// </summary>
    public static List<Conversation> MergeHistory(
        IEnumerable<Conversation> transcripts, IReadOnlyList<HistoryEntry> history)
    {
        var bySession = new Dictionary<string, Conversation>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in transcripts) bySession[c.SessionId] = c;

        foreach (var group in history.GroupBy(h => h.SessionId, StringComparer.OrdinalIgnoreCase))
        {
            var entries = group.OrderBy(h => h.When).ToList();

            if (bySession.TryGetValue(group.Key, out var existing))
            {
                var known = new HashSet<string>(
                    existing.Prompts.Select(p => Normalise(p.Text)), StringComparer.OrdinalIgnoreCase);

                var extra = entries
                    .Where(h => known.Add(Normalise(h.Text)))
                    .Select(h => new Prompt(h.When, h.Text))
                    .ToList();

                if (extra.Count == 0) continue;

                bySession[group.Key] = Clone(existing, existing.Prompts
                    .Concat(extra)
                    .OrderBy(p => p.When)
                    .ToList());
                continue;
            }

            // No transcript: everything that is left is what was typed, and where.
            var project = entries
                .GroupBy(h => h.Project, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .FirstOrDefault() ?? "";

            bySession[group.Key] = new Conversation
            {
                SessionId = group.Key,
                TranscriptPath = null,
                Cwd = project,
                Cwds = new[] { project },
                Start = entries[0].When,
                End = entries[^1].When,
                MessageCount = 0,
                Prompts = entries.Select(h => new Prompt(h.When, h.Text)).ToList(),
            };
        }

        return bySession.Values.ToList();
    }

    private static string Normalise(string text) => TextSummary.OneLine(text, int.MaxValue);

    private static Conversation Clone(Conversation source, IReadOnlyList<Prompt> prompts) => new()
    {
        SessionId = source.SessionId,
        TranscriptPath = source.TranscriptPath,
        Title = source.Title,
        Cwd = source.Cwd,
        Cwds = source.Cwds,
        Branches = source.Branches,
        Start = source.Start,
        End = source.End,
        MessageCount = source.MessageCount,
        Bytes = source.Bytes,
        Prompts = prompts,
    };

    private void SaveCache(List<Conversation> parsed)
    {
        try
        {
            var entries = new List<CachedConversation>(parsed.Count);
            foreach (var c in parsed)
            {
                if (c.TranscriptPath is null) continue;
                var ticks = File.Exists(c.TranscriptPath)
                    ? new FileInfo(c.TranscriptPath).LastWriteTimeUtc.Ticks
                    : 0;
                entries.Add(CachedConversation.From(c, ticks));
            }

            _cache.Entries = entries;
            _cache.Save();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"cache write skipped: {ex.Message}");
        }
    }
}
