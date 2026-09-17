using AcDream.Core.Plugins;
using AcDream.Plugin.Abstractions;

namespace AcDream.Runtime.Navigation;

// Who is driving. One walk runs at a time, and it belongs to whoever asked for it: a
// plugin, by id, or the player through a chat command. Another plugin asking while that
// walk is under way is refused rather than quietly taking the character; the player's own
// commands always win. A plugin that goes away takes its walk and its pauses with it.
internal sealed partial class RuntimeNavigationAutomation : IScopedNavigationSource
{
    /// <summary>The owner of walks and pauses asked for through chat, which outrank every plugin's.</summary>
    internal const string PlayerOwner = "player";

    private string? _walkOwner;

    /// <summary>Who owns the walk under way, or null when no walk is.</summary>
    internal string? WalkOwner
    {
        get
        {
            NavigationWalkController? walk;
            string? owner;
            lock (_gate)
            {
                walk = _walk;
                owner = _walkOwner;
            }
            return walk is { IsBusy: true } ? owner : null;
        }
    }

    public INavigationAutomation ScopeTo(string ownerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        return new OwnedNavigation(this, ownerId);
    }

    public void Release(string ownerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        NavigationWalkController? walk;
        bool ownsWalk;
        lock (_gate)
        {
            _pauses.RemoveAll(pause => pause.Owner == ownerId);
            walk = _walk;
            ownsWalk = _walkOwner == ownerId;
            if (ownsWalk)
                _walkOwner = null;
        }
        if (ownsWalk && walk is { IsBusy: true })
            walk.Stop();
    }

    // A walk request from an owner: refused while another owner's walk is under way, unless
    // the request is the player's.
    private PluginNavigationCommandStatus BeginWalk(
        string owner,
        Func<PluginNavigationCommandStatus> validate,
        Action<NavigationWalkController> start)
    {
        if (!TryWalk(out NavigationWalkController walk))
            return PluginNavigationCommandStatus.Unavailable;
        PluginNavigationCommandStatus validity = validate();
        if (validity != PluginNavigationCommandStatus.Accepted)
            return validity;
        lock (_gate)
        {
            if (owner != PlayerOwner
                && _walkOwner is { } current
                && current != owner
                && walk.IsBusy)
            {
                return PluginNavigationCommandStatus.Held;
            }
            _walkOwner = owner;
        }
        start(walk);
        return PluginNavigationCommandStatus.Accepted;
    }

    private static PluginNavigationCommandStatus ValidateArrival(uint objectId, float arrivalMeters) =>
        objectId == 0u || !(arrivalMeters > 0f) || arrivalMeters > MaximumGoToArrivalMeters
            ? PluginNavigationCommandStatus.Rejected
            : PluginNavigationCommandStatus.Accepted;

    private PluginNavigationCommandStatus GoToFor(string owner, uint objectId, float arrivalMeters) =>
        BeginWalk(
            owner,
            () => ValidateArrival(objectId, arrivalMeters),
            walk => walk.WalkTo(objectId, arrivalMeters));

    private PluginNavigationCommandStatus GoToFor(string owner, PluginNavigationPosition position, float arrivalMeters) =>
        BeginWalk(
            owner,
            () => !double.IsFinite(position.EastWest)
                || !double.IsFinite(position.NorthSouth)
                || double.IsInfinity(position.Elevation)
                || !(arrivalMeters > 0f)
                || arrivalMeters > MaximumGoToArrivalMeters
                ? PluginNavigationCommandStatus.Rejected
                : PluginNavigationCommandStatus.Accepted,
            walk => walk.WalkToPlace(position.CellId, RuntimeNavigationProjection.LandblockLocal(position), arrivalMeters));

    private PluginNavigationCommandStatus StandOnFor(string owner, uint objectId, float arrivalMeters) =>
        BeginWalk(
            owner,
            () => ValidateArrival(objectId, arrivalMeters),
            walk => walk.StandOn(objectId, arrivalMeters));

