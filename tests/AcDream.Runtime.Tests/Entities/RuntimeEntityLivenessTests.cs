using System.Numerics;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.World.Cells;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Entities;

public sealed class RuntimeEntityLivenessTests
{
    private const uint Player = 0x5000_000Au;

    [Fact]
    public void OutOfRangeWorldEntityExpiresAfterTwentyFiveSeconds()
    {
        var tracker = new RuntimeEntityLivenessTracker();
        var samples = new[] { Sample(0x7000_0001u, generation: 4, visible: false) };

        Assert.Empty(tracker.Tick(10.0, samples));
        Assert.Empty(tracker.Tick(34.999, samples));
        Assert.Equal(
            new RuntimeEntityExpiryCandidate(
                new RuntimeEntityKey(0x7000_0001u, 4),
                0x7000_0001u),
            Assert.Single(tracker.Tick(35.0, samples)));
        Assert.Equal(0, tracker.DeadlineCount);
    }

    [Fact]
    public void ReturningToVisibilityCancelsTheDeadline()
    {
        var tracker = new RuntimeEntityLivenessTracker();
        Assert.Empty(tracker.Tick(0.0, [Sample(1, 1, visible: false)]));
        Assert.Empty(tracker.Tick(20.0, [Sample(1, 1, visible: true)]));
        Assert.Empty(tracker.Tick(40.0, [Sample(1, 1, visible: false)]));
        Assert.Empty(tracker.Tick(64.9, [Sample(1, 1, visible: false)]));
        Assert.Single(tracker.Tick(65.0, [Sample(1, 1, visible: false)]));
    }

    [Fact]
    public void NonWorldRetentionCancelsTheDeadline()
    {
        var tracker = new RuntimeEntityLivenessTracker();
        Assert.Empty(tracker.Tick(0.0, [Sample(1, 1, visible: false)]));
        Assert.Empty(tracker.Tick(30.0, [Sample(1, 1, visible: false, retained: true)]));
        Assert.Equal(0, tracker.DeadlineCount);
    }

    [Fact]
    public void ReusedGuidGetsANewGenerationDeadline()
    {
        var tracker = new RuntimeEntityLivenessTracker();
        Assert.Empty(tracker.Tick(0.0, [Sample(1, 1, visible: false)]));
        Assert.Empty(tracker.Tick(24.0, [Sample(1, 2, visible: false)]));
        Assert.Empty(tracker.Tick(25.0, [Sample(1, 2, visible: false)]));
        Assert.Equal(
            new RuntimeEntityExpiryCandidate(new RuntimeEntityKey(1, 2), 1),
            Assert.Single(tracker.Tick(49.0, [Sample(1, 2, visible: false)])));
    }

    [Fact]
    public void RemovedRecordDropsItsDeadline()
    {
        var tracker = new RuntimeEntityLivenessTracker();
        Assert.Empty(tracker.Tick(0.0, [Sample(1, 1, visible: false)]));

        Assert.Empty(tracker.Tick(30.0, []));

        Assert.Equal(0, tracker.DeadlineCount);
    }

    [Fact]
    public void VisibilityIsThePlayersLandblockAndItsEightNeighbours()
    {
        const uint player = 0x3032_0001u;

        Assert.True(RuntimeEntityLivenessController.IsWithinVisibleLandblocks(player, 0x3032_00A7u));
        Assert.True(RuntimeEntityLivenessController.IsWithinVisibleLandblocks(player, 0x3132_0001u));
        Assert.True(RuntimeEntityLivenessController.IsWithinVisibleLandblocks(player, 0x2F31_0001u));
        Assert.True(RuntimeEntityLivenessController.IsWithinVisibleLandblocks(player, 0x3133_00FFu));
        Assert.False(RuntimeEntityLivenessController.IsWithinVisibleLandblocks(player, 0x3232_0001u));
        Assert.False(RuntimeEntityLivenessController.IsWithinVisibleLandblocks(player, 0x3034_0001u));
        Assert.False(RuntimeEntityLivenessController.IsWithinVisibleLandblocks(player, 0x2E30_0001u));
    }

    [Fact]
    public void VisibilityDoesNotDependOnDistanceInsideTheNeighbourhood()
    {
        // The far corner of a diagonal neighbour is ~543 m away and used to
        // fall outside the old 384 m sphere while the server still knew it.
        Assert.True(RuntimeEntityLivenessController.IsWithinVisibleLandblocks(0x3032_0001u, 0x3133_0001u));
    }

