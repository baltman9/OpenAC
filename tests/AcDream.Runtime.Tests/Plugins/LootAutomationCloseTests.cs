using System.Buffers.Binary;
using AcDream.Core.Items;
using AcDream.Core.Net.Messages;
using AcDream.Plugin.Abstractions;
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
