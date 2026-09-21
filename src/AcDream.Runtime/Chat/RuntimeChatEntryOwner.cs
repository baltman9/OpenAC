using System;
using System.Collections.Generic;

namespace AcDream.Runtime.Chat;

/// <summary>
/// The lines a chat entry can recall, kept outside the entry itself so every
/// front end recalls the same ones.
/// </summary>
public interface IChatEntryHistory
{
    /// <summary>How many lines are remembered.</summary>
    int Count { get; }

    /// <summary>Remembers a line that was just sent.</summary>
    void Remember(string line);

    /// <summary>
    /// The line before the one being recalled, or null when there is nothing
    /// older to go back to.
    /// </summary>
    string? RecallPrevious();

    /// <summary>
    /// The line after the one being recalled. Null means the walk has come
    /// back past the newest line and the entry should be empty again.
    /// </summary>
    string? RecallNext();

    /// <summary>Puts the walk back to "not recalling anything".</summary>
    void ResetRecall();

    /// <summary>Whether a recalled line is currently in the entry.</summary>
    bool IsRecalling { get; }
}

/// <summary>
/// The chat entry: the line being typed, where it will go when it is sent,
/// and the lines sent before it. One owner serves every front end, so a line
/// typed at a console does exactly what the same line typed in a chat box
/// does: the same command routing, the same channel, the same reply target.
/// A front end adds only its own presentation, a caret and a selection in a
/// window, an echoed draft on a console.
/// </summary>
public sealed class RuntimeChatEntryOwner : IChatEntryHistory
{
    /// <summary>How many sent lines are remembered for recall.</summary>
    public const int HistoryLimit = 100;

    private readonly List<string> _history = [];
    private int _recallIndex = -1;
    private string _draft = string.Empty;
    private bool _composedDraftPending;
    private Func<bool>? _inputActiveSource;
    private Func<bool>? _focusEntry;

    /// <summary>
    /// Raised when the draft was changed by something other than the front end
    /// the player is typing into: a composed line, a recalled one, or the
    /// clearing that follows a send.
    /// </summary>
    public event Action<string>? DraftChanged;

    /// <summary>The line as it stands, unsent.</summary>
    public string Draft => _draft;

    /// <summary>Where a plain line of text goes when it is sent.</summary>
    public ChatChannelKind ActiveChannel { get; private set; } = ChatChannelKind.Say;

    /// <summary>Who a tell is addressed to while the entry is aimed at one.</summary>
    public string? TellTarget { get; private set; }

    /// <summary>
    /// The object id behind <see cref="TellTarget"/>: speaking to whoever is
    /// selected aims at the object, not at its name.
    /// </summary>
    public uint TellTargetGuid { get; private set; }

    /// <summary>
    /// Whether the keyboard is going into the chat entry rather than into the
    /// character. Automation that steers by holding keys has to know.
    /// </summary>
    public bool IsInputActive =>
        _inputActiveSource is { } source ? source() : _composedDraftPending;

    /// <inheritdoc />
    public int Count => _history.Count;

    /// <inheritdoc />
    public bool IsRecalling => _recallIndex >= 0;

    /// <summary>
    /// Lends the owner the two things only a front end with a keyboard knows:
    /// whether the entry is taking keystrokes, and how to put the keyboard in
    /// it. A front end that has neither, a console, leaves both out, and the
    /// entry counts as taking input between a composed line and the send that
    /// follows it.
    /// </summary>
    /// <param name="isInputActive">
    /// Whether typing currently goes to text rather than to the character.
    /// </param>
    public void BindInputActiveSource(Func<bool>? isInputActive) =>
        _inputActiveSource = isInputActive;

    /// <param name="focusEntry">
    /// Puts the keyboard in the entry; false when there is no entry to put it
    /// in, which refuses the compose that asked for it.
    /// </param>
    public void BindEntryFocus(Func<bool>? focusEntry) =>
        _focusEntry = focusEntry;

    /// <summary>
    /// The draft as the player last left it. Setting it to what it already is
    /// tells nobody, so a front end can echo its own typing back without a
    /// loop.
    /// </summary>
    public void SetDraft(string? text)
    {
        string value = text ?? string.Empty;
        if (string.Equals(_draft, value, StringComparison.Ordinal))
            return;
        _draft = value;
        DraftChanged?.Invoke(value);
    }

    /// <summary>
    /// Stages text in the entry without sending it. Refused while the player
    /// is already typing, so an automated paste never overwrites a line they
    /// are in the middle of.
    /// </summary>
    public bool Compose(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (IsInputActive)
            return false;
        if (_focusEntry is { } focus && !focus())
            return false;
        ResetRecall();
        SetDraft(text);
        _composedDraftPending = true;
        return true;
    }

    /// <summary>
    /// Sends a line the way the chat box sends it: the same routing, the same
    /// active channel and the same tell target. The line is remembered and the
    /// draft is cleared, exactly as pressing Enter in the entry does.
    /// </summary>
    /// <param name="text">
    /// The line to send, or null to send the draft as it stands.
    /// </param>
    public SubmitOutcome Submit(
        string? text,
        IChatCommandFeedback feedback,
        ICommandBus bus)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        ArgumentNullException.ThrowIfNull(bus);
        string line = text ?? _draft;
        _composedDraftPending = false;
        if (line.Trim().Length == 0)
        {
            Clear();
            return SubmitOutcome.Empty;
        }

        SubmitOutcome outcome = ChatCommandRouter.Submit(
            line, feedback, bus, ActiveChannel, TellTarget, TellTargetGuid);
        Remember(line);
        Clear();
        return outcome;
    }

    /// <summary>Empties the draft and stops recalling.</summary>
    public void Clear()
    {
        ResetRecall();
        SetDraft(string.Empty);
    }

    // -- What was sent before ---------------------------------------------

    /// <inheritdoc />
    public void Remember(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        _history.Add(line);
        if (_history.Count > HistoryLimit)
            _history.RemoveAt(0);
        _recallIndex = -1;
    }

    /// <inheritdoc />
    public string? RecallPrevious()
    {
        if (_history.Count == 0)
            return null;
        _recallIndex = _recallIndex < 0
            ? _history.Count - 1
            : Math.Max(0, _recallIndex - 1);
        string line = _history[_recallIndex];
        SetDraft(line);
        return line;
    }

    /// <inheritdoc />
    public string? RecallNext()
    {
        if (_recallIndex < 0)
            return null;
        _recallIndex++;
        if (_recallIndex >= _history.Count)
        {
            _recallIndex = -1;
            SetDraft(string.Empty);
            return null;
        }
        string line = _history[_recallIndex];
        SetDraft(line);
        return line;
    }

    /// <inheritdoc />
    public void ResetRecall() => _recallIndex = -1;

    // -- Where a line goes -------------------------------------------------

    /// <summary>
    /// Aims plain text at a channel. Any tell target is dropped: a channel and
    /// a named listener are alternatives, not a pair.
    /// </summary>
    public void SetChannel(ChatChannelKind channel)
    {
        ActiveChannel = channel;
        TellTarget = null;
        TellTargetGuid = 0u;
    }

    /// <summary>Aims plain text at one listener by name and object id.</summary>
    public void SetTellTarget(string name, uint targetGuid)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ActiveChannel = ChatChannelKind.Tell;
        TellTarget = name;
        TellTargetGuid = targetGuid;
    }
}
