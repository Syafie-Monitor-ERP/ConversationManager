using ConversationManager.Models;

namespace ConversationManager.Services;

/// <summary>One heading in the overview, and what sits under it.</summary>
public sealed class ConversationGroup
{
    public required string Name { get; init; }

    /// <summary>The second line: where this work happened, or how many conversations there are.</summary>
    public string Subtitle { get; init; } = "";

    public required IReadOnlyList<Conversation> Conversations { get; init; }

    /// <summary>Most recent activity in the group - what the groups are ordered by.</summary>
    public DateTimeOffset LastActive => Conversations.Max(c => c.End);
}

/// <summary>
/// Arranges the whole history when nothing is being searched for. Branch first, because "which
/// conversations touched this branch" is the question that started this tool.
/// </summary>
public static class ConversationGrouper
{
    public const string NoBranchLabel = "no branch recorded";

    public static List<ConversationGroup> Group(
        IReadOnlyList<Conversation> conversations, GroupMode mode, DateTimeOffset now) => mode switch
    {
        GroupMode.Branch => ByBranch(conversations),
        GroupMode.Folder => ByFolder(conversations),
        _ => ByRecency(conversations, now),
    };

    /// <summary>
    /// A conversation that switched branches mid-session is listed under each of them. That is
    /// the honest answer: the work really does belong to both.
    /// </summary>
    private static List<ConversationGroup> ByBranch(IReadOnlyList<Conversation> conversations)
    {
        var buckets = new Dictionary<string, List<Conversation>>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in conversations)
        {
            if (c.Branches.Count == 0)
            {
                Bucket(buckets, NoBranchLabel).Add(c);
                continue;
            }
            foreach (var branch in c.Branches)
                Bucket(buckets, branch).Add(c);
        }

        return buckets
            .Select(kv => new ConversationGroup
            {
                Name = kv.Key,
                Subtitle = FolderSubtitle(kv.Value),
                Conversations = Newest(kv.Value),
            })
            // The unbranched pile is real but never the answer, so it sinks to the bottom.
            .OrderBy(g => g.Name == NoBranchLabel ? 1 : 0)
            .ThenByDescending(g => g.LastActive)
            .ToList();
    }

    private static List<ConversationGroup> ByFolder(IReadOnlyList<Conversation> conversations)
    {
        var buckets = new Dictionary<string, List<Conversation>>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in conversations)
            Bucket(buckets, string.IsNullOrWhiteSpace(c.Cwd) ? "(unknown folder)" : c.Cwd).Add(c);

        return buckets
            .Select(kv => new ConversationGroup
            {
                Name = Conversation.FolderNameOf(kv.Key),
                Subtitle = kv.Key,
                Conversations = Newest(kv.Value),
            })
            .OrderByDescending(g => g.LastActive)
            .ToList();
    }

    private static List<ConversationGroup> ByRecency(
        IReadOnlyList<Conversation> conversations, DateTimeOffset now)
    {
        return conversations
            .GroupBy(c => TimeText.DayBucketRank(c.End, now))
            .OrderBy(g => g.Key)
            .Select(g => new ConversationGroup
            {
                Name = TimeText.DayBucket(g.First().End, now),
                // No subtitle: the heading already carries a count pill, and a time bucket has no
                // one folder or branch to name.
                Subtitle = "",
                Conversations = Newest(g.ToList()),
            })
            .ToList();
    }

    private static List<Conversation> Bucket(
        Dictionary<string, List<Conversation>> buckets, string key)
    {
        if (!buckets.TryGetValue(key, out var list))
            buckets[key] = list = new List<Conversation>();
        return list;
    }

    private static List<Conversation> Newest(List<Conversation> items) =>
        items.OrderByDescending(c => c.End).ToList();

    /// <summary>Which folders a branch was worked in - two folders on one branch is worth seeing.</summary>
    private static string FolderSubtitle(List<Conversation> items)
    {
        var folders = items
            .Select(c => c.Cwd)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return folders.Count switch
        {
            0 => Count(items.Count),
            1 => folders[0],
            _ => string.Join("   ·   ", folders),
        };
    }

    private static string Count(int n) => n == 1 ? "1 conversation" : $"{n} conversations";
}
