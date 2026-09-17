using AcDream.Core.Items;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime;
using AcDream.Runtime.Entities;

namespace AcDream.Headless.Plugins;

/// <summary>
/// Projects the shared object table onto the plugin world-object contract
/// for the headless host, mirroring AppAutomationSurface's projection.
/// </summary>
/// <remarks>
/// This is what a headless plugin needs to resolve a bare object id (a
/// trade partner's guid, most concretely) into a name and basic
/// classification. Only the fields a headless macro actually reads today
/// are populated with real data (name, weenie class id, item type,
/// container/wielder ids, object classification, ownership, position);
/// appraisal-detail fields (spell ids, icon id, capacities) are left at
/// their record defaults until a headless macro needs them.
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

        uint playerId = _runtime.PlayerIdentity.ServerGuid;
        bool owned = item is not null && IsPlayerOwned(item, playerId, _runtime.InventoryOwner.Objects);
        value = new PluginWorldObject(
            objectId,
            item?.WeenieClassId ?? 0u,
            item?.Name ?? record?.Snapshot.Name ?? $"0x{objectId:X8}",
            PluginObjectClassifier.Classify(
                (uint)(item?.Type ?? ItemType.None),
                item?.PublicWeenieBitfield ?? 0u),
            (uint)(item?.Type ?? ItemType.None),
            item?.ContainerId ?? 0u,
            item?.WielderId ?? 0u)
        {
            IsOwned = owned,
            IsLandscape = record is not null
                && !owned
                && (item?.ContainerId ?? 0u) == 0u
                && (item?.WielderId ?? 0u) == 0u,
            StackSize = Math.Max(1, item?.StackSize ?? 1),
        };
        return true;
    }

    private static bool IsPlayerOwned(
        ClientObject item,
        uint playerId,
        ClientObjectTable objects)
    {
        if (item.WielderId == playerId || item.ContainerId == playerId)
            return true;
        uint parentId = item.ContainerId;
        for (int depth = 0; parentId != 0u && depth < 4; depth++)
        {
            ClientObject? parent = objects.Get(parentId);
            if (parent is null)
                return false;
            if (parent.WielderId == playerId || parent.ContainerId == playerId)
                return true;
            parentId = parent.ContainerId;
        }
        return false;
    }
}
