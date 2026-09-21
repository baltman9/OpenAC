using AcDream.Core.Items;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Gameplay;

/// <summary>
/// Taking a stackable thing into an inventory with no free slot left.
/// Joining a stack the character already holds costs no slot at all, so
/// whether a pack has room is not what decides it -- and while the search
/// for a pack with room ran first, a character whose packs were full was
/// refused a pickup that would only have added to a stack already inside
/// one of them. The player double-clicking and automation taking loot were
/// both refused, and neither had any way to tell that the thing it wanted
/// needed no room.
///
/// The join is settled first now, and only what it cannot take falls through
/// to the search and to the "completely full" notice.
///
/// Mutation: put the room search back in front of the join and
/// <see cref="AFullInventoryStillTakesWhatJoinsAStackItAlreadyHolds"/> and
/// <see cref="ThePanelPathAndTheOutcomePathAgreeOnAFullInventory"/> both go
/// red -- the first on the refusal, the second on the join never going out.
/// </summary>
public sealed class FullPackStackPickupTests
{
    private const uint Player = 0x50000001u;
    private const uint Corpse = 0x8000E001u;
    private const uint LooseCoins = 0x8000E002u;
    private const uint CarriedCoins = 0x8000E003u;
    private const uint Trinket = 0x8000E004u;
    private const uint CoinWeenie = 0x0000_0111u;
    private const int CoinStackMax = 100;

    /// <summary>
    /// The case the defect was reported on: every slot is taken and the
    /// quantity on the ground fits in a stack already carried.
    /// </summary>
    [Fact]
    public void AFullInventoryStillTakesWhatJoinsAStackItAlreadyHolds()
    {
        using var h = new Harness(carriedStackSize: 10, looseStackSize: 5);

        Assert.Equal(
            RuntimeBackpackPlacementOutcome.Sent,
            h.Interaction.TryPlaceWorldItemInBackpack(LooseCoins));

        Assert.Equal([(LooseCoins, CarriedCoins, 5u)], h.Merges);
        Assert.Empty(h.Pickups);
        Assert.Empty(h.Notices);
    }

    /// <summary>
    /// The stack it would join is already at its limit, so the quantity has
    /// nowhere to go and no free slot to sit in on its own. This is the
    /// refusal the notice is for, and it still carries one.
    /// </summary>
    [Fact]
    public void AFullInventoryOverAFullStackIsStillRefused()
    {
        using var h = new Harness(
            carriedStackSize: CoinStackMax,
            looseStackSize: 5);

        Assert.Equal(
            RuntimeBackpackPlacementOutcome.NoRoom,
            h.Interaction.TryPlaceWorldItemInBackpack(LooseCoins));

        Assert.Empty(h.Merges);
        Assert.Empty(h.Pickups);
        Assert.NotEmpty(h.Notices);
    }

    /// <summary>
    /// Only part of the quantity would fit in the carried stack, so the rest
    /// needs a slot of its own and there is none. A join is all or nothing:
    /// a stack that cannot take the whole amount is passed over, and with no
    /// room anywhere the take is refused.
    /// </summary>
    [Fact]
    public void AFullInventoryOverAStackThatCannotTakeTheWholeAmountIsRefused()
    {
        using var h = new Harness(
            carriedStackSize: CoinStackMax - 2,
            looseStackSize: 5);

        Assert.Equal(
            RuntimeBackpackPlacementOutcome.NoRoom,
            h.Interaction.TryPlaceWorldItemInBackpack(LooseCoins));

        Assert.Empty(h.Merges);
        Assert.Empty(h.Pickups);
    }

    /// <summary>
    /// Something that does not stack needs a slot whatever else is carried,
    /// so a full inventory refuses it exactly as before.
    /// </summary>
    [Fact]
    public void AFullInventoryStillRefusesSomethingThatDoesNotStack()
    {
        using var h = new Harness(carriedStackSize: 10, looseStackSize: 5);

        Assert.Equal(
            RuntimeBackpackPlacementOutcome.NoRoom,
            h.Interaction.TryPlaceWorldItemInBackpack(Trinket));

        Assert.Empty(h.Merges);
        Assert.Empty(h.Pickups);
        Assert.NotEmpty(h.Notices);
    }

