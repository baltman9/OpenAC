namespace AcDream.Plugin.Abstractions;

/// <summary>Normalized allegiance state known by the current session.</summary>
public readonly record struct PluginAllegianceSnapshot(
    long Revision,
    bool IsKnown,
    string Name,
    uint Rank,
    uint MemberCount,
    uint VassalCount,
    uint MonarchObjectId)
{
    /// <summary>True when a monarch identity is available.</summary>
    public bool HasMonarch => MonarchObjectId != 0u;
}

/// <summary>How the client answered a plugin's allegiance command.</summary>
public enum PluginAllegianceCommandStatus
{
    /// <summary>
    /// The command could not be sent: the character is not in the world,
    /// there is no live session, or this host does not provide the surface.
    /// </summary>
    Unavailable = 0,

    /// <summary>
    /// The command went out to the server. Whether the server allows it is
    /// the server's own decision, and arrives later as a restated allegiance.
    /// </summary>
    Sent,

    /// <summary>
    /// The client refused the command before sending it, because the object
    /// named cannot be the target of one: a zero, a patron who is not a
    /// player the client can see, or someone who is not in the character's
    /// own allegiance.
    /// </summary>
    InvalidTarget,

    /// <summary>
    /// The client had a target it could act on and still did not send the
    /// command: something about the session itself stood in the way. The
    /// reason, where there is one to give, is in the result's notice. This
    /// is not a statement about the target, and a plugin that is choosing
    /// another target on a refusal should not treat it as one.
    /// </summary>
    Refused,
}

/// <summary>The outcome of one allegiance command.</summary>
/// <param name="Status">What the client did with the command.</param>
/// <param name="Notice">Why it was refused, when there is something to say.</param>
public readonly record struct PluginAllegianceCommandResult(
    PluginAllegianceCommandStatus Status,
    string? Notice = null)
{
    /// <summary>True when the command actually went out to the server.</summary>
    public bool Accepted => Status == PluginAllegianceCommandStatus.Sent;
}

/// <summary>
/// Reads authoritative allegiance state, and sends the two allegiance
/// commands the client's own social panel sends. Everything read here is the
/// state the server last told the client; a command only asks, it does not
/// decide.
/// </summary>
public interface IAllegianceAutomation
{
    /// <summary>True when the host is tracking allegiance state.</summary>
    bool IsAvailable => false;

    /// <summary>The latest normalized allegiance snapshot.</summary>
    PluginAllegianceSnapshot Snapshot => default;

    /// <summary>
    /// Asks the server to pledge the character to another player as its
    /// patron. The patron has to be a player the client can currently see,
    /// which is what the client's own panel requires as well: swearing is
    /// done face to face.
    /// </summary>
    /// <param name="patronObjectId">The player to swear to.</param>
    /// <returns>
    /// <see cref="PluginAllegianceCommandStatus.Sent"/> once it is on its way,
    /// <see cref="PluginAllegianceCommandStatus.InvalidTarget"/> when that is
    /// not a player standing there,
    /// <see cref="PluginAllegianceCommandStatus.Refused"/> when the client
    /// would not send it for a reason that is not about the target, and
    /// <see cref="PluginAllegianceCommandStatus.Unavailable"/> off-world.
    /// </returns>
    PluginAllegianceCommandResult Swear(uint patronObjectId) =>
        new(PluginAllegianceCommandStatus.Unavailable);

    /// <summary>
    /// Asks the server to break the tie between the character and someone in
    /// its allegiance -- its patron, or one of its vassals. The target has to
    /// be someone the client has been told is in that allegiance; it does not
    /// have to be nearby, or even logged in.
    /// </summary>
    /// <param name="targetObjectId">The patron or vassal to break from.</param>
    /// <returns>
    /// <see cref="PluginAllegianceCommandStatus.Sent"/> once it is on its way,
    /// <see cref="PluginAllegianceCommandStatus.InvalidTarget"/> when that
    /// character is not in the allegiance,
    /// <see cref="PluginAllegianceCommandStatus.Refused"/> when the client
    /// would not send it for a reason that is not about the target, and
    /// <see cref="PluginAllegianceCommandStatus.Unavailable"/> off-world.
    /// </returns>
    PluginAllegianceCommandResult Break(uint targetObjectId) =>
        new(PluginAllegianceCommandStatus.Unavailable);
}
