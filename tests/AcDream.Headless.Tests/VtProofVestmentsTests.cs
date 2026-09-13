using AcDream.Core.Items;
using AcDream.Plugin.Abstractions;

namespace AcDream.Headless.Tests;

/// <summary>
/// The offline half of the session proof's dressing step. It decides which of
/// the character's own equipment a redirected item enchantment could land on,
/// and the live run acts on that answer before it starts the macro.
/// </summary>
public sealed class VtProofVestmentsTests
{
    /// <summary>
    /// The real projection the live run saw: three armour pieces, a caster, a
    /// missile-ammunition stack and a thrown weapon, all owned and none of the
    /// armour worn.
    /// Mutation: widen <c>VtProofVestments.Locations</c> to
    /// <c>EquipMask.All</c> and this fails — the run would try to "dress"
    /// itself in the wand and the arrows, unwield the caster every rule needs,
    /// and stop the macro at its own preparation gate.
    /// </summary>
    [Fact]
    public void OnlyClothingArmourAndTheShieldHandCountAsSomethingABaneCanLandOn()
    {
        PluginEquipmentItem[] owned =
        [
            Item(0x80006ABDu, "Wand", EquipMask.Held, EquipMask.Held),
            Item(0x80002816u, "Arrow", EquipMask.MissileAmmo),
            Item(0x800000C9u, "Atlatl Dart", EquipMask.MissileAmmo),
            Item(0x8000329Bu, "Greater Amuli Shadow Coat", (EquipMask)0x00001A00u),
            Item(0x80001BB3u, "Pathwarden Plate Hauberk", (EquipMask)0x00001E00u),
            Item(0x80001BB5u, "Pathwarden Robe", (EquipMask)0x00007F01u),
        ];

        Assert.Equal(
            ["Greater Amuli Shadow Coat", "Pathwarden Plate Hauberk", "Pathwarden Robe"],
            VtProofVestments.NotYetWorn(owned).Select(static item => item.Name));
        Assert.False(VtProofVestments.IsDressed(owned));
    }

    /// <summary>
    /// A necklace is jewellery, not a vestment: the server does not redirect a
    /// Bane onto one, so wearing one is not being dressed.
    /// Mutation: add <c>EquipMask.Jewelry</c> to <c>Locations</c> and this
    /// fails — the run would call itself dressed and the Bane rows would go on
    /// finding nothing.
    /// </summary>
    [Fact]
    public void JewelleryIsNotSomethingABaneCanLandOn()
    {
        PluginEquipmentItem[] owned =
        [
            Item(1u, "Amulet", EquipMask.NeckWear, EquipMask.NeckWear),
            Item(2u, "Ring", EquipMask.FingerWear, EquipMask.FingerWearLeft),
        ];

        Assert.False(VtProofVestments.IsDressed(owned));
        Assert.Empty(VtProofVestments.NotYetWorn(owned));
    }

    /// <summary>
    /// Once one piece is on, the character is dressed and the step has nothing
    /// left to do for it — so a rerun against an already-dressed character
    /// stages nothing and changes nothing.
    /// Mutation: drop the <c>!item.IsEquipped</c> filter from
    /// <c>NotYetWorn</c> and this fails — every run would re-issue a wield for
    /// a piece already on.
    /// </summary>
    [Fact]
    public void APieceAlreadyOnIsNotStagedAgain()
    {
        PluginEquipmentItem[] owned =
        [
            Item(1u, "Greater Amuli Shadow Coat", (EquipMask)0x00001A00u, (EquipMask)0x00001A00u),
            Item(2u, "Pathwarden Robe", (EquipMask)0x00007F01u),
        ];

        Assert.True(VtProofVestments.IsDressed(owned));
        Assert.Equal(
            ["Pathwarden Robe"],
            VtProofVestments.NotYetWorn(owned).Select(static item => item.Name));
    }

    private static PluginEquipmentItem Item(
        uint objectId,
        string name,
        EquipMask validLocations,
        EquipMask equippedLocation = EquipMask.None) =>
        new(
            objectId,
            name,
            0u,
            (uint)validLocations,
            (uint)equippedLocation,
            0u,
            0u,
            0,
            0,
            0,
            0,
            0d);
}
