using System.Collections.Immutable;
using System.Numerics;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Physics;
using AcDream.Core.Net.Messages;
using AcDream.Core.Spells;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Gameplay;

public sealed class RuntimeVendorRangeQueryTests
{
    private const uint Player = 0x50000001u;
    private const uint Vendor = 0x50000010u;
    private const uint Landblock = 0x01010001u;
    private const uint SetupId = 0x02000001u;

    [Fact]
    public void EnforceRange_PlayerWithinVendorUseRadius_LeavesSessionOpen()
    {
        using GameRuntime runtime = Create();
        runtime.PlayerIdentity.ServerGuid = Player;
        Add(runtime, Player, Landblock, 100f, 100f);
        Add(runtime, Vendor, Landblock, 102f, 100f, useRadius: 3f); // 2 m away, 3 m radius
        Open(runtime, Vendor);

        RuntimeVendorRangeQuery.EnforceRange(runtime);

        Assert.Equal(Vendor, runtime.InventoryOwner.Vendor.VendorId);
    }

    [Fact]
    public void EnforceRange_PlayerMovesBeyondVendorUseRadius_ClosesTheSession()
    {
        using GameRuntime runtime = Create();
        runtime.PlayerIdentity.ServerGuid = Player;
        RuntimeEntityRecord playerRecord =
            Add(runtime, Player, Landblock, 100f, 100f);
        Add(runtime, Vendor, Landblock, 102f, 100f, useRadius: 3f);
        Open(runtime, Vendor);

        // Walk 20 m away — well beyond the vendor's own authored 3 m radius.
        SetPosition(playerRecord, Landblock, 122f, 100f);
        RuntimeVendorRangeQuery.EnforceRange(runtime);

        Assert.Equal(0u, runtime.InventoryOwner.Vendor.VendorId);
    }

    [Fact]
    public void EnforceRange_PlayerStaysWithinRadiusAfterSmallMove_LeavesSessionOpen()
    {
        using GameRuntime runtime = Create();
        runtime.PlayerIdentity.ServerGuid = Player;
        RuntimeEntityRecord playerRecord =
            Add(runtime, Player, Landblock, 100f, 100f);
        Add(runtime, Vendor, Landblock, 102f, 100f, useRadius: 3f);
        Open(runtime, Vendor);

        SetPosition(playerRecord, Landblock, 101f, 100f);
        RuntimeVendorRangeQuery.EnforceRange(runtime);

        Assert.Equal(Vendor, runtime.InventoryOwner.Vendor.VendorId);
    }

    [Fact]
    public void EnforceRange_NoVendorOpen_IsANoOpAndDoesNotThrow()
    {
        using GameRuntime runtime = Create();
        runtime.PlayerIdentity.ServerGuid = Player;
        Add(runtime, Player, Landblock, 100f, 100f);

        RuntimeVendorRangeQuery.EnforceRange(runtime);

        Assert.Equal(0u, runtime.InventoryOwner.Vendor.VendorId);
    }

    [Fact]
    public void EnforceRange_VendorUseRadiusAbsent_UsesRawZeroWithNoFallback()
    {
        using GameRuntime runtime = Create();
        runtime.PlayerIdentity.ServerGuid = Player;
        RuntimeEntityRecord playerRecord =
            Add(runtime, Player, Landblock, 100f, 100f);
        Add(runtime, Vendor, Landblock, 100f, 100f, useRadius: null);
        Open(runtime, Vendor);

        // Exact same position: distance 0 <= radius 0 — still open.
        RuntimeVendorRangeQuery.EnforceRange(runtime);
        Assert.Equal(Vendor, runtime.InventoryOwner.Vendor.VendorId);

        // Any nonzero move at all — even 5 cm — is out of range at radius 0.
        SetPosition(playerRecord, Landblock, 100.05f, 100f);
        RuntimeVendorRangeQuery.EnforceRange(runtime);
        Assert.Equal(0u, runtime.InventoryOwner.Vendor.VendorId);
    }

    [Fact]
    public void EnforceRange_VendorEntityRetired_ClosesTheSession()
    {
        using GameRuntime runtime = Create();
        runtime.PlayerIdentity.ServerGuid = Player;
        Add(runtime, Player, Landblock, 100f, 100f);
        RuntimeEntityRecord vendorRecord =
            Add(runtime, Vendor, Landblock, 102f, 100f, useRadius: 3f);
        Open(runtime, Vendor);

        Assert.True(runtime.EntityObjects.Entities.RemoveActive(vendorRecord));

        RuntimeVendorRangeQuery.EnforceRange(runtime);

        Assert.Equal(0u, runtime.InventoryOwner.Vendor.VendorId);
    }

    [Fact]
    public void EnforceRange_TeleportQueued_ClosesTheSessionBeforeArrival()
    {
        using GameRuntime runtime = Create();
        runtime.PlayerIdentity.ServerGuid = Player;
        Add(runtime, Player, Landblock, 100f, 100f);
        Add(runtime, Vendor, Landblock, 100f, 100f, useRadius: 3f);
        Open(runtime, Vendor);

        Assert.True(runtime.TransitOwner.TryQueueTeleportStart(1));
        Assert.True(runtime.TransitOwner.HasPendingTeleportStart);
        Assert.False(runtime.TransitOwner.IsTeleportActive);

        RuntimeVendorRangeQuery.EnforceRange(runtime);

        Assert.Equal(0u, runtime.InventoryOwner.Vendor.VendorId);
    }

