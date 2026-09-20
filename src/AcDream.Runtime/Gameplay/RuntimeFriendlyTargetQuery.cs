using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Physics;

namespace AcDream.Runtime.Gameplay;

public static class RuntimeFriendlyTargetQuery
{
    public static uint? FindClosestOtherPlayer(GameRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        uint playerGuid = runtime.PlayerIdentity.ServerGuid;
        if (playerGuid == 0u
            || !runtime.EntityObjects.Entities.TryGetActive(
                playerGuid,
                out RuntimeEntityRecord playerRecord)
            || !RuntimePhysicsState.TryGetAbsoluteWorldPosition(
                playerRecord,
                out Vector3 playerWorld))
        {
            return null;
        }

        uint? closest = null;
        float closestDistanceSquared = float.PositiveInfinity;
        foreach (RuntimeEntityRecord record
            in runtime.EntityObjects.Entities.ActiveRecords)
        {
            if (record.ServerGuid == playerGuid
                || !RuntimePhysicsState.TryGetAbsoluteWorldPosition(
                    record,
                    out Vector3 position)
                || (record.FinalPhysicsState
                    & (PhysicsStateFlags.Hidden
                        | PhysicsStateFlags.NoDraw)) != 0)
            {
                continue;
            }
            if (!IsPlayer(record))
                continue;

            float distanceSquared = Vector3.DistanceSquared(
                playerWorld,
                position);
            if (distanceSquared >= closestDistanceSquared)
                continue;
            closestDistanceSquared = distanceSquared;
            closest = record.ServerGuid;
        }
        return closest;
    }

    public static uint? FindPlayerByName(GameRuntime runtime, string name)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrEmpty(name);
        uint playerGuid = runtime.PlayerIdentity.ServerGuid;
        if (playerGuid == 0u
            || !runtime.EntityObjects.Entities.TryGetActive(
                playerGuid,
                out RuntimeEntityRecord playerRecord)
            || !RuntimePhysicsState.TryGetAbsoluteWorldPosition(
                playerRecord,
                out Vector3 playerWorld))
        {
            return null;
        }

        uint? closest = null;
        float closestDistanceSquared = float.PositiveInfinity;
        foreach (RuntimeEntityRecord record
            in runtime.EntityObjects.Entities.ActiveRecords)
        {
            if (record.ServerGuid == playerGuid
                || !RuntimePhysicsState.TryGetAbsoluteWorldPosition(
                    record,
                    out Vector3 position)
                || (record.FinalPhysicsState
                    & (PhysicsStateFlags.Hidden
                        | PhysicsStateFlags.NoDraw)) != 0
                || !IsPlayer(record)
                || !string.Equals(
                    record.Snapshot.Name,
                    name,
                    StringComparison.Ordinal))
            {
                continue;
            }

            float distanceSquared = Vector3.DistanceSquared(
                playerWorld,
                position);
            if (distanceSquared >= closestDistanceSquared)
                continue;
            closestDistanceSquared = distanceSquared;
            closest = record.ServerGuid;
        }
        return closest;
    }

    public static string? TryGetName(GameRuntime runtime, uint guid)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return runtime.EntityObjects.Entities.TryGetActive(
            guid,
            out RuntimeEntityRecord record)
            ? record.Snapshot.Name
            : null;
    }

    /// <summary>Horizontal live-world distance from the local player.</summary>
    public static bool TryGetDistance(
        GameRuntime runtime,
        uint guid,
        out float distance)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        uint playerGuid = runtime.PlayerIdentity.ServerGuid;
        if (playerGuid == 0u
            || !runtime.EntityObjects.Entities.TryGetActive(
                playerGuid,
                out RuntimeEntityRecord player)
            || !RuntimePhysicsState.TryGetAbsoluteWorldPosition(
                player,
                out Vector3 from)
            || !runtime.EntityObjects.Entities.TryGetActive(
                guid,
                out RuntimeEntityRecord target)
            || !RuntimePhysicsState.TryGetAbsoluteWorldPosition(
                target,
                out Vector3 to)
            || (target.FinalPhysicsState
                & (PhysicsStateFlags.Hidden | PhysicsStateFlags.NoDraw)) != 0)
        {
            distance = float.PositiveInfinity;
            return false;
        }

        distance = Vector2.Distance(
            new Vector2(from.X, from.Y),
            new Vector2(to.X, to.Y));
        return true;
    }

    private static bool IsPlayer(RuntimeEntityRecord record) =>
        EntityCollisionFlagsExt
            .FromPwdBitfield(record.Snapshot.ObjectDescriptionFlags ?? 0u)
            .HasFlag(EntityCollisionFlags.IsPlayer);
}