    // Inside a sealed dungeon cell the loaded set is the player's cell plus
    // the cells on its authored visible-cell list; nothing else in the
    // dungeon's landblock stays alive. The server forgets on the same set and
    // only announces a destroyed object to clients that still know it, so a
    // corpse kept beyond this set is never deleted by a message.

    [Fact]
    public void SealedDungeonKeepsThePlayersCellAndItsVisibleCells()
    {
        var set = new RuntimeEntityVisibleCellSet();
        set.Update(DungeonCell(0x01D9_0102u, 0x01D9_0103u, 0x01D9_0110u), 0x01D9_0102u);

        Assert.True(set.IsSealedDungeon);
        Assert.True(set.Contains(0x01D9_0102u));
        Assert.True(set.Contains(0x01D9_0103u));
        Assert.True(set.Contains(0x01D9_0110u));
    }

    [Fact]
    public void SealedDungeonDropsCellsOfTheSameLandblockOutsideTheVisibleList()
    {
        var set = new RuntimeEntityVisibleCellSet();
        set.Update(DungeonCell(0x01D9_0102u, 0x01D9_0103u), 0x01D9_0102u);

        Assert.False(set.Contains(0x01D9_0140u));
        Assert.False(set.Contains(0x01D9_FFFFu));
        // The outdoor terrain above the dungeon is released as well.
        Assert.False(set.Contains(0x01D9_0001u));
        Assert.False(set.Contains(0x01DA_0102u));
    }

    [Fact]
    public void MovingToAnotherDungeonCellReplacesTheVisibleList()
    {
        var set = new RuntimeEntityVisibleCellSet();
        set.Update(DungeonCell(0x01D9_0102u, 0x01D9_0103u), 0x01D9_0102u);
        Assert.True(set.Contains(0x01D9_0103u));

        set.Update(DungeonCell(0x01D9_0120u, 0x01D9_0121u), 0x01D9_0120u);

        Assert.True(set.Contains(0x01D9_0120u));
        Assert.True(set.Contains(0x01D9_0121u));
        Assert.False(set.Contains(0x01D9_0102u));
        Assert.False(set.Contains(0x01D9_0103u));
    }

    [Fact]
    public void ADungeonCellUsesItsOwnIdWhenTheRecordsCellLagsBehind()
    {
        var set = new RuntimeEntityVisibleCellSet();
        set.Update(DungeonCell(0x01D9_0120u, 0x01D9_0121u), playerCellId: 0x01D9_0102u);

        Assert.True(set.Contains(0x01D9_0120u));
        Assert.False(set.Contains(0x01D9_0102u));
    }

    [Fact]
    public void ADungeonCellFromAnotherLandblockThanTheRecordIsNotJudgedPerCell()
    {
        // The physics cell still points into the dungeon the player left
        // while the record already sits in the new one (its landblock is
        // still loading): nothing in the new landblock may start a
        // deadline on the old cell's visible list.
        var set = new RuntimeEntityVisibleCellSet();
        set.Update(DungeonCell(0x01D9_0120u, 0x01D9_0121u), playerCellId: 0x0A9B_0102u);

        Assert.False(set.IsSealedDungeon);
        Assert.True(set.Contains(0x0A9B_0140u));
        Assert.True(set.Contains(0x0A9B_0001u));
        Assert.False(set.Contains(0x01D9_0120u));
    }

    [Fact]
    public void AnIndoorCellSeenFromOutsideKeepsTheLandblockNeighbourhood()
    {
        var set = new RuntimeEntityVisibleCellSet();
        set.Update(
            IndoorCell(0x3032_0102u, seenOutside: true, 0x3032_0103u),
            0x3032_0102u);

        Assert.False(set.IsSealedDungeon);
        Assert.True(set.Contains(0x3032_0140u));
        Assert.True(set.Contains(0x3032_0001u));
        Assert.True(set.Contains(0x3133_00FFu));
        Assert.False(set.Contains(0x3232_0001u));
    }

    [Fact]
    public void AnUnknownPlayerCellKeepsTheLandblockNeighbourhood()
    {
        var set = new RuntimeEntityVisibleCellSet();
        set.Update(playerCell: null, 0x3032_0001u);

        Assert.False(set.IsSealedDungeon);
        Assert.True(set.Contains(0x3032_00A7u));
        Assert.True(set.Contains(0x3133_0001u));
        Assert.False(set.Contains(0x3232_0001u));
    }

