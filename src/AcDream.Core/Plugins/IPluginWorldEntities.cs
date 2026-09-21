using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Plugins;

/// <summary>
/// The one producer of what a plugin sees in the world: which objects are
/// there, and a notification as each one appears.
/// <para>
/// Membership and the notifications come from the same place, so a plugin
/// cannot be told about an object that is not in the list, or find one in the
/// list it was never told about. Every client answers these two questions
/// from one implementation of this, which is why the answer does not depend
/// on which client is running the plugin.
/// </para>
/// </summary>
public interface IPluginWorldEntities
{
    /// <summary>
    /// The objects in the world right now, as a snapshot that is not mutated
    /// under a reader.
    /// </summary>
    IReadOnlyList<WorldEntitySnapshot> Entities { get; }

    /// <summary>
    /// Starts telling a handler about objects as they appear, after first
    /// replaying the ones already there. A handler added late therefore hears
    /// about everything exactly once, in one order, without polling.
    /// </summary>
    void Subscribe(Action<WorldEntitySnapshot> handler);

    /// <summary>
    /// Stops telling a handler about objects. A handler that is not
    /// subscribed is ignored.
    /// </summary>
    void Unsubscribe(Action<WorldEntitySnapshot> handler);
}
