using System.Numerics;
using AcDream.Core.Items;
using AcDream.Core.Physics;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Physics;
using AcDream.Runtime.World;

namespace AcDream.Runtime.Gameplay;

public static class RuntimeVendorRangeQuery
{
    public static void EnforceRange(GameRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        VendorState vendor = runtime.InventoryOwner.Vendor;
        uint vendorId = vendor.VendorId;
        if (vendorId == 0u)
            return;

        RuntimeWorldTransitState transit = runtime.TransitOwner;
        if (transit.HasPendingTeleportStart || transit.IsTeleportActive)
        {
            vendor.Close();
            return;
        }

        uint playerGuid = runtime.PlayerIdentity.ServerGuid;
        if (playerGuid == 0u
            || !runtime.EntityObjects.Entities.TryGetActive(
                playerGuid,
                out RuntimeEntityRecord playerRecord)
            || !RuntimePhysicsState.TryGetAbsoluteWorldPosition(
                playerRecord,
                out Vector3 playerPosition))
        {
            return;
        }

        if (!runtime.EntityObjects.Entities.TryGetActive(
                vendorId,
                out RuntimeEntityRecord vendorRecord)
            || !RuntimePhysicsState.TryGetAbsoluteWorldPosition(
                vendorRecord,
                out Vector3 vendorPosition))
        {
            vendor.Close();
            return;
        }

        float useRadius = vendorRecord.Snapshot.UseRadius ?? 0f;

        float playerRadius =
            runtime.EntityObjects.Physics.ResolveObjectTableHost(playerGuid)
                ?.Radius ?? 0f;
        float vendorRadius =
            runtime.EntityObjects.Physics.ResolveObjectTableHost(vendorId)
                ?.Radius ?? 0f;
        bool inRange = ObjectRangeMath.ObjectsInRange(
            playerPosition,
            playerRadius,
            0f,
            vendorPosition,
            vendorRadius,
            0f,
            useRadius,
            useRadii: true,
            ignoreZDelta: false);

        if (!inRange)
            vendor.Close();
    }
}
