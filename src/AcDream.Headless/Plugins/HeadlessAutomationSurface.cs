using AcDream.Core.Chat;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime;
using AcDream.Runtime.Gameplay;

namespace AcDream.Headless.Plugins;

internal sealed class HeadlessAutomationSurface
    : IAutomationSurface, IPluginChat, ILoginAutomation, IDialogAutomation
{
    private readonly GameRuntime _runtime;
    private readonly Func<string, bool>? _submitChatText;
    private readonly Func<bool>? _requestLogout;
    private readonly Func<uint, bool, bool>? _answerConfirmation;
    private readonly object _gate = new();
    private Action<PluginChatMessage>? _chatReceived;

    internal HeadlessAutomationSurface(
        GameRuntime runtime,
        Func<string, bool>? submitChatText = null,
        Func<bool>? requestLogout = null,
        Func<uint, bool, bool>? answerConfirmation = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _submitChatText = submitChatText;
        _requestLogout = requestLogout;
        _answerConfirmation = answerConfirmation;
    }

    public bool IsAvailable =>
        _runtime.Lifecycle.State == RuntimeLifecycleState.InWorld;

    public ICharacterInfo Character => NoOpAutomationSurface.Instance.Character;
    public ISpellCatalog Spells => NoOpAutomationSurface.Instance.Spells;
    public IMagicCommands Magic => NoOpAutomationSurface.Instance.Magic;
    public IPluginChat Chat => this;
    public ILoginAutomation Login => this;
    public IDialogAutomation Dialogs => this;

    bool ILoginAutomation.Logout() => IsAvailable && (_requestLogout?.Invoke() ?? false);

    bool IDialogAutomation.Answer(uint contextId, bool accept) =>
        _answerConfirmation?.Invoke(contextId, accept) ?? false;

    public event Action<PluginChatMessage> Received
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_gate)
                _chatReceived += value;
        }
        remove
        {
            if (value is null)
                return;
            lock (_gate)
                _chatReceived -= value;
        }
    }

    public IDisposable RegisterFilter(Func<PluginChatMessage, bool> suppress)
    {
        ArgumentNullException.ThrowIfNull(suppress);
        return _runtime.CommunicationOwner.Chat.Filters.Register(suppress);
    }

    /// <summary>Projects one delivered line and raises <see cref="Received"/>.</summary>
    internal void RaiseChatReceived(ulong sequence, in RuntimeChatEntry entry)
    {
        Action<PluginChatMessage>? handlers;
        lock (_gate)
            handlers = _chatReceived;
        if (handlers is null)
            return;

        var message = new PluginChatMessage(
            sequence,
            entry.SenderGuid,
            entry.Kind,
            entry.Sender,
            entry.Text,
            entry.ChannelName)
        {
            LogTextType = entry.LogTextType,
            CombatKind = entry.CombatKind,
            Received = entry.Received,
        };
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try { ((Action<PluginChatMessage>)handler)(message); }
            catch { /* plugin errors don't propagate out of event dispatch */ }
        }
    }

    public void PostSystemMessage(string text) =>
        PostMessage(text, (int)RetailLogTextType.Default);

    public void PostMessage(string text, int logTextType)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
            return;
        _runtime.CommunicationOwner.AddText(
            text,
            RetailLogTextTypeCodec.FromPluginValue(logTextType));
    }

    public bool Submit(string text) =>
        IsAvailable
        && !string.IsNullOrWhiteSpace(text)
        && (_submitChatText?.Invoke(text) ?? false);
}
