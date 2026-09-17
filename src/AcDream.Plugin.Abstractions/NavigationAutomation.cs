namespace AcDream.Plugin.Abstractions;

/// <summary>
/// Where something stands in the world: the cell it occupies plus its position
/// on the global map grid.
/// </summary>
/// <param name="CellId">
/// The packed identifier of the landblock cell the object is in. The high half
/// is the landblock and the low half the cell inside it.
/// </param>
/// <param name="EastWest">
/// East-west map coordinate, growing eastwards. One unit is 240 meters, the
/// same units the in-game location readout uses.
/// </param>
/// <param name="NorthSouth">
/// North-south map coordinate, growing northwards, in the same 240-meter units
/// as <paramref name="EastWest"/>.
/// </param>
/// <param name="Elevation">
/// Height above the cell floor, in the same 240-meter units as the horizontal
/// coordinates; multiply by 240 for meters.
/// </param>
/// <param name="HeadingDegrees">
/// The direction the object faces, in degrees clockwise from north (0 to 360).
/// </param>
/// <param name="IsOutdoor">
/// True when the cell is an outdoor landscape cell; false inside a dungeon or
/// building interior.
/// </param>
public readonly record struct PluginNavigationPosition(
    uint CellId,
    double EastWest,
    double NorthSouth,
    double Elevation,
    float HeadingDegrees,
    bool IsOutdoor)
{
    /// <summary>
    /// The flat ground distance in meters between this position and
    /// <paramref name="other"/>, ignoring any difference in elevation.
    /// </summary>
    /// <param name="other">The position to measure to.</param>
    /// <returns>The distance in meters.</returns>
    public double HorizontalDistanceMeters(in PluginNavigationPosition other)
    {
        double dx = EastWest - other.EastWest;
        double dy = NorthSouth - other.NorthSouth;
        return Math.Sqrt(dx * dx + dy * dy) * 240d;
    }
}

/// <summary>
/// One world object seen from the navigation surface: who it is, where it is,
/// and, for doors and other lockable things, whether it is open or locked.
/// </summary>
/// <param name="ObjectId">The server-assigned id of the object.</param>
/// <param name="Name">
/// The object's display name, or an empty string when the client has not
/// received one yet.
/// </param>
/// <param name="Position">Where the object currently stands.</param>
public readonly record struct PluginNavigationObject(
    uint ObjectId,
    string Name,
    PluginNavigationPosition Position)
{
    /// <summary>True when the object is a door.</summary>
    public bool IsDoor { get; init; }

    /// <summary>
    /// True when the object reports itself as open. False both for a closed
    /// object and for one that never reports an open state.
    /// </summary>
    public bool IsOpen { get; init; }

    /// <summary>
    /// True when the object reports itself as locked. False both for an
    /// unlocked object and for one that never reports a lock state.
    /// </summary>
    public bool IsLocked { get; init; }

    /// <summary>
    /// True when the client actually knows this object's open or locked state,
    /// so a plugin can tell "closed" apart from "never reported".
    /// </summary>
    public bool HasLockState { get; init; }

    /// <summary>
    /// How hard the lock is to pick, taken from the object's lockpick
    /// resistance; zero when the object carries no such value.
    /// </summary>
    public int LockDifficulty { get; init; }
}

/// <summary>The local movement state sampled atomically by a plugin tick.</summary>
/// <param name="IsAvailable">
/// True when a session is in the world and the remaining fields carry real
/// values. When it is false every other field is at its default.
/// </param>
/// <param name="IsPortalSpace">
/// True while the player is in transit through portal space, after leaving one
/// place and before arriving at the next.
/// </param>
/// <param name="LocalObjectId">The player character's own object id.</param>
/// <param name="Position">
/// The player's live locally predicted position, updated every frame.
/// </param>
/// <param name="IsMoving">
/// True when the player is actually moving or is holding a movement input.
/// </param>
/// <param name="IsAirborne">True when the player has no ground under foot.</param>
public readonly record struct PluginNavigationSnapshot(
    bool IsAvailable,
    bool IsPortalSpace,
    uint LocalObjectId,
    PluginNavigationPosition Position,
    bool IsMoving,
    bool IsAirborne)
{
    /// <summary>
    /// The last position the server confirmed, as opposed to the locally
    /// predicted <see cref="Position"/>. Equal to <see cref="Position"/> when
    /// no server position has been accepted yet.
    /// </summary>
    public PluginNavigationPosition ConfirmedPosition { get; init; }

    /// <summary>
    /// A counter that grows every time the server confirms a new position, so a
    /// plugin can tell a fresh confirmation from a repeat. Zero when nothing
    /// has been confirmed yet.
    /// </summary>
    public ulong ConfirmedPositionRevision { get; init; }
}