    [Fact]
    public void LeavingTheDungeonForgetsItsVisibleList()
    {
        var set = new RuntimeEntityVisibleCellSet();
        set.Update(DungeonCell(0x01D9_0102u, 0x01D9_0103u), 0x01D9_0102u);

        set.Update(playerCell: null, 0xA9B4_0001u);

        Assert.False(set.IsSealedDungeon);
        Assert.False(set.Contains(0x01D9_0103u));
        Assert.True(set.Contains(0xA9B4_0020u));
    }

    // The controller runs over the canonical object table alone, so a host
    // without any presentation (headless) expires on the same schedule as
    // the graphical client.

    [Fact]
    public void AnObjectOutsideTheNeighbourhoodIsHandedToTheSinkAfterTwentyFiveSeconds()
    {
        var lifetime = new RuntimeEntityObjectLifetime();
        var identity = new RuntimeLocalPlayerIdentityState { ServerGuid = Player };
        var sink = new RecordingSink();
        var controller = new RuntimeEntityLivenessController(
            lifetime,
            identity,
            sink,
            new FixedCellSource(null));
        Register(lifetime, Player, 0x3032_0001u);
        Register(lifetime, 0x7000_0001u, 0xA9B4_0001u);
        Register(lifetime, 0x7000_0002u, 0x3133_0020u);

        controller.Tick(0.0);
        controller.Tick(24.0);
        Assert.Empty(sink.Expired);

        controller.Tick(26.0);

        RuntimeEntityExpiryCandidate expired = Assert.Single(sink.Expired);
        Assert.Equal(0x7000_0001u, expired.ServerGuid);
        Assert.Equal(7, expired.Generation);
    }

    [Fact]
    public void AnObjectHeldByAContainerNeverExpires()
    {
        var lifetime = new RuntimeEntityObjectLifetime();
        var identity = new RuntimeLocalPlayerIdentityState { ServerGuid = Player };
        var sink = new RecordingSink();
        var controller = new RuntimeEntityLivenessController(
            lifetime,
            identity,
            sink,
            new FixedCellSource(null));
        Register(lifetime, Player, 0x3032_0001u);
        Register(lifetime, 0x7000_0001u, 0xA9B4_0001u, containerId: Player);

        controller.Tick(0.0);
        controller.Tick(30.0);

        Assert.Empty(sink.Expired);
    }

    [Fact]
    public void TheCanonicalSinkDeletesTheObjectWithNoPresentationPresent()
    {
        var lifetime = new RuntimeEntityObjectLifetime();
        var identity = new RuntimeLocalPlayerIdentityState { ServerGuid = Player };
        var controller = new RuntimeEntityLivenessController(
            lifetime,
            identity,
            new RuntimeCanonicalEntityExpirySink(lifetime),
            new FixedCellSource(null));
        Register(lifetime, Player, 0x3032_0001u);
        Register(lifetime, 0x7000_0001u, 0xA9B4_0001u);
        Register(lifetime, 0x7000_0002u, 0x3133_0020u);
        Assert.Equal(3, lifetime.Entities.Count);

        controller.Tick(0.0);
        controller.Tick(26.0);

        Assert.Equal(2, lifetime.Entities.Count);
        Assert.False(lifetime.Entities.TryGetActive(0x7000_0001u, out _));
        Assert.True(lifetime.Entities.TryGetActive(0x7000_0002u, out _));
        Assert.Equal(0, lifetime.Entities.PendingTeardownCount);
    }

    [Fact]
    public void ASealedDungeonCellExpiresTheDungeonsOtherCells()
    {
        var lifetime = new RuntimeEntityObjectLifetime();
        var identity = new RuntimeLocalPlayerIdentityState { ServerGuid = Player };
        var sink = new RecordingSink();
        var cells = new FixedCellSource(DungeonCell(0x01D9_0102u, 0x01D9_0103u));
        var controller = new RuntimeEntityLivenessController(lifetime, identity, sink, cells);
        Register(lifetime, Player, 0x01D9_0102u);
        Register(lifetime, 0x7000_0001u, 0x01D9_0140u);
        Register(lifetime, 0x7000_0002u, 0x01D9_0103u);

        controller.Tick(0.0);
        controller.Tick(26.0);

        Assert.Equal(0x7000_0001u, Assert.Single(sink.Expired).ServerGuid);
    }

