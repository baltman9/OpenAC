// Copyright (c) OpenAC contributors.
// Distributed under the terms of the MIT license.

using AcDream.Plugin.Abstractions;

namespace AcDream.Plugin.Tests.Fixtures;

/// <summary>
/// A settable <see cref="IPluginChat"/> for tests. Chat messages,
/// system messages, and submitted text are captured in lists for
/// post-test inspection. The <see cref="LinkClicked"/> event can be
/// fired on demand via <see cref="RaiseLinkClicked"/>.
/// </summary>
public sealed class FakePluginChat : IPluginChat
{
    private readonly object _gate = new();
    private Action<PluginChatLinkClicked>? _linkClicked;

    /// <summary>
    /// Every message posted through <see cref="PostSystemMessage"/>,
    /// in order.
    /// </summary>
    public List<string> SystemMessages { get; } = [];

    /// <summary>
    /// Every message posted through <see cref="PostMessage(string, int)"/>,
    /// in order.
    /// </summary>
    public List<(string Text, int LogTextType)> TypedMessages { get; } = [];

    /// <summary>
    /// Every text submitted through <see cref="Submit"/>, in order.
    /// </summary>
    public List<string> SubmittedText { get; } = [];

    /// <summary>
    /// Controls the return value of <see cref="Submit"/>.
    /// </summary>
    public bool SubmitResult { get; set; } = true;

    /// <summary>
    /// The chat messages returned by <see cref="CaptureMessages"/>.
    /// </summary>
    public List<PluginChatMessage> CapturedMessages { get; } = [];

    // ── IPluginChat ───────────────────────────────────────────────────────

    /// <inheritdoc/>
    public event Action<PluginChatLinkClicked> LinkClicked
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_gate) _linkClicked += value;
        }
        remove
        {
            if (value is null) return;
            lock (_gate) _linkClicked -= value;
        }
    }

    /// <inheritdoc/>
    public event Action<PluginChatMessage> Received
    {
        add { }
        remove { }
    }

    /// <inheritdoc/>
    public IReadOnlyList<PluginChatMessage> CaptureMessages(ulong afterSequence) =>
        CapturedMessages.Where(m => m.Sequence > afterSequence).ToArray();

    /// <inheritdoc/>
    public IDisposable RegisterFilter(Func<PluginChatMessage, bool> suppress) =>
        NoOpPluginRegistration.Instance;

    /// <summary>
    /// Every input interceptor currently installed, in registration order.
    /// A test drives them with <see cref="Intercept"/>.
    /// </summary>
    public List<Func<string, PluginChatInputDecision>> InputInterceptors { get; } = [];

    /// <inheritdoc/>
    public IDisposable RegisterInputInterceptor(
        Func<string, PluginChatInputDecision> intercept)
    {
        ArgumentNullException.ThrowIfNull(intercept);
        lock (_gate)
            InputInterceptors.Add(intercept);
        return new InterceptorRemoval(this, intercept);
    }

    /// <summary>
    /// Runs <paramref name="typed"/> through the installed interceptors the
    /// way a host does: in order, the first that does not pass decides.
    /// </summary>
    public PluginChatInputDecision Intercept(string typed)
    {
        Func<string, PluginChatInputDecision>[] interceptors;
        lock (_gate)
            interceptors = InputInterceptors.ToArray();
        foreach (Func<string, PluginChatInputDecision> interceptor in interceptors)
        {
            PluginChatInputDecision decision = interceptor(typed);
            if (decision.Action != PluginChatInputAction.Pass)
                return decision;
        }
        return PluginChatInputDecision.Pass;
    }

    private sealed class InterceptorRemoval(
        FakePluginChat owner,
        Func<string, PluginChatInputDecision> intercept) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._gate)
                owner.InputInterceptors.Remove(intercept);
        }
    }

    /// <inheritdoc/>
    public void PostSystemMessage(string text)
    {
        SystemMessages.Add(text);
    }

    /// <inheritdoc/>
    public void PostMessage(string text, int logTextType)
    {
        TypedMessages.Add((text, logTextType));
    }

    /// <inheritdoc/>
    public bool Submit(string text)
    {
        SubmittedText.Add(text);
        return SubmitResult;
    }

    // ── Raise methods for tests ───────────────────────────────────────────

    /// <summary>
    /// Fires <see cref="LinkClicked"/> for every listener.
    /// </summary>
    public void RaiseLinkClicked(PluginChatLinkClicked link)
    {
        Action<PluginChatLinkClicked>? handlers;
        lock (_gate)
            handlers = _linkClicked;
        if (handlers is null)
            return;
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try { ((Action<PluginChatLinkClicked>)handler)(link); }
            catch { }
        }
    }
}