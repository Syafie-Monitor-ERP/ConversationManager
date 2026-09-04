using System.IO;
using System.Globalization;
using System.Text.Json;
using ConversationManager.Models;

namespace ConversationManager.Services;

public enum TurnRole
{
    You,
    Claude,

    /// <summary>A tool call, collapsed to one line.</summary>
    Tool,

    /// <summary>Claude's reasoning, when the transcript kept it.</summary>
    Thinking,
}

/// <summary>One readable step of a conversation.</summary>
public sealed class TranscriptTurn
{
    public required TurnRole Role { get; init; }
    public required string Text { get; init; }
    public DateTimeOffset When { get; init; }

    /// <summary>For a tool call: what it was, e.g. "Bash" or "Edit".</summary>
    public string? ToolName { get; init; }

    /// <summary>For a tool call: the head of what it returned.</summary>
    public string? Result { get; init; }

    public bool HasResult => !string.IsNullOrEmpty(Result);
}

/// <summary>
/// Rebuilds a transcript into something a person can read: your prompts, Claude's replies, and
/// each tool call as a single line instead of the file dumps that surround it.
///
/// The point is recovering context without resuming the session - so this is a summary, not a
/// faithful replay. Tool output is cut to its first lines, which is where the answer usually is.
/// </summary>
public static class TranscriptReader
{
    private const int MaxTurns = 3000;
    private const int MaxResultChars = 400;
    private const int MaxTextChars = 20_000;

    public static Task<List<TranscriptTurn>> ReadAsync(string path, CancellationToken token = default) =>
        Task.Run(() => Read(path, token), token);

    public static List<TranscriptTurn> Read(string path, CancellationToken token = default)
    {
        var turns = new List<TranscriptTurn>();
        if (!File.Exists(path)) return turns;

        // Results arrive in later records than the calls they answer, so they are collected first.
        var results = CollectToolResults(path, token);

        foreach (var line in File.ReadLines(path))
        {
            token.ThrowIfCancellationRequested();
            if (turns.Count >= MaxTurns) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;

                var type = Str(root, "type");
                var when = Time(root, "timestamp");

                if (type == "user")
                {
                    var text = UserText(root);
                    if (text is not null)
                        turns.Add(new TranscriptTurn { Role = TurnRole.You, Text = Cap(text), When = when });
                    continue;
                }

                if (type != "assistant") continue;
                if (!root.TryGetProperty("message", out var message) ||
                    message.ValueKind != JsonValueKind.Object) continue;
                if (!message.TryGetProperty("content", out var content)) continue;

                if (content.ValueKind == JsonValueKind.String)
                {
                    var text = content.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                        turns.Add(new TranscriptTurn { Role = TurnRole.Claude, Text = Cap(text!), When = when });
                    continue;
                }
                if (content.ValueKind != JsonValueKind.Array) continue;

                foreach (var block in content.EnumerateArray())
                {
                    if (block.ValueKind != JsonValueKind.Object) continue;

                    switch (Str(block, "type"))
                    {
                        case "text":
                            var text = Str(block, "text");
                            if (!string.IsNullOrWhiteSpace(text))
                                turns.Add(new TranscriptTurn
                                {
                                    Role = TurnRole.Claude, Text = Cap(text!), When = when,
                                });
                            break;

                        case "thinking":
                            var thought = Str(block, "thinking");
                            if (!string.IsNullOrWhiteSpace(thought))
                                turns.Add(new TranscriptTurn
                                {
                                    Role = TurnRole.Thinking, Text = Cap(thought!), When = when,
                                });
                            break;

                        case "tool_use":
                            var name = Str(block, "name") ?? "tool";
                            var id = Str(block, "id");
                            turns.Add(new TranscriptTurn
                            {
                                Role = TurnRole.Tool,
                                ToolName = name,
                                Text = ToolSummary(name, block),
                                When = when,
                                Result = id is not null && results.TryGetValue(id, out var r) ? r : null,
                            });
                            break;
                    }
                }
            }
        }

        return turns;
    }

    /// <summary>The one line worth showing for a tool call: the command, the file, the query.</summary>
    private static string ToolSummary(string name, JsonElement block)
    {
        if (!block.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object)
            return name;

        // The field that says what the call was actually about, per tool.
        foreach (var field in new[] { "command", "file_path", "pattern", "path", "prompt", "url", "query" })
        {
            var value = Str(input, field);
            if (!string.IsNullOrWhiteSpace(value))
                return TextSummary.OneLine(value!, 160);
        }

        return TextSummary.OneLine(input.GetRawText(), 160);
    }

    private static Dictionary<string, string> CollectToolResults(string path, CancellationToken token)
    {
        var results = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in File.ReadLines(path))
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;
            // Cheap gate: only user records carry results, and they always name the id.
            if (!line.Contains("tool_result", StringComparison.Ordinal)) continue;

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (!root.TryGetProperty("message", out var message) ||
                    message.ValueKind != JsonValueKind.Object) continue;
                if (!message.TryGetProperty("content", out var content) ||
                    content.ValueKind != JsonValueKind.Array) continue;

                foreach (var block in content.EnumerateArray())
                {
                    if (block.ValueKind != JsonValueKind.Object) continue;
                    if (Str(block, "type") != "tool_result") continue;

                    var id = Str(block, "tool_use_id");
                    if (id is null || results.ContainsKey(id)) continue;

                    results[id] = TextSummary.OneLine(ResultText(block), MaxResultChars);
                }
            }
        }

        return results;
    }

    private static string ResultText(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var content)) return "";
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
        if (content.ValueKind != JsonValueKind.Array) return "";

        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object) continue;
            var text = Str(part, "text");
            if (!string.IsNullOrWhiteSpace(text)) return text!;
        }
        return "";
    }

    private static string? UserText(JsonElement root)
    {
        if (root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True)
            return null;
        if (!root.TryGetProperty("message", out var message) ||
            message.ValueKind != JsonValueKind.Object) return null;
        if (!message.TryGetProperty("content", out var content)) return null;

        var origin = root.TryGetProperty("origin", out var o) && o.ValueKind == JsonValueKind.Object
            ? Str(o, "kind")
            : null;
        if (origin is not null && origin != "human") return null;

        if (content.ValueKind == JsonValueKind.String)
        {
            var text = content.GetString();
            if (string.IsNullOrWhiteSpace(text)) return null;
            return origin is null && IsWrapped(text!) ? null : text;
        }

        if (content.ValueKind != JsonValueKind.Array) return null;

        List<string>? parts = null;
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object) continue;
            if (Str(block, "type") != "text") continue;
            var t = Str(block, "text");
            if (!string.IsNullOrWhiteSpace(t)) (parts ??= new List<string>()).Add(t!);
        }
        return parts is null ? null : string.Join("\n", parts);
    }

    private static bool IsWrapped(string text) => text.TrimStart().StartsWith('<');

    private static string Cap(string text) =>
        text.Length <= MaxTextChars ? text : text[..MaxTextChars] + "\n\n[…truncated]";

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static DateTimeOffset Time(JsonElement obj, string name)
    {
        var s = Str(obj, name);
        if (s is null) return default;
        return DateTimeOffset.TryParse(s, null,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed.ToLocalTime()
            : default;
    }
}
