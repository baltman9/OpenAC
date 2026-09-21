using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Plugins;

/// <summary>
/// Where the drawn decoration of the landscape is recorded for a plugin to
/// read. It is a separate store from the objects the world server sends,
/// because the two are different kinds of thing: scenery has no server
/// identity and no command can name it.
/// </summary>
public interface ISceneryObjectStore
{
    /// <summary>
    /// Records a piece of scenery, replacing an entry already held under the
    /// same drawn id.
    /// </summary>
    void AddScenery(WorldEntitySnapshot snapshot);

    /// <summary>
    /// Forgets a piece of scenery. Answers whether one was held.
    /// </summary>
    bool RemoveSceneryById(uint id);
}
