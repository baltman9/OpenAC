namespace AcDream.Plugin.Abstractions;

public readonly record struct PluginLootContainer(
    uint ObjectId,
    uint WeenieClassId,
    string Name,
    float Distance,
    bool HasBeenOpened,
    bool IsRequested,
    bool IsCurrent)
{
    public string LongDescription { get; init; } = string.Empty;
    public bool IsGeneratedRare { get; init; }
    public bool IsIdentified { get; init; }

    /// <summary>
    /// Whether <see cref="Position"/> carries a real place in the world. A
    /// corpse the client knows of but cannot place has none.
    /// </summary>
    public bool HasPosition { get; init; }

    /// <summary>
    /// Where the corpse is, which is what lets a looter ask how far round the
    /// character would have to turn to face it.
    /// </summary>
    public PluginNavigationPosition Position { get; init; }
}

public readonly record struct PluginAppraisalState(
    long Revision,
    uint AwaitingObjectId,
    uint CurrentObjectId);

public interface ILootAutomation
{
    bool IsAvailable => false;
    bool IsBusy => false;
    uint RequestedContainerId => 0u;
    uint CurrentContainerId => 0u;
    PluginItemUseCompletion LastItemUseCompletion => default;
    PluginInventoryCompletion LastInventoryCompletion => default;
    PluginAppraisalState Appraisal => default;

    IReadOnlyList<PluginLootContainer> CaptureCorpses(float maximumDistance) =>
        Array.Empty<PluginLootContainer>();

    IReadOnlyList<PluginInventoryItem> CaptureCurrentContents() =>
        Array.Empty<PluginInventoryItem>();

    bool TryCaptureProperties(
        uint objectId,
        out PluginItemProperties properties)
    {
        properties = default;
        return false;
    }

    PluginItemCommandResult Open(uint containerObjectId) =>
        new(PluginItemCommandStatus.Unavailable);

    PluginItemCommandResult Identify(uint objectId) =>
        new(PluginItemCommandStatus.Unavailable);

    PluginItemCommandResult Pickup(
        uint objectId,
        bool mainPack = false) =>
        new(PluginItemCommandStatus.Unavailable);
}