    /// <summary>
    /// Room left over: the join still comes first, and the quantity goes to
    /// the stack rather than taking a slot of its own. This is what the
    /// placement did before the order changed, and it has to keep doing it.
    /// </summary>
    [Fact]
    public void AnInventoryWithRoomStillPrefersTheStackOverAFreeSlot()
    {
        using var h = new Harness(
            carriedStackSize: 10,
            looseStackSize: 5,
            itemSlots: 8);

        Assert.Equal(
            RuntimeBackpackPlacementOutcome.Sent,
            h.Interaction.TryPlaceWorldItemInBackpack(LooseCoins));

        Assert.Equal([(LooseCoins, CarriedCoins, 5u)], h.Merges);
        Assert.Empty(h.Pickups);
    }

    /// <summary>
    /// The inventory panel asks through the bool and automation through the
    /// outcome. They are the same placement, so they reach the same ending:
    /// whichever one asks, the join goes out.
    /// </summary>
    [Fact]
    public void ThePanelPathAndTheOutcomePathAgreeOnAFullInventory()
    {
        using var h = new Harness(carriedStackSize: 10, looseStackSize: 5);

        Assert.True(h.Interaction.PlaceWorldItemInBackpack(LooseCoins));

        Assert.Equal([(LooseCoins, CarriedCoins, 5u)], h.Merges);
        Assert.Empty(h.Pickups);
    }

    /// <summary>
    /// A character carrying a part-filled stack of coins in an inventory with
    /// no room to spare, standing over a corpse holding more of the same
    /// coins and one thing that does not stack.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        internal Harness(
            int carriedStackSize,
            int looseStackSize,
            int itemSlots = 1)
        {
            Objects.AddOrUpdate(new ClientObject
            {
                ObjectId = Player,
                Name = "Player",
                Type = ItemType.Creature,
                ItemsCapacity = itemSlots,
                ContainersCapacity = 0,
            });
            Objects.AddOrUpdate(Coins(CarriedCoins, Player, carriedStackSize));
            Objects.ReplaceContents(Player, [CarriedCoins]);

            Objects.AddOrUpdate(new ClientObject
            {
                ObjectId = Corpse,
                Name = "Corpse",
                Type = ItemType.Container,
                ItemsCapacity = 8,
            });
            Objects.AddOrUpdate(Coins(LooseCoins, Corpse, looseStackSize));
            Objects.AddOrUpdate(new ClientObject
            {
                ObjectId = Trinket,
                Name = "Trinket",
                Type = ItemType.Misc,
                ContainerId = Corpse,
                WeenieClassId = 0x0000_0222u,
                StackSize = 1,
                StackSizeMax = 1,
            });
            Objects.ReplaceContents(Corpse, [LooseCoins, Trinket]);

            Interaction = new RuntimeItemInteraction(
                Objects,
                new RuntimeInteractionTransactionState(
                    new InventoryTransactionState(Objects)),
                new InteractionState(),
                playerGuid: () => Player,
                sendUse: null,
                sendUseWithTarget: null,
                sendWield: null,
                sendDrop: null,
                groundObjectId: () => Corpse,
                placeInBackpack: (item, container, placement) =>
                    Pickups.Add((item, container, placement)),
                systemMessage: Notices.Add,
                sendStackableMerge: (source, target, amount) =>
                    Merges.Add((source, target, amount)));
        }

        internal ClientObjectTable Objects { get; } = new();
        internal RuntimeItemInteraction Interaction { get; }
        internal List<(uint Item, uint Container, int Placement)> Pickups { get; } = [];
        internal List<(uint Source, uint Target, uint Amount)> Merges { get; } = [];
        internal List<string> Notices { get; } = [];

        public void Dispose() => Interaction.Dispose();
    }

    private static ClientObject Coins(
        uint objectId,
        uint containerId,
        int stackSize) => new()
    {
        ObjectId = objectId,
        Name = "Pyreal",
        Type = ItemType.Money,
        ContainerId = containerId,
        WeenieClassId = CoinWeenie,
        StackSize = stackSize,
        StackSizeMax = CoinStackMax,
    };
}
