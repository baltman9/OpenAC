using AcDream.Core.Items;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Plugins;
using AcDream.Runtime.Tests.Support;

namespace AcDream.Runtime.Tests.Plugins;

/// <summary>
/// A take that sends nothing must not answer that it started. The placement
/// has four endings that send nothing -- no pack has room, a merge that was
/// planned and did not go out, a placement for this item already waiting on
/// the server, and a request this client could not take at all -- and all
/// four came back as Started. A bot looting a corpse into a full pack was
/// told its request was on its way and then waited for a completion that
/// could never arrive; nothing in its own state would ever have said
/// otherwise.
///
/// The placement now says which ending it reached and the surface maps each
/// one. Mutation: map every ending onto Started again and each case below
/// fails on the status it expected.
/// </summary>
public sealed class LootPickupOutcomeTests
{
    private const uint Corpse = 0x8000D001u;
    private const uint Gem = 0x8000D002u;

    [Theory]
    [InlineData(
        RuntimeBackpackPlacementOutcome.Sent,
        PluginItemCommandStatus.Started)]
    [InlineData(
        RuntimeBackpackPlacementOutcome.NoRoom,
        PluginItemCommandStatus.Refused)]
    [InlineData(
        RuntimeBackpackPlacementOutcome.AlreadyPending,
        PluginItemCommandStatus.Busy)]
    [InlineData(
        RuntimeBackpackPlacementOutcome.NotDispatched,
        PluginItemCommandStatus.Refused)]
    [InlineData(
        RuntimeBackpackPlacementOutcome.NotThisClients,
        PluginItemCommandStatus.Refused)]
    public void EachEndingOfTheTakeIsAnsweredAsWhatItReallyWas(
        RuntimeBackpackPlacementOutcome outcome,
        PluginItemCommandStatus expected)
    {
        using var host = new NoWindowGameRuntimeHost();
        Assert.Equal(RuntimeSessionStartStatus.Connected, host.Start().Status);
        using RuntimeAutomationSurface surface = OpenCorpseWithAGem(
            host,
            outcome);

        Assert.Equal(expected, surface.Loot.Pickup(Gem).Status);
    }

    /// <summary>
    /// Only the one ending that really sent something says the plugin may now
    /// wait for the server. This is the property a bot's loop keys on.
    /// </summary>
    [Theory]
    [InlineData(RuntimeBackpackPlacementOutcome.Sent, true)]
    [InlineData(RuntimeBackpackPlacementOutcome.NoRoom, false)]
    [InlineData(RuntimeBackpackPlacementOutcome.AlreadyPending, false)]
    [InlineData(RuntimeBackpackPlacementOutcome.NotDispatched, false)]
    [InlineData(RuntimeBackpackPlacementOutcome.NotThisClients, false)]
    public void OnlyASentTakeIsAccepted(
        RuntimeBackpackPlacementOutcome outcome,
        bool accepted)
    {
        using var host = new NoWindowGameRuntimeHost();
        Assert.Equal(RuntimeSessionStartStatus.Connected, host.Start().Status);
        using RuntimeAutomationSurface surface = OpenCorpseWithAGem(
            host,
            outcome);

        Assert.Equal(accepted, surface.Loot.Pickup(Gem).Accepted);
    }

