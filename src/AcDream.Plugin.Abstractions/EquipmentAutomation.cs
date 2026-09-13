namespace AcDream.Plugin.Abstractions;

/// <summary>One owned item that can participate in VTank equipment policy.</summary>
public readonly record struct PluginEquipmentItem(
    uint ObjectId,
    string Name,
    uint ItemType,
    uint ValidLocations,
    uint EquippedLocation,
    uint ContainerObjectId,
    uint WielderObjectId,
    byte CombatUse,
    int DamageType,
    int WeaponSkill,
    int Damage,
    double DamageVariance)
{
    public bool IsEquipped => EquippedLocation != 0u;
    public uint AmmoType { get; init; }
    public int StackSize { get; init; } = 1;
    public int WeaponType { get; init; }

    /// <summary>
    /// How many creatures one swing of this weapon can strike. Greater than
    /// one means a kill sentence may name a creature the swing was not aimed
    /// at.
    /// </summary>
    public int Cleaving { get; init; }

    /// <summary>
    /// The imbue burned into the weapon: the rends that change which element
    /// it strikes with, plus the critical bonuses.
    /// </summary>
    public int ImbuedEffect { get; init; }

    /// <summary>
    /// The damage type this weapon cleaves the target's resistance to. Same
    /// bit layout as <see cref="DamageType"/>. The name is the profile
    /// format's word for it; the client calls the same property a resistance
    /// modifier type.
    /// </summary>
    public int ResistanceCleaving { get; init; }

    /// <summary>
    /// The creature type this weapon slays, or zero. A weapon that slays what
    /// is being fought outranks every other consideration.
    /// </summary>
    public int SlayerCreatureType { get; init; }

    /// <summary>
    /// Rating bonuses a quest weapon can carry. Each says only whether the
    /// weapon has the bonus at all, which is the only thing weapon choice
    /// asks: a bonus is worth a fixed number of points however large its own
    /// multiplier happens to be.
    /// </summary>
    public bool CrushingBlow { get; init; }

    /// <inheritdoc cref="CrushingBlow"/>
    public bool BitingStrike { get; init; }

    /// <inheritdoc cref="CrushingBlow"/>
    public bool ArmorCleaving { get; init; }
}

public enum PluginEquipmentCommandStatus
{
    Unavailable = 0,
    InvalidItem,
    Busy,
    AlreadyEquipped,
    Started,
    Refused,
}

public readonly record struct PluginEquipmentCommandResult(
    PluginEquipmentCommandStatus Status,
    string? Notice = null)
{
    public bool Accepted => Status is
        PluginEquipmentCommandStatus.AlreadyEquipped
        or PluginEquipmentCommandStatus.Started;
}

public interface IEquipmentAutomation
{
    bool IsAvailable => false;
    bool IsBusy => false;

    /// <summary>
    /// Everything the character owns that can be worn or wielded, in a
    /// defined order: what is equipped first, then by name, then by object
    /// id. Clients rely on that order — "the first wand" means the one being
    /// held if any wand is — so a host must not hand back an arbitrary
    /// sequence.
    /// </summary>
    IReadOnlyList<PluginEquipmentItem> CaptureOwnedEquipment() =>
        Array.Empty<PluginEquipmentItem>();

    PluginEquipmentCommandResult Equip(
        uint objectId,
        uint requestedLocation = 0u) =>
        new(PluginEquipmentCommandStatus.Unavailable);
}
