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
}

/// <summary>
/// The state of the client's single shared appraisal slot, shared by the
/// user's own assess action and every plugin's Identify request.
/// </summary>
/// <param name="Revision">
/// Bumps on every state change to this slot; cheap to poll for "did
/// anything happen" without comparing the other fields.
/// </param>
/// <param name="AwaitingObjectId">
/// The object id currently awaiting a response, or 0 if none is in
/// flight. Only one request -- of either origin -- can be in flight at a
/// time; a plugin's own Identify can still be displaced by a later one
/// (its response is then dropped), but never by the reverse: a plugin
/// Identify while the user's own assess is awaiting is refused outright.
/// </param>
/// <param name="CurrentObjectId">
/// The completion signal: the object id of the most recently completed
/// appraisal response, of either origin. This is NOT the client's
/// examination window's displayed object -- a plugin's background
/// Identify never opens or retargets that window, so this can (and
/// routinely does) advance to an object the window is not showing. Poll
/// this against the id you passed to Identify to learn when your own
/// request completed.
/// </param>
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

    /// <summary>
    /// Identifies an item scoped to loot handling: only the currently open
    /// corpse/container's contents, or the corpse itself, is a valid
    /// target. Use IWorldObjectAutomation.Identify to assess any object
    /// the client can currently see (owned, equipped, landscape, a vendor
    /// listing, or open-container content) -- this member exists
    /// separately because a loot-sorting plugin should not accidentally
    /// identify something outside the container it is currently working.
    /// </summary>
    PluginItemCommandResult Identify(uint objectId) =>
        new(PluginItemCommandStatus.Unavailable);

    PluginItemCommandResult Pickup(
        uint objectId,
        bool mainPack = false) =>
        new(PluginItemCommandStatus.Unavailable);
}