    /// <summary>
    /// A refusal a plugin cannot act on is worth no more than a silence, so
    /// the two endings that are the client's own decision say which one it
    /// was.
    /// </summary>
    [Theory]
    [InlineData(RuntimeBackpackPlacementOutcome.NoRoom)]
    [InlineData(RuntimeBackpackPlacementOutcome.NotDispatched)]
    public void ARefusalTheClientChoseSaysWhy(
        RuntimeBackpackPlacementOutcome outcome)
    {
        using var host = new NoWindowGameRuntimeHost();
        Assert.Equal(RuntimeSessionStartStatus.Connected, host.Start().Status);
        using RuntimeAutomationSurface surface = OpenCorpseWithAGem(
            host,
            outcome);

        PluginItemCommandResult result = surface.Loot.Pickup(Gem);
        Assert.Equal(PluginItemCommandStatus.Refused, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.Notice));
    }

    /// <summary>
    /// The real placement, not a stand-in: it reports the send it made, and
    /// reports a request it could not take at all as that rather than as a
    /// send. A no-room run needs a character whose packs are really full,
    /// which this host has no way to build; the mapping above is what pins
    /// that ending.
    /// </summary>
    [Fact]
    public void TheRealPlacementReportsTheSendItMadeAndTheRequestItCouldNotTake()
    {
        using var host = new NoWindowGameRuntimeHost();
        Assert.Equal(RuntimeSessionStartStatus.Connected, host.Start().Status);
        ClientObjectTable objects = host.Runtime.InventoryOwner.Objects;
        AddCorpse(objects, Corpse);
        AddGem(objects, Gem, Corpse);
        objects.ReplaceContents(Corpse, [Gem]);
        Assert.True(host.Runtime.InventoryOwner.ExternalContainers.RequestOpen(
            Corpse,
            isCorpse: true));
        Assert.True(host.Runtime.InventoryOwner.ExternalContainers
            .ApplyViewContents(Corpse));

        Assert.Equal(
            RuntimeBackpackPlacementOutcome.NotThisClients,
            host.Runtime.ItemInteractionOwner.TryPlaceWorldItemInBackpack(0u));
        Assert.Equal(
            RuntimeBackpackPlacementOutcome.Sent,
            host.Runtime.ItemInteractionOwner.TryPlaceWorldItemInBackpack(Gem));
        // The bool the inventory panel reads still answers true for both the
        // send and the endings that send nothing, which is what its callers
        // have always been given.
        Assert.True(host.Runtime.ItemInteractionOwner
            .PlaceWorldItemInBackpack(Gem));
    }

    private static RuntimeAutomationSurface OpenCorpseWithAGem(
        NoWindowGameRuntimeHost host,
        RuntimeBackpackPlacementOutcome outcome)
    {
        ClientObjectTable objects = host.Runtime.InventoryOwner.Objects;
        AddCorpse(objects, Corpse);
        AddGem(objects, Gem, Corpse);
        objects.ReplaceContents(Corpse, [Gem]);
        var surface = new RuntimeAutomationSurface();
        surface.Bind(
            host.Runtime,
            host.Runtime.CharacterOwner,
            host.Runtime.ActionOwner.SpellCast);
        surface.BindItems(
            host.Runtime.ItemInteractionOwner.TryUseItemForAutomation,
            host.Runtime.ItemInteractionOwner.TryApplyItem,
            host.Runtime.ItemInteractionOwner.TryMoveItemForAutomation,
            host.Runtime.ItemInteractionOwner.TryMergeItemsForAutomation,
            host.Runtime.ItemInteractionOwner.TryDropItemForAutomation,
            host.Runtime.ItemInteractionOwner.TryGiveItemForAutomation,
            (_, _) => outcome,
            host.Runtime.ItemInteractionOwner.TryAppraiseForAutomation);
        Assert.True(host.Runtime.InventoryOwner.ExternalContainers.RequestOpen(
            Corpse,
            isCorpse: true));
        Assert.True(host.Runtime.InventoryOwner.ExternalContainers
            .ApplyViewContents(Corpse));
        host.Runtime.InventoryOwner.Transactions.CompleteUse(0u);
        return surface;
    }

    private static void AddCorpse(ClientObjectTable objects, uint objectId) =>
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = objectId,
            Name = "Corpse",
            Type = ItemType.Container,
            PublicWeenieBitfield = (uint)(
                PublicWeenieFlags.Corpse | PublicWeenieFlags.Openable),
            Useability = ItemUseability.Remote,
        });

    private static void AddGem(
        ClientObjectTable objects,
        uint objectId,
        uint containerId) =>
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = objectId,
            Name = "Gem",
            Type = ItemType.Gem,
            ContainerId = containerId,
            StackSize = 1,
            StackSizeMax = 1,
        });
}