    // Objects outside the world. The server destroys a corpse's contents
    // (and a creature's wielded items) without a message; the original client
    // drops them itself 25 seconds after it stops looking at them, or after
    // their container goes away.

    private const uint Corpse = 0x8000_1000u;

    [Fact]
    public void ClosingACorpseDestroysItsContentsTwentyFiveSecondsLater()
    {
        var world = new ContentsWorld();
        world.Corpse(Corpse, 0x3032_0001u, 0x8000_1001u, 0x8000_1002u);
        world.Controller.Tick(1.0);

        world.Objects.StopViewingContentsTree(Corpse);
        world.Controller.Tick(25.5);
        Assert.NotNull(world.Objects.Get(0x8000_1001u));

        world.Controller.Tick(26.5);

        Assert.Null(world.Objects.Get(0x8000_1001u));
        Assert.Null(world.Objects.Get(0x8000_1002u));
        Assert.False(world.Lifetime.Entities.TryGetActive(0x8000_1001u, out _));
        Assert.NotNull(world.Objects.Get(Corpse));
    }

    [Fact]
    public void AnItemLootedIntoThePackLeavesTheQueue()
    {
        var world = new ContentsWorld();
        world.Corpse(Corpse, 0x3032_0001u, 0x8000_1001u, 0x8000_1002u);
        world.Objects.StopViewingContentsTree(Corpse);

        world.Objects.ApplyConfirmedServerMove(0x8000_1001u, Player, newWielderId: 0u);
        world.Controller.Tick(1.0);
        world.Controller.Tick(30.0);

        Assert.NotNull(world.Objects.Get(0x8000_1001u));
        Assert.Null(world.Objects.Get(0x8000_1002u));
    }

    [Fact]
    public void ACreateForAQueuedItemTakesItOffTheQueue()
    {
        // Opening the corpse again re-sends every item's create.
        var world = new ContentsWorld();
        world.Corpse(Corpse, 0x3032_0001u, 0x8000_1001u);
        world.Objects.StopViewingContentsTree(Corpse);
        Assert.Equal(1, world.Controller.QueuedObjectCount);

        Spawned(world.Lifetime, ContainedSpawn(0x8000_1001u, Corpse));
        world.Controller.Tick(1.0);
        world.Controller.Tick(30.0);

        Assert.Equal(0, world.Controller.QueuedObjectCount);
        Assert.NotNull(world.Objects.Get(0x8000_1001u));
    }

    [Fact]
    public void ADestroyedCorpseTakesWhatItStillHeldWithItTwentyFiveSecondsLater()
    {
        var world = new ContentsWorld();
        world.Corpse(Corpse, 0x3032_0001u, 0x8000_1001u, 0x8000_1002u);
        world.Controller.Tick(1.0);

        Assert.True(RuntimeCanonicalEntityExpirySink.DeleteCanonicalOnly(
            world.Lifetime,
            new DeleteObject.Parsed(Corpse, 7)));
        world.Controller.Tick(25.5);
        Assert.NotNull(world.Objects.Get(0x8000_1001u));
        world.Controller.Tick(26.5);

        Assert.Null(world.Objects.Get(0x8000_1001u));
        Assert.Null(world.Objects.Get(0x8000_1002u));
    }

    [Fact]
    public void ALootedCorpsesItemsDoNotAccumulate()
    {
        // A bot looting all night: every corpse is opened, one item taken,
        // the corpse closed and later destroyed by the server. What the
        // client holds must come back to the player and what it looted.
        var world = new ContentsWorld();
        int baseline = world.Objects.Objects.Count();
        double now = 0.0;
        for (int corpse = 0; corpse < 40; corpse++)
        {
            uint corpseGuid = 0x8001_0000u + (uint)corpse * 0x10u;
            uint[] items = Enumerable.Range(1, 6).Select(i => corpseGuid + (uint)i).ToArray();
            world.Corpse(corpseGuid, 0x3032_0001u, items);
            world.Objects.ApplyConfirmedServerMove(items[0], Player, newWielderId: 0u);
            world.Objects.StopViewingContentsTree(corpseGuid);
            now += 2.0;
            world.Controller.Tick(now);
            RuntimeCanonicalEntityExpirySink.DeleteCanonicalOnly(
                world.Lifetime,
                new DeleteObject.Parsed(corpseGuid, 7));
        }

        world.Controller.Tick(now + 26.0);

        Assert.Equal(baseline + 40, world.Objects.Objects.Count());
        Assert.Equal(1 + 40, world.Lifetime.Entities.Count);
        Assert.Equal(0, world.Controller.QueuedObjectCount);
    }

