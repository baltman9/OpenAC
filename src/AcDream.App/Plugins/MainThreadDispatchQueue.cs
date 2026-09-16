using System.Collections.Concurrent;

namespace AcDream.App.Plugins;

/// <summary>
/// Marshals a small unit of work from whatever thread a plugin called from
/// onto the window's own thread, blocking the caller until it has run.
///
/// GLFW's clipboard calls (what <see cref="WindowPluginClipboard"/> ends up
/// making through Silk.NET's <c>IKeyboard.ClipboardText</c>) are documented
/// as main-thread-only: calling them off that thread does not throw, it
/// just silently does nothing to the OS clipboard -- exactly how the bug
/// this queue exists to fix presented (TrySetText returned true, the
/// clipboard stayed empty). A plugin's own event handlers can run on
/// whatever thread the plugin chose (an async network continuation, its
/// own background thread), so the write has to be marshaled rather than
/// assumed to already be on the right thread.
/// </summary>
public sealed class MainThreadDispatchQueue
{
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
    private readonly ConcurrentQueue<Action> _pending = new();

    /// <summary>True when called from the thread that constructed this queue.</summary>
    public bool IsOnOwnerThread => Environment.CurrentManagedThreadId == _ownerThreadId;

    /// <summary>
    /// Runs every action queued so far. Must only be called from the owner
    /// thread (the window's update loop calls this once per frame).
    /// </summary>
    public void Drain()
    {
        while (_pending.TryDequeue(out Action? action))
            action();
    }

    /// <summary>
    /// Runs <paramref name="action"/> on the owner thread and waits for it
    /// to finish, up to <paramref name="timeout"/>. Runs inline, with no
    /// queueing or waiting, when already on the owner thread. Returns false
    /// if the timeout elapsed before a queued action ran (the owner thread
    /// stopped draining -- window shutting down, most likely).
    /// </summary>
    public bool InvokeAndWait(Action action, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (IsOnOwnerThread)
        {
            action();
            return true;
        }
        using var done = new ManualResetEventSlim(initialState: false);
        _pending.Enqueue(() =>
        {
            try
            {
                action();
            }
            finally
            {
                done.Set();
            }
        });
        return done.Wait(timeout);
    }
}
