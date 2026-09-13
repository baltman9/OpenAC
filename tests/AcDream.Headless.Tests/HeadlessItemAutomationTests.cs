using AcDream.Core.Items;
using AcDream.Headless.Hosting;
using AcDream.Headless.Plugins;
using AcDream.Runtime;
using AcDream.Runtime.Gameplay;

namespace AcDream.Headless.Tests;

public sealed class HeadlessItemAutomationTests
{
    private const uint Player = 0x50000001u;
    private const uint Item = 0x50000A01u;
    private const uint Container = 0x50000B01u;
    private const uint Source = 0x50000A03u;
    private const uint Target = 0x50000A04u;
    private const uint TargetedUseability =
        ((ItemUseability.Remote | ItemUseability.Self) << 16) | ItemUseability.Contained;

    [Fact]
    public void TryUse_OwnedUseableItemSends()
    {
        var h = new Harness();
        h.AddOwnedItem(Item, ItemUseability.Contained);

        Assert.True(h.Automation.TryUse(Item));

        Assert.Equal(new[] { Item }, h.Transport.UseCalls);
    }

    [Fact]
    public void TryUse_TargetedItemDoesNotSend()
    {
        var h = new Harness();
        h.AddOwnedItem(Item, TargetedUseability);

        Assert.False(h.Automation.TryUse(Item));

        Assert.Empty(h.Transport.UseCalls);
    }

    [Fact]
    public void TryUse_TradeStateItemDoesNotSend()
    {
        var h = new Harness();
        h.AddOwnedItem(Item, ItemUseability.Contained, tradeState: 1);

        Assert.False(h.Automation.TryUse(Item));

        Assert.Empty(h.Transport.UseCalls);
    }

    [Fact]
    public void TryUse_VolatileRareDoesNotSend()
    {
        var h = new Harness();
        h.AddOwnedItem(
            Item,
            ItemUseability.Contained,
            flags: PublicWeenieFlags.VolatileRare);

        Assert.False(h.Automation.TryUse(Item));

        Assert.Empty(h.Transport.UseCalls);
    }

    [Fact]
    public void TryUse_NonUseableItemDoesNotSend()
    {
        var h = new Harness();
        h.AddOwnedItem(Item, ItemUseability.No);

        Assert.False(h.Automation.TryUse(Item));

        Assert.Empty(h.Transport.UseCalls);
    }

    [Fact]
    public void TryUse_SecondUseInsideTheThrottleDoesNotSend()
    {
        var h = new Harness();
        h.AddOwnedItem(Item, ItemUseability.Contained);

        Assert.True(h.Automation.TryUse(Item));
        Assert.False(h.Automation.TryUse(Item));

        Assert.Equal(new[] { Item }, h.Transport.UseCalls);
    }

    [Fact]
    public void TryUse_SecondUseAfterTheThrottleElapsesSends()
    {
        var h = new Harness();
        h.AddOwnedItem(Item, ItemUseability.Contained);

        Assert.True(h.Automation.TryUse(Item));
        h.Runtime.ActionOwner.Transactions.CompleteUse(0u);
        _ = h.Runtime.Clock.Advance(0.25);
        Assert.True(h.Automation.TryUse(Item));

        Assert.Equal(new[] { Item, Item }, h.Transport.UseCalls);
    }

    [Fact]
    public void TryUse_RefusesWithoutDispatchingWhenTheSenderReturnsFalse()
    {
        var h = new Harness();
        h.AddOwnedItem(Item, ItemUseability.Contained);
        h.Transport.SendUseSucceeds = false;

        Assert.False(h.Automation.TryUse(Item));

        Assert.Equal(0, h.Runtime.InventoryOwner.Transactions.BusyCount);
        Assert.True(h.Runtime.InventoryOwner.Transactions.CanBeginRequest);
    }

    [Fact]
    public void TryUse_ReleasesTheReservationAndRethrowsWhenDispatchThrows()
    {
        var h = new Harness();
        h.AddOwnedItem(Item, ItemUseability.Contained);
        h.Transport.ThrowOnSend = true;

        Assert.Throws<InvalidOperationException>(() => h.Automation.TryUse(Item));

        Assert.Equal(0, h.Runtime.InventoryOwner.Transactions.BusyCount);
        Assert.True(h.Runtime.InventoryOwner.Transactions.CanBeginRequest);
    }

