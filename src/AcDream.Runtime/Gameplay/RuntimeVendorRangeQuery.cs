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

        // A vendor whose create message carried no use radius really has none:
        // the descriptor zeroes that field when its flag is absent, so an
        // absent radius means zero metres rather than "unknown, be generous".
        float useRadius = vendorRecord.Snapshot.UseRadius ?? 0f;

        // Both bodies are measured as cylinders, each with its own girth AND
        // its own height, which is what the range check behind a vendor window
        // has always done (see the research note on vendor range). Measured as
        // flat discs on the floor instead, any step or slope between the two
        // becomes a vertical gap that is added to the distance, so a character
        // the server judged in range is judged out of it here and the window is
        // closed the same frame it opened. When a shape is not to hand the
        // answer is a zero girth and a zero height, as it was before.
        (float Radius, float Height) playerBody =
            runtime.EntityObjects.Physics.EntityBodyShape(playerGuid)
                ?? (0f, 0f);
        (float Radius, float Height) vendorBody =
            runtime.EntityObjects.Physics.EntityBodyShape(vendorId)
                ?? (0f, 0f);

        bool inRange = ObjectRangeMath.ObjectsInRange(
            playerPosition,
            playerBody.Radius,
            playerBody.Height,
            vendorPosition,
            vendorBody.Radius,
            vendorBody.Height,
            useRadius,
            useRadii: true,
            ignoreZDelta: false);

        if (!inRange)
            vendor.Close();
    }
}
