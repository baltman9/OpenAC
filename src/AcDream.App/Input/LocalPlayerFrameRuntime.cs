using AcDream.App.Net;
using AcDream.App.Physics;
using AcDream.App.Rendering;
using AcDream.App.World;
using AcDream.Core.Physics;

namespace AcDream.App.Input;

internal interface ILocalPlayerPresentationRuntime
{
    bool CanPresentPlayer { get; }
    PlayerMovementController? Controller { get; }
}

internal interface ILocalPlayerFrameRuntime :
    ILocalPlayerPresentationRuntime,
    IRuntimeLocalPlayerFrameHost
{
    bool IRuntimeLocalPlayerFrameHost.CanAdvancePlayer =>
        CanPresentPlayer;
}

/// <summary>
/// What a frame needs to know about the local player's body as the world
/// holds it: which drawable it is, whether it is out of sight, and whether
/// its clock should run. Named on its own so the frame can be built over a
/// world that is not being drawn.
/// </summary>
internal interface ILocalPlayerWorldFacts
{
    uint ResolveLocalEntityId(uint serverGuid);
    bool IsHidden(uint serverGuid);
    RetailObjectClockDisposition GetRootObjectClockDisposition(uint serverGuid);
}

/// <summary>The drawn world's answers, which is where they come from in a real run.</summary>
internal sealed class LiveEntityWorldFacts(LiveEntityRuntime liveEntities)
    : ILocalPlayerWorldFacts
{
    private readonly LiveEntityRuntime _liveEntities =
        liveEntities ?? throw new ArgumentNullException(nameof(liveEntities));

    public uint ResolveLocalEntityId(uint serverGuid) =>
        _liveEntities.TryGetWorldEntity(serverGuid, out var entity)
            ? entity.Id
            : 0u;

    public bool IsHidden(uint serverGuid) => _liveEntities.IsHidden(serverGuid);

    public RetailObjectClockDisposition GetRootObjectClockDisposition(
        uint serverGuid) =>
        _liveEntities.GetRootObjectClockDisposition(serverGuid);
}

internal sealed class LiveLocalPlayerFrameRuntime : ILocalPlayerFrameRuntime
{
    private readonly CameraController _camera;
    private readonly ILocalPlayerModeSource _mode;
    private readonly IRuntimeLocalPlayerControllerSource _controller;
    private readonly IChaseCameraSource _chase;
    private readonly DispatcherMovementInputSource _input;
    private readonly ILocalPlayerWorldFacts _worldFacts;
    private readonly ILocalPlayerIdentitySource _identity;
    private readonly ILocalPlayerPhysicsHostSource _physicsHost;
    private readonly LocalPlayerProjectionController _projection;
    private readonly LocalPlayerOutboundController _outbound;
    private readonly ILiveWorldSessionSource _session;

    public LiveLocalPlayerFrameRuntime(
        CameraController camera,
        ILocalPlayerModeSource mode,
        IRuntimeLocalPlayerControllerSource controller,
        IChaseCameraSource chase,
        DispatcherMovementInputSource input,
        ILocalPlayerWorldFacts worldFacts,
        ILocalPlayerIdentitySource identity,
        ILocalPlayerPhysicsHostSource physicsHost,
        LocalPlayerProjectionController projection,
        LocalPlayerOutboundController outbound,
        ILiveWorldSessionSource session)
    {
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));
        _mode = mode ?? throw new ArgumentNullException(nameof(mode));
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _chase = chase ?? throw new ArgumentNullException(nameof(chase));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _worldFacts = worldFacts ?? throw new ArgumentNullException(nameof(worldFacts));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _physicsHost = physicsHost ?? throw new ArgumentNullException(nameof(physicsHost));
        _projection = projection ?? throw new ArgumentNullException(nameof(projection));
        _outbound = outbound ?? throw new ArgumentNullException(nameof(outbound));
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    // The overlay holding the keyboard (a plugin window with focus) only
    // takes the movement keys away - DispatcherMovementInputSource yields
    // nothing while it does - it must not stop the player advancing, or the
    // character stands frozen on screen while a bot's commands carry on.
    public bool CanPresentPlayer =>
        !_camera.IsFlyMode
        && _mode.IsPlayerMode
        && _controller.Controller is not null
        && _chase.Legacy is not null
        && _input.IsAvailable;

    public PlayerMovementController? Controller => _controller.Controller;

    public uint ResolveLocalEntityId() =>
        _worldFacts.ResolveLocalEntityId(_identity.ServerGuid);

    public void HandleTargeting() => _physicsHost.Host?.HandleTargetting();

    public bool IsHidden => _worldFacts.IsHidden(_identity.ServerGuid);

    public RetailObjectClockDisposition ObjectClockDisposition =>
        _worldFacts.GetRootObjectClockDisposition(_identity.ServerGuid);

    public void Project(
        PlayerMovementController controller,
        MovementResult movement,
        bool hidden) =>
        _projection.Project(controller, movement, hidden);

    public void SendPreNetwork(
        PlayerMovementController controller,
        MovementResult movement,
        bool hidden) =>
        _outbound.SendPreNetworkActions(
            _session.CurrentSession,
            controller,
            movement,
            hidden);

    public void SendPostNetwork(
        PlayerMovementController controller,
        bool hidden) =>
        _outbound.SendPostNetworkPosition(
            _session.CurrentSession,
            controller,
            hidden);
}
