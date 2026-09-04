using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ConversationManager.Models;

namespace ConversationManager.Services;

/// <summary>
/// A parsed transcript, flattened for storage. A DTO rather than the model itself so the cache
/// file holds only raw facts - no derived properties to go stale, and no shape change every time
/// the model grows a convenience getter.
/// </summary>
public sealed class CachedConversation
{
    public string SessionId { get; set; } = "";
    public string Path { get; set; } = "";
    public long Length { get; set; }
    public long ModifiedTicks { get; set; }
    public string? Title { get; set; }
    public string Cwd { get; set; } = "";
    public List<string> Cwds { get; set; } = new();
    public List<string> Branches { get; set; } = new();
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }
    public int MessageCount { get; set; }
    public List<CachedPrompt> Prompts { get; set; } = new();

    public Conversation ToConversation() => new()
    {
        SessionId = SessionId,
        TranscriptPath = Path,
        Title = Title,
        Cwd = Cwd,
        Cwds = Cwds,
        Branches = Branches,
        Start = Start,
        End = End,
        MessageCount = MessageCount,
        Bytes = Length,
        Prompts = Prompts.Select(p => new Prompt(p.When, p.Text)).ToList(),
    };

    public static CachedConversation From(Conversation c, long modifiedTicks) => new()
    {
        SessionId = c.SessionId,
        Path = c.TranscriptPath ?? "",
        Length = c.Bytes,
        ModifiedTicks = modifiedTicks,
        Title = c.Title,
        Cwd = c.Cwd,
        Cwds = c.Cwds.ToList(),
        Branches = c.Branches.ToList(),
        Start = c.Start,
        End = c.End,
        MessageCount = c.MessageCount,
        Prompts = c.Prompts.Select(p => new CachedPrompt { When = p.When, Text = p.Text }).ToList(),
    };
}

public sealed class CachedPrompt
{
    public DateTimeOffset When { get; set; }
    public string Text { get; set; } = "";
}

/// <summary>
/// Remembers what each transcript parsed to, so a rescan only reads files that actually changed.
///
/// Today the whole store parses in about a second and the cache is barely noticeable. It earns
/// its place later: the transcripts here grow by roughly 60MB a month, and a year of them would
/// otherwise be re-read in full on every launch.
/// </summary>
public sealed class IndexCache
{
    private const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public List<CachedConversation> Entries { get; set; } = new();

    [JsonIgnore]
    public string? LoadedFrom { get; private set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "index-cache.json");

    public static IndexCache Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var cache = JsonSerializer.Deserialize<IndexCache>(File.ReadAllText(path), JsonOptions);
                if (cache is not null && cache.Version == CurrentVersion)
                {
                    cache.LoadedFrom = path;
                    return cache;
                }
            }
        }
        catch (Exception ex)
        {
            // A stale or corrupt cache is never worth a crash - reparsing is only slow, not wrong.
            System.Diagnostics.Debug.WriteLine($"index cache load failed: {ex.Message}");
        }
        return new IndexCache { LoadedFrom = path };
    }

    public void Save(string? path = null)
    {
        path ??= LoadedFrom ?? DefaultPath;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"index cache save failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The cached parse of a file, but only when the file on disk is byte-for-byte the one that
    /// was parsed. Size alone would miss an in-place edit; the write time alone would miss a
    /// restored copy, so both have to agree.
    /// </summary>
    public CachedConversation? Match(string path, long length, DateTime modifiedUtc)
    {
        _byPath ??= Entries
            .GroupBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

        if (!_byPath.TryGetValue(path, out var entry)) return null;
        return entry.Length == length && entry.ModifiedTicks == modifiedUtc.Ticks ? entry : null;
    }

    private Dictionary<string, CachedConversation>? _byPath;
}
