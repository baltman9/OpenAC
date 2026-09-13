using System.Numerics;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics.Motion;
using AcDream.Core.Properties;
using AcDream.Runtime.Entities;

namespace AcDream.Runtime.Gameplay;

public readonly record struct RuntimeHostileTargetSnapshot(
    uint ObjectId,
    string Name,
    uint WeenieClassId,
    float Distance,
    float RelativeAngleDegrees,
    bool IsHealthKnown,
    float HealthFraction)
{
    public int SpeciesId { get; init; }

    /// <summary>
    /// Always zero. A monster's maximum health is never sent to a client —
    /// only the fraction remaining is — so a caller that needs the absolute
    /// figure has to bring its own table of them.
    /// </summary>
    public int MaximumHealth { get; init; }
    public bool HasShield { get; init; }
    public ushort Incarnation { get; init; }
    public long HealthRevision { get; init; }
    public double SecondsSinceHealthUpdate { get; init; } =
        double.PositiveInfinity;
}

/// <summary>
/// The hostile-monster view an automation client scans. Membership is the
/// classification alone — a creature that is attackable, is not the player,
/// is not another player and is not a pet. Visibility and remaining health
/// are deliberately NOT membership terms: a client decides for itself when a
/// monster it can no longer see, or whose health has reached zero, stops
/// being worth attacking, and it needs the object in the list to make that
/// call. The same predicate answers every entry point here, so a caller can
/// never pick a target that <see cref="IsHostile"/> would then refuse.
/// </summary>
public static class RuntimeHostileTargetQuery
{
    private static bool IsEligible(
        uint playerGuid,
        ClientObject? player,
        RuntimeEntityRecord record,
        ClientObject? candidate) =>
        record.ServerGuid != playerGuid
        && record.Snapshot.Position is not null
        && CombatTargetPolicy.IsHostileMonster(playerGuid, player, candidate);

    public static IReadOnlyList<RuntimeHostileTargetSnapshot> Capture(
        GameRuntime runtime,
        float maximumDistance)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (float.IsNaN(maximumDistance) || maximumDistance <= 0f)
            return Array.Empty<RuntimeHostileTargetSnapshot>();

        uint playerGuid = runtime.PlayerIdentity.ServerGuid;
        if (playerGuid == 0u
            || !runtime.EntityObjects.Entities.TryGetActive(
                playerGuid,
                out RuntimeEntityRecord playerRecord)
            || playerRecord.Snapshot.Position is not { } playerPosition)
        {
            return Array.Empty<RuntimeHostileTargetSnapshot>();
        }

        Vector3 playerWorld = AbsolutePosition(playerPosition);
        float playerHeading = MoveToMath.GetHeading(new Quaternion(
            playerPosition.RotationX,
            playerPosition.RotationY,
            playerPosition.RotationZ,
            playerPosition.RotationW));
        float maximumDistanceSquared = maximumDistance * maximumDistance;
        ClientObjectTable objects = runtime.InventoryOwner.Objects;
        ClientObject? player = objects.Get(playerGuid);
        var targets = new List<RuntimeHostileTargetSnapshot>();