    [Fact]
    public void TryMove_FullStackSendsPutInContainer()
    {
        var h = new Harness();
        h.AddOwnedItem(Item, ItemUseability.Undef, stackSize: 1, stackSizeMax: 1);
        h.AddOwnedContainer(Container);

        Assert.True(h.Automation.TryMove(Item, Container, amount: 0u, placement: 3));

        Assert.Equal((Item, Container, 3), Assert.Single(h.Puts));
        Assert.Empty(h.Splits);
    }

    [Fact]
    public void TryMove_PartialStackSendsSplitWithAmountAndClampedPlacement()
    {
        var h = new Harness();
        h.AddOwnedItem(Item, ItemUseability.Undef, stackSize: 5, stackSizeMax: 10);
        h.AddOwnedContainer(Container);

        Assert.True(h.Automation.TryMove(Item, Container, amount: 2u, placement: -1));

        Assert.Equal((Item, Container, 0u, 2u), Assert.Single(h.Splits));
        Assert.Empty(h.Puts);
    }

    [Fact]
    public void TryMerge_SendsThePlannersAmount()
    {
        var h = new Harness();
        h.AddOwnedItem(
            Source, ItemUseability.Undef, stackSize: 3, stackSizeMax: 10, weenieClassId: 7u);
        h.AddOwnedItem(
            Target, ItemUseability.Undef, stackSize: 2, stackSizeMax: 10, weenieClassId: 7u);

        Assert.True(h.Automation.TryMerge(Source, Target, amount: 0u));

        Assert.Equal((Source, Target, 3u), Assert.Single(h.Merges));
    }

    [Fact]
    public void TryMoveAndTryMerge_RefuseWhileBusy()
    {
        var h = new Harness();
        h.AddOwnedItem(Item, ItemUseability.Undef, stackSize: 1, stackSizeMax: 1);
        h.AddOwnedContainer(Container);
        h.AddOwnedItem(
            Source, ItemUseability.Undef, stackSize: 3, stackSizeMax: 10, weenieClassId: 7u);
        h.AddOwnedItem(
            Target, ItemUseability.Undef, stackSize: 2, stackSizeMax: 10, weenieClassId: 7u);
        h.Runtime.InventoryOwner.Transactions.IncrementBusyCount();

        Assert.False(h.Automation.TryMove(Item, Container, amount: 0u, placement: 0));
        Assert.False(h.Automation.TryMerge(Source, Target, amount: 0u));

        Assert.Empty(h.Puts);
        Assert.Empty(h.Merges);
    }

    [Fact]
    public void TryMoveAndTryMerge_RefuseWhileARequestIsPending()
    {
        var h = new Harness();
        h.AddOwnedItem(Item, ItemUseability.Undef, stackSize: 1, stackSizeMax: 1);
        h.AddOwnedContainer(Container);
        h.AddOwnedItem(
            Source, ItemUseability.Undef, stackSize: 3, stackSizeMax: 10, weenieClassId: 7u);
        h.AddOwnedItem(
            Target, ItemUseability.Undef, stackSize: 2, stackSizeMax: 10, weenieClassId: 7u);
        Assert.True(h.Automation.TryMove(Item, Container, amount: 0u, placement: 0));
        h.Puts.Clear();

        Assert.False(h.Automation.TryMove(Item, Container, amount: 0u, placement: 0));
        Assert.False(h.Automation.TryMerge(Source, Target, amount: 0u));

        Assert.Empty(h.Puts);
        Assert.Empty(h.Merges);
    }

    [Fact]
    public void TryMove_RefusesWithoutDispatchingWhenTheSenderReturnsFalse()
    {
        var h = new Harness();
        h.AddOwnedItem(Item, ItemUseability.Undef, stackSize: 1, stackSizeMax: 1);
        h.AddOwnedContainer(Container);
        h.PutResult = false;

        Assert.False(h.Automation.TryMove(Item, Container, amount: 0u, placement: 0));

        Assert.False(h.Runtime.InventoryOwner.Transactions.HasPendingRequest);
    }

