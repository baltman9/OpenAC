using System.Text.Json;

namespace AcDream.Runtime.Session;

/// <summary>
/// Appends one session's status events to its status file, one JSON line
/// per event.
/// </summary>
public sealed class SessionStatusWriter : IDisposable
{
    private const int VocabularyVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string? _path;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string> _diagnostic;
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private bool _directoryEnsured;
    private bool _failureReported;
    private bool _connected;
    private bool _exited;
    private bool _disposed;

    /// <param name="path">
    /// Where the events go; absent or blank turns the writer into a no-op.
    /// </param>
    /// <param name="timeProvider">The clock that stamps each event.</param>
    /// <param name="diagnostic">
    /// Where a write failure is reported; the host's own log when it has
    /// one, standard error otherwise.
    /// </param>
    public SessionStatusWriter(
        string? path,
        TimeProvider? timeProvider = null,
        Action<string>? diagnostic = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _diagnostic = diagnostic ?? Console.Error.WriteLine;
    }

    /// <summary>
    /// Whether events are being written. A failed write does not turn this
    /// off: the writer keeps offering the next line a chance.
    /// </summary>
    public bool IsEnabled => _path is not null && !_disposed;

    public void Started(string sessionId) =>
        Write(new
        {
            v = VocabularyVersion,
            e = "started",
            t = Now(),
            sessionId,
        });

    public void Connected(string sessionId)
    {
        if (!IsEnabled)
            return;

        lock (_gate)
        {
            if (_disposed || _exited)
                return;

            if (_connected)
            {
                if (!TryWriteLocked(new
                    {
                        v = VocabularyVersion,
                        e = "disconnected",
                        t = Now(),
                        sessionId,
                        reason = "reconnect",
                    }))
                {
                    return;
                }
                _connected = false;
            }

            if (TryWriteLocked(new
                {
                    v = VocabularyVersion,
                    e = "connected",
                    t = Now(),
                    sessionId,
                }))
            {
                _connected = true;
            }
        }
    }

    public void CharacterList(string sessionId, LiveSessionRosterReport roster)
    {
        ArgumentNullException.ThrowIfNull(roster);
        if (!IsEnabled)
            return;

        Write(new
        {
            v = VocabularyVersion,
            e = "characterList",
            t = Now(),
            sessionId,
            accountName = roster.AccountName,
            slotCount = roster.SlotCount,
            characters = roster.Entries
                .Select(static entry => new
                {
                    id = entry.Id,
                    name = entry.Name,
                    secondsGreyedOut = entry.SecondsGreyedOut,
                })
                .ToArray(),
        });
    }

    public void EnteredWorld(string sessionId, uint characterId, string characterName) =>
        Write(new
        {
            v = VocabularyVersion,
            e = "enteredWorld",
            t = Now(),
            sessionId,
            characterId,
            characterName,
        });

    public void CharacterCreated(string sessionId, uint guid, string name) =>
        Write(new
        {
            v = VocabularyVersion,
            e = "characterCreated",
            t = Now(),
            sessionId,
            guid,
            name,
        });

    public void CreationFailed(string sessionId, uint code, string reason, string name) =>
        Write(new
        {
            v = VocabularyVersion,
            e = "creationFailed",
            t = Now(),
            sessionId,
            code,
            reason,
            name,
        });

    public void PluginLoaded(string sessionId, string plugin) =>
        Write(new
        {
            v = VocabularyVersion,
            e = "pluginLoaded",
            t = Now(),
            sessionId,
            plugin,
        });

    public void PluginFailed(string sessionId, string plugin, string error) =>
        Write(new
        {
            v = VocabularyVersion,
            e = "pluginFailed",
            t = Now(),
            sessionId,
            plugin,
            error,
        });

    public void LoginCommandFailed(
        string sessionId,
        int commandIndex,
        string command,
        string error) =>
        Write(new
        {
            v = VocabularyVersion,
            e = "loginCommandFailed",
            t = Now(),
            sessionId,
            commandIndex,
            command,
            error,
        });