    private const uint Pack = 0x8000_1100u;

    /// <summary>A viewed corpse holding one item and a viewed pack of two.</summary>
    private static ContentsWorld CorpseWithAPack()
    {
        var world = new ContentsWorld();
        world.Corpse(Corpse, 0x3032_0001u, 0x8000_1001u, Pack);
        Spawned(world.Lifetime, ContainedSpawn(0x8000_1101u, Pack));
        Spawned(world.Lifetime, ContainedSpawn(0x8000_1102u, Pack));
        world.Objects.ReplaceContents(Pack, [0x8000_1101u, 0x8000_1102u]);
        return world;
    }

    [Fact]
    public void ANestedPacksItemsGoTwentyFiveSecondsAfterThePack()
    {
        var world = CorpseWithAPack();
        world.Controller.Tick(1.0);

        world.Objects.StopViewingContentsTree(Corpse);
        world.Controller.Tick(26.5);
        Assert.Null(world.Objects.Get(Pack));
        Assert.Null(world.Objects.Get(0x8000_1001u));
        Assert.NotNull(world.Objects.Get(0x8000_1101u));

        world.Controller.Tick(51.0);
        Assert.NotNull(world.Objects.Get(0x8000_1101u));
        world.Controller.Tick(52.0);

        Assert.Null(world.Objects.Get(0x8000_1101u));
        Assert.Null(world.Objects.Get(0x8000_1102u));
    }

    [Fact]
    public void APackLootedFromAClosedCorpseKeepsItsItems()
    {
        var world = CorpseWithAPack();
        world.Controller.Tick(1.0);
        world.Objects.StopViewingContentsTree(Corpse);

        world.Objects.ApplyConfirmedServerMove(Pack, Player, newWielderId: 0u);
        world.Controller.Tick(30.0);
        world.Controller.Tick(60.0);

        Assert.NotNull(world.Objects.Get(Pack));
        Assert.NotNull(world.Objects.Get(0x8000_1101u));
        Assert.NotNull(world.Objects.Get(0x8000_1102u));
    }

    private const uint Partner = 0x5000_0B00u;

    [Fact]
    public void AnItemThePartnerOffersLeavesTheQueueAndTheTradeEndQueuesWhatItHolds()
    {
        var world = new ContentsWorld();
        world.Controller.Tick(1.0);
        Spawned(world.Lifetime, ContainedSpawn(0x8000_2001u, Partner));
        Spawned(world.Lifetime, ContainedSpawn(0x8000_2002u, 0x8000_2001u));
        world.Objects.ReplaceContents(0x8000_2001u, [0x8000_2002u]);
        // Moved into the partner's pack while no trade was open: queued.
        world.Objects.ApplyConfirmedServerMove(0x8000_2001u, Partner, newWielderId: 0u);
        Assert.Equal(2, world.Controller.QueuedObjectCount);

        world.Trade.ApplyRegister(new GameEvents.RegisterTrade(Partner, Player, 0ul), Player);
        world.Trade.ApplyAdd(new GameEvents.AddToTrade(0x8000_2001u, (uint)RuntimeTradeSide.Partner, 0u));
        Assert.Equal(0, world.Controller.QueuedObjectCount);

        world.Controller.Tick(20.0);
        world.Trade.ApplyReset();
        world.Controller.Tick(46.0);

        Assert.NotNull(world.Objects.Get(0x8000_2001u));
        Assert.Null(world.Objects.Get(0x8000_2002u));
    }

    [Fact]
    public void AVendorsWindowTakesItsItemsOffTheQueue()
    {
        // An item sold to a vendor moves into the vendor, a container the
        // player does not own; the vendor's refreshed list shows it again.
        const uint vendor = 0x8000_3000u;
        var world = new ContentsWorld();
        world.Controller.Tick(1.0);
        Spawned(world.Lifetime, ContainedSpawn(0x8000_3001u, Player));
        world.Objects.ApplyConfirmedServerMove(0x8000_3001u, vendor, newWielderId: 0u);
        Assert.Equal(1, world.Controller.QueuedObjectCount);

        world.Vendor.Apply(
            vendor,
            new VendorShopProfile(0u, 0u, 0u, false, 1f, 1f, 0u, 0u, string.Empty),
            [new VendorShopItem(0x8000_3001u, 1, 1u, "Sold", null, 0u, 10)]);
        world.Controller.Tick(30.0);

        Assert.Equal(0, world.Controller.QueuedObjectCount);
        Assert.NotNull(world.Objects.Get(0x8000_3001u));
    }

