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

    /// <summary>
    /// The character went through a portal, or a login placed it in the
    /// world. Raised from the one runtime owner that watches the portal, so
    /// both clients report the same transition once, in the same order. The
    /// sink stamps the revision; the caller passes zero.
    /// </summary>
    /// <param name="transition">Where the character went, and how far along.</param>
    void FirePortalTransition(PluginPortalTransition transition);

    /// <summary>
    /// A use the character started on an object finished, with whatever the
    /// server said about it.
    /// </summary>
    /// <param name="completion">Which use finished, and with what result.</param>
    void FireItemUseCompleted(PluginItemUseCompletion completion);

    /// <summary>
    /// An activation the character started on an object -- a lever, a door,
    /// a portal -- completed, failed or was interrupted.
    /// </summary>
    /// <param name="completion">Which activation ended, and how.</param>
    void FireActivationCompleted(PluginActivationCompletion completion);

    /// <summary>
    /// The walk the client is driving reached a new state: it started, made
    /// progress, arrived or gave up. Raised from the one runtime owner that
    /// watches the walk, so both clients report the same states in the same
    /// order for the same walk.
    /// </summary>
    /// <param name="report">Where the walk stands now.</param>
    void FireNavigationChanged(PluginGoToReport report);

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