    public void Disconnected(string sessionId, string reason)
    {
        if (!IsEnabled)
            return;

        lock (_gate)
        {
            if (_disposed || _exited)
                return;

            if (TryWriteLocked(new
                {
                    v = VocabularyVersion,
                    e = "disconnected",
                    t = Now(),
                    sessionId,
                    reason,
                }))
            {
                _connected = false;
            }
        }
    }

    public void Exited(string sessionId, int code, string reason)
    {
        if (!IsEnabled)
            return;

        lock (_gate)
        {
            if (_disposed || _exited)
                return;

            if (_connected)
            {
                if (!TryWriteLocked(new
                    {
                        v = VocabularyVersion,
                        e = "disconnected",
                        t = Now(),
                        sessionId,
                        reason = "process-exit",
                    }))
                {
                    return;
                }
                _connected = false;
            }

            if (TryWriteLocked(new
                {
                    v = VocabularyVersion,
                    e = "exited",
                    t = Now(),
                    sessionId,
                    code,
                    reason,
                }))
            {
                _exited = true;
            }
        }
    }

    private string Now() =>
        _timeProvider.GetUtcNow().ToString(
            "O",
            System.Globalization.CultureInfo.InvariantCulture);

    private void Write<T>(T value)
    {
        if (_path is null || _disposed)
            return;

        lock (_gate)
        {
            if (_disposed || _exited)
                return;

            _ = TryWriteLocked(value);
        }
    }

    private bool TryWriteLocked<T>(T value)
    {
        string path = _path!;
        try
        {
            string line = JsonSerializer.Serialize(value, JsonOptions);
            StreamWriter writer = EnsureWriterLocked(path);
            writer.WriteLine(line);
            // Flushed line by line: whoever is watching this file wants the
            // event as it happens, not when a buffer happens to fill.
            writer.Flush();
            _failureReported = false;
            return true;
        }
        catch (Exception error) when (IsRecoverableIoFailure(error))
        {
            // The handle may be the broken part, so let it go; the next
            // line opens a fresh one. No retry loop, no waiting: a session
            // has better things to do than fight for a log file.
            CloseWriterLocked();
            ReportFailureOnceLocked(path, error);
            return false;
        }
    }

    /// <summary>
    /// The one handle this writer holds for the session's lifetime. It is
    /// opened for appending and shared for reading, writing and deletion,
    /// so that anyone watching the file -- a launcher, a supervisor, a
    /// person tailing it -- can never take the session's ability to write
    /// away from it. Opening once instead of per line also means a watcher
    /// that asks for exclusive reading is turned away itself rather than
    /// interrupting the stream it came to watch.
    /// </summary>
    private StreamWriter EnsureWriterLocked(string path)
    {
        if (_writer is not null)
            return _writer;

        EnsureDirectory(path);
        FileStream stream = new(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(stream);
        return _writer;
    }

    private void CloseWriterLocked()
    {
        StreamWriter? writer = _writer;
        _writer = null;
        if (writer is null)
            return;

        try
        {
            writer.Dispose();
        }
        catch (Exception error)
            when (IsRecoverableIoFailure(error)
                || error is ObjectDisposedException)
        {
        }
    }

    private void EnsureDirectory(string path)
    {
        if (_directoryEnsured)
            return;

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        _directoryEnsured = true;
    }

    /// <summary>
    /// Says once that a status line could not be written. Repeating it for
    /// every line would bury the host's own output; the next line that does
    /// get written arms this again, so a fresh problem is still heard.
    /// </summary>
    private void ReportFailureOnceLocked(string path, Exception error)
    {
        if (_failureReported)
            return;

        _failureReported = true;
        try
        {
            _diagnostic(
                $"[status-writer] could not write to '{path}' "
                + $"({error.GetType().Name}: {error.Message}); the next "
                + "event will try again.");
        }
        catch (Exception diagnosticError)
            when (IsRecoverableIoFailure(diagnosticError)
                || diagnosticError is ObjectDisposedException
                or InvalidOperationException)
        {
        }
    }

    /// <summary>
    /// Releases the status file. Nothing is written afterwards, and the
    /// file can be read, moved or deleted freely.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            CloseWriterLocked();
        }
    }

    private static bool IsRecoverableIoFailure(Exception error) =>
        error is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException
            or System.Security.SecurityException
            or DirectoryNotFoundException;
}
