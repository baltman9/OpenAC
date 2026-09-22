using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Chat;

/// <summary>
/// The ordered set of interceptors consulted for a line the player typed,
/// before it is sent anywhere. Interceptors run in registration order and
/// the first one that does not pass decides.
/// </summary>
public sealed class ChatInputInterceptors
{
    private readonly object _gate = new();
    private Func<string, PluginChatInputDecision>[] _interceptors = [];
    private readonly HashSet<Func<string, PluginChatInputDecision>> _faulted = [];

    /// <summary>
    /// Reports an interceptor that threw. It is reported the first time only;
    /// the interceptor is skipped for the line that made it throw and chat
    /// carries on with the next one.
    /// </summary>
    public Action<Exception>? InterceptorFaulted { get; set; }

    public int Count
    {
        get
        {
            lock (_gate)
                return _interceptors.Length;
        }
    }

    /// <summary>
    /// Adds an interceptor at the end of the order. Dispose the result to
    /// remove it.
    /// </summary>
    public IDisposable Register(Func<string, PluginChatInputDecision> intercept)
    {
        ArgumentNullException.ThrowIfNull(intercept);
        lock (_gate)
        {
            var replacement = new Func<string, PluginChatInputDecision>[_interceptors.Length + 1];
            Array.Copy(_interceptors, replacement, _interceptors.Length);
            replacement[^1] = intercept;
            _interceptors = replacement;
        }
        return new Registration(this, intercept);
    }

    /// <summary>
    /// What the interceptors make of <paramref name="typed"/>: the first
    /// decision that is not a pass, or a pass when every interceptor passed
    /// or there are none.
    /// </summary>
    public PluginChatInputDecision Decide(string typed)
    {
        ArgumentNullException.ThrowIfNull(typed);
        Func<string, PluginChatInputDecision>[] interceptors;
        lock (_gate)
            interceptors = _interceptors;
        if (interceptors.Length == 0)
            return PluginChatInputDecision.Pass;

        foreach (Func<string, PluginChatInputDecision> interceptor in interceptors)
        {
            PluginChatInputDecision decision;
            try
            {
                decision = interceptor(typed);
            }
            catch (Exception error)
            {
                ReportFault(interceptor, error);
                continue;
            }
            if (decision.Action != PluginChatInputAction.Pass)
                return decision;
        }
        return PluginChatInputDecision.Pass;
    }

    private void ReportFault(
        Func<string, PluginChatInputDecision> interceptor,
        Exception error)
    {
        bool first;
        lock (_gate)
            first = _faulted.Add(interceptor);
        if (!first)
            return;
        try { InterceptorFaulted?.Invoke(error); }
        catch { /* a reporter that throws must not break chat either */ }
    }

    private void Remove(Func<string, PluginChatInputDecision> intercept)
    {
        lock (_gate)
        {
            int index = Array.IndexOf(_interceptors, intercept);
            if (index < 0)
                return;
            _faulted.Remove(intercept);
            if (_interceptors.Length == 1)
            {
                _interceptors = [];
                return;
            }
            var replacement = new Func<string, PluginChatInputDecision>[_interceptors.Length - 1];
            if (index > 0)
                Array.Copy(_interceptors, 0, replacement, 0, index);
            if (index < _interceptors.Length - 1)
            {
                Array.Copy(
                    _interceptors,
                    index + 1,
                    replacement,
                    index,
                    _interceptors.Length - index - 1);
            }
            _interceptors = replacement;
        }
    }

    private sealed class Registration(
        ChatInputInterceptors owner,
        Func<string, PluginChatInputDecision> intercept) : IDisposable
    {
        private ChatInputInterceptors? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.Remove(intercept);
    }
}