    [Fact]
    public void AWieldedObjectLeavesVisibilityWithItsHolder()
    {
        var lifetime = new RuntimeEntityObjectLifetime();
        var identity = new RuntimeLocalPlayerIdentityState { ServerGuid = Player };
        var sink = new RecordingSink();
        var controller = new RuntimeEntityLivenessController(
            lifetime,
            identity,
            sink,
            new FixedCellSource(null));
        Register(lifetime, Player, 0x3032_0001u);
        Register(lifetime, 0x7000_0001u, 0xA9B4_0001u);
        Assert.NotNull(lifetime.RegisterEntity(
            ContainedSpawn(0x7000_0002u, containerId: null, wielderId: 0x7000_0001u)).Canonical);

        controller.Tick(0.0);
        controller.Tick(26.0);

        Assert.Equal(
            [0x7000_0001u, 0x7000_0002u],
            sink.Expired.Select(candidate => candidate.ServerGuid).Order());
    }

    [Fact]
    public void ALootedWeaponStaysInThePackWhenItsOldWielderLeaves()
    {
        // The create named a wielder; the loot moved it into the pack. Only
        // the live placement may decide, or a recycled wielder id leaving
        // view would take the weapon out of the player's pack.
        var lifetime = new RuntimeEntityObjectLifetime();
        var identity = new RuntimeLocalPlayerIdentityState { ServerGuid = Player };
        var sink = new RecordingSink();
        using var controller = new RuntimeEntityLivenessController(
            lifetime,
            identity,
            sink,
            new FixedCellSource(null));
        Register(lifetime, Player, 0x3032_0001u);
        Register(lifetime, 0x7000_0001u, 0xA9B4_0001u);
        Spawned(lifetime, ContainedSpawn(0x7000_0002u, containerId: null, wielderId: 0x7000_0001u));
        lifetime.Objects.ApplyConfirmedServerMove(0x7000_0002u, Player, newWielderId: 0u);

        controller.Tick(0.0);
        controller.Tick(26.0);

        Assert.Equal(0x7000_0001u, Assert.Single(sink.Expired).ServerGuid);
    }

    [Fact]
    public void AnObjectWhoseHolderIsGoneIsNotExpiredByVisibility()
    {
        // A deleted creature only detaches what it held.
        var lifetime = new RuntimeEntityObjectLifetime();
        var identity = new RuntimeLocalPlayerIdentityState { ServerGuid = Player };
        var sink = new RecordingSink();
        var controller = new RuntimeEntityLivenessController(
            lifetime,
            identity,
            sink,
            new FixedCellSource(null));
        Register(lifetime, Player, 0x3032_0001u);
        Assert.NotNull(lifetime.RegisterEntity(
            ContainedSpawn(0x7000_0002u, containerId: null, wielderId: 0x7000_0001u)).Canonical);

        controller.Tick(0.0);
        controller.Tick(30.0);

        Assert.Empty(sink.Expired);
    }

    private sealed class ContentsWorld
    {
        public RuntimeEntityObjectLifetime Lifetime { get; } = new();
        public ExternalContainerState Ground { get; } = new();
        public VendorState Vendor { get; } = new();
        public RuntimeTradeState Trade { get; }
        public RuntimeEntityLivenessController Controller { get; }
        public ClientObjectTable Objects => Lifetime.Objects;

        public ContentsWorld()
        {
            Trade = new RuntimeTradeState(Lifetime.Objects);
            Controller = new RuntimeEntityLivenessController(
                Lifetime,
                new RuntimeLocalPlayerIdentityState { ServerGuid = Player },
                new RuntimeCanonicalEntityExpirySink(Lifetime),
                new FixedCellSource(null),
                Ground,
                Trade,
                Vendor);
            Register(Lifetime, Player, 0x3032_0001u);
        }

        /// <summary>A corpse in the world whose contents the player is viewing.</summary>
        public void Corpse(uint guid, uint cell, params uint[] items)
        {
            Spawned(Lifetime, ContainedSpawn(guid, containerId: null));
            foreach (uint item in items)
                Spawned(Lifetime, ContainedSpawn(item, guid));
            Objects.ReplaceContents(guid, items);
        }
    }

