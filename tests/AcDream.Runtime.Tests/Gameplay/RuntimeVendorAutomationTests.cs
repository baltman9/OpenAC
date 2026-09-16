using AcDream.Core.Items;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Tests.Support;

namespace AcDream.Runtime.Tests.Gameplay;

// RuntimeVendorAutomation is the E-VENDOR plugin surface adapter: it
// projects VendorState (with retail pricing) and stages buy/sell lists
// locally, committing them through the same WorldSession builders the
// vendor window's own Buy All / Sell All buttons use.
public sealed class RuntimeVendorAutomationTests
{
    private static readonly VendorShopProfile Profile = new(
        MerchandiseItemTypes: 0xFFFFFFFFu,
        MerchandiseMinValue: 0u,
        MerchandiseMaxValue: 1_000_000u,
        DealMagicalItems: true,
        BuyPrice: 2.0f,
        SellPrice: 0.5f,
        AlternateCurrencyWcid: 0u,
        AlternateCurrencyAmount: 0u,
        AlternateCurrencyPluralName: string.Empty);

    [Fact]
    public void ReportsClosedUntilAVendorOpens()
    {
        using var vendor = new RuntimeVendorAutomation(new NoWindowGameRuntimeHost().Runtime);
        Assert.False(vendor.IsOpen);
        Assert.Empty(vendor.Items);
    }

    [Fact]
    public void ProjectsListedItemsWithRetailBuyPricing()
    {
        using var host = new NoWindowGameRuntimeHost();
        host.Start();
        using var vendor = new RuntimeVendorAutomation(host.Runtime);

        host.Runtime.InventoryOwner.Vendor.Apply(
            0x40001000u,
            Profile,
            [
                new VendorShopItem(
                    ItemGuid: 0x50002000u,
                    StackSize: 1,
                    WeenieClassId: 1234u,
                    Name: "Fixture Sword",
                    ItemType: (uint)ItemType.Weapon,
                    IconId: 0x06000001u,
                    Value: 100),
            ]);

        Assert.True(vendor.IsOpen);
        Assert.Equal(0x40001000u, vendor.VendorObjectId);
        PluginVendorItem item = Assert.Single(vendor.Items);
        Assert.Equal(0x50002000u, item.TemplateObjectId);
        Assert.Equal("Fixture Sword", item.Name);
        // BuyPrice(100, 2.0, qty 1) == 200.
        Assert.Equal(200, item.UnitPrice);
    }

    [Fact]
    public void CapturesPropertiesForAMaterializedShopItem()
    {
        using var host = new NoWindowGameRuntimeHost();
        host.Start();
        using var vendor = new RuntimeVendorAutomation(host.Runtime);

        host.Runtime.InventoryOwner.Vendor.Apply(
            0x40001000u,
            Profile,
            [
                new VendorShopItem(
                    ItemGuid: 0x50002000u,
                    StackSize: 1,
                    WeenieClassId: 1234u,
                    Name: "Fixture Sword",
                    ItemType: (uint)ItemType.Weapon,
                    IconId: 0x06000001u,
                    Value: 100),
            ]);

        Assert.True(vendor.TryCaptureProperties(0x50002000u, out PluginItemProperties properties));
        Assert.False(vendor.TryCaptureProperties(0x99999999u, out _));
    }

    [Fact]
    public void StagesAndCommitsABuyListThroughTheWireBuilder()
    {
        using var host = new NoWindowGameRuntimeHost();
        host.Start();
        using var vendor = new RuntimeVendorAutomation(host.Runtime);
        host.Runtime.InventoryOwner.Vendor.Apply(
            0x40001000u,
            Profile,
            [
                new VendorShopItem(
                    ItemGuid: 0x50002000u,
                    StackSize: 5,
                    WeenieClassId: 1234u,
                    Name: "Fixture Sword",
                    ItemType: (uint)ItemType.Weapon,
                    IconId: 0x06000001u,
                    Value: 100),
            ]);
        var captured = new List<byte[]>();
        host.Runtime.Session.CurrentSession!.GameActionCapture = body => captured.Add(body);

        Assert.Equal(
            PluginVendorCommandStatus.Sent,
            vendor.AddToBuyList(0x50002000u, 2).Status);
        Assert.Equal([(0x50002000u, 2)], vendor.BuyList);

        Assert.Equal(PluginVendorCommandStatus.Sent, vendor.BuyAll().Status);
        Assert.NotEmpty(captured);
        Assert.Empty(vendor.BuyList);
    }

    [Fact]
    public void RejectsAddingAnItemTheVendorDoesNotList()
    {
        using var host = new NoWindowGameRuntimeHost();
        host.Start();
        using var vendor = new RuntimeVendorAutomation(host.Runtime);
        host.Runtime.InventoryOwner.Vendor.Apply(0x40001000u, Profile, []);

        Assert.Equal(
            PluginVendorCommandStatus.InvalidItem,
            vendor.AddToBuyList(0x50002000u, 1).Status);
    }

    [Fact]
    public void BuyAllReportsInvalidItemWhenTheBuyListIsEmpty()
    {
        using var host = new NoWindowGameRuntimeHost();
        host.Start();
        using var vendor = new RuntimeVendorAutomation(host.Runtime);
        host.Runtime.InventoryOwner.Vendor.Apply(0x40001000u, Profile, []);

        Assert.Equal(PluginVendorCommandStatus.InvalidItem, vendor.BuyAll().Status);
    }

    [Fact]
    public void ClosingTheVendorRaisesClosedAndClearsStagedLists()
    {
        using var host = new NoWindowGameRuntimeHost();
        host.Start();
        using var vendor = new RuntimeVendorAutomation(host.Runtime);
        host.Runtime.InventoryOwner.Vendor.Apply(
            0x40001000u,
            Profile,
            [
                new VendorShopItem(
                    ItemGuid: 0x50002000u,
                    StackSize: 1,
                    WeenieClassId: 1234u,
                    Name: "Fixture Sword",
                    ItemType: (uint)ItemType.Weapon,
                    IconId: 0x06000001u,
                    Value: 100),
            ]);
        vendor.AddToBuyList(0x50002000u, 1);

        int opened = 0;
        int closed = 0;
        vendor.Opened += _ => opened++;
        vendor.Closed += () => closed++;

        host.Runtime.InventoryOwner.Vendor.Close();

        Assert.Equal(0, opened); // Opened already happened before subscribing.
        Assert.Equal(1, closed);
        Assert.False(vendor.IsOpen);
        Assert.Empty(vendor.BuyList);
    }

    [Fact]
    public void OpenedFiresWhenAVendorFirstOpens()
    {
        using var host = new NoWindowGameRuntimeHost();
        host.Start();
        using var vendor = new RuntimeVendorAutomation(host.Runtime);
        int opened = 0;
        uint openedVendorId = 0u;
        vendor.Opened += id =>
        {
            opened++;
            openedVendorId = id;
        };

        host.Runtime.InventoryOwner.Vendor.Apply(0x40001000u, Profile, []);

        Assert.Equal(1, opened);
        Assert.Equal(0x40001000u, openedVendorId);
    }
}
