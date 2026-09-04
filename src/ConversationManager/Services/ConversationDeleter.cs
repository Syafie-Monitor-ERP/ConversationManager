using System.IO;
using System.Text;
using System.Text.Json;
using ConversationManager.Models;

namespace ConversationManager.Services;

/// <summary>Whether a removed transcript can be fished back out of the Recycle Bin.</summary>
public enum DeleteMode
{
    /// <summary>What the app does: the file goes to the Recycle Bin and can be restored.</summary>
    Recycle,

    /// <summary>Straight off the disk. Used by the tests, which have nothing worth keeping.</summary>
    Permanent,
}

/// <summary>
/// What a delete is about to do, worked out before anything is touched, so the confirmation can
/// name real numbers instead of asking "are you sure?" about an unknown quantity.
/// </summary>
public sealed record DeletePlan(int Count, int TranscriptCount, int HistoryOnlyCount, long Bytes)
{
    public static DeletePlan For(IEnumerable<Conversation> conversations)
    {
        var count = 0;
        var transcripts = 0;
        long bytes = 0;

        foreach (var c in conversations)
        {
            count++;
            if (!c.HasTranscript) continue;
            transcripts++;
            bytes += c.Bytes;
        }

        return new DeletePlan(count, transcripts, count - transcripts, bytes);
    }

    public string Question => Count == 1
        ? "Delete this conversation?"
        : $"Delete {Count} conversations?";

    /// <summary>
    /// The body of the confirmation. Both halves of the delete are spelled out, because the
    /// second one is the surprising one: removing only the transcript leaves every prompt in
    /// history.jsonl, and the conversation reappears on the next scan as a history-only card.
    /// </summary>
    public string Detail(string what, DeleteMode mode)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(what)) lines.Add(what + Environment.NewLine);

        var one = TranscriptCount == 1;
        var destination = mode == DeleteMode.Recycle
            ? (one ? "goes to the Recycle Bin" : "go to the Recycle Bin")
            : (one ? "is deleted" : "are deleted");

        if (TranscriptCount > 0)
            lines.Add($"{Plural(TranscriptCount, "transcript")} ({Size(Bytes)}) {destination}.");

        lines.Add($"{(Count == 1 ? "Its" : "Their")} prompts are removed from history.jsonl, " +
                  "which is backed up first as history.jsonl.bak.");

        if (HistoryOnlyCount > 0 && TranscriptCount > 0)
            lines.Add($"{Plural(HistoryOnlyCount, "conversation")} " +
                      $"{(HistoryOnlyCount == 1 ? "has" : "have")} no transcript left, so only " +
                      "the prompts go.");

        lines.Add(Environment.NewLine + "Claude Code cannot resume a deleted conversation.");

        return string.Join(Environment.NewLine, lines);
    }

    internal static string Plural(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    internal static string Size(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1024.0 / 1024.0:0.#} MB"
        : $"{Math.Max(1, bytes / 1024)} KB";
}

/// <summary>What a delete actually managed to do.</summary>
public sealed class DeleteReport
{
    /// <summary>The conversations that are really gone, and can be dropped from the UI.</summary>
    public List<string> RemovedSessionIds { get; } = new();

    public int FilesRemoved { get; set; }

    public long BytesRemoved { get; set; }

    public int HistoryLinesRemoved { get; set; }

    /// <summary>Anything that refused to go, one sentence each. Never thrown, always reported.</summary>
    public List<string> Errors { get; } = new();

    /// <summary>One line for the status bar.</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();

            if (RemovedSessionIds.Count > 0)
                parts.Add($"Deleted {DeletePlan.Plural(RemovedSessionIds.Count, "conversation")}");

            if (FilesRemoved > 0)
                parts.Add($"{DeletePlan.Plural(FilesRemoved, "transcript")} " +
                          $"({DeletePlan.Size(BytesRemoved)})");

            if (HistoryLinesRemoved > 0)
                parts.Add($"{DeletePlan.Plural(HistoryLinesRemoved, "history line")} removed");

            if (Errors.Count > 0)
                parts.Add(Errors.Count == 1 ? Errors[0] : $"{Errors.Count} failed: {Errors[0]}");

            return parts.Count == 0 ? "Nothing was deleted" : string.Join("   ·   ", parts);
        }
    }
}

