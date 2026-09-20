using AcDream.Core.Chat;
using AcDream.Runtime;
using AcDream.Runtime.Chat;

namespace AcDream.Headless.Hosting;

/// <summary>
/// The console as a chat box: it prints the chat feed's lines with the same
/// words a window shows, and its own notices in a form that cannot be mistaken
/// for one of them.
/// </summary>
internal sealed class HeadlessConsoleRenderer : IRuntimeEventObserver, IDisposable
{
    private const string Reset = "[0m";
    private const string Dim = "[2m";

    /// <summary>
    /// In front of every line the console produces about itself — never in
    /// front of a line of chat.
    /// </summary>
    internal const string NoticePrefix = "-- ";

    private readonly TextWriter _output;
    private readonly bool _useColor;
    private readonly RuntimeChatFeed? _chat;
    private readonly int _windowId;
    private bool _disposed;

    /// <param name="chat">
    /// The chat feed to render. Null leaves the console silent about chat —
    /// used where there is no session behind it.
    /// </param>
    /// <param name="windowId">
    /// Which chat window's filters decide what is shown. The console stands in
    /// for the main window.
    /// </param>
    internal HeadlessConsoleRenderer(
        TextWriter output,
        bool useColor,
        RuntimeChatFeed? chat = null,
        int windowId = ChatWindowState.MainWindowId)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _useColor = useColor;
        _chat = chat;
        _windowId = windowId;
        if (_chat is not null)
            _chat.LineAppended += WriteChatLine;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        if (_chat is not null)
            _chat.LineAppended -= WriteChatLine;
        _disposed = true;
    }

    /// <summary>
    /// One chat-box line: the feed's words unchanged, behind the short tag
    /// that stands in for the colour a window would draw it in. Lines the
    /// window's filters exclude are not printed here either.
    /// </summary>
    internal void WriteChatLine(RuntimeChatLine line)
    {
        if (_chat is not null && !_chat.BelongsTo(_windowId, line.LogTextType))
            return;
        WriteLine(
            RuntimeChatLineTags.For(line) + line.Text, dim: false);
    }

    /// <summary>
    /// Chat reaches the console through the chat feed, which carries the
    /// finished text; this stream carries the same entries unworded.
    /// </summary>
    public void OnChat(in RuntimeChatDelta delta)
    {
    }

    /// <summary>Client-local text: what a window puts in its status overlay.</summary>
    internal void WriteInterfaceText(string text) =>
        WriteLine(RuntimeChatLineTags.ClientLocal + text, dim: false);

    /// <summary>A notice about the session itself, never a line of chat.</summary>
    internal void WriteNotice(string text) => WriteLine(text, dim: true);

    public void OnLifecycle(in RuntimeLifecycleDelta delta)
    {
        switch (delta.Current)
        {
            case RuntimeLifecycleState.InWorld:
                WriteNotice("entered world");
                break;
            case RuntimeLifecycleState.Stopping:
                WriteNotice("disconnecting");
                break;
            case RuntimeLifecycleState.Faulted:
                WriteNotice("session faulted");
                break;
        }
    }

    public void OnCommand(in RuntimeCommandDelta delta)
    {
        if (delta.Status == RuntimeCommandStatus.Rejected)
        {
            WriteNotice(
                $"command rejected: {delta.Domain} {delta.Text}".TrimEnd());
        }
    }

    public void OnPortal(in RuntimePortalDelta delta)
    {
        if (delta.Portal.IsMaterialized)
            WriteNotice($"portal -> cell 0x{delta.Portal.DestinationCell:X8}");
    }

    public void OnEntity(in RuntimeEntityDelta delta)
    {
    }

    public void OnInventory(in RuntimeInventoryDelta delta)
    {
    }

    public void OnMovement(in RuntimeMovementDelta delta)
    {
    }

    public void OnCombat(in RuntimeCombatDelta delta)
    {
    }

    private void WriteLine(string text, bool dim)
    {
        string line = dim ? NoticePrefix + text : text;
        _output.WriteLine(_useColor && dim ? Dim + line + Reset : line);
        _output.Flush();
    }
}
