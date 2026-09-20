using AcDream.Runtime.Chat;

namespace AcDream.Headless.Hosting;

/// <summary>
/// One session a console can talk to: who it is, the chat entry a typed line
/// goes into, and whether a verb on its command registry is already spoken
/// for.
/// </summary>
/// <param name="Id">The session id, which also addresses it.</param>
/// <param name="Entry">
/// The shared chat entry. The console reads the draft, the active channel and
/// the reply target off it, exactly as a chat box does.
/// </param>
/// <param name="Submit">
/// Sends a line through that entry, or the draft as it stands when the line
/// is null.
/// </param>
/// <param name="ClaimsVerb">
/// Whether the session's one command registry already answers a verb.
/// </param>
internal sealed record HeadlessConsoleSession(
    string Id,
    RuntimeChatEntryOwner Entry,
    Func<string?, SubmitOutcome> Submit,
    Func<string, bool> ClaimsVerb);

/// <summary>
/// The console as the chat entry's second front end. A typed line goes into
/// the one <see cref="RuntimeChatEntryOwner"/> the chat box uses, so it is
/// routed, channelled and answered identically: what the line did is said by
/// the shared feedback, in the chat feed, not by this class.
///
/// The console keeps three verbs of its own, and each is offered to the
/// session's command registry first, so a plugin that registers the same verb
/// is never shadowed.
/// </summary>
internal sealed class HeadlessConsoleController : IDisposable
{
    /// <summary>Ends the process, gracefully.</summary>
    internal const string QuitVerb = "quit";

    /// <summary>Chooses which session an unaddressed line goes to.</summary>
    internal const string SessionVerb = "session";

    /// <summary>Lists the sessions this process is running.</summary>
    internal const string SessionsVerb = "sessions";

    private readonly HeadlessConsoleInputReader _reader;
    private readonly TextWriter _output;
    private readonly HeadlessConsoleSession[] _sessions;
    private readonly CancellationTokenSource _quitRequested;
    private readonly List<(HeadlessConsoleSession Session, Action<string> Handler)>
        _draftSubscriptions = [];
    private int _default;
    private bool _disposed;

