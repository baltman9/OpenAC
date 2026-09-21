using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Plugins;

public sealed class WorldGameState : IGameState, ISceneryObjectStore
{
    private readonly List<WorldEntitySnapshot> _scenery = new();
    private readonly Dictionary<uint, int> _sceneryIndexById = new();
    private IPluginWorldEntities? _worldEntities;

    /// <summary>
    /// The objects in the world, answered by the one producer that reads the
    /// runtime's object directory. This type keeps no object list of its own,
    /// so nothing in a host can put something in front of a plugin that the
    /// runtime does not have. Empty until the producer is bound.
    /// </summary>
    public IReadOnlyList<WorldEntitySnapshot> Entities =>
        _worldEntities?.Entities ?? [];

    /// <summary>
    /// Names the producer that answers <see cref="Entities"/>. Called once,
    /// where the host builds its runtime.
    /// </summary>
    public void BindWorldEntities(IPluginWorldEntities worldEntities)
    {
        ArgumentNullException.ThrowIfNull(worldEntities);
        if (_worldEntities is not null)
            throw new InvalidOperationException(
                "The world objects a plugin reads already have a producer; a "
                + "second one would mean two answers to the same question.");
        _worldEntities = worldEntities;
    }

    /// <summary>
    /// The drawn decoration of the landscape. It is kept apart from
    /// <see cref="Entities"/> because it is a different kind of thing: it has
    /// no server identity and no command can name it.
    /// </summary>
    public IReadOnlyList<WorldEntitySnapshot> SceneryObjects => _scenery;

    public Func<IReadOnlyList<ContractSnapshot>>? ContractsSource { get; set; }

    public IReadOnlyList<ContractSnapshot> Contracts =>
        ContractsSource?.Invoke() ?? [];

    public void AddScenery(WorldEntitySnapshot snapshot) =>
        Upsert(_scenery, _sceneryIndexById, snapshot);

    public bool RemoveSceneryById(uint id) =>
        Remove(_scenery, _sceneryIndexById, id);

    public void ClearScenery()
    {
        _scenery.Clear();
        _sceneryIndexById.Clear();
    }

    private static void Upsert(
        List<WorldEntitySnapshot> items,
        Dictionary<uint, int> indexById,
        WorldEntitySnapshot snapshot)
    {
        if (indexById.TryGetValue(snapshot.Id, out int index))
        {
            items[index] = snapshot;
            return;
        }

        indexById.Add(snapshot.Id, items.Count);
        items.Add(snapshot);
    }

    private static bool Remove(
        List<WorldEntitySnapshot> items,
        Dictionary<uint, int> indexById,
        uint id)
    {
        if (!indexById.Remove(id, out int index))
            return false;

        int lastIndex = items.Count - 1;
        if (index != lastIndex)
        {
            WorldEntitySnapshot moved = items[lastIndex];
            items[index] = moved;
            indexById[moved.Id] = index;
        }
        items.RemoveAt(lastIndex);
        return true;
    }
}
