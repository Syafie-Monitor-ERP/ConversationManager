using System.IO;
using System.Globalization;
using System.Text.Json;
using ConversationManager.Models;

namespace ConversationManager.Services;

/// <summary>One line of history.jsonl: something typed, where, and when.</summary>
public sealed record HistoryEntry(string SessionId, string Project, DateTimeOffset When, string Text);

/// <summary>
/// Reads ~\.claude\history.jsonl - every prompt ever submitted, with its session id, folder and
/// timestamp.
///
/// This matters more than it looks. Claude Code prunes transcripts, but history.jsonl keeps
/// going: on the machine this was built for, 171 sessions appear in history while only 48
/// transcripts survive. Without this file, three quarters of the user's past is unsearchable.
/// </summary>
public static class HistoryReader
{
    public static List<HistoryEntry> Read(string path)
    {
        if (!File.Exists(path)) return new List<HistoryEntry>();
        try
        {
            return Parse(File.ReadLines(path));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"history read failed: {ex.Message}");
            return new List<HistoryEntry>();
        }
    }

    public static List<HistoryEntry> Parse(IEnumerable<string> lines)
    {
        var entries = new List<HistoryEntry>();

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
                continue;
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;

                var display = Str(root, "display");
                var sessionId = Str(root, "sessionId");
                if (string.IsNullOrWhiteSpace(display) || string.IsNullOrWhiteSpace(sessionId))
                    continue;
                if (IsCommand(display!)) continue;

                entries.Add(new HistoryEntry(
                    sessionId!,
                    Str(root, "project") ?? "",
                    Epoch(root, "timestamp"),
                    display!.Trim()));
            }
        }

        return entries;
    }

    /// <summary>
    /// Slash commands and shell-mode lines are keystrokes, not descriptions of work. Indexing
    /// them means a search for anything short matches every session that ever ran /clear.
    /// </summary>
    private static bool IsCommand(string display)
    {
        var t = display.TrimStart();
        return t.Length == 0 || t[0] == '/' || t[0] == '!' || t[0] == '#';
    }

    private static DateTimeOffset Epoch(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v)) return default;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt64(out var ms) =>
                DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime(),
            JsonValueKind.String when long.TryParse(v.GetString(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var ms2) =>
                DateTimeOffset.FromUnixTimeMilliseconds(ms2).ToLocalTime(),
            _ => default,
        };
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
