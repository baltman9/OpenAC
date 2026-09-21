// Copyright (c) OpenAC contributors.
// Distributed under the terms of the MIT license.

using AcDream.Plugin.Abstractions;

namespace AcDream.Plugin.Tests.Fixtures;

/// <summary>
/// A settable <see cref="INavigationAutomation"/> for tests. Snapshot,
/// report, intent, and object collections are all settable and
/// inspectable. The <see cref="SnapshotChanged"/> event can be fired on
/// demand via <see cref="RaiseSnapshotChanged"/>.
/// </summary>
public sealed class FakeNavigationAutomation : INavigationAutomation
{
    private readonly object _gate = new();
    private Action<PluginNavigationSnapshot>? _snapshotChanged;

    /// <summary>
    /// Gets or sets the snapshot returned by <see cref="Snapshot"/>.
    /// </summary>
    public PluginNavigationSnapshot SnapshotValue { get; set; }

    /// <summary>
    /// Gets or sets the report returned by <see cref="GoToReport"/>.
    /// </summary>
    public PluginGoToReport GoToReportValue { get; set; }

    /// <summary>
    /// The objects returned by <see cref="TryGetObject"/>.
    /// </summary>
    public Dictionary<uint, PluginNavigationObject> Objects { get; } = [];

    /// <summary>
    /// The objects returned by <see cref="CaptureObjects"/>.
    /// </summary>
    public List<PluginNavigationObject> WorldObjects { get; } = [];

    /// <summary>
    /// Every intent passed to <see cref="SetMovementIntent"/>, in order.
    /// </summary>
    public List<PluginMovementIntent> Intents { get; } = [];

    /// <summary>
    /// Every heading passed to <see cref="FaceHeading"/>, in order.
    /// </summary>
    public List<float> FacedHeadings { get; } = [];

    /// <summary>
    /// Count of calls to <see cref="ClearMovementIntent"/>.
    /// </summary>
    public int ClearCount { get; set; }

    /// <summary>
    /// Count of calls to <see cref="StopGoTo"/>.
    /// </summary>
    public int StopGoToCount { get; set; }

    /// <summary>
    /// Controls the return value of <see cref="GoTo(uint, float)"/>,
    /// <see cref="GoTo(PluginNavigationPosition, float)"/>,
    /// <see cref="StandOn"/>, and <see cref="Follow"/>.
    /// </summary>
    public PluginNavigationCommandStatus GoToAnswer { get; set; } =
        PluginNavigationCommandStatus.Accepted;

    /// <summary>
    /// Every <c>(objectId, arrivalMeters)</c> passed to
    /// <see cref="GoTo(uint, float)"/>, in order.
    /// </summary>
    public List<(uint ObjectId, float ArrivalMeters)> GoToCalls { get; } = [];

    /// <summary>
    /// Every <c>(position, arrivalMeters)</c> passed to
    /// <see cref="GoTo(PluginNavigationPosition, float)"/>, in order.
    /// </summary>
    public List<(PluginNavigationPosition Position, float ArrivalMeters)> GoToPositionCalls { get; } = [];

    /// <summary>
    /// Every <c>(objectId, arrivalMeters)</c> passed to
    /// <see cref="StandOn"/>, in order.
    /// </summary>
    public List<(uint ObjectId, float ArrivalMeters)> StandOnCalls { get; } = [];

    /// <summary>
    /// Every <c>(playerId, bufferMeters)</c> passed to
    /// <see cref="Follow"/>, in order.
    /// </summary>
    public List<(uint PlayerId, float BufferMeters)> FollowCalls { get; } = [];

    /// <summary>
    /// Every <c>Func<string?></c> registered via <see cref="PauseGoToWhile"/>.
    /// </summary>
    public List<Func<string?>> GoToPauses { get; } = [];

    /// <summary>
    /// Every <see cref="Move"/> call recorded as a tuple, in order.
    /// </summary>
    public List<(PluginMoveDirection Direction, PluginMovePace Pace, float Amount, PluginMoveUnit Unit)> MoveCalls { get; } = [];

    /// <summary>
    /// Every jump power passed to <see cref="Jump"/>.
    /// </summary>
    public List<float> JumpCalls { get; } = [];

    /// <summary>
    /// The <see cref="MoveReport"/> value returned.
    /// </summary>
    public PluginMoveReport MoveReportValue { get; set; }

    /// <summary>
    /// Controls the <see cref="TryFindObject"/> result and value.
    /// </summary>
    public PluginNavigationObject? FoundObject { get; set; }

    // ── INavigationAutomation ─────────────────────────────────────────────