    private static void Register(
        RuntimeEntityObjectLifetime lifetime,
        uint guid,
        uint cell,
        uint? containerId = null)
    {
        RuntimeEntityRegistrationResult result = lifetime.RegisterEntity(
            Spawn(guid, cell, containerId));
        Assert.NotNull(result.Canonical);
    }

    /// <summary>A create as the live session applies it: registered, then ingested.</summary>
    private static void Spawned(
        RuntimeEntityObjectLifetime lifetime,
        WorldSession.EntitySpawn spawn)
    {
        RuntimeEntityRegistrationResult registration = lifetime.RegisterEntity(spawn);
        RuntimeEntityRecord canonical = Assert.IsType<RuntimeEntityRecord>(registration.Canonical);
        Assert.True(lifetime.ApplyAcceptedSpawn(
            canonical,
            canonical.CreateIntegrationVersion,
            canonical.Snapshot,
            replaceGeneration: false));
    }

    private static WorldSession.EntitySpawn ContainedSpawn(
        uint guid,
        uint? containerId,
        uint? wielderId = null,
        ushort instance = 7) =>
        new WorldSession.EntitySpawn(
            guid,
            null,
            0x02000001u,
            Array.Empty<CreateObject.AnimPartChange>(),
            Array.Empty<CreateObject.TextureChange>(),
            Array.Empty<CreateObject.SubPaletteSwap>(),
            null,
            null,
            "item",
            null,
            null,
            null,
            PhysicsState: 0,
            InstanceSequence: instance,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1) with
        {
            ContainerId = containerId,
            WielderId = wielderId,
        };

    private static WorldSession.EntitySpawn Spawn(uint guid, uint cell, uint? containerId)
    {
        const ushort instance = 7;
        var position = new CreateObject.ServerPosition(cell, 51f, 10f, 5f, 1f, 0f, 0f, 0f);
        var timestamps = new PhysicsTimestamps(1, 1, 1, 1, 0, 1, 0, 1, instance);
        var physics = new PhysicsSpawnData(
            RawState: 0,
            Position: position,
            Movement: null,
            AnimationFrame: null,
            SetupTableId: 0x02000001u,
            MotionTableId: 0x09000001u,
            SoundTableId: null,
            PhysicsScriptTableId: null,
            Parent: null,
            Children: null,
            Scale: null,
            Friction: null,
            Elasticity: null,
            Translucency: null,
            Velocity: null,
            Acceleration: null,
            AngularVelocity: null,
            DefaultScriptType: null,
            DefaultScriptIntensity: null,
            Timestamps: timestamps);
        return new WorldSession.EntitySpawn(
            guid,
            position,
            0x02000001u,
            Array.Empty<CreateObject.AnimPartChange>(),
            Array.Empty<CreateObject.TextureChange>(),
            Array.Empty<CreateObject.SubPaletteSwap>(),
            null,
            null,
            "fixture",
            null,
            null,
            0x09000001u,
            PhysicsState: 0,
            InstanceSequence: instance,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: physics) with
        {
            ContainerId = containerId,
        };
    }

    private static EnvCell DungeonCell(uint id, params uint[] visibleCells) =>
        IndoorCell(id, seenOutside: false, visibleCells);

    private static EnvCell IndoorCell(uint id, bool seenOutside, params uint[] visibleCells) =>
        new(
            id,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            Vector3.Zero,
            Vector3.One,
            portals: [],
            stabList: visibleCells,
            seenOutside: seenOutside,
            containmentBsp: null);

    private static RuntimeEntityLivenessSample Sample(
        uint guid,
        ushort generation,
        bool visible,
        bool retained = false) =>
        new(
            new RuntimeEntityKey(guid, generation),
            guid,
            visible,
            retained);

    private sealed class RecordingSink : IRuntimeEntityExpirySink
    {
        public List<RuntimeEntityExpiryCandidate> Expired { get; } = [];

        public bool Expire(RuntimeEntityExpiryCandidate candidate)
        {
            Expired.Add(candidate);
            return true;
        }

        public bool Destroy(DeleteObject.Parsed delete) => true;
    }

    private sealed class FixedCellSource(ObjCell? cell) : IRuntimeEntityCurrentCellSource
    {
        public ObjCell? CurrentCell => cell;
    }
}