/// <summary>
/// Removes conversations from the two places Claude Code keeps them.
///
/// Both places, always - and that is the whole point of this class. Deleting the .jsonl alone
/// looks like it worked and is not: history.jsonl still holds every prompt of that session, so
/// the next scan brings the conversation back as a history-only card with its title intact. A
/// conversation is only gone when both sources have forgotten it.
/// </summary>
public static class ConversationDeleter
{
    public static DeleteReport Delete(
        IReadOnlyList<Conversation> conversations,
        AppConfig config,
        DeleteMode mode = DeleteMode.Recycle)
    {
        var report = new DeleteReport();
        if (conversations.Count == 0) return report;

        // Phase one: the transcripts. A file that will not go keeps its conversation on screen,
        // because half a delete that reports success is worse than a delete that failed loudly.
        var removed = new List<Conversation>();
        foreach (var conversation in conversations)
        {
            var path = conversation.TranscriptPath;
            if (path is null || !File.Exists(path))
            {
                removed.Add(conversation);
                continue;
            }

            try
            {
                Remove(path, mode);
                report.FilesRemoved++;
                report.BytesRemoved += conversation.Bytes;
                removed.Add(conversation);
            }
            catch (Exception ex)
            {
                report.Errors.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        // Phase two: the prompts. One rewrite for the whole batch - the file is read and written
        // once however many conversations are going.
        var sessionIds = removed
            .Select(c => c.SessionId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var historyOk = true;
        try
        {
            report.HistoryLinesRemoved = RewriteHistory(config.HistoryFile, sessionIds);
        }
        catch (Exception ex)
        {
            historyOk = false;
            report.Errors.Add($"history.jsonl was left alone: {ex.Message}");
        }

        foreach (var conversation in removed)
        {
            // With history untouched, a conversation that only ever lived there is still there.
            if (!historyOk && !conversation.HasTranscript) continue;
            report.RemovedSessionIds.Add(conversation.SessionId);
        }

        // index-cache.json is left as it is on purpose: its entries are keyed by file path, and
        // an entry whose file no longer exists is simply never matched again, then dropped the
        // next time a scan rewrites the cache.

        return report;
    }

    private static void Remove(string path, DeleteMode mode)
    {
        if (mode == DeleteMode.Recycle && OperatingSystem.IsWindows())
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            return;
        }

        File.Delete(path);
    }

    /// <summary>
    /// Rewrites history.jsonl without the given sessions, keeping the previous file as
    /// history.jsonl.bak. Returns how many lines went.
    /// </summary>
    private static int RewriteHistory(string path, IReadOnlySet<string> sessionIds)
    {
        if (sessionIds.Count == 0 || !File.Exists(path)) return 0;

        var kept = new List<string>();
        var removed = 0;

        foreach (var line in File.ReadLines(path))
        {
            if (SessionIdOf(line) is { } id && sessionIds.Contains(id))
            {
                removed++;
                continue;
            }
            kept.Add(line);
        }

        if (removed == 0) return 0;

        // Written through a temp file and swapped in, so an interrupted write cannot leave the
        // user with half a history. LF endings and no BOM, because that is what Claude Code
        // writes and what its own reader expects back.
        var temp = path + ".tmp";
        try
        {
            using (var writer = new StreamWriter(temp, append: false, new UTF8Encoding(false)))
            {
                writer.NewLine = "\n";
                foreach (var line in kept) writer.WriteLine(line);
            }

            File.Replace(temp, path, path + ".bak");
        }
        catch (Exception)
        {
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch (Exception)
            {
                // Leaving a .tmp behind is untidy, not a failure worth reporting over the real one.
            }
            throw;
        }

        return removed;
    }

    /// <summary>
    /// The session a history line belongs to, or null for a line that cannot be read - which is
    /// kept. Nothing is deleted on the strength of a guess.
    /// </summary>
    internal static string? SessionIdOf(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            return doc.RootElement.TryGetProperty("sessionId", out var v) &&
                   v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
