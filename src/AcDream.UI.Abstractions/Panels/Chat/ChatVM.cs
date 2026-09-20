using System.Globalization;
using System.Numerics;
using AcDream.Core.Chat;
using AcDream.Core.Combat;
using AcDream.Runtime.Chat;

namespace AcDream.UI.Abstractions.Panels.Chat;

public sealed class ChatVM : IDisposable, IChatCommandFeedback
{
    /// <summary>Default number of tail entries rendered.</summary>
    public const int DefaultDisplayLimit = 20;

    private readonly ChatLog _log;
    private readonly RuntimeChatFeed _feed;
    private readonly ChatCommandTargetState _commandTargets;
    private readonly bool _ownsCommandTargets;
    private readonly int _displayLimit;
    private bool _disposed;

    public string? LastIncomingTellSender =>
        _commandTargets.LastIncomingTellSender;

    public string? LastOutgoingTellTarget =>
        _commandTargets.LastOutgoingTellTarget;

    public string? LastMonarchSender =>
        _commandTargets.LastMonarchSender;

    public string? LastPatronSender =>
        _commandTargets.LastPatronSender;

    public Func<float>? FpsProvider { get; init; }

    public Func<Vector3>? PositionProvider { get; init; }

    public Action<string>? OnInterfaceText { get; init; }

    public long Revision => _log.Revision;

    public ChatVM(
        ChatLog log,
        int displayLimit = DefaultDisplayLimit,
        ChatCommandTargetState? commandTargets = null)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        if (displayLimit < 1)
            throw new ArgumentOutOfRangeException(nameof(displayLimit), displayLimit, "must be >= 1");
        _displayLimit = displayLimit;
        // The panel shows every line the log holds and leaves per-window
        // filtering to the window controller, so this view of the feed carries
        // no filters of its own.
        _feed = new RuntimeChatFeed(log);
        _commandTargets = commandTargets ?? new ChatCommandTargetState(_log);
        _ownsCommandTargets = commandTargets is null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _feed.Dispose();
        if (_ownsCommandTargets)
            _commandTargets.Dispose();
        _disposed = true;
    }

    public void ShowSystemMessage(string text) => _log.OnSystemMessage(text, chatType: 0x00u);

    public void ShowInterfaceText(string text)
    {
        if (OnInterfaceText is { } hook)
            hook(text);
        else
            _log.OnSystemMessage(text, chatType: (uint)RetailLogTextType.ClientLocal);
    }

    public void Clear() => _log.Clear();

    public void ResetSessionTargets()
    {
        _commandTargets.ResetSession();
    }

    public void ShowFps()
    {
        var fps = FpsProvider?.Invoke();
        ShowSystemMessage(fps is null
            ? "Framerate: (provider unavailable)"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"Framerate: {fps.Value:F1} FPS"));
    }

    public void ShowLocation()
    {
        var pos = PositionProvider?.Invoke();
        ShowSystemMessage(pos is null
            ? "Location: (provider unavailable)"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"Location: ({pos.Value.X:F1}, {pos.Value.Y:F1}, {pos.Value.Z:F1})"));
    }

    public IReadOnlyList<string> RecentLines()
    {
        IReadOnlyList<RuntimeChatLine> lines = _feed.Snapshot(_displayLimit);
        if (lines.Count == 0) return Array.Empty<string>();

        var text = new string[lines.Count];
        for (int i = 0; i < lines.Count; i++)
            text[i] = lines[i].Text;
        return text;
    }

    public static string FormatEntry(ChatEntry entry)
        => RuntimeChatFeed.Format(entry);

    public static string FormatEntryTagged(ChatEntry entry)
        => RuntimeChatFeed.FormatTagged(entry);

    internal static bool ShouldTagSender(ChatEntry entry)
        => RuntimeChatFeed.ShouldTagSender(entry);

    public IReadOnlyList<FormattedLine> RecentLinesDetailed()
    {
        IReadOnlyList<RuntimeChatLine> lines = _feed.Snapshot(_displayLimit);
        if (lines.Count == 0) return Array.Empty<FormattedLine>();

        var formatted = new FormattedLine[lines.Count];
        for (int i = 0; i < lines.Count; i++)
        {
            RuntimeChatLine line = lines[i];
            formatted[i] = new FormattedLine(
                Text: line.Text,
                Kind: line.Kind,
                CombatKind: line.CombatKind,
                LogTextType: line.LogTextType,
                Spans: line.Spans,
                Sequence: line.Sequence);
        }
        return formatted;
    }
}

/// <param name="Sequence">
/// Identity of the log entry this line was formatted from — stable while entries are appended
/// and older ones dropped. 0 when the line has no entry behind it.
/// </param>
public readonly record struct FormattedLine(
    string Text,
    ChatKind Kind,
    CombatLineKind? CombatKind,
    uint LogTextType,
    IReadOnlyList<ChatTextSpan>? Spans = null,
    long Sequence = 0);
