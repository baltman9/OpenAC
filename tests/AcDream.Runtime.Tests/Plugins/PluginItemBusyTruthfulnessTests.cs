using AcDream.Core.Items;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Plugins;
using AcDream.Runtime.Tests.Support;

namespace AcDream.Runtime.Tests.Plugins;

/// <summary>
/// IsBusy is the one thing a caller can look at to decide whether to ask now
/// or wait, so it has to be true for every reason a command would come back
/// busy -- not just for the one that is easiest to see. A command refused for
/// a reason IsBusy does not report leaves the caller with nothing to wait on:
/// it reads "not busy", asks, is refused anyway, and either spins or sleeps
/// out a whole heartbeat for a fifth of a second's pacing.
/// </summary>
public sealed class PluginItemBusyTruthfulnessTests
{
    private const uint Corpse = 0x8000B001u;
    private const uint OwnedItem = 0x8000B002u;

    /// <summary>
    /// Reason one: a request of the caller's own is still in flight.
    /// Mutation: drop the in-flight half of the predicate and IsBusy reads
    /// false here.
    /// </summary>
    [Fact]
    public void ARequestInFlightReadsAsBusyOnBothItemSurfaces()
    {
        using Fixture fixture = Fixture.Create();

        fixture.Runtime.InventoryOwner.Transactions.IncrementBusyCount();

        Assert.True(fixture.Surface.Items.IsBusy);
        Assert.True(fixture.Surface.Loot.IsBusy);
        Assert.Equal(
            PluginItemCommandStatus.Busy,
            fixture.Surface.Loot.Open(Corpse).Status);
        Assert.Equal(
            PluginItemCommandStatus.Busy,
            fixture.Surface.Items.Use(OwnedItem).Status);
    }

    /// <summary>
    /// Reason two: the short pacing the client keeps between one use and the
    /// next. This is the one that was invisible -- the request channel is
    /// free, so IsBusy read false, and the very next command was refused all
    /// the same. Mutation: drop the pacing half of the predicate and IsBusy
    /// reads false while these commands still come back busy.
    /// </summary>
    [Fact]
    public void ThePacingBetweenTwoUsesReadsAsBusyOnBothItemSurfaces()
    {
        using Fixture fixture = Fixture.Create();

        Assert.True(
            fixture.Runtime.ItemInteractionOwner
                .TryConsumeUseThrottleForAutomation());

        // Nothing is in flight; only the pacing stands in the way.
        Assert.True(fixture.Runtime.InventoryOwner.Transactions.CanBeginRequest);
        Assert.True(fixture.Surface.Items.IsBusy);
        Assert.True(fixture.Surface.Loot.IsBusy);
        Assert.Equal(
            PluginItemCommandStatus.Busy,
            fixture.Surface.Loot.Open(Corpse).Status);
        Assert.Equal(
            PluginItemCommandStatus.Busy,
            fixture.Surface.Items.Use(OwnedItem).Status);
        Assert.Equal(
            PluginItemCommandStatus.Busy,
            fixture.Surface.Items.Apply(OwnedItem, Corpse).Status);
    }

    /// <summary>
    /// The contract in the direction that matters to a caller: once IsBusy
    /// reads false, asking is not refused as busy. Every reason is cleared
    /// here in turn, and the command only goes through once the last of them
    /// has.
    /// </summary>
    [Fact]
    public void OnceIsBusyReadsFalseNeitherAUseNorAnOpenIsRefusedAsBusy()
    {
        using Fixture fixture = Fixture.Create();

        fixture.Runtime.InventoryOwner.Transactions.IncrementBusyCount();
        Assert.True(
            fixture.Runtime.ItemInteractionOwner
                .TryConsumeUseThrottleForAutomation());
        Assert.True(fixture.Surface.Items.IsBusy);

        // The in-flight request finishes, but the pacing has not lapsed:
        // still busy, and still refused.
        fixture.Runtime.InventoryOwner.Transactions.CompleteUse(0u);
        Assert.True(fixture.Surface.Items.IsBusy);
        Assert.Equal(
            PluginItemCommandStatus.Busy,
            fixture.Surface.Loot.Open(Corpse).Status);

        // The pacing lapses too. Now, and only now, IsBusy reads false -- and
        // what it says holds.
        fixture.Host.Advance(
            (RuntimeInteractionTransactionState.RetailUseThrottleMs + 50L)
                / 1000d);
        Assert.False(fixture.Surface.Items.IsBusy);
        Assert.False(fixture.Surface.Loot.IsBusy);
        Assert.NotEqual(
            PluginItemCommandStatus.Busy,
            fixture.Surface.Loot.Open(Corpse).Status);
    }

    /// <summary>
    /// Using one item on another takes the same pacing, and used to report a
    /// refusal there as a start -- the caller was told its request had gone
    /// out when nothing had been sent, and then waited for an answer that
    /// could never come.
    /// </summary>
    [Fact]
    public void AnApplyInsideThePacingIsBusyRatherThanReportedAsStarted()
    {
        using Fixture fixture = Fixture.Create();

        Assert.True(
            fixture.Runtime.ItemInteractionOwner
                .TryConsumeUseThrottleForAutomation());

        PluginItemCommandResult early =
            fixture.Surface.Items.Apply(OwnedItem, Corpse);

        Assert.Equal(PluginItemCommandStatus.Busy, early.Status);
        Assert.Empty(fixture.Sent);
    }

    private sealed class Fixture : IDisposable
    {
        private Fixture(
            NoWindowGameRuntimeHost host,
            RuntimeAutomationSurface surface,
            List<byte[]> sent)
        {
            Host = host;
            Surface = surface;
            Sent = sent;
        }

        internal NoWindowGameRuntimeHost Host { get; }

        internal RuntimeAutomationSurface Surface { get; }

        internal List<byte[]> Sent { get; }

        internal GameRuntime Runtime => Host.Runtime;

        internal static Fixture Create()
        {
            var host = new NoWindowGameRuntimeHost();
            Assert.Equal(
                RuntimeSessionStartStatus.Connected,
                host.Start().Status);
            var sent = new List<byte[]>();
            host.Runtime.Session.CurrentSession!.GameMessageCapture =
                (body, _) => sent.Add(body);
            var surface = new RuntimeAutomationSurface();
            surface.Bind(
                host.Runtime,
                host.Runtime.CharacterOwner,
                host.Runtime.ActionOwner.SpellCast);
            RuntimeItemInteraction items = host.Runtime.ItemInteractionOwner;
            surface.BindItems(
                items.TryUseItemForAutomation,
                items.TryApplyItem,
                items.TryMoveItemForAutomation,
                items.TryMergeItemsForAutomation,
                items.TryDropItemForAutomation,
                items.TryGiveItemForAutomation,
                items.TryPlaceWorldItemInBackpack,
                items.TryAppraiseForAutomation);

            host.Runtime.InventoryOwner.Objects.AddOrUpdate(new ClientObject
            {
                ObjectId = Corpse,
                Name = "Corpse",
                Type = ItemType.Container,
                PublicWeenieBitfield = (uint)(
                    PublicWeenieFlags.Corpse | PublicWeenieFlags.Openable),
                Useability = ItemUseability.Remote,
            });
            host.Runtime.InventoryOwner.Objects.AddOrUpdate(new ClientObject
            {
                ObjectId = OwnedItem,
                Name = "Flask",
                ContainerId = host.Runtime.PlayerIdentity.ServerGuid,
                Useability = ItemUseability.Contained,
            });
            return new Fixture(host, surface, sent);
        }

        public void Dispose()
        {
            Surface.Dispose();
            Host.Dispose();
        }
    }
}
