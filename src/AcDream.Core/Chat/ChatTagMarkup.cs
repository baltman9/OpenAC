namespace AcDream.Core.Chat;

public readonly record struct ChatTextTag(string Type, string Format, string Data)
{
    public bool TryGetCoordinate(out double eastWest, out double northSouth)
    {
        eastWest = 0d;
        northSouth = 0d;
        if (!string.Equals(Type, "COORD", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Format, "World", StringComparison.OrdinalIgnoreCase))
            return false;
        return TryParseCoordinatePair(Data, out eastWest, out northSouth);
    }

    public static bool TryParseCoordinatePair(string? text, out double eastWest, out double northSouth)
    {
        eastWest = 0d;
        northSouth = 0d;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        string[] parts = text.Split(',', StringSplitOptions.TrimEntries);
        return parts.Length == 2
            && TryParseCoordinate(parts[0], 'N', 'S', out northSouth)
            && TryParseCoordinate(parts[1], 'E', 'W', out eastWest);
    }

    private static bool TryParseCoordinate(string text, char positive, char negative, out double value)
    {
        value = 0d;
        if (text.Length < 2)
            return false;
        char suffix = char.ToUpperInvariant(text[^1]);
        if (suffix != positive && suffix != negative)
            return false;
        if (!double.TryParse(text[..^1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double magnitude)
            || magnitude < 0d)
            return false;
        value = suffix == negative ? -magnitude : magnitude;
        return true;
    }

    public bool TryGetIidString(out uint objectId, out string name)
    {
        objectId = 0;
        name = string.Empty;
        if (!string.Equals(Format, "IIDString", StringComparison.Ordinal))
            return false;

        int split = Data.IndexOf(':');
        if (split <= 0 || split == Data.Length - 1)
            return false;
        if (!uint.TryParse(
                Data.AsSpan(0, split),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out objectId))
        {
            return false;
        }

        name = Data[(split + 1)..];
        return true;
    }
}

public enum ChatSpanRole
{
    Body,

    Timestamp,
}

public readonly record struct ChatTextSpan(
    string Text,
    ChatTextTag? Tag,
    ChatSpanRole Role = ChatSpanRole.Body);

public static class ChatTagMarkup
{
    public static IReadOnlyList<ChatTextSpan> Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return Array.Empty<ChatTextSpan>();
        if (text.IndexOf('<') < 0)
            return new[] { new ChatTextSpan(text, null) };

        var spans = new List<ChatTextSpan>();
        ChatTextTag? open = null;
        int runStart = 0;

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '<')
                continue;

            int close = text.IndexOf('>', i + 1);
            if (close < 0)
                break;   // unterminated: the rest is plain text

            // Flush the text before this marker under whatever tag was open.
            if (i > runStart)
                spans.Add(new ChatTextSpan(text[runStart..i], open));

            open = TryParseStartTag(text[(i + 1)..close]);
            runStart = close + 1;
            i = close;
        }

        if (runStart < text.Length)
            spans.Add(new ChatTextSpan(text[runStart..], open));

        return spans;
    }

    private static ChatTextTag? TryParseStartTag(string inner)
    {
        int first = inner.IndexOf(':');
        if (first <= 0 || first == inner.Length - 1)
            return null;

        int second = inner.IndexOf(':', first + 1);
        if (second < 0)
        {
            // TYPE:FORMAT with no payload. Still a tag — the data is empty.
            return new ChatTextTag(inner[..first], inner[(first + 1)..], string.Empty);
        }

        return new ChatTextTag(
            inner[..first],
            inner[(first + 1)..second],
            inner[(second + 1)..]);
    }
}