        foreach (RuntimeEntityRecord record
            in runtime.EntityObjects.Entities.ActiveRecords)
        {
            ClientObject? candidate = objects.Get(record.ServerGuid);
            if (record.Snapshot.Position is not { } position
                || !IsEligible(playerGuid, player, record, candidate))
            {
                continue;
            }

            bool hasHealth = runtime.ActionOwner.Combat.HasHealth(
                record.ServerGuid);
            float health = hasHealth
                ? runtime.ActionOwner.Combat.GetHealthPercent(record.ServerGuid)
                : 1f;

            // The separation is measured in three dimensions: a monster on the
            // gallery above must not read as though it were at your feet.
            Vector3 targetWorld = AbsolutePosition(position);
            float distanceSquared = Vector3.DistanceSquared(
                playerWorld,
                targetWorld);
            if (distanceSquared > maximumDistanceSquared)
                continue;

            float targetHeading = MoveToMath.PositionHeading(
                playerWorld,
                targetWorld);
            float relativeAngle = NormalizeSignedDegrees(
                targetHeading - playerHeading);
            int speciesId = candidate?.Properties.GetInt(
                (uint)PropertyInt.CreatureType) ?? 0;
            bool hasShield = candidate is not null
                && objects.GetEquippedBy(candidate.ObjectId).Any(static item =>
                    (item.Type & ItemType.Armor) != 0);
            runtime.ActionOwner.TryGetHealthActivity(
                record.ServerGuid,
                out long healthRevision,
                out double healthAge);
            targets.Add(new RuntimeHostileTargetSnapshot(
                record.ServerGuid,
                candidate?.Name ?? string.Empty,
                candidate?.WeenieClassId ?? 0u,
                MathF.Sqrt(distanceSquared),
                relativeAngle,
                hasHealth,
                health)
            {
                SpeciesId = speciesId,
                HasShield = hasShield,
                Incarnation = record.Incarnation,
                HealthRevision = healthRevision,
                SecondsSinceHealthUpdate = healthAge,
            });
        }

        return targets.Count == 0
            ? Array.Empty<RuntimeHostileTargetSnapshot>()
            : targets.ToArray();
    }

    public static uint? FindClosest(GameRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        uint playerGuid = runtime.PlayerIdentity.ServerGuid;
        if (playerGuid == 0u
            || !runtime.EntityObjects.Entities.TryGetActive(
                playerGuid,
                out RuntimeEntityRecord playerRecord)
            || playerRecord.Snapshot.Position is not { } playerPosition)
        {
            return null;
        }

        Vector3 playerWorld = AbsolutePosition(playerPosition);
        ClientObjectTable objects = runtime.InventoryOwner.Objects;
        ClientObject? player = objects.Get(playerGuid);
        uint? closest = null;
        float closestDistanceSquared = float.PositiveInfinity;
        foreach (RuntimeEntityRecord record
            in runtime.EntityObjects.Entities.ActiveRecords)
        {
            if (record.Snapshot.Position is not { } position
                || !IsEligible(
                    playerGuid,
                    player,
                    record,
                    objects.Get(record.ServerGuid)))
            {
                continue;
            }

            float distanceSquared = Vector3.DistanceSquared(
                playerWorld,
                AbsolutePosition(position));
            if (distanceSquared >= closestDistanceSquared)
                continue;
            closestDistanceSquared = distanceSquared;
            closest = record.ServerGuid;
        }
        return closest;
    }

    public static bool IsHostile(
        GameRuntime runtime,
        uint objectId)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        uint playerGuid = runtime.PlayerIdentity.ServerGuid;
        if (objectId == 0u
            || playerGuid == 0u
            || !runtime.EntityObjects.Entities.TryGetActive(
                objectId,
                out RuntimeEntityRecord record))
        {
            return false;
        }

        ClientObjectTable objects = runtime.InventoryOwner.Objects;
        return IsEligible(
            playerGuid,
            objects.Get(playerGuid),
            record,
            objects.Get(objectId));
    }

    private static Vector3 AbsolutePosition(
        CreateObject.ServerPosition position)
    {
        int landblockX =
            (int)((position.LandblockId >> 24) & 0xFFu);
        int landblockY =
            (int)((position.LandblockId >> 16) & 0xFFu);
        return new Vector3(
            position.PositionX + landblockX * 192f,
            position.PositionY + landblockY * 192f,
            position.PositionZ);
    }

    private static float NormalizeSignedDegrees(float degrees)
    {
        float normalized = degrees % 360f;
        if (normalized > 180f)
            normalized -= 360f;
        else if (normalized < -180f)
            normalized += 360f;
        return normalized;
    }
}
