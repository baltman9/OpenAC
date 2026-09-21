using AcDream.Plugin.Abstractions;

namespace AcDream.Runtime.Chat;

public interface ICommandBus
{
    void Publish<T>(T command) where T : notnull;
}

public interface IPluginCommandBus : ICommandBus
{
    bool TryHandlePluginCommand(string commandLine);

    /// <summary>
    /// What the plugins loaded behind this bus make of a line the player
    /// typed, before it is offered to plugin verbs or sent. A bus with no
    /// plugins behind it passes every line.
    /// </summary>
    PluginChatInputDecision InterceptChatInput(string typed) =>
        PluginChatInputDecision.Pass;
}