    [Fact]
    public void EnforceRange_TeleportActive_ClosesTheSession()
    {
        using GameRuntime runtime = Create();
        runtime.PlayerIdentity.ServerGuid = Player;
        Add(runtime, Player, Landblock, 100f, 100f);
        Add(runtime, Vendor, Landblock, 100f, 100f, useRadius: 3f);
        Open(runtime, Vendor);

        Assert.True(runtime.TransitOwner.TryQueueTeleportStart(1));
        Assert.True(runtime.TransitOwner.ActivateQueuedTeleport());
        Assert.True(runtime.TransitOwner.IsTeleportActive);

        RuntimeVendorRangeQuery.EnforceRange(runtime);

        Assert.Equal(0u, runtime.InventoryOwner.Vendor.VendorId);
    }

    [Fact]
    public void EnforceRange_ThrowingChangedObserverDuringAutoClose_DoesNotPropagate()
    {
        using GameRuntime runtime = Create();
        runtime.PlayerIdentity.ServerGuid = Player;
        RuntimeEntityRecord playerRecord =
            Add(runtime, Player, Landblock, 100f, 100f);
        Add(runtime, Vendor, Landblock, 102f, 100f, useRadius: 3f);
        Open(runtime, Vendor);

        Action<VendorTransition> throwingObserver =
            _ => throw new InvalidOperationException("boom");
        runtime.InventoryOwner.Vendor.Changed += throwingObserver;

        SetPosition(playerRecord, Landblock, 122f, 100f);
        var exception = Record.Exception(
            () => RuntimeVendorRangeQuery.EnforceRange(runtime));

        Assert.Null(exception);
        Assert.Equal(0u, runtime.InventoryOwner.Vendor.VendorId);

        runtime.InventoryOwner.Vendor.Changed -= throwingObserver;
    }

    // ── a step between the two bodies ────────────────────────────────────────
    //
    // Both bodies are half a metre wide and 1.8 m tall; the vendor's use radius
    // is 2 m; the vendor stands 2.9 m away horizontally and 0.6 m higher, on a
    // step. Measured as cylinders with their real heights:
    //   centre distance = sqrt(2.9^2 + 0.6^2) = 2.9614
    //   girth gap       = 2.9614 - (0.5 + 0.5) = 1.9614  <= 2.0  -> in range
    //   (the vertical gap is 0.6 - 1.8 = -1.2, i.e. the two cylinders overlap
    //    in height, so it adds nothing)
    // Measured as flat discs on the floor, with both heights zero:
    //   vertical gap = 0.6, and both gaps positive, so the distance becomes
    //   sqrt(1.9614^2 + 0.6^2) = 2.0512  > 2.0  -> judged out of range
    // The 5 cm split either side of the 2 m radius is what the live symptom
    // was: the window opened and was shut again the same frame.

    private const float BodyRadius = 0.5f;
    private const float BodyHeight = 1.8f;

    [Fact]
    public void EnforceRange_VendorUpAStepButWithinItsUseRadius_LeavesSessionOpen()
    {
        using GameRuntime runtime = Create();
        CacheBody(runtime, BodyRadius, BodyHeight);
        runtime.PlayerIdentity.ServerGuid = Player;
        Add(runtime, Player, Landblock, 100f, 100f, z: 5f);
        Add(runtime, Vendor, Landblock, 102.9f, 100f, z: 5.6f, useRadius: 2f);
        Open(runtime, Vendor);

        // Both shapes must actually be to hand, or this pins the fallback.
        Assert.Equal(
            (BodyRadius, BodyHeight),
            runtime.EntityObjects.Physics.EntityBodyShape(Player));
        Assert.Equal(
            (BodyRadius, BodyHeight),
            runtime.EntityObjects.Physics.EntityBodyShape(Vendor));

        RuntimeVendorRangeQuery.EnforceRange(runtime);

        Assert.Equal(Vendor, runtime.InventoryOwner.Vendor.VendorId);
    }

    [Fact]
    public void EnforceRange_TheStepAloneIsWhatTheZeroHeightMeasureAddedTo()
    {
        var player = new Vector3(100f, 100f, 5f);
        var vendor = new Vector3(102.9f, 100f, 5.6f);

        Assert.True(ObjectRangeMath.ObjectsInRange(
            player, BodyRadius, BodyHeight,
            vendor, BodyRadius, BodyHeight,
            2f, useRadii: true, ignoreZDelta: false));

        // The same two bodies with their heights thrown away: 2.0512 m > 2 m.
        Assert.False(ObjectRangeMath.ObjectsInRange(
            player, BodyRadius, 0f,
            vendor, BodyRadius, 0f,
            2f, useRadii: true, ignoreZDelta: false));
    }

