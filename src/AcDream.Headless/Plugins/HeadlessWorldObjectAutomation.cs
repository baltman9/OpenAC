using AcDream.Core.Items;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;

namespace AcDream.Headless.Plugins;

/// <summary>
/// Projects the shared object table onto the plugin world-object contract
/// for the headless host, through the same
/// <see cref="RuntimeWorldObjectProjection"/> the graphical host uses, so
/// classification, ownership, and navigation math cannot drift between
/// hosts.
/// </summary>
/// <remarks>
/// <see cref="TryGet"/> and <see cref="CaptureObjects"/> are real. The
/// active-enchantment field is the one supplier the graphical host has
/// that this one doesn't (no headless macro tracks active enchantments
/// yet), so it always projects empty for that field; everything else --
/// name, weenie class id, item type, container/wielder ids, retail
/// classification, ownership, position, appraisal data, capacities,
/// stack size, door-open state, icon id -- comes straight from the same
/// object table and entity record both hosts already have.
/// <see cref="OpenContainerObjectId"/>, <see cref="TryCaptureProperties"/>,
/// and <see cref="Identify"/> remain the interface's inert defaults; they
/// need appraisal-wire and external-container machinery no headless macro
/// exercises yet.
/// </remarks>
internal sealed class HeadlessWorldObjectAutomation : IWorldObjectAutomation
{
    private readonly GameRuntime _runtime;

    internal HeadlessWorldObjectAutomation(GameRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public bool IsAvailable =>
        _runtime.Lifecycle.State == RuntimeLifecycleState.InWorld;

    public bool TryGet(uint objectId, out PluginWorldObject value)
    {
        if (!IsAvailable || objectId == 0u)
        {
            value = default;
            return false;
        }

        _runtime.EntityObjects.Entities.TryGetActive(
            objectId, out RuntimeEntityRecord? record);
        ClientObject? item = _runtime.InventoryOwner.Objects.Get(objectId);
        if (record is null && item is null)
        {
            value = default;
            return false;
        }

        value = RuntimeWorldObjectProjection.Project(
            record,
            item,
            _runtime.PlayerIdentity.ServerGuid,
            _runtime.InventoryOwner.Objects);
        return true;
    }

    public IReadOnlyList<PluginWorldObject> CaptureObjects()
    {
        if (!IsAvailable)
            return Array.Empty<PluginWorldObject>();

        ClientObjectTable objects = _runtime.InventoryOwner.Objects;
        uint playerId = _runtime.PlayerIdentity.ServerGuid;
        var result = new List<PluginWorldObject>();
        var captured = new HashSet<uint>();
        foreach (RuntimeEntityRecord record in
            _runtime.EntityObjects.Entities.ActiveRecords.ToArray())
        {
            ClientObject? item = objects.Get(record.ServerGuid);
            result.Add(RuntimeWorldObjectProjection.Project(
                record, item, playerId, objects));
            captured.Add(record.ServerGuid);
        }
        foreach (ClientObject item in objects.Objects)
        {
            if (!captured.Add(item.ObjectId))
                continue;
            result.Add(RuntimeWorldObjectProjection.Project(
                null, item, playerId, objects));
        }
        result.Sort(static (left, right) => left.ObjectId.CompareTo(right.ObjectId));
        return result;
    }
}
