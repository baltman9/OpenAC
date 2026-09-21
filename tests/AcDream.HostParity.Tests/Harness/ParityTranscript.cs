using System.Globalization;

namespace AcDream.HostParity.Tests;

/// <summary>
/// What one arm did and saw, as ordered lines. A scenario writes the same
/// lines on both arms; the two transcripts then have to be the same text,
/// which makes "the clients behave the same" a thing a test can say rather
/// than a thing a reviewer has to check by eye.
///
/// A line is <c>key = value</c> under a step heading. The key is what the
/// scenario asked; the value is what that arm answered. Keys are what an
/// expected difference is written against, so a line that is allowed to
/// differ is named rather than skipped.
/// </summary>
internal sealed class ParityTranscript
{
    private readonly List<ParityTranscriptLine> _lines = [];
    private string _step = "start";

    internal IReadOnlyList<ParityTranscriptLine> Lines => _lines;

    /// <summary>Names the step the following lines belong to.</summary>
    internal void Step(string name)
    {
        _step = name;
        _lines.Add(new ParityTranscriptLine($"{name}/", "--"));
    }

    /// <summary>Records one thing this arm answered.</summary>
    internal void Record(string key, object? value) =>
        _lines.Add(new ParityTranscriptLine($"{_step}/{key}", Format(value)));

    /// <summary>Records everything the host asked to send since last asked.</summary>
    internal void RecordOutbound(ParityArm arm)
    {
        ArgumentNullException.ThrowIfNull(arm);
        IReadOnlyList<ParityOutbound> sent = arm.Operations.TakeOutbound();
        Record("outbound.count", sent.Count);
        for (int index = 0; index < sent.Count; index++)
            Record($"outbound[{index}]", sent[index].ToString());
    }

    public override string ToString() => string.Join(
        Environment.NewLine,
        _lines.Select(static line => $"{line.Key} = {line.Value}"));

    private static string Format(object? value) => value switch
    {
        null => "<none>",
        bool flag => flag ? "true" : "false",
        float number => number.ToString("0.0000", CultureInfo.InvariantCulture),
        double number => number.ToString("0.0000", CultureInfo.InvariantCulture),
        uint id => $"0x{id:X8}",
        string text => $"\"{text}\"",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "<none>",
    };
}

internal readonly record struct ParityTranscriptLine(string Key, string Value);