/// <summary>
/// The movement keys a plugin wants held down. The host applies this as if the
/// player were holding those keys, until it is replaced or cleared.
/// </summary>
/// <param name="Forward">Hold the forward key.</param>
/// <param name="Backward">Hold the backward key.</param>
/// <param name="StrafeLeft">Sidestep to the left without turning.</param>
/// <param name="StrafeRight">Sidestep to the right without turning.</param>
/// <param name="TurnLeft">Turn on the spot to the left.</param>
/// <param name="TurnRight">Turn on the spot to the right.</param>
/// <param name="Run">Run rather than walk. Defaults to true.</param>
/// <param name="Jump">Jump.</param>
public readonly record struct PluginMovementIntent(
    bool Forward = false,
    bool Backward = false,
    bool StrafeLeft = false,
    bool StrafeRight = false,
    bool TurnLeft = false,
    bool TurnRight = false,
    bool Run = true,
    bool Jump = false);

/// <summary>What became of a movement command a plugin sent.</summary>
public enum PluginNavigationCommandStatus
{
    /// <summary>
    /// The host cannot take movement commands at all right now: no session is
    /// in the world, or this host does not drive movement.
    /// </summary>
    Unavailable = 0,

    /// <summary>The command was taken and applied.</summary>
    Accepted,

    /// <summary>
    /// The host could take commands but refused this one, for instance because
    /// it arrived for a session that has already ended.
    /// </summary>
    Rejected,
}

/// <summary>
/// Reading where the player and nearby objects are, and driving the player's
/// own movement.
/// </summary>
public interface INavigationAutomation
{
    /// <summary>
    /// The player's current movement state. Its <c>IsAvailable</c> is false
    /// when no session is in the world.
    /// </summary>
    PluginNavigationSnapshot Snapshot { get; }

    /// <summary>Look up one world object by its id.</summary>
    /// <param name="objectId">The object id to look up.</param>
    /// <param name="value">The object, when one was found.</param>
    /// <returns>
    /// False when no session is in the world, the id is zero, the object is
    /// unknown to the client, or its position cannot be resolved.
    /// </returns>
    bool TryGetObject(uint objectId, out PluginNavigationObject value);

    /// <summary>
    /// Find the object closest to <paramref name="near"/> whose name matches
    /// exactly, ignoring letter case, within the given radius.
    /// </summary>
    /// <param name="name">The name to match.</param>
    /// <param name="near">The position distances are measured from.</param>
    /// <param name="maximumDistanceMeters">
    /// How far to search, in meters, measured along the ground.
    /// </param>
    /// <param name="value">The nearest matching object, when one was found.</param>
    /// <returns>
    /// False when nothing matched inside the radius, when the name is blank or
    /// the distance is negative or not a number, and on a host that does not
    /// implement the search: the default implementation always returns false.
    /// </returns>
    bool TryFindObject(
        string name,
        in PluginNavigationPosition near,
        double maximumDistanceMeters,
        out PluginNavigationObject value)
    {
        value = default;
        return false;
    }

    /// <summary>
    /// A detached list of every world object whose position the client can
    /// resolve, ordered by object id, for plugin-owned proximity policies such
    /// as an automatic door opener. Hosts may return an empty list.
    /// </summary>
    /// <returns>
    /// The objects, or an empty list when no session is in the world or the
    /// host does not offer the projection.
    /// </returns>
    IReadOnlyList<PluginNavigationObject> CaptureObjects() =>
        Array.Empty<PluginNavigationObject>();

    /// <summary>
    /// Hold the given movement keys until the intent is replaced or cleared.
    /// </summary>
    /// <param name="intent">The keys to hold.</param>
    /// <returns>
    /// <see cref="PluginNavigationCommandStatus.Unavailable"/> when no session
    /// is in the world, otherwise whether the session took the command.
    /// </returns>
    PluginNavigationCommandStatus SetMovementIntent(
        in PluginMovementIntent intent);

    /// <summary>Release every movement key the plugin was holding.</summary>
    /// <returns>
    /// <see cref="PluginNavigationCommandStatus.Unavailable"/> when no session
    /// is in the world, otherwise whether the session took the command.
    /// </returns>
    PluginNavigationCommandStatus ClearMovementIntent();

    /// <summary>Turn the player on the spot to face a compass direction.</summary>
    /// <param name="headingDegrees">
    /// The direction to face, in degrees clockwise from north.
    /// </param>
    /// <returns>
    /// <see cref="PluginNavigationCommandStatus.Unavailable"/> when no session
    /// is in the world or the host does not implement turning, which is what
    /// the default implementation always returns; otherwise whether the session
    /// took the command.
    /// </returns>
    PluginNavigationCommandStatus FaceHeading(float headingDegrees) =>
        PluginNavigationCommandStatus.Unavailable;
}
