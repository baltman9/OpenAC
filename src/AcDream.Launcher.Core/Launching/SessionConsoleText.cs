namespace AcDream.Launcher.Core.Launching;

/// <summary>One stretch of console text in one colour.</summary>
/// <param name="Text">The text, with no escape sequences in it.</param>
/// <param name="Color">The colour as 0xRRGGBB, or null for the console's own default.</param>
/// <param name="Dim">Whether the session marked the text as a quiet notice of its own.</param>
public readonly record struct SessionConsoleRun(string Text, int? Color, bool Dim);

/// <summary>
/// Turns what a windowless session writes into coloured runs. A session that
/// colours its lines says so with terminal escapes, and those are followed
/// exactly. One that does not still tags each line with its kind, and the
/// tag picks the colour the game shows that kind of line in.
/// </summary>
public static class SessionConsoleText
{
    private const char Escape = '';

    /// <summary>Splits one line into its runs.</summary>
    public static IReadOnlyList<SessionConsoleRun> ParseLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.IndexOf(Escape) < 0)
            return [new SessionConsoleRun(line, ColorForTag(line), Dim: IsNotice(line))];

        var runs = new List<SessionConsoleRun>();
        int? color = null;
        bool dim = false;
        int position = 0;
        while (position < line.Length)
        {
            int escape = line.IndexOf(Escape, position);
            if (escape < 0)
            {
                Add(runs, line[position..], color, dim);
                break;
            }

            Add(runs, line[position..escape], color, dim);
            // Only "set graphics" sequences mean anything here: ESC [ numbers m.
            int end = escape + 1 < line.Length && line[escape + 1] == '['
                ? line.IndexOf('m', escape + 2)
                : -1;
            if (end < 0)
            {
                position = escape + 1;
                continue;
            }

            Apply(line[(escape + 2)..end], ref color, ref dim);
            position = end + 1;
        }

        return runs;
    }

    /// <summary>
    /// The colour of a line the session did not colour itself, from the tag
    /// the session puts in front of every chat line. Null when there is no
    /// tag, or the kind has no colour of its own.
    /// </summary>
    public static int? ColorForTag(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.Length < 3 || line[0] != '[')
            return null;
        int close = line.IndexOf(']');
        if (close < 0)
            return null;
        return line[1..close] switch
        {
            "say" => 0xFFFFFF,
            "tell" or "admin" or "social" or "fellowship" => 0xFFFF3F,
            "system" => 0xFF7FFF,
            "combat" or "help" => 0xFF3F3F,
            "magic" or "spell" => 0x3FBFFF,
            "channel" => 0xFF9696,
            "emote" => 0xD2D2C8,
            "advance" => 0x3FDCDC,
            "allegiance" => 0xEE921E,
            "abuse" or "general" or "trade" or "lfg" or "roleplay" or "society" => 0xB4DCF0,
            "all" or "msg" or "appraise" or "broadcast" or "recall" or "craft" or "salvage" => 0x7FFF7F,
            _ => null,
        };
    }

    private static bool IsNotice(string line) => line.StartsWith("-- ", StringComparison.Ordinal);

    private static void Add(List<SessionConsoleRun> runs, string text, int? color, bool dim)
    {
        if (text.Length > 0)
            runs.Add(new SessionConsoleRun(text, color, dim));
    }

    private static void Apply(string parameters, ref int? color, ref bool dim)
    {
        string[] parts = parameters.Length == 0 ? ["0"] : parameters.Split(';');
        for (int index = 0; index < parts.Length; index++)
        {
            if (!int.TryParse(parts[index], out int code))
                continue;
            switch (code)
            {
                case 0:
                    color = null;
                    dim = false;
                    break;
                case 2:
                    dim = true;
                    break;
                case 22:
                    dim = false;
                    break;
                case 39:
                    color = null;
                    break;
                case 38 when index + 4 < parts.Length && parts[index + 1] == "2"
                    && byte.TryParse(parts[index + 2], out byte red)
                    && byte.TryParse(parts[index + 3], out byte green)
                    && byte.TryParse(parts[index + 4], out byte blue):
                    color = (red << 16) | (green << 8) | blue;
                    index += 4;
                    break;
            }
        }
    }
}
