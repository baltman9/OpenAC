using AcDream.Core.Items;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Gameplay;

public sealed class AutoWieldGenerationTests
{
    private const uint Player = 0x50000001u;

    [Fact]
    public void GenerationReplacementCancelsPendingWeaponSwitch()
    {
        var objects = new ClientObjectTable();
        var blocker = new ClientObject
        {
            ObjectId = 0x60000001u,
            WielderId = Player,
            ValidLocations = EquipMask.MeleeWeapon,
        };
        objects.AddOrUpdate(blocker);
        objects.MoveItem(
            blocker.ObjectId,
            Player,
            newSlot: -1,
            newEquipLocation: EquipMask.MeleeWeapon);
        var requested = new ClientObject
        {
            ObjectId = 0x60000002u,
            ContainerId = Player,
            ValidLocations = EquipMask.MeleeWeapon,
        };
        objects.AddOrUpdate(requested);

        using var controller = new AutoWieldController(
            objects,
            () => Player,
            sendWield: null,
            sendPutItemInContainer: (_, _, _) => true);
        Assert.True(controller.TryWield(requested));
        Assert.True(controller.IsBusy);

        objects.ReplaceGeneration(WorldReplacement(blocker.ObjectId), generation: 2);

        Assert.False(controller.IsBusy);
    }

    /// <summary>
    /// Mutation pin: send the secondary wield before clearing an occupied
    /// Shield slot. The blocker move is omitted and the server sees a clash.
    /// </summary>
    [Fact]
    public void SecondaryMeleeIntentClearsShieldBlockerBeforeWield()
    {
        var objects = new ClientObjectTable();
        const uint blockerId = 0x60000010u;
        const uint swordId = 0x60000011u;
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = blockerId,
            Type = ItemType.Armor,
            WielderId = Player,
            ValidLocations = EquipMask.Shield,
        });
        objects.MoveItem(blockerId, Player, -1, EquipMask.Shield);
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = swordId,
            Type = ItemType.MeleeWeapon,
            ContainerId = Player,
            ValidLocations = EquipMask.MeleeWeapon,
        });
        var actions = new List<string>();
        using var controller = new AutoWieldController(
            objects,
            () => Player,
            (item, mask) =>
            {
                actions.Add($"wield:{item:X8}:{mask:X8}");
                return true;
            },
            (item, _, _) =>
            {
                actions.Add($"move:{item:X8}");
                return true;
            });

        Assert.True(controller.TryWieldSecondary(objects.Get(swordId)!));
        Assert.Equal(["move:60000010"], actions);
        Assert.True(objects.ApplyConfirmedServerMove(
            blockerId, Player, newWielderId: 0u));
        Assert.Equal(
            ["move:60000010", "wield:60000011:00200000"], actions);
    }

    private static WeenieData WorldReplacement(uint guid) => new(
        Guid: guid,
        Name: "replacement",
        Type: ItemType.Misc,
        WeenieClassId: 1,
        IconId: 0,
        IconOverlayId: 0,
        IconUnderlayId: 0,
        Effects: 0,
        Value: null,
        StackSize: null,
        StackSizeMax: null,
        Burden: null,
        ContainerId: null,
        WielderId: null,
        ValidLocations: null,
        CurrentWieldedLocation: null,
        Priority: null,
        ItemsCapacity: null,
        ContainersCapacity: null,
        Structure: null,
        MaxStructure: null,
        Workmanship: null);
}
