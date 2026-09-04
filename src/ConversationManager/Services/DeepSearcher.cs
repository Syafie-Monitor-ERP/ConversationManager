using System.IO;
using System.Collections.Concurrent;
using System.Text.Json;
using ConversationManager.Models;

namespace ConversationManager.Services;

/// <summary>
/// Searches the parts of a transcript the index leaves behind: Claude's replies, and the tool
/// input and output that makes up most of the file.
///
/// This goes back to disk on purpose. Those layers are 97% of the bytes, they answer far fewer
/// questions than the prompts do, and holding them in memory would cost hundreds of megabytes to
/// make a search that mostly returns noise faster.
/// </summary>
public static class DeepSearcher
{
    /// <summary>
    /// Enough matched lines in one file to know the term is in there. Counting all of them in a
    /// build log with ten thousand hits changes nothing on screen.
    /// </summary>
    private const int MaxHitsPerFile = 200;

    public static Task<Dictionary<string, List<FieldHit>>> ScanAsync(
        IEnumerable<Conversation> conversations, string query, CancellationToken token = default)
    {
        var terms = QueryMatcher.Terms(query);
        var targets = conversations.Where(c => c.TranscriptPath is not null).ToList();

        if (terms.Length == 0 || targets.Count == 0)
            return Task.FromResult(new Dictionary<string, List<FieldHit>>());

        return Task.Run(() =>
        {
            var found = new ConcurrentDictionary<string, List<FieldHit>>();

            Parallel.ForEach(
                targets,
                new ParallelOptions { CancellationToken = token },
                conversation =>
                {
                    var hits = ScanFile(conversation.TranscriptPath!, terms, token);
                    if (hits.Count > 0) found[conversation.SessionId] = hits;
                });

            return new Dictionary<string, List<FieldHit>>(found, StringComparer.OrdinalIgnoreCase);
        }, token);
    }

    /// <summary>Hits in one transcript, one entry per layer, or empty when a term is missing.</summary>
    public static List<FieldHit> ScanFile(string path, string[] terms, CancellationToken token = default)
    {
        var counts = new Dictionary<(SearchLayer, string), int>();
        var samples = new Dictionary<(SearchLayer, string), FieldHit>();
        var matchedLines = 0;

        IEnumerable<string> lines;
        try
        {
            lines = File.ReadLines(path);
        }
        catch (Exception)
        {
            return new List<FieldHit>();
        }

        try
        {
            foreach (var line in lines)
            {
                token.ThrowIfCancellationRequested();
                if (matchedLines >= MaxHitsPerFile) break;
                if (line.Length == 0) continue;

                // The cheap gate: scan the raw JSON text first, and only parse the line if a
                // term is somewhere in it. Almost every line fails this and costs one scan.
                var present = terms.Where(t => line.Contains(t, StringComparison.OrdinalIgnoreCase))
                                   .ToArray();
                if (present.Length == 0) continue;

                matchedLines++;
                Classify(line, present, counts, samples);
            }
        }
        catch (OperationCanceledException)
        {
            return new List<FieldHit>();
        }
        catch (Exception)
        {
            // A file being written while it is read is normal; keep whatever was found.
        }

        // Same AND rule as the shallow search: a term nobody saw means this is not a match.
        foreach (var term in terms)
        {
            if (!counts.Keys.Any(k => string.Equals(k.Item2, term, StringComparison.OrdinalIgnoreCase)))
                return new List<FieldHit>();
        }

        var result = new List<FieldHit>();
        foreach (var ((layer, term), count) in counts)
        {
            var sample = samples[(layer, term)];
            result.Add(new FieldHit
            {
                Layer = layer,
                Kind = sample.Kind,
                Term = term,
                Snippet = sample.Snippet,
                SnippetMatchStart = sample.SnippetMatchStart,
                SnippetMatchLength = sample.SnippetMatchLength,
                Count = count,
            });
        }

        // One hit per layer, strongest first - the caller shows the best and counts the rest.
        return result
            .GroupBy(h => h.Layer)
            .Select(g => g.OrderByDescending(h => h.Score).First())
            .OrderByDescending(h => h.Score)
            .ToList();
    }

    /// <summary>
    /// Works out whether a matched line is Claude speaking or a tool dumping output, and pulls a
    /// readable snippet out of it. Text blocks are preferred: a hit inside a 200KB file listing
    /// is real, but quoting it teaches the user nothing.
    /// </summary>
    private static void Classify(
        string line,
        string[] terms,
        Dictionary<(SearchLayer, string), int> counts,
        Dictionary<(SearchLayer, string), FieldHit> samples)
    {
        SearchLayer layer;
        string? prose = null;

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;

            if (type == "assistant")
            {
                prose = AssistantText(root);
                // An assistant record whose hit is only in a tool call is a tool hit, not prose.
                layer = prose is not null ? SearchLayer.Assistant : SearchLayer.Tool;
            }
            else
            {
                layer = SearchLayer.Tool;
            }
        }
        catch (JsonException)
        {
            layer = SearchLayer.Tool;
        }

        foreach (var term in terms)
        {
            var key = (layer, term);
            counts[key] = counts.GetValueOrDefault(key) + 1;
            if (samples.ContainsKey(key)) continue;

            var source = prose;
            int start;
            if (source is not null)
            {
                start = source.IndexOf(term, StringComparison.OrdinalIgnoreCase);
                if (start < 0)
                {
                    // The term is in the record but not in the prose - fall back to the raw line.
                    source = null;
                    start = -1;
                }
            }
            else
            {
                start = -1;
            }

            if (source is null)
            {
                source = line;
                start = source.IndexOf(term, StringComparison.OrdinalIgnoreCase);
                if (start < 0) continue;
            }

            var (snippet, matchStart, matchLength) =
                TextSummary.Snippet(source, start, term.Length);

            samples[key] = new FieldHit
            {
                Layer = layer,
                Kind = MatchKind.Substring,
                Term = term,
                Snippet = snippet,
                SnippetMatchStart = matchStart,
                SnippetMatchLength = matchLength,
            };
        }
    }

    /// <summary>The words Claude actually said in one assistant record, if any.</summary>
    private static string? AssistantText(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message) ||
            message.ValueKind != JsonValueKind.Object) return null;
        if (!message.TryGetProperty("content", out var content)) return null;

        if (content.ValueKind == JsonValueKind.String) return content.GetString();
        if (content.ValueKind != JsonValueKind.Array) return null;

        List<string>? parts = null;
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object) continue;
            if (!block.TryGetProperty("type", out var bt) || bt.GetString() != "text") continue;
            if (!block.TryGetProperty("text", out var text) ||
                text.ValueKind != JsonValueKind.String) continue;
            (parts ??= new List<string>()).Add(text.GetString() ?? "");
        }

        return parts is null ? null : string.Join("\n", parts);
    }
}