    [Fact]
    public void TryMove_SplitRefusesWithoutDispatchingWhenTheSenderReturnsFalse()
    {
        var h = new Harness();
        h.AddOwnedItem(Item, ItemUseability.Undef, stackSize: 5, stackSizeMax: 10);
        h.AddOwnedContainer(Container);
        h.SplitResult = false;

        Assert.False(h.Automation.TryMove(Item, Container, amount: 2u, placement: 0));

        Assert.False(h.Runtime.InventoryOwner.Transactions.HasPendingRequest);
    }

    [Fact]
    public void TryMerge_RefusesWithoutDispatchingWhenTheSenderReturnsFalse()
    {
        var h = new Harness();
        h.AddOwnedItem(
            Source, ItemUseability.Undef, stackSize: 3, stackSizeMax: 10, weenieClassId: 7u);
        h.AddOwnedItem(
            Target, ItemUseability.Undef, stackSize: 2, stackSizeMax: 10, weenieClassId: 7u);
        h.MergeResult = false;

        Assert.False(h.Automation.TryMerge(Source, Target, amount: 0u));

        Assert.False(h.Runtime.InventoryOwner.Transactions.HasPendingRequest);
    }

    private sealed class FakeTransport : IRuntimeInteractionTransport
    {
        internal bool SendUseSucceeds { get; set; } = true;
        internal bool ThrowOnSend { get; set; }
        internal List<uint> UseCalls { get; } = [];

        public bool IsInWorld => true;

        public bool TrySendUse(uint serverGuid, out uint sequence)
        {
            UseCalls.Add(serverGuid);
            if (ThrowOnSend)
                throw new InvalidOperationException("send failed");
            sequence = SendUseSucceeds ? 1u : 0u;
            return SendUseSucceeds;
        }

        public bool TrySendPickup(
            uint itemGuid, uint destinationContainerId, int placement, out uint sequence)
        {
            sequence = 0u;
            return false;
        }
    }

    private sealed class Harness
    {
        internal readonly GameRuntime Runtime;
        internal readonly FakeTransport Transport = new();
        internal readonly List<(uint Item, uint Container, int Placement)> Puts = [];
        internal readonly List<(uint Item, uint Container, uint Placement, uint Amount)> Splits = [];
        internal readonly List<(uint Source, uint Target, uint Amount)> Merges = [];
        internal bool PutResult = true;
        internal bool SplitResult = true;
        internal bool MergeResult = true;
        internal readonly HeadlessItemAutomation Automation;

        internal Harness()
        {
            Runtime = NewRuntime();
            Runtime.PlayerIdentity.ServerGuid = Player;
            Runtime.InventoryOwner.Objects.AddOrUpdate(new ClientObject
            {
                ObjectId = Player,
                Type = ItemType.Creature,
            });
            Automation = new HeadlessItemAutomation(
                Runtime,
                Transport,
                (item, container, placement) =>
                {
                    Puts.Add((item, container, placement));
                    return PutResult;
                },
                (item, container, placement, amount) =>
                {
                    Splits.Add((item, container, placement, amount));
                    return SplitResult;
                },
                (source, target, amount) =>
                {
                    Merges.Add((source, target, amount));
                    return MergeResult;
                });
        }

        internal void AddOwnedItem(
            uint id,
            uint useability,
            int stackSize = 1,
            int stackSizeMax = 1,
            uint weenieClassId = 0u,
            int tradeState = 0,
            PublicWeenieFlags flags = PublicWeenieFlags.None) =>
            Runtime.InventoryOwner.Objects.AddOrUpdate(new ClientObject
            {
                ObjectId = id,
                Type = ItemType.Misc,
                ContainerId = Player,
                WeenieClassId = weenieClassId,
                Useability = useability,
                StackSize = stackSize,
                StackSizeMax = stackSizeMax,
                TradeState = tradeState,
                PublicWeenieBitfield = (uint)flags,
            });

        internal void AddOwnedContainer(uint id) =>
            Runtime.InventoryOwner.Objects.AddOrUpdate(new ClientObject
            {
                ObjectId = id,
                Type = ItemType.Container,
                ContainerId = Player,
                ItemsCapacity = 24,
            });

        private static GameRuntime NewRuntime()
        {
            var gameplay = new HeadlessGameplayOperations();
            var runtime = new GameRuntime(new GameRuntimeDependencies(
                gameplay,
                gameplay,
                gameplay,
                gameplay));
            gameplay.Bind(runtime, catalog: null, () => "account");
            return runtime;
        }
    }
}