    [Fact]
    public void EnforceRange_VendorUpAStepAndGenuinelyTooFar_ClosesTheSession()
    {
        using GameRuntime runtime = Create();
        CacheBody(runtime, BodyRadius, BodyHeight);
        runtime.PlayerIdentity.ServerGuid = Player;
        // 8 m away: girth gap 7 m, far outside the 2 m use radius however the
        // heights are counted.
        Add(runtime, Player, Landblock, 100f, 100f, z: 5f);
        Add(runtime, Vendor, Landblock, 108f, 100f, z: 5.6f, useRadius: 2f);
        Open(runtime, Vendor);

        RuntimeVendorRangeQuery.EnforceRange(runtime);

        Assert.Equal(0u, runtime.InventoryOwner.Vendor.VendorId);
    }

    [Fact]
    public void EnforceRange_VendorWithARealBodyGoesAway_ClosesTheSession()
    {
        using GameRuntime runtime = Create();
        CacheBody(runtime, BodyRadius, BodyHeight);
        runtime.PlayerIdentity.ServerGuid = Player;
        Add(runtime, Player, Landblock, 100f, 100f, z: 5f);
        RuntimeEntityRecord vendorRecord =
            Add(runtime, Vendor, Landblock, 102.9f, 100f, z: 5.6f, useRadius: 2f);
        Open(runtime, Vendor);

        Assert.True(runtime.EntityObjects.Entities.RemoveActive(vendorRecord));

        RuntimeVendorRangeQuery.EnforceRange(runtime);

        Assert.Equal(0u, runtime.InventoryOwner.Vendor.VendorId);
    }

    private static void CacheBody(GameRuntime runtime, float radius, float height) =>
        runtime.EntityObjects.Physics.DataCache.CacheSetup(
            SetupId,
            new FlatSetupCollision(
                ImmutableArray<FlatCollisionCylinder>.Empty,
                [new FlatCollisionSphere(Vector3.Zero, radius)],
                height: height,
                radius: radius,
                stepUpHeight: 0.4f,
                stepDownHeight: 0.4f));

    private static void Open(GameRuntime runtime, uint vendorGuid) =>
        Assert.True(runtime.InventoryOwner.Vendor.Apply(
            vendorGuid,
            default,
            Array.Empty<VendorShopItem>()));

    private static void SetPosition(
        RuntimeEntityRecord record,
        uint landblock,
        float x,
        float y,
        float z = 5f)
    {
        record.Snapshot = record.Snapshot with
        {
            Position = new CreateObject.ServerPosition(
                landblock, x, y, z, 1f, 0f, 0f, 0f),
        };
    }

    private static GameRuntime Create()
    {
        var operations = new Operations();
        return new GameRuntime(new GameRuntimeDependencies(
            operations,
            operations,
            operations,
            operations));
    }

    private static RuntimeEntityRecord Add(
        GameRuntime runtime,
        uint guid,
        uint landblock,
        float x,
        float y,
        float z = 5f,
        float? useRadius = null)
    {
        RuntimeEntityRecord record = runtime.EntityObjects
            .RegisterEntity(Spawn(guid, landblock, x, y, z, useRadius))
            .Canonical!;
        Assert.True(runtime.EntityObjects.ApplyAcceptedSpawn(
            record,
            record.CreateIntegrationVersion,
            record.Snapshot,
            replaceGeneration: false));
        return record;
    }

    private static WorldSession.EntitySpawn Spawn(
        uint guid,
        uint landblock,
        float x,
        float y,
        float z,
        float? useRadius) =>
        new(
            guid,
            new CreateObject.ServerPosition(landblock, x, y, z, 1f, 0f, 0f, 0f),
            SetupId,
            [],
            [],
            [],
            null,
            null,
            guid.ToString("X8"),
            null,
            null,
            null,
            UseRadius: useRadius);

    private sealed class Operations :
        IRuntimeCombatAttackOperations,
        IRuntimeCombatTargetOperations,
        IRuntimeCombatModeOperations,
        IRuntimeSpellCastOperations
    {
        public bool CanStartAttack(bool allowAutoTarget) => false;
        public void PrepareAttackRequest() { }
        public bool SendAttack(AttackHeight height, float power, bool allowAutoTarget) => false;
        public void SendCancelAttack() { }
        public bool IsDualWield => false;
        public bool PlayerReadyForAttack => false;
        public bool AutoRepeatAttack => false;
        public bool AutoTarget => false;
        public uint? SelectClosestTarget() => null;
        public bool IsInWorld => false;
        public IReadOnlyList<ClientObject> GetOrderedEquipment() => [];
        public void NotifyExplicitCombatModeRequest() { }
        public void SendChangeCombatMode(CombatMode mode) { }
        public uint LocalPlayerId => 0u;
        public bool CanSend => false;
        public bool HasRequiredComponents(uint spellId) => false;
        public bool IsTargetCompatible(
            uint targetId,
            SpellMetadata spell,
            bool showMessage) => false;
        public void StopCompletely() { }
        public void SendUntargeted(uint spellId) { }
        public void SendTargeted(uint targetId, uint spellId) { }
        public void DisplayMessage(string message) { }
        public void IncrementBusy() { }
    }
}
