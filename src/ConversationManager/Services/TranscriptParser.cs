using System.IO;
using System.Globalization;
using System.Text.Json;
using ConversationManager.Models;

namespace ConversationManager.Services;

/// <summary>
/// Reads one .jsonl transcript and keeps only what a search needs. See
/// <see cref="Conversation"/> for why the bulk of the file is deliberately thrown away.
/// </summary>
public static class TranscriptParser
{
    /// <summary>A branch value meaning detached, or not a git repo - nothing to search for.</summary>
    public const string NoBranch = "HEAD";

    /// <summary>
    /// Anything longer than this in a single prompt is a paste, not a sentence. The head of it is
    /// still searchable; keeping megabytes of pasted log in the index is not.
    /// </summary>
    private const int MaxPromptChars = 4000;

    public static Conversation? ParseFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return Parse(File.ReadLines(path), Path.GetFileNameWithoutExtension(path), path, info.Length);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"parse failed for {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The parse itself, over lines rather than a file, so tests can feed it synthetic
    /// transcripts without touching disk.
    /// </summary>
    public static Conversation? Parse(
        IEnumerable<string> lines, string fallbackSessionId, string? path, long bytes)
    {
        string? sessionId = null;
        string? title = null;
        var branches = new Dictionary<string, int>(StringComparer.Ordinal);
        var cwds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var prompts = new List<Prompt>();
        var messageCount = 0;
        DateTimeOffset? first = null, last = null;
        var any = false;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                // A half-written last line is normal while a session is live. Skip it and keep
                // what was already read rather than losing the conversation.
                continue;
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                any = true;

                var type = Str(root, "type");
                sessionId ??= Str(root, "sessionId");

                var when = Time(root, "timestamp");
                if (when is not null)
                {
                    if (first is null || when < first) first = when;
                    if (last is null || when > last) last = when;
                }

                var branch = Str(root, "gitBranch");
                if (!string.IsNullOrWhiteSpace(branch) && branch != NoBranch)
                    branches[branch!] = branches.GetValueOrDefault(branch!) + 1;

                var cwd = Str(root, "cwd");
                if (!string.IsNullOrWhiteSpace(cwd))
                    cwds[cwd!] = cwds.GetValueOrDefault(cwd!) + 1;

                switch (type)
                {
                    case "ai-title":
                        // Rewritten as the session goes on, so the last one is the best summary.
                        var t = Str(root, "aiTitle");
                        if (!string.IsNullOrWhiteSpace(t)) title = t;
                        break;

                    case "assistant":
                        messageCount++;
                        break;

                    case "user":
                        messageCount++;
                        var text = HumanPromptText(root);
                        if (text is not null)
                            prompts.Add(new Prompt(when ?? last ?? default, Cap(text)));
                        break;
                }
            }
        }

        if (!any) return null;

        return new Conversation
        {
            SessionId = string.IsNullOrWhiteSpace(sessionId) ? fallbackSessionId : sessionId!,
            TranscriptPath = path,
            Title = title,
            Branches = branches.OrderByDescending(kv => kv.Value)
                               .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                               .Select(kv => kv.Key).ToList(),
            Cwd = PrimaryCwd(cwds),
            Cwds = cwds.OrderByDescending(kv => kv.Value)
                       .ThenBy(kv => kv.Key.Length)
                       .Select(kv => kv.Key).ToList(),
            Start = first ?? default,
            End = last ?? first ?? default,
            MessageCount = messageCount,
            Bytes = bytes,
            Prompts = prompts,
        };
    }

    /// <summary>
    /// The folder the session actually worked in. Most-used wins, and the shortest breaks a tie,
    /// because a session that dips into subfolders belongs to the root it started from.
    /// </summary>
    private static string PrimaryCwd(Dictionary<string, int> cwds) =>
        cwds.OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key.Length)
            .Select(kv => kv.Key)
            .FirstOrDefault() ?? "";

    /// <summary>
    /// Pulls out what the user typed, and nothing else. A user record is far more often a tool
    /// result being fed back to the model than a person speaking, so this filter is what keeps
    /// the prompt layer worth searching at all.
    /// </summary>
    private static string? HumanPromptText(JsonElement root)
    {
        if (root.TryGetProperty("isMeta", out var meta) &&
            meta.ValueKind == JsonValueKind.True) return null;

        if (!root.TryGetProperty("message", out var message) ||
            message.ValueKind != JsonValueKind.Object) return null;
        if (!message.TryGetProperty("content", out var content)) return null;

        var origin = root.TryGetProperty("origin", out var o) && o.ValueKind == JsonValueKind.Object
            ? Str(o, "kind")
            : null;

        // Newer transcripts label the speaker outright, and that label is the whole answer.
        if (origin is not null && origin != "human") return null;

        var text = content.ValueKind switch
        {
            JsonValueKind.String => content.GetString(),
            JsonValueKind.Array => JoinTextBlocks(content),
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(text)) return null;

        // Older transcripts carry no origin at all, so fall back on shape: slash commands and
        // their output arrive wrapped in tags, real typing does not.
        if (origin is null && IsMachineText(text!)) return null;

        return text;
    }

    private static string? JoinTextBlocks(JsonElement array)
    {
        List<string>? parts = null;
        foreach (var block in array.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object) continue;
            if (Str(block, "type") != "text") continue;
            var t = Str(block, "text");
            if (string.IsNullOrWhiteSpace(t)) continue;
            (parts ??= new List<string>()).Add(t!);
        }
        return parts is null ? null : string.Join("\n", parts);
    }

    private static readonly string[] MachinePrefixes =
    {
        "<command-name>", "<local-command-stdout>", "<local-command-stderr>",
        "<system-reminder>", "<command-message>", "<user-prompt-submit-hook>",
        "Caveat: The messages below were generated",
        "[Request interrupted",
        "API Error",
    };

    private static bool IsMachineText(string text)
    {
        var t = text.TrimStart();
        foreach (var prefix in MachinePrefixes)
            if (t.StartsWith(prefix, StringComparison.Ordinal)) return true;
        return false;
    }

    private static string Cap(string text) =>
        text.Length <= MaxPromptChars ? text : text[..MaxPromptChars];

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static DateTimeOffset? Time(JsonElement obj, string name)
    {
        var s = Str(obj, name);
        if (s is null) return null;
        return DateTimeOffset.TryParse(s, null,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed) ? parsed.ToLocalTime() : null;
    }
}
