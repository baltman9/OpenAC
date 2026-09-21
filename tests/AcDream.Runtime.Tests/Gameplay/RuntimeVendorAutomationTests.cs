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
        // SellPrice(100, 0.5, qty 1) == ceil(0.5*100 - 0.1) == 50.
        Assert.Equal(50, item.UnitPrice);
    }

    [Fact]
    public void ProjectedUnitPriceMatchesTheVendorWindowSellRateFormula()
    {
        using var host = new NoWindowGameRuntimeHost();
        host.Start();
        using var vendor = new RuntimeVendorAutomation(host.Runtime);

        var shopItem = new VendorShopItem(
            ItemGuid: 0x50002000u,
            StackSize: 1,
            WeenieClassId: 1234u,
            Name: "Fixture Sword",
            ItemType: (uint)ItemType.Weapon,
            IconId: 0x06000001u,
            Value: 137);
        host.Runtime.InventoryOwner.Vendor.Apply(0x40001000u, Profile, [shopItem]);

        // The window's VendorUiController.ComputeShopItemPrice prices a shop
        // listing with VendorPricing.SellPrice against the vendor's SellPrice
        // rate -- the adapter must reproduce that exact call, not BuyPrice.
        int perUnit = VendorPricing.PerUnitValue(shopItem.Value ?? 0, shopItem.DescStackSize);
        int expected = VendorPricing.SellPrice(perUnit, shopItem.ItemType ?? 0u, Profile.SellPrice, 1);

        PluginVendorItem item = Assert.Single(vendor.Items);
        Assert.Equal(expected, item.UnitPrice);
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

    /// <summary>
    /// Mutation: project SellPrice as BuyRate, or drop any one of the
    /// merchandise fields, and a plugin can no longer tell what the vendor
    /// pays or what it refuses -- which is the whole of a vendor-visit plan.
    /// </summary>
    [Fact]
    public void ProjectsTheShopTermsAVisitPlannerNeeds()
    {
        using var host = new NoWindowGameRuntimeHost();
        host.Start();
        using var vendor = new RuntimeVendorAutomation(host.Runtime);

        var profile = new VendorShopProfile(
            MerchandiseItemTypes: (uint)ItemType.Weapon | (uint)ItemType.Armor,
            MerchandiseMinValue: 25u,
            MerchandiseMaxValue: 30_000u,
            DealMagicalItems: true,
            BuyPrice: 0.75f,
            SellPrice: 1.15f,
            AlternateCurrencyWcid: 0u,
            AlternateCurrencyAmount: 0u,
            AlternateCurrencyPluralName: string.Empty);
        host.Runtime.InventoryOwner.Vendor.Apply(0x40001000u, profile, []);

        PluginVendorProfile projected = vendor.Profile;
        // BuyPrice is what the vendor PAYS; SellPrice is what it charges, and
        // that already reaches plugins per listing as UnitPrice.
        Assert.Equal(0.75f, projected.BuyRate);
        Assert.Equal((uint)ItemType.Weapon | (uint)ItemType.Armor, projected.DealsInItemTypes);
        Assert.Equal(25u, projected.MinimumValue);
        Assert.Equal(30_000u, projected.MaximumValue);
        Assert.True(projected.DealsInMagicalItems);
        Assert.False(projected.UsesAlternateCurrency);
        Assert.Equal(0u, projected.AlternateCurrencyWeenieClassId);
        Assert.Equal(0u, projected.AlternateCurrencyAmount);
        Assert.Null(projected.AlternateCurrencyName);
    }

    /// <summary>
    /// Mutation: carry the state's currency name and amount through
    /// unconditionally and a coin vendor reports an empty-named currency it
    /// does not take, so a planner counts the wrong purse.
    /// </summary>
    [Fact]
    public void ProjectsTheAlternateCurrencyOnlyWhenTheVendorTakesOne()
    {
        using var host = new NoWindowGameRuntimeHost();
        host.Start();
        using var vendor = new RuntimeVendorAutomation(host.Runtime);

        host.Runtime.InventoryOwner.Vendor.Apply(
            0x40001000u,
            Profile with
            {
                AlternateCurrencyWcid = 20630u,
                AlternateCurrencyAmount = 17u,
                AlternateCurrencyPluralName = "Writs of Refusal",
            },
            []);

        PluginVendorProfile takesWrits = vendor.Profile;
        Assert.True(takesWrits.UsesAlternateCurrency);
        Assert.Equal(20630u, takesWrits.AlternateCurrencyWeenieClassId);
        Assert.Equal(17u, takesWrits.AlternateCurrencyAmount);
        Assert.Equal("Writs of Refusal", takesWrits.AlternateCurrencyName);

        // A coin vendor whose state still carries a stale amount must not
        // report a currency it does not take.
        host.Runtime.InventoryOwner.Vendor.Apply(
            0x40001001u,
            Profile with
            {
                AlternateCurrencyWcid = 0u,
                AlternateCurrencyAmount = 17u,
                AlternateCurrencyPluralName = "Writs of Refusal",
            },
            []);

        PluginVendorProfile takesCoin = vendor.Profile;
        Assert.False(takesCoin.UsesAlternateCurrency);
        Assert.Equal(0u, takesCoin.AlternateCurrencyAmount);
        Assert.Null(takesCoin.AlternateCurrencyName);
    }

    /// <summary>
    /// Mutation: return the last profile when nothing is open and a plugin
    /// plans against a shop the character has already walked away from.
    /// </summary>
    [Fact]
    public void ReportsNoShopTermsUntilAVendorOpensAndAgainOnceItCloses()
    {
        using var host = new NoWindowGameRuntimeHost();
        host.Start();
        using var vendor = new RuntimeVendorAutomation(host.Runtime);

        Assert.Equal(default, vendor.Profile);

        host.Runtime.InventoryOwner.Vendor.Apply(0x40001000u, Profile, []);
        Assert.Equal(Profile.BuyPrice, vendor.Profile.BuyRate);

        host.Runtime.InventoryOwner.Vendor.Close();
        Assert.Equal(default, vendor.Profile);
    }

    /// <summary>
    /// The no-limit sentinel a plugin compares against has to be the one the
    /// client itself treats as "no limit"; if the two ever part, a plugin
    /// reads a ceiling of four billion as a real one.
    /// </summary>
    [Fact]
    public void NoValueLimitIsTheSameSentinelTheClientTreatsAsUnlimited()
    {
        Assert.Equal(VendorSellAcceptability.NoLimit, PluginVendorProfile.NoValueLimit);

        using var host = new NoWindowGameRuntimeHost();
        host.Start();
        using var vendor = new RuntimeVendorAutomation(host.Runtime);
        host.Runtime.InventoryOwner.Vendor.Apply(
            0x40001000u,
            Profile with
            {
                MerchandiseMinValue = VendorSellAcceptability.NoLimit,
                MerchandiseMaxValue = VendorSellAcceptability.NoLimit,
            },
            []);

        Assert.Equal(PluginVendorProfile.NoValueLimit, vendor.Profile.MinimumValue);
        Assert.Equal(PluginVendorProfile.NoValueLimit, vendor.Profile.MaximumValue);
    }

    /// <summary>
    /// Mutation: drop MaxStackSize or ItemType from the listing projection
    /// and a plugin can neither count the pack slots a purchase needs nor
    /// tell whether the vendor will take the item back.
    /// </summary>
    [Fact]
    public void ProjectsEachListingsStackCeilingAndCategory()
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
                    StackSize: 100,
                    WeenieClassId: 1234u,
                    Name: "Fixture Peas",
                    ItemType: (uint)ItemType.SpellComponents,
                    IconId: 0x06000001u,
                    Value: 5,
                    DescStackSize: 1,
                    MaxStackSize: 25),
                new VendorShopItem(
                    ItemGuid: 0x50002001u,
                    StackSize: 1,
                    WeenieClassId: 1235u,
                    Name: "Fixture Sword",
                    ItemType: (uint)ItemType.Weapon,
                    IconId: 0x06000001u,
                    Value: 100),
            ]);

        IReadOnlyList<PluginVendorItem> items = vendor.Items;
        Assert.Equal(25, items[0].MaxStackSize);
        Assert.Equal((uint)ItemType.SpellComponents, items[0].ItemType);
        // Nothing authored a maximum for the sword, so it does not stack --
        // the same reading the vendor window makes of the missing field.
        Assert.Equal(1, items[1].MaxStackSize);
        Assert.Equal((uint)ItemType.Weapon, items[1].ItemType);
        // The category a plugin compares against the vendor's own mask.
        Assert.NotEqual(0u, items[0].ItemType & vendor.Profile.DealsInItemTypes);
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
    public void BuyAllDoesNotHoldTheClientWideInventoryBusyGate()
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
        host.Runtime.Session.CurrentSession!.GameActionCapture = _ => { };

        vendor.AddToBuyList(0x50002000u, 1);
        Assert.Equal(PluginVendorCommandStatus.Sent, vendor.BuyAll().Status);

        // A vendor transaction is not the same "use" the client-wide busy
        // count guards -- retail never produces the generic use-completion
        // that reservation expects, so holding it here would wedge every
        // other item command until the response arrived (or forever, if it
        // never does).
        Assert.Equal(0, host.Runtime.ActionOwner.Transactions.Inventory.BusyCount);
    }

    [Fact]
    public void BuyAllRefusesASecondCallUntilTheResponseArrivesThenAccepts()
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
        host.Runtime.Session.CurrentSession!.GameActionCapture = _ => { };

        vendor.AddToBuyList(0x50002000u, 1);
        Assert.Equal(PluginVendorCommandStatus.Sent, vendor.BuyAll().Status);

        vendor.AddToBuyList(0x50002000u, 1);
        Assert.Equal(PluginVendorCommandStatus.Busy, vendor.BuyAll().Status);

        // Generic use completion reports the transaction outcome, while the
        // outstanding inventory operation waits for the vendor response.
        host.Runtime.ActionOwner.Transactions.CompleteUse(0u);
        Assert.Equal(PluginVendorCommandStatus.Busy, vendor.BuyAll().Status);
        host.Runtime.InventoryOwner.Vendor.Apply(0x40001000u, Profile, []);
        Assert.Equal(PluginVendorCommandStatus.Sent, vendor.BuyAll().Status);
    }

    [Fact]
    public void PollRaisesTransactionCompletedOnASuccessfulBuyResponse()
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
        host.Runtime.Session.CurrentSession!.GameActionCapture = _ => { };

        var completions = new List<PluginVendorTransaction>();
        vendor.TransactionCompleted += completions.Add;

        vendor.AddToBuyList(0x50002000u, 1);
        vendor.BuyAll();

        // Before H2, LastItemUseCompletion.Revision never advances for a
        // vendor buy/sell (nothing here calls TryDispatchUse), so the old
        // revision-polling Poll() never observed this response at all.
        host.Runtime.ActionOwner.Transactions.CompleteUse(0u);
        vendor.Poll();

        PluginVendorTransaction completion = Assert.Single(completions);
        Assert.Equal(PluginVendorTransactionKind.Buy, completion.Kind);
        Assert.True(completion.Success);
        Assert.Null(completion.Notice);
    }

    [Fact]
    public void PollRaisesTransactionCompletedOnAFailedSellResponse()
    {
        using var host = new NoWindowGameRuntimeHost();
        host.Start();
        using var vendor = new RuntimeVendorAutomation(host.Runtime);
        host.Runtime.InventoryOwner.Vendor.Apply(0x40001000u, Profile, []);
        host.Runtime.PlayerIdentity.ServerGuid = 0x50000001u;
        host.Runtime.Session.CurrentSession!.GameActionCapture = _ => { };
        host.Runtime.InventoryOwner.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = 0x50002000u,
            WeenieClassId = 1234u,
            Name = "Fixture Sword",
            Type = ItemType.Weapon,
            IconId = 0x06000001u,
            Value = 100,
            StackSize = 1,
            ContainerId = host.Runtime.PlayerIdentity.ServerGuid,
        });

        var completions = new List<PluginVendorTransaction>();
        vendor.TransactionCompleted += completions.Add;

        Assert.Equal(
            PluginVendorCommandStatus.Sent,
            vendor.AddToSellList(0x50002000u).Status);
        Assert.Equal(PluginVendorCommandStatus.Sent, vendor.SellAll().Status);

        host.Runtime.ActionOwner.Transactions.CompleteUse(0x0002u);
        vendor.Poll();

        PluginVendorTransaction completion = Assert.Single(completions);
        Assert.Equal(PluginVendorTransactionKind.Sell, completion.Kind);
        Assert.False(completion.Success);
        Assert.NotNull(completion.Notice);
        Assert.Contains("weenie error 2", completion.Notice, StringComparison.Ordinal);
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

    /// <summary>
    /// Mutation pin: omit matching vendor-response completion in the shared
    /// request owner; the final pending assertion fails.
    /// </summary>
    [Fact]
    public void BuyAllUsesSharedPendingShopUntilMatchingRefresh()
    {
        using var host = new NoWindowGameRuntimeHost();
        host.Start();
        using var vendor = new RuntimeVendorAutomation(host.Runtime);
        const uint firstVendor = 0x40001000u;
        host.Runtime.InventoryOwner.Vendor.Apply(firstVendor, Profile,
        [
            new VendorShopItem(0x50002000u, 1, 1234u, "Sword",
                (uint)ItemType.Weapon, 0x06000001u, 100),
        ]);
        host.Runtime.Session.CurrentSession!.GameActionCapture = _ => { };
        Assert.Equal(PluginVendorCommandStatus.Sent,
            vendor.AddToBuyList(0x50002000u, 1).Status);

        Assert.Equal(PluginVendorCommandStatus.Sent, vendor.BuyAll().Status);
        Assert.True(host.Runtime.InventoryOwner.Transactions.TryGetPending(
            out PendingInventoryRequest request));
        Assert.Equal(InventoryRequestKind.Shop, request.Kind);
        Assert.Equal(firstVendor, request.ItemId);
        Assert.Equal(0, host.Runtime.InventoryOwner.Transactions.BusyCount);

        host.Runtime.ActionOwner.Transactions.CompleteUse(0u);
        Assert.True(host.Runtime.InventoryOwner.Transactions.HasPendingRequest);
        host.Runtime.InventoryOwner.Vendor.Apply(0x40002000u, Profile, []);
        Assert.True(host.Runtime.InventoryOwner.Transactions.HasPendingRequest);
        host.Runtime.InventoryOwner.Vendor.Apply(firstVendor, Profile, []);
        Assert.False(host.Runtime.InventoryOwner.Transactions.HasPendingRequest);
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

    [Fact]
    public void AddToSellListRejectsAnItemNotOwnedByThePlayer()
    {
        using var host = new NoWindowGameRuntimeHost();
        host.Start();
        using var vendor = new RuntimeVendorAutomation(host.Runtime);
        host.Runtime.InventoryOwner.Vendor.Apply(0x40001000u, Profile, []);
        host.Runtime.PlayerIdentity.ServerGuid = 0x50000001u;
        host.Runtime.InventoryOwner.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = 0x50002000u,
            Type = ItemType.Weapon,
            Value = 100,
            StackSize = 1,
            ContainerId = 0x50009999u, // some other container, not the player
        });

        Assert.Equal(
            PluginVendorCommandStatus.InvalidItem,
            vendor.AddToSellList(0x50002000u).Status);
    }

    [Fact]
    public void AddToSellListRejectsAVendorListingGuid()
    {
        using var host = new NoWindowGameRuntimeHost();
        host.Start();
        using var vendor = new RuntimeVendorAutomation(host.Runtime);
        host.Runtime.PlayerIdentity.ServerGuid = 0x50000001u;
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
        // A materialized client object under the same guid, owned by the
        // player -- ownership alone must not be enough; a vendor listing
        // can never be staged for sale, it belongs to the shop.
        host.Runtime.InventoryOwner.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = 0x50002000u,
            Type = ItemType.Weapon,
            Value = 100,
            StackSize = 1,
            ContainerId = 0x50000001u,
        });

        Assert.Equal(
            PluginVendorCommandStatus.InvalidItem,
            vendor.AddToSellList(0x50002000u).Status);
    }

    [Fact]
    public void AddToSellListRejectsAnEquippedItem()
    {
        using var host = new NoWindowGameRuntimeHost();
        host.Start();
        using var vendor = new RuntimeVendorAutomation(host.Runtime);
        host.Runtime.InventoryOwner.Vendor.Apply(0x40001000u, Profile, []);
        host.Runtime.PlayerIdentity.ServerGuid = 0x50000001u;
        host.Runtime.InventoryOwner.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = 0x50002000u,
            Type = ItemType.Weapon,
            Value = 100,
            StackSize = 1,
            ContainerId = 0x50000001u,
            CurrentlyEquippedLocation = EquipMask.MeleeWeapon,
        });

        Assert.Equal(
            PluginVendorCommandStatus.InvalidItem,
            vendor.AddToSellList(0x50002000u).Status);
    }

    [Fact]
    public void AddToSellListAcceptsAPlayerOwnedUnequippedNonListingItem()
    {
        using var host = new NoWindowGameRuntimeHost();
        host.Start();
        using var vendor = new RuntimeVendorAutomation(host.Runtime);
        host.Runtime.InventoryOwner.Vendor.Apply(0x40001000u, Profile, []);
        host.Runtime.PlayerIdentity.ServerGuid = 0x50000001u;
        host.Runtime.InventoryOwner.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = 0x50002000u,
            Type = ItemType.Weapon,
            Value = 100,
            StackSize = 1,
            ContainerId = 0x50000001u,
        });

        Assert.Equal(
            PluginVendorCommandStatus.Sent,
            vendor.AddToSellList(0x50002000u).Status);
    }

    [Fact]
    public void OpeningAVendorClearsStagedListsFromThePreviousVendor()
    {
        using var host = new NoWindowGameRuntimeHost();
        host.Start();
        using var vendor = new RuntimeVendorAutomation(host.Runtime);
        host.Runtime.PlayerIdentity.ServerGuid = 0x50000001u;
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
        host.Runtime.InventoryOwner.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = 0x50003000u,
            Type = ItemType.Weapon,
            Value = 100,
            StackSize = 1,
            ContainerId = 0x50000001u,
        });
        vendor.AddToBuyList(0x50002000u, 1);
        vendor.AddToSellList(0x50003000u);
        Assert.NotEmpty(vendor.BuyList);
        Assert.NotEmpty(vendor.SellList);

        // A vendor switch (a new ApproachVendor without an intervening
        // Close) -- the old shopping list is against the wrong vendor.
        host.Runtime.InventoryOwner.Vendor.Apply(0x40002000u, Profile, []);

        Assert.Empty(vendor.BuyList);
        Assert.Empty(vendor.SellList);
    }

    [Fact]
    public void RemoveAndClearReportUnavailableBeforeTheSessionReachesInWorld()
    {
        using var host = new NoWindowGameRuntimeHost();
        var vendor = new RuntimeVendorAutomation(host.Runtime);

        Assert.Equal(
            PluginVendorCommandStatus.Unavailable,
            vendor.RemoveFromBuyList(0x50002000u).Status);
        Assert.Equal(
            PluginVendorCommandStatus.Unavailable,
            vendor.RemoveFromSellList(0x50002000u).Status);
        Assert.Equal(
            PluginVendorCommandStatus.Unavailable,
            vendor.ClearBuyList().Status);
        Assert.Equal(
            PluginVendorCommandStatus.Unavailable,
            vendor.ClearSellList().Status);
    }

    [Fact]
    public void AnErrorLessUseDoneAfterAMoveRequestFailureIsReportedAsAFailure()
    {
        using var host = new NoWindowGameRuntimeHost();
        host.Start();
        host.Runtime.PlayerIdentity.ServerGuid = 0x50000001u;
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
        host.Runtime.Session.CurrentSession!.GameActionCapture = _ => { };

        var completions = new List<PluginVendorTransaction>();
        vendor.TransactionCompleted += completions.Add;

        vendor.AddToBuyList(0x50002000u, 1);
        vendor.BuyAll();

        // A rejected vendor buy/sell (no pack space, over-burden, negative
        // payout, ...) can arrive as InventoryServerSaveFailed followed by
        // UseDone with error zero. The request failure decides the outcome.
        host.Runtime.InventoryOwner.Objects.RejectMove(
            host.Runtime.PlayerIdentity.ServerGuid, 0x0002u);
        host.Runtime.ActionOwner.Transactions.CompleteUse(0u);
        vendor.Poll();

        PluginVendorTransaction completion = Assert.Single(completions);
        Assert.Equal(PluginVendorTransactionKind.Buy, completion.Kind);
        Assert.False(completion.Success);
        Assert.NotNull(completion.Notice);
        Assert.Contains("weenie error 2", completion.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public void AMoveRequestFailureCarryingNoErrorCodeDropsTheParenthetical()
    {
        // A latched failure can itself carry error == 0 -- the rejection is
        // real (RolledBack), but there is no weenie error code to report.
        // The notice must say plainly that the transaction failed, not the
        // misleading "(weenie error 0)".
        using var host = new NoWindowGameRuntimeHost();
        host.Start();
        host.Runtime.PlayerIdentity.ServerGuid = 0x50000001u;
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
        host.Runtime.Session.CurrentSession!.GameActionCapture = _ => { };

        var completions = new List<PluginVendorTransaction>();
        vendor.TransactionCompleted += completions.Add;

        vendor.AddToBuyList(0x50002000u, 1);
        vendor.BuyAll();

        host.Runtime.InventoryOwner.Objects.RejectMove(
            host.Runtime.PlayerIdentity.ServerGuid, 0u);
        host.Runtime.ActionOwner.Transactions.CompleteUse(0u);
        vendor.Poll();

        PluginVendorTransaction completion = Assert.Single(completions);
        Assert.False(completion.Success);
        Assert.NotNull(completion.Notice);
        Assert.DoesNotContain("weenie error", completion.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailureWithAnItemWireIdIsAttributedToThePendingShopRequest()
    {
        using var host = new NoWindowGameRuntimeHost();
        host.Start();
        host.Runtime.PlayerIdentity.ServerGuid = 0x50000001u;
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
        host.Runtime.Session.CurrentSession!.GameActionCapture = _ => { };

        var completions = new List<PluginVendorTransaction>();
        vendor.TransactionCompleted += completions.Add;

        vendor.AddToBuyList(0x50002000u, 1);
        vendor.BuyAll();

        host.Runtime.InventoryOwner.Objects.RejectMove(0x50002000u, 0x0002u);
        host.Runtime.ActionOwner.Transactions.CompleteUse(0u);
        vendor.Poll();

        PluginVendorTransaction completion = Assert.Single(completions);
        Assert.Equal(PluginVendorTransactionKind.Buy, completion.Kind);
        Assert.False(completion.Success);
    }

    [Fact]
    public void ASuccessfulBuyWithNoMoveRequestFailureStillReportsSuccess()
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
        host.Runtime.Session.CurrentSession!.GameActionCapture = _ => { };

        var completions = new List<PluginVendorTransaction>();
        vendor.TransactionCompleted += completions.Add;

        vendor.AddToBuyList(0x50002000u, 1);
        vendor.BuyAll();
        host.Runtime.ActionOwner.Transactions.CompleteUse(0u);
        vendor.Poll();

        PluginVendorTransaction completion = Assert.Single(completions);
        Assert.True(completion.Success);
        Assert.Null(completion.Notice);
    }
}