    private PluginNavigationCommandStatus FollowFor(string owner, uint playerId, float bufferMeters) =>
        BeginWalk(
            owner,
            () => ValidateArrival(playerId, bufferMeters),
            walk => walk.Follow(playerId, bufferMeters));

    // A plugin stops only the walk it started; the player stops any.
    private PluginNavigationCommandStatus StopGoToFor(string owner)
    {
        if (!TryWalk(out NavigationWalkController walk))
            return PluginNavigationCommandStatus.Unavailable;
        if (!walk.IsBusy)
            return PluginNavigationCommandStatus.Rejected;
        lock (_gate)
        {
            if (owner != PlayerOwner && _walkOwner is { } current && current != owner)
                return PluginNavigationCommandStatus.Held;
            _walkOwner = null;
        }
        walk.Stop();
        return PluginNavigationCommandStatus.Accepted;
    }

    private PluginGoToReport GoToReportFor()
    {
        if (!TryWalk(out NavigationWalkController walk))
            return default;
        PluginGoToReport report = RuntimeNavigationProjection.GoToReport(walk.Report);
        return report with { Owner = WalkOwner };
    }

    private IDisposable PauseGoToWhileFor(string owner, Func<string?> need)
    {
        ArgumentNullException.ThrowIfNull(need);
        var pause = new GoToPause(this, owner, need);
        lock (_gate)
            _pauses.Add(pause);
        return pause;
    }

    /// <summary>One plugin's view: reads and moves pass straight through; walks and pauses carry its id.</summary>
    private sealed class OwnedNavigation(RuntimeNavigationAutomation inner, string owner) : INavigationAutomation
    {
        public PluginNavigationSnapshot Snapshot => inner.Snapshot;

        public bool TryGetObject(uint objectId, out PluginNavigationObject value) =>
            inner.TryGetObject(objectId, out value);

        public bool TryFindObject(
            string name,
            in PluginNavigationPosition near,
            double maximumDistanceMeters,
            out PluginNavigationObject value) =>
            inner.TryFindObject(name, in near, maximumDistanceMeters, out value);

        public IReadOnlyList<PluginNavigationObject> CaptureObjects() => inner.CaptureObjects();

        public PluginNavigationCommandStatus SetMovementIntent(in PluginMovementIntent intent) =>
            inner.SetMovementIntent(in intent);

        public PluginNavigationCommandStatus ClearMovementIntent() => inner.ClearMovementIntent();

        public PluginNavigationCommandStatus FaceHeading(float headingDegrees) => inner.FaceHeading(headingDegrees);

        public PluginNavigationCommandStatus Move(
            PluginMoveDirection direction,
            PluginMovePace pace,
            float amount,
            PluginMoveUnit unit = PluginMoveUnit.MetersOrDegrees) =>
            inner.Move(direction, pace, amount, unit);

        public PluginNavigationCommandStatus StopMoving() => inner.StopMoving();

        public PluginNavigationCommandStatus StopMoving(PluginMoveChannel channel) => inner.StopMoving(channel);

        public PluginNavigationCommandStatus Jump(float power) => inner.Jump(power);

        public PluginMoveReport MoveReport => inner.MoveReport;

        public PluginNavigationCommandStatus GoTo(uint objectId, float arrivalMeters) =>
            inner.GoToFor(owner, objectId, arrivalMeters);

        public PluginNavigationCommandStatus GoTo(PluginNavigationPosition position, float arrivalMeters) =>
            inner.GoToFor(owner, position, arrivalMeters);

        public PluginNavigationCommandStatus StandOn(uint objectId, float arrivalMeters) =>
            inner.StandOnFor(owner, objectId, arrivalMeters);

        public PluginNavigationCommandStatus Follow(uint playerId, float bufferMeters) =>
            inner.FollowFor(owner, playerId, bufferMeters);

        public PluginNavigationCommandStatus StopGoTo() => inner.StopGoToFor(owner);

        public PluginGoToReport GoToReport => inner.GoToReportFor();

        public IDisposable PauseGoToWhile(Func<string?> need) => inner.PauseGoToWhileFor(owner, need);
    }
}
