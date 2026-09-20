using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Plugins;

/// <summary>
/// The one object a client hands the plugin surface for world events: a
/// plugin subscribes to it as <see cref="IEvents"/>, and the runtime raises
/// what happened through the <c>Fire</c> members.
///
/// It is a single interface rather than a pair so a client cannot supply the
/// half plugins listen to without the half the runtime speaks into. That
/// combination compiles, looks wired and delivers nothing: every world event
/// a plugin waits for stays silently dead for the whole run.
/// </summary>
public interface IPluginEventSink : IEvents
{
    /// <summary>The character has arrived in the world.</summary>
    void FireLoginComplete();

    /// <summary>The character has left the world.</summary>
    void FireLogoff();

    /// <summary>The character died, with the message the server sent.</summary>
    /// <param name="deathMessage">What the server said about the death.</param>
    void FireLocalPlayerDied(string deathMessage);

    /// <summary>An object appeared, moved, changed or went away.</summary>
    /// <param name="change">Which object, and what happened to it.</param>
    void FireObjectChanged(PluginObjectChange change);

    /// <summary>A container the character can see inside was opened.</summary>
    /// <param name="containerObjectId">The container that opened.</param>
    void FireContainerOpened(uint containerObjectId);

    /// <summary>A container the character could see inside was closed.</summary>
    /// <param name="containerObjectId">The container that closed.</param>
    void FireContainerClosed(uint containerObjectId);

    /// <summary>The player is being asked to accept or decline something.</summary>
    /// <param name="confirmation">What is being asked, and under which id.</param>
    void FireConfirmationRequested(PluginConfirmation confirmation);
}
