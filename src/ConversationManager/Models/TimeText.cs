namespace ConversationManager.Models;

/// <summary>
/// Ages and durations written the way the user asked the question - "which conversation was that,
/// two days ago?" - rather than as timestamps they have to subtract in their head.
/// </summary>
public static class TimeText
{
    /// <summary>"just now", "14m ago", "3h ago", "yesterday", "5d ago", "3w ago", "2 Mar".</summary>
    public static string Relative(DateTimeOffset when, DateTimeOffset now)
    {
        var span = now - when;
        if (span < TimeSpan.Zero) return "just now";

        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";

        // Past a day, count calendar days rather than multiples of 24h, so a conversation from
        // Monday evening reads as "yesterday" on Tuesday morning instead of "2d ago".
        var days = (now.Date - when.Date).Days;
        if (days == 1) return "yesterday";
        if (days < 7) return $"{days}d ago";
        if (days < 28) return $"{days / 7}w ago";
        return when.Year == now.Year ? when.ToString("d MMM") : when.ToString("d MMM yyyy");
    }

    /// <summary>How long the session itself ran: "4m", "1h 20m", "spans 3 days".</summary>
    public static string Duration(TimeSpan span)
    {
        if (span.TotalMinutes < 1) return "under a minute";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m";
        if (span.TotalHours < 24)
        {
            var mins = span.Minutes;
            return mins == 0 ? $"{(int)span.TotalHours}h" : $"{(int)span.TotalHours}h {mins}m";
        }
        // A session picked up again the next day is worth flagging as such, not as "31h".
        var days = (int)Math.Round(span.TotalDays);
        return days <= 1 ? "spans 2 days" : $"spans {days} days";
    }

    /// <summary>Bucket heading for the recency view.</summary>
    public static string DayBucket(DateTimeOffset when, DateTimeOffset now)
    {
        var days = (now.Date - when.Date).Days;
        return days switch
        {
            <= 0 => "Today",
            1 => "Yesterday",
            < 7 => "Earlier this week",
            < 14 => "Last week",
            < 31 => "This month",
            < 62 => "Last month",
            _ => "Older",
        };
    }

    /// <summary>Ordering key for <see cref="DayBucket"/>, since the names do not sort.</summary>
    public static int DayBucketRank(DateTimeOffset when, DateTimeOffset now)
    {
        var days = (now.Date - when.Date).Days;
        return days switch
        {
            <= 0 => 0,
            1 => 1,
            < 7 => 2,
            < 14 => 3,
            < 31 => 4,
            < 62 => 5,
            _ => 6,
        };
    }
}
