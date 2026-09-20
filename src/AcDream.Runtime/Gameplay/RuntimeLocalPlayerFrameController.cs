using AcDream.Core.Physics;

namespace AcDream.Runtime.Gameplay;

public interface IRuntimeMovementInputSource
{
    MovementInput Capture();
}

public interface IRuntimeLocalPlayerFrameHost
{
    bool CanAdvancePlayer { get; }
    PlayerMovementController? Controller { get; }
    uint ResolveLocalEntityId();
    void HandleTargeting();
    bool IsHidden { get; }
    RetailObjectClockDisposition ObjectClockDisposition { get; }
    void Project(
        PlayerMovementController controller,
        MovementResult movement,
        bool hidden);
    void SendPreNetwork(
        PlayerMovementController controller,
        MovementResult movement,
        bool hidden);
    void SendPostNetwork(
        PlayerMovementController controller,
        bool hidden);
}

public readonly record struct RuntimeLocalPlayerPresentationFrame(
    MovementResult Movement,
    bool Hidden,
    bool AdvancedBeforeNetwork);

public sealed class RuntimeLocalPlayerFrameController
{
    private readonly IRuntimeLocalPlayerFrameHost _host;
    private readonly IRuntimeMovementInputSource _input;
    private readonly Action? _publishMovement;
    private readonly IRuntimeArmedApproachDrive? _armedApproaches;
    private RuntimeLocalPlayerMotionArming? _motionArming;
    private AdvancedFrame? _advancedFrame;

    private readonly record struct AdvancedFrame(
        PlayerMovementController Controller,
        MovementResult Movement,
        bool ObjectAdvanced,
        bool Hidden,
        bool ObjectQuantumAdvanced);

    /// <param name="host">Whoever gives this frame its body and its world.</param>
    /// <param name="input">Where the character's own movement comes from.</param>
    /// <param name="publishMovement">Told once the frame has closed.</param>
    /// <param name="armedApproaches">
    /// The end of a walk begun in order to act on something, taken once each
    /// frame right after the body has advanced. Absent leaves the character
    /// walking with nothing to finish what the walk was for.
    /// </param>
    public RuntimeLocalPlayerFrameController(
        IRuntimeLocalPlayerFrameHost host,
        IRuntimeMovementInputSource input,
        Action? publishMovement = null,
        IRuntimeArmedApproachDrive? armedApproaches = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _publishMovement = publishMovement;
        _armedApproaches = armedApproaches;
    }

    /// <summary>
    /// Gives the character's own locomotion somewhere to be set up from. It
    /// is done from here because it must be retried: the cycles the character
    /// moves itself by are built from content that need not be to hand the
    /// moment the body is.
    /// </summary>
    internal void BindMotionArming(RuntimeLocalPlayerMotionArming arming) =>
        _motionArming = arming
            ?? throw new ArgumentNullException(nameof(arming));

    public bool HiddenPartPoseDirty => _advancedFrame is
    {
        Hidden: true,
        ObjectQuantumAdvanced: true,
    };

    public void AdvanceBeforeNetwork(float deltaSeconds)
    {
        AdvancePlayerBeforeNetwork(deltaSeconds);
        // A walk begun in order to act on something ends here, right after
        // the body has had its step: an arrival sends what was armed for it,
        // and a walk that has stopped getting anywhere is given up on. It
        // runs whether or not the body advanced this frame, because a walk
        // that ended while the body was standing still still has to be
        // answered.
        _armedApproaches?.DriveArmedApproaches();
    }