    /// <inheritdoc/>
    public event Action<PluginNavigationSnapshot> SnapshotChanged
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_gate) _snapshotChanged += value;
        }
        remove
        {
            if (value is null) return;
            lock (_gate) _snapshotChanged -= value;
        }
    }

    /// <inheritdoc/>
    public PluginNavigationSnapshot Snapshot => SnapshotValue;

    /// <inheritdoc/>
    public PluginGoToReport GoToReport => GoToReportValue;

    /// <inheritdoc/>
    public PluginMoveReport MoveReport => MoveReportValue;

    /// <inheritdoc/>
    public bool TryGetObject(uint objectId, out PluginNavigationObject value) =>
        Objects.TryGetValue(objectId, out value);

    /// <inheritdoc/>
    public bool TryFindObject(
        string name,
        in PluginNavigationPosition near,
        double maximumDistanceMeters,
        out PluginNavigationObject value)
    {
        value = FoundObject ?? default;
        return FoundObject.HasValue;
    }

    /// <inheritdoc/>
    public IReadOnlyList<PluginNavigationObject> CaptureObjects() => WorldObjects;

    /// <inheritdoc/>
    public PluginNavigationCommandStatus SetMovementIntent(in PluginMovementIntent intent)
    {
        Intents.Add(intent);
        return PluginNavigationCommandStatus.Accepted;
    }

    /// <inheritdoc/>
    public PluginNavigationCommandStatus ClearMovementIntent()
    {
        ClearCount++;
        return PluginNavigationCommandStatus.Accepted;
    }

    /// <inheritdoc/>
    public PluginNavigationCommandStatus FaceHeading(float headingDegrees)
    {
        FacedHeadings.Add(headingDegrees);
        return PluginNavigationCommandStatus.Accepted;
    }

    /// <inheritdoc/>
    public PluginNavigationCommandStatus Move(
        PluginMoveDirection direction,
        PluginMovePace pace,
        float amount,
        PluginMoveUnit unit = PluginMoveUnit.MetersOrDegrees)
    {
        MoveCalls.Add((direction, pace, amount, unit));
        return PluginNavigationCommandStatus.Accepted;
    }

    /// <inheritdoc/>
    public PluginNavigationCommandStatus StopMoving() =>
        PluginNavigationCommandStatus.Accepted;

    /// <inheritdoc/>
    public PluginNavigationCommandStatus StopMoving(PluginMoveChannel channel) =>
        PluginNavigationCommandStatus.Accepted;

    /// <inheritdoc/>
    public PluginNavigationCommandStatus Jump(float power)
    {
        JumpCalls.Add(power);
        return PluginNavigationCommandStatus.Accepted;
    }

    /// <inheritdoc/>
    public PluginNavigationCommandStatus GoTo(uint objectId, float arrivalMeters)
    {
        GoToCalls.Add((objectId, arrivalMeters));
        if (GoToAnswer == PluginNavigationCommandStatus.Accepted)
            GoToReportValue = GoToReportValue with
            {
                Sequence = GoToReportValue.Sequence + 1,
                State = PluginGoToState.Planning,
            };
        return GoToAnswer;
    }

    /// <inheritdoc/>
    public PluginNavigationCommandStatus GoTo(
        PluginNavigationPosition position, float arrivalMeters)
    {
        GoToPositionCalls.Add((position, arrivalMeters));
        if (GoToAnswer == PluginNavigationCommandStatus.Accepted)
            GoToReportValue = GoToReportValue with
            {
                Sequence = GoToReportValue.Sequence + 1,
                State = PluginGoToState.Planning,
            };
        return GoToAnswer;
    }

    /// <inheritdoc/>
    public PluginNavigationCommandStatus StandOn(uint objectId, float arrivalMeters)
    {
        StandOnCalls.Add((objectId, arrivalMeters));
        if (GoToAnswer == PluginNavigationCommandStatus.Accepted)
            GoToReportValue = GoToReportValue with
            {
                Sequence = GoToReportValue.Sequence + 1,
                State = PluginGoToState.Planning,
            };
        return GoToAnswer;
    }

    /// <inheritdoc/>
    public PluginNavigationCommandStatus Follow(uint playerId, float bufferMeters)
    {
        FollowCalls.Add((playerId, bufferMeters));
        if (GoToAnswer == PluginNavigationCommandStatus.Accepted)
            GoToReportValue = GoToReportValue with
            {
                Sequence = GoToReportValue.Sequence + 1,
                State = PluginGoToState.Planning,
            };
        return GoToAnswer;
    }

    /// <inheritdoc/>
    public PluginNavigationCommandStatus StopGoTo()
    {
        StopGoToCount++;
        GoToReportValue = GoToReportValue with
        {
            State = PluginGoToState.Stopped,
            Reason = "stopped",
        };
        return PluginNavigationCommandStatus.Accepted;
    }

    /// <inheritdoc/>
    public IDisposable PauseGoToWhile(Func<string?> need)
    {
        GoToPauses.Add(need);
        return new GoToPauseHandle(() => GoToPauses.Remove(need));
    }

    // ── Raise methods for tests ────────────────────────────────────────────

    /// <summary>
    /// Fires <see cref="SnapshotChanged"/> for every listener.
    /// </summary>
    public void RaiseSnapshotChanged(PluginNavigationSnapshot snapshot)
    {
        Action<PluginNavigationSnapshot>? handlers;
        lock (_gate)
            handlers = _snapshotChanged;
        if (handlers is null)
            return;
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try { ((Action<PluginNavigationSnapshot>)handler)(snapshot); }
            catch { }
        }
    }

    private sealed class GoToPauseHandle(Action remove) : IDisposable
    {
        public void Dispose() => remove();
    }
}