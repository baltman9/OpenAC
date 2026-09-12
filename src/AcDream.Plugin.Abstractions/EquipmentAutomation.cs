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
    /// bit layout as <see cref="DamageType"/>.
    /// </summary>
    public int ResistanceCleaving { get; init; }
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

    IReadOnlyList<PluginEquipmentItem> CaptureOwnedEquipment() =>
        Array.Empty<PluginEquipmentItem>();

    PluginEquipmentCommandResult Equip(
        uint objectId,
        uint requestedLocation = 0u) =>
        new(PluginEquipmentCommandStatus.Unavailable);
}