    private void AdvancePlayerBeforeNetwork(float deltaSeconds)
    {
        _advancedFrame = null;
        PlayerMovementController? controller = _host.Controller;
        if (!_host.CanAdvancePlayer
            || controller is null
            || !controller.CanExecuteLiveMovement)
        {
            return;
        }

        if (!float.IsFinite(deltaSeconds) || deltaSeconds <= 0f)
        {
            bool rejectedHidden = _host.IsHidden;
            MovementResult rejected =
                controller.CapturePresentationResult();
            _host.Project(controller, rejected, rejectedHidden);
            _advancedFrame = new AdvancedFrame(
                controller,
                rejected,
                ObjectAdvanced: false,
                Hidden: rejectedHidden,
                ObjectQuantumAdvanced: false);
            return;
        }

        controller.LocalEntityId = _host.ResolveLocalEntityId();
        // Before the character is asked to take a step, make sure it is the
        // character's own cycles it takes that step by.
        _motionArming?.EnsureArmed(controller);

        bool hidden = _host.IsHidden;
        if (_host.ObjectClockDisposition
            is RetailObjectClockDisposition.Suspend)
        {
            controller.SuspendObjectUpdate(deltaSeconds);
            MovementResult paused =
                controller.CapturePresentationResult();
            _host.Project(controller, paused, hidden);
            _advancedFrame = new AdvancedFrame(
                controller,
                paused,
                ObjectAdvanced: false,
                Hidden: hidden,
                ObjectQuantumAdvanced: false);
            return;
        }

        MovementResult movement = hidden
            ? controller.TickHidden(
                deltaSeconds,
                _host.HandleTargeting)
            : controller.Update(
                deltaSeconds,
                _input.Capture(),
                _host.HandleTargeting);

        _host.Project(controller, movement, hidden);
        _host.SendPreNetwork(controller, movement, hidden);
        _advancedFrame = new AdvancedFrame(
            controller,
            movement,
            ObjectAdvanced: true,
            Hidden: hidden,
            ObjectQuantumAdvanced:
                controller.AdvancedObjectQuantumLastTick);
    }

    public void RunPostNetworkCommandPhase()
    {
        PlayerMovementController? controller = _host.Controller;
        if (!_host.CanAdvancePlayer
            || controller is null
            || !controller.CanExecuteLiveMovement)
        {
            return;
        }

        bool hidden = _host.IsHidden;
        if (_advancedFrame is { } advanced
            && ReferenceEquals(advanced.Controller, controller))
        {
            MovementResult movement = RefreshSpatialFields(
                advanced.Movement,
                controller);
            _host.Project(controller, movement, hidden);
            _advancedFrame = new AdvancedFrame(
                controller,
                movement,
                advanced.ObjectAdvanced,
                advanced.Hidden,
                advanced.ObjectQuantumAdvanced);

            if (!advanced.ObjectAdvanced)
                return;
        }

        _host.SendPostNetwork(controller, hidden);
        _publishMovement?.Invoke();
    }

    public bool TryGetPresentationAfterNetwork(
        out RuntimeLocalPlayerPresentationFrame frame)
    {
        frame = default;
        PlayerMovementController? controller = _host.Controller;
        if (!_host.CanAdvancePlayer
            || controller is null
            || !controller.CanExecuteLiveMovement)
        {
            return false;
        }

        bool hidden = _host.IsHidden;
        if (_advancedFrame is { } advanced
            && ReferenceEquals(advanced.Controller, controller))
        {
            MovementResult movement = RefreshSpatialFields(
                advanced.Movement,
                controller);
            frame = new RuntimeLocalPlayerPresentationFrame(
                movement,
                hidden,
                AdvancedBeforeNetwork: advanced.ObjectAdvanced);
            return true;
        }

        MovementResult initial =
            controller.CapturePresentationResult();
        _host.Project(controller, initial, hidden);
        frame = new RuntimeLocalPlayerPresentationFrame(
            initial,
            hidden,
            AdvancedBeforeNetwork: false);
        return true;
    }

    private static MovementResult RefreshSpatialFields(
        MovementResult movement,
        PlayerMovementController controller) =>
        movement with
        {
            Position = controller.Position,
            RenderPosition = controller.RenderPosition,
            CellId = controller.CellId,
            IsOnGround = controller.CanSendPositionEvent,
        };
}
