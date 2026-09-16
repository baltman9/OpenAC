namespace AcDream.Plugin.Abstractions;

public enum PluginObjectClass
{
    Unknown = 0,
    MeleeWeapon = 1,
    Armor = 2,
    Clothing = 3,
    Jewelry = 4,
    Monster = 5,
    Food = 6,
    Money = 7,
    Misc = 8,
    MissileWeapon = 9,
    Container = 10,
    Gem = 11,
    SpellComponent = 12,
    Key = 13,
    Portal = 14,
    TradeNote = 15,
    ManaStone = 16,
    Plant = 17,
    BaseCooking = 18,
    BaseAlchemy = 19,
    BaseFletching = 20,
    CraftedCooking = 21,
    CraftedAlchemy = 22,
    CraftedFletching = 23,
    Player = 24,
    Vendor = 25,
    Door = 26,
    Corpse = 27,
    Lifestone = 28,
    HealingKit = 29,
    Lockpick = 30,
    WandStaffOrb = 31,
    Bundle = 32,
    Book = 33,
    Journal = 34,
    Sign = 35,
    Housing = 36,
    Npc = 37,
    Foci = 38,
    Salvage = 39,
    Ust = 40,
    Services = 41,
    Scroll = 42,
    CombatPet = 43,
}

// Shared retail item-type/public-weenie-bitfield classification. Lives here
// (rather than behind ClientObject) so a value materialized only as raw
// fields -- a vendor shop listing, for instance -- can still be classified
// without constructing a full ClientObject.
public static class PluginObjectClassifier
{
    public static PluginObjectClass Classify(uint itemType, uint publicWeenieBitfield)
    {
        PluginObjectClass result = itemType switch
        {
            _ when (itemType & 0x00000001u) != 0u => PluginObjectClass.MeleeWeapon,
            _ when (itemType & 0x00000002u) != 0u => PluginObjectClass.Armor,
            _ when (itemType & 0x00000004u) != 0u => PluginObjectClass.Clothing,
            _ when (itemType & 0x00000008u) != 0u => PluginObjectClass.Jewelry,
            _ when (itemType & 0x00000010u) != 0u => PluginObjectClass.Monster,
            _ when (itemType & 0x00000020u) != 0u => PluginObjectClass.Food,
            _ when (itemType & 0x00000040u) != 0u => PluginObjectClass.Money,
            _ when (itemType & 0x00000080u) != 0u => PluginObjectClass.Misc,
            _ when (itemType & 0x00000100u) != 0u => PluginObjectClass.MissileWeapon,
            _ when (itemType & 0x00000200u) != 0u => PluginObjectClass.Container,
            _ when (itemType & 0x00000400u) != 0u => PluginObjectClass.Bundle,
            _ when (itemType & 0x00000800u) != 0u => PluginObjectClass.Gem,
            _ when (itemType & 0x00001000u) != 0u => PluginObjectClass.SpellComponent,
            _ when (itemType & 0x00004000u) != 0u => PluginObjectClass.Key,
            _ when (itemType & 0x00008000u) != 0u => PluginObjectClass.WandStaffOrb,
            _ when (itemType & 0x00010000u) != 0u => PluginObjectClass.Portal,
            _ when (itemType & 0x00040000u) != 0u => PluginObjectClass.TradeNote,
            _ when (itemType & 0x00080000u) != 0u => PluginObjectClass.ManaStone,
            _ when (itemType & 0x00100000u) != 0u => PluginObjectClass.Services,
            _ when (itemType & 0x00200000u) != 0u => PluginObjectClass.Plant,
            _ when (itemType & 0x00400000u) != 0u => PluginObjectClass.BaseCooking,
            _ when (itemType & 0x00800000u) != 0u => PluginObjectClass.BaseAlchemy,
            _ when (itemType & 0x01000000u) != 0u => PluginObjectClass.BaseFletching,
            _ when (itemType & 0x02000000u) != 0u => PluginObjectClass.CraftedCooking,
            _ when (itemType & 0x04000000u) != 0u => PluginObjectClass.CraftedAlchemy,
            _ when (itemType & 0x08000000u) != 0u => PluginObjectClass.CraftedFletching,
            _ when (itemType & 0x20000000u) != 0u => PluginObjectClass.Ust,
            _ when (itemType & 0x40000000u) != 0u => PluginObjectClass.Salvage,
            _ => PluginObjectClass.Unknown,
        };

        result = publicWeenieBitfield switch
        {
            _ when (publicWeenieBitfield & 0x00000008u) != 0u => PluginObjectClass.Player,
            _ when (publicWeenieBitfield & 0x00000200u) != 0u => PluginObjectClass.Vendor,
            _ when (publicWeenieBitfield & 0x00001000u) != 0u => PluginObjectClass.Door,
            _ when (publicWeenieBitfield & 0x00002000u) != 0u => PluginObjectClass.Corpse,
            _ when (publicWeenieBitfield & 0x00004000u) != 0u => PluginObjectClass.Lifestone,
            _ when (publicWeenieBitfield & 0x00008000u) != 0u => PluginObjectClass.Food,
            _ when (publicWeenieBitfield & 0x00010000u) != 0u => PluginObjectClass.HealingKit,
            _ when (publicWeenieBitfield & 0x00020000u) != 0u => PluginObjectClass.Lockpick,
            _ when (publicWeenieBitfield & 0x00040000u) != 0u => PluginObjectClass.Portal,
            _ when (publicWeenieBitfield & 0x00800000u) != 0u => PluginObjectClass.Foci,
            _ when (publicWeenieBitfield & 0x00000001u) != 0u => PluginObjectClass.Container,
            _ => result,
        };

        if ((itemType & 0x00002000u) != 0u && result == PluginObjectClass.Unknown)
        {
            result = (publicWeenieBitfield & 0x00000002u) != 0u
                ? PluginObjectClass.Journal
                : (publicWeenieBitfield & 0x00000004u) != 0u
                    ? PluginObjectClass.Sign
                    : (publicWeenieBitfield & 0x0000000Fu) != 0u
                        ? PluginObjectClass.Book
                        : result;
        }
        if (result == PluginObjectClass.Monster && (publicWeenieBitfield & 0x10u) == 0u)
            result = PluginObjectClass.Npc;
        if (result == PluginObjectClass.Monster && (publicWeenieBitfield & 0x04000000u) != 0u)
            result = PluginObjectClass.CombatPet;
        return result;
    }
}

