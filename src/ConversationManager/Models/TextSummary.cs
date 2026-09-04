namespace ConversationManager.Models;

/// <summary>Turning transcript text into something that fits on one line of a card.</summary>
public static class TextSummary
{
    /// <summary>Collapses whitespace and trims to a length, adding an ellipsis if cut.</summary>
    public static string OneLine(string text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var chars = new char[text.Length];
        var n = 0;
        var lastWasSpace = false;
        foreach (var c in text)
        {
            var isSpace = char.IsWhiteSpace(c);
            if (isSpace)
            {
                if (n == 0 || lastWasSpace) continue;
                chars[n++] = ' ';
            }
            else
            {
                chars[n++] = c;
            }
            lastWasSpace = isSpace;
        }
        while (n > 0 && chars[n - 1] == ' ') n--;

        var flat = new string(chars, 0, n);
        return flat.Length <= max ? flat : flat[..Math.Max(0, max - 1)].TrimEnd() + "…";
    }

    /// <summary>
    /// A window of text centred on a match, so the hit is visible rather than cut off. Returns
    /// the snippet and where the match landed inside it, for highlighting.
    /// </summary>
    public static (string Text, int MatchStart, int MatchLength) Snippet(
        string text, int matchStart, int matchLength, int width = 150)
    {
        if (string.IsNullOrEmpty(text) || matchStart < 0 || matchStart >= text.Length)
            return (OneLine(text ?? "", width), 0, 0);

        matchLength = Math.Clamp(matchLength, 0, text.Length - matchStart);

        // The whole string is flattened in one pass, carrying the match offsets with it. Doing the
        // three pieces separately looks equivalent and is not: each piece gets its own edges
        // trimmed, which welds the match to the word beside it ("staletoolchain").
        var (flat, start, length) = Flatten(text, matchStart, matchStart + matchLength);

        // A little context before the hit, and the rest of the room to what follows it.
        var before = Math.Min(start, Math.Max(20, (width - length) / 3));
        var from = start - before;
        var leading = from > 0 ? "…" : "";

        var head = flat[from..start];
        var room = Math.Max(0, width - leading.Length - head.Length - length);
        var rest = flat[(start + length)..];
        var tail = rest.Length <= room ? rest : rest[..room].TrimEnd() + "…";

        return (leading + head + flat.Substring(start, length) + tail,
            leading.Length + head.Length,
            length);
    }

    /// <summary>
    /// Collapses whitespace across the whole string while reporting where a range of the original
    /// ended up in the result.
    /// </summary>
    private static (string Flat, int Start, int Length) Flatten(string text, int from, int to)
    {
        var chars = new char[text.Length];
        var n = 0;
        var lastWasSpace = false;
        var start = 0;
        var end = 0;

        for (var i = 0; i <= text.Length; i++)
        {
            // Recorded before the character at i is written, so an offset lands ahead of any
            // space that was collapsed in front of it rather than behind it.
            if (i == from) start = n;
            if (i == to) end = n;
            if (i == text.Length) break;

            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                if (n == 0 || lastWasSpace)
                {
                    lastWasSpace = true;
                    continue;
                }
                chars[n++] = ' ';
                lastWasSpace = true;
                continue;
            }

            chars[n++] = c;
            lastWasSpace = false;
        }

        while (n > 0 && chars[n - 1] == ' ') n--;

        start = Math.Clamp(start, 0, n);
        end = Math.Clamp(end, start, n);
        return (new string(chars, 0, n), start, end - start);
    }
}
