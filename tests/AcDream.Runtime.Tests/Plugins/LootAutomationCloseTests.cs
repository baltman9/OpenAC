using System.Buffers.Binary;
using AcDream.Core.Items;
using AcDream.Core.Net.Messages;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Plugins;
using AcDream.Runtime.Tests.Support;

namespace AcDream.Runtime.Tests.Plugins;

public sealed class LootAutomationCloseTests
{
    [Fact]
    public void CurrentCorpseCloseUsesTheCanonicalRequestAndWaitsForServerState()
    {
        const uint corpse = 0x8000C001u;
        const uint otherCorpse = 0x8000C002u;
        using var host = new NoWindowGameRuntimeHost();
        Assert.Equal(RuntimeSessionStartStatus.Connected, host.Start().Status);
        var sent = new List<byte[]>();
        host.Runtime.Session.CurrentSession!.GameMessageCapture =
            (body, _) => sent.Add(body);
        using var surface = new RuntimeAutomationSurface();
        surface.Bind(
            host.Runtime,
            host.Runtime.CharacterOwner,
            host.Runtime.ActionOwner.SpellCast);
        AddCorpse(host.Runtime.InventoryOwner.Objects, corpse);
        AddCorpse(host.Runtime.InventoryOwner.Objects, otherCorpse);
        Assert.True(host.Runtime.InventoryOwner.ExternalContainers.RequestOpen(
            corpse,
            isCorpse: true));
        Assert.True(host.Runtime.InventoryOwner.ExternalContainers
            .ApplyViewContents(corpse));

        Assert.Equal(
            PluginItemCommandStatus.InvalidTarget,
            surface.Loot.Close(otherCorpse).Status);
        Assert.Empty(sent);

        host.Runtime.InventoryOwner.Transactions.IncrementBusyCount();
        Assert.Equal(
            PluginItemCommandStatus.Busy,
            surface.Loot.Close(corpse).Status);
        Assert.Empty(sent);
        host.Runtime.InventoryOwner.Transactions.CompleteUse(0u);

        Assert.Equal(
            PluginItemCommandStatus.Started,
            surface.Loot.Close(corpse).Status);
        byte[] message = Assert.Single(sent);
        Assert.Equal(
            InteractRequests.UseOpcode,
            BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(8)));
        Assert.Equal(
            corpse,
            BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(12)));
        Assert.Equal(
            corpse,
            host.Runtime.InventoryOwner.ExternalContainers.CurrentContainerId);

        Assert.True(host.Runtime.InventoryOwner.ExternalContainers
            .ApplyClose(corpse));
        host.Runtime.InventoryOwner.Transactions.CompleteUse(0u);
        Assert.Equal(
            0u,
            host.Runtime.InventoryOwner.ExternalContainers.CurrentContainerId);
    }

    /// <summary>
    /// Closing one container and opening the next are two uses, and the host
    /// paces uses apart. The open that lands inside that pacing window has
    /// not failed -- it is early -- so it comes back busy, and once the
    /// window has passed the very same call goes out. A refusal here is read
    /// by callers as "this container would not open", which costs the
    /// container one of its attempts and arms a back-off of its own: that is
    /// what put a two-second pause between one corpse and the next. Mutation:
    /// map the pacing window onto Refused again and the first open answers
    /// Refused instead of Busy.
    /// </summary>
    [Fact]
    public void AnOpenInsideTheUsePacingWindowIsBusyRatherThanRefused()
    {
        const uint corpse = 0x8000C011u;
        const uint nextCorpse = 0x8000C012u;
        using var host = new NoWindowGameRuntimeHost();
        Assert.Equal(RuntimeSessionStartStatus.Connected, host.Start().Status);
        var sent = new List<byte[]>();
        host.Runtime.Session.CurrentSession!.GameMessageCapture =
            (body, _) => sent.Add(body);
        using var surface = new RuntimeAutomationSurface();
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
            host.Runtime.ItemInteractionOwner.PlaceWorldItemInBackpack,
            host.Runtime.ItemInteractionOwner.TryAppraiseForAutomation);
        AddCorpse(host.Runtime.InventoryOwner.Objects, corpse);
        AddCorpse(host.Runtime.InventoryOwner.Objects, nextCorpse);
        Assert.True(host.Runtime.InventoryOwner.ExternalContainers.RequestOpen(
            corpse,
            isCorpse: true));
        Assert.True(host.Runtime.InventoryOwner.ExternalContainers
            .ApplyViewContents(corpse));

        Assert.Equal(
            PluginItemCommandStatus.Started,
            surface.Loot.Close(corpse).Status);
        Assert.True(host.Runtime.InventoryOwner.ExternalContainers
            .ApplyClose(corpse));
        host.Runtime.InventoryOwner.Transactions.CompleteUse(0u);

        // The request channel is free; only the pacing between two uses is
        // not, and that is not this container's doing.
        Assert.True(host.Runtime.InventoryOwner.Transactions.CanBeginRequest);
        Assert.Equal(
            PluginItemCommandStatus.Busy,
            surface.Loot.Open(nextCorpse).Status);
        Assert.Single(sent);

        host.Advance(
            (RuntimeInteractionTransactionState.RetailUseThrottleMs + 50L)
                / 1000d);
        Assert.Equal(
            PluginItemCommandStatus.Started,
            surface.Loot.Open(nextCorpse).Status);
        Assert.Equal(2, sent.Count);
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
}