public readonly record struct PluginWorldObject(
    uint ObjectId,
    uint WeenieClassId,
    string Name,
    PluginObjectClass ObjectClass,
    uint ItemType,
    uint ContainerObjectId,
    uint WielderObjectId)
{
    public bool IsOwned { get; init; }
    public bool IsLandscape { get; init; }
    public bool HasPosition { get; init; }
    public PluginNavigationPosition Position { get; init; }
    public bool HasAppraisalData { get; init; }
    public int LastIdTime { get; init; }
    public bool IsDoorOpen { get; init; }
    public int StackSize { get; init; } = 1;
    public int ItemsCapacity { get; init; }
    public int ContainersCapacity { get; init; }
    public IReadOnlyList<uint> SpellIds { get; init; } = Array.Empty<uint>();
    public IReadOnlyList<uint> ActiveSpellIds { get; init; } = Array.Empty<uint>();
    public uint IconId { get; init; }
}

public interface IWorldObjectAutomation
{
    bool IsAvailable => false;
    uint OpenContainerObjectId => 0u;

    IReadOnlyList<PluginWorldObject> CaptureObjects() =>
        Array.Empty<PluginWorldObject>();

    bool TryGet(uint objectId, out PluginWorldObject value)
    {
        value = default;
        return false;
    }

    bool TryCaptureProperties(
        uint objectId,
        out PluginItemProperties properties)
    {
        properties = default;
        return false;
    }

    PluginItemCommandResult Identify(uint objectId) =>
        new(PluginItemCommandStatus.Unavailable);
}