    internal HeadlessConsoleController(
        TextReader input,
        TextWriter output,
        IReadOnlyList<HeadlessConsoleSession> sessions,
        CancellationTokenSource quitRequested)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(sessions);
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _quitRequested = quitRequested
            ?? throw new ArgumentNullException(nameof(quitRequested));
        if (sessions.Count == 0)
        {
            throw new ArgumentException(
                "A console needs at least one session to talk to.",
                nameof(sessions));
        }
        _sessions = sessions.ToArray();
        foreach (HeadlessConsoleSession session in _sessions)
        {
            HeadlessConsoleSession captured = session;
            void OnDraftChanged(string draft) => ShowDraft(captured, draft);
            captured.Entry.DraftChanged += OnDraftChanged;
            _draftSubscriptions.Add((captured, OnDraftChanged));
        }
        _reader = new HeadlessConsoleInputReader(input);
    }

    internal int LastDrainCount { get; private set; }

    internal HeadlessConsoleInputReader Reader => _reader;

    /// <summary>Which session an unaddressed line goes to.</summary>
    internal string DefaultSessionId => _sessions[_default].Id;

    internal void DrainDue()
    {
        int count = 0;
        while (_reader.TryDequeue(out string line))
        {
            Handle(line);
            count++;
        }
        LastDrainCount = count;
    }

    private void Handle(string rawLine)
    {
        HeadlessConsoleSession session = _sessions[_default];
        string line = rawLine;
        if (TrySplitAddress(line, out HeadlessConsoleSession? addressed, out string rest))
        {
            session = addressed!;
            line = rest;
        }

        string trimmed = line.Trim();
        // Enter on an empty console line is Enter on an empty chat entry: it
        // sends whatever is staged there, which is how a line a plugin
        // composed goes out.
        if (trimmed.Length == 0)
        {
            Send(session, null);
            return;
        }

        if (IsOwnVerb(session, trimmed, QuitVerb, out _))
        {
            WriteLine(session, "quitting (graceful logout)");
            _quitRequested.Cancel();
            return;
        }

        if (IsOwnVerb(session, trimmed, SessionsVerb, out _))
        {
            ListSessions();
            return;
        }

        if (IsOwnVerb(session, trimmed, SessionVerb, out string argument))
        {
            ChooseSession(session, argument);
            return;
        }

        Send(session, line);
    }

    private void Send(HeadlessConsoleSession session, string? line)
    {
        try
        {
            // What the line did is said by the shared feedback, in the chat
            // feed the console already prints, so nothing is said here.
            _ = session.Submit(line);
        }
        catch (Exception error)
        {
            WriteLine(
                session,
                "command failed: " + error.GetBaseException().Message);
        }
    }

    /// <summary>
    /// A line the console answers itself, but only where the session's one
    /// command registry does not already answer that verb.
    /// </summary>
    private static bool IsOwnVerb(
        HeadlessConsoleSession session,
        string trimmed,
        string verb,
        out string argument)
    {
        argument = string.Empty;
        if (trimmed.Length < verb.Length + 1 || trimmed[0] != '/')
            return false;
        if (!trimmed.AsSpan(1).StartsWith(verb, StringComparison.OrdinalIgnoreCase))
            return false;
        int after = verb.Length + 1;
        if (trimmed.Length > after && trimmed[after] is not (' ' or '\t'))
            return false;
        if (session.ClaimsVerb(verb))
            return false;
        argument = trimmed.Length > after ? trimmed[after..].Trim() : string.Empty;
        return true;
    }

    /// <summary>
    /// <c>@id line</c> sends one line to a named session without changing
    /// which one the next line goes to. It is only read as an address when
    /// the process really runs that session, so the server verbs that start
    /// with an at sign are left alone.
    /// </summary>
    private bool TrySplitAddress(
        string rawLine,
        out HeadlessConsoleSession? session,
        out string rest)
    {
        session = null;
        rest = rawLine;
        string trimmed = rawLine.TrimStart();
        if (_sessions.Length < 2 || trimmed.Length < 2 || trimmed[0] != '@')
            return false;

        int separator = trimmed.IndexOfAny([' ', '\t'], 1);
        string id = separator < 0 ? trimmed[1..] : trimmed[1..separator];
        foreach (HeadlessConsoleSession candidate in _sessions)
        {
            if (!string.Equals(candidate.Id, id, StringComparison.Ordinal))
                continue;
            session = candidate;
            rest = separator < 0 ? string.Empty : trimmed[(separator + 1)..];
            return true;
        }
        return false;
    }

    private void ChooseSession(HeadlessConsoleSession current, string argument)
    {
        if (argument.Length == 0)
        {
            WriteLine(current, "talking to " + DefaultSessionId);
            return;
        }
        for (int index = 0; index < _sessions.Length; index++)
        {
            if (!string.Equals(_sessions[index].Id, argument, StringComparison.Ordinal))
                continue;
            _default = index;
            WriteLine(current, "now talking to " + argument);
            return;
        }
        WriteLine(current, "no session is called " + argument);
    }

    private void ListSessions()
    {
        foreach (HeadlessConsoleSession session in _sessions)
        {
            string marker = ReferenceEquals(session, _sessions[_default])
                ? " (talking to this one)"
                : string.Empty;
            WriteLine(session, "session " + session.Id + marker);
        }
    }

    /// <summary>
    /// A draft something else staged -- a plugin composing a line -- is shown
    /// as it stands, so the person at the console can see what the next Enter
    /// will send.
    /// </summary>
    private void ShowDraft(HeadlessConsoleSession session, string draft)
    {
        if (_disposed || draft.Length == 0)
            return;
        WriteLine(session, "draft: " + draft);
    }

    /// <summary>
    /// A line about the session itself. With more than one session every line
    /// says which one it belongs to.
    /// </summary>
    private void WriteLine(HeadlessConsoleSession session, string text)
    {
        _output.WriteLine(
            HeadlessConsoleRenderer.NoticePrefix
            + (_sessions.Length > 1 ? "[" + session.Id + "] " : string.Empty)
            + text);
        _output.Flush();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach ((HeadlessConsoleSession session, Action<string> handler)
            in _draftSubscriptions)
        {
            session.Entry.DraftChanged -= handler;
        }
        _draftSubscriptions.Clear();
        _reader.Dispose();
    }
}
