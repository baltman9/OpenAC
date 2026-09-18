using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Physics;
using DatReaderWriter.Types;

namespace AcDream.Runtime.Tests.Physics;

/// <summary>
/// The ordinary-physics step protocol: one step is begun and completed at a
/// time, the commit object that describes it is reused from step to step,
/// and the ticket a begin returns is what says which step a caller holds.
/// </summary>
public sealed class RuntimeOrdinaryPhysicsUpdaterTests
{
    [Fact]
    public void AStepIsCompletedAndItsSnapshotReachesTheAcknowledgement()
    {
        using var fixture = new Fixture();

        Assert.True(fixture.Begin(
            out RuntimeOrdinaryPhysicsCommit commit,
            out RuntimeOrdinaryPhysicsTicket ticket));
        RuntimePhysicsFrameSnapshot? seen = null;
        bool completed = fixture.Updater.Complete(
            commit,
            ticket,
            0,
            0,
            snapshot =>
            {
                seen = snapshot;
                return true;
            });

        Assert.True(completed);
        Assert.NotNull(seen);
        Assert.Equal(commit.Snapshot, seen!.Value);
    }

    /// <summary>The commit belongs to the updater and describes every one of
    /// its steps in turn; a second updater has its own.</summary>
    [Fact]
    public void EveryStepOfAnUpdaterIsDescribedByTheSameCommit()
    {
        using var fixture = new Fixture();

        Assert.True(fixture.Begin(
            out RuntimeOrdinaryPhysicsCommit first,
            out RuntimeOrdinaryPhysicsTicket ticket));
        Assert.True(fixture.Updater.Complete(first, ticket, 0, 0, Accept));
        Assert.True(fixture.Begin(out RuntimeOrdinaryPhysicsCommit second, out _));
        Assert.Same(first, second);

        var other = new RuntimeOrdinaryPhysicsUpdater(fixture.Physics);
        Assert.True(fixture.Begin(
            out RuntimeOrdinaryPhysicsCommit elsewhere, out _, other));
        Assert.NotSame(first, elsewhere);
    }

    /// <summary>A caller holding the commit of an earlier step is refused the
    /// way a caller completing twice is: the object describes the step that
    /// superseded it, and the ticket is what says so.</summary>
    [Fact]
    public void CompletingAStepThatHasBeenSupersededIsRefused()
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Begin(
            out RuntimeOrdinaryPhysicsCommit commit,
            out RuntimeOrdinaryPhysicsTicket stale));

        Assert.True(fixture.Begin(out _, out RuntimeOrdinaryPhysicsTicket current));
        Assert.NotEqual(stale, current);

        Assert.Throws<InvalidOperationException>(
            () => fixture.Updater.Complete(commit, stale, 0, 0, Accept));

        // The step that superseded it is still completable, once.
        Assert.True(fixture.Updater.Complete(commit, current, 0, 0, Accept));
    }

    [Fact]
    public void CompletingTheSameStepTwiceIsRefused()
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Begin(
            out RuntimeOrdinaryPhysicsCommit commit,
            out RuntimeOrdinaryPhysicsTicket ticket));
        Assert.True(fixture.Updater.Complete(commit, ticket, 0, 0, Accept));

        Assert.Throws<InvalidOperationException>(
            () => fixture.Updater.Complete(commit, ticket, 0, 0, Accept));
    }

    [Fact]
    public void ATicketOfNoStepIsRefused()
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Begin(out RuntimeOrdinaryPhysicsCommit commit, out _));

        Assert.Throws<InvalidOperationException>(
            () => fixture.Updater.Complete(commit, default, 0, 0, Accept));
    }

    [Fact]
    public void ACommitOfAnotherUpdaterIsRefused()
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Begin(
            out RuntimeOrdinaryPhysicsCommit commit,
            out RuntimeOrdinaryPhysicsTicket ticket));
        var other = new RuntimeOrdinaryPhysicsUpdater(fixture.Physics);

        Assert.Throws<InvalidOperationException>(
            () => other.Complete(commit, ticket, 0, 0, Accept));
    }

    /// <summary>Ownership lost between the begin and the complete: the step
    /// is refused without an exception, and the next step is unaffected.</summary>
    [Fact]
    public void AStepWhoseOwnerWentAwayCompletesAsRefused()
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Begin(
            out RuntimeOrdinaryPhysicsCommit commit,
            out RuntimeOrdinaryPhysicsTicket ticket));

        fixture.OwnerValid = false;
        Assert.False(fixture.Updater.Complete(commit, ticket, 0, 0, Accept));
        Assert.True(commit.Completed);

        fixture.OwnerValid = true;
        Assert.True(fixture.Begin(
            out RuntimeOrdinaryPhysicsCommit next,
            out RuntimeOrdinaryPhysicsTicket nextTicket));
        Assert.True(fixture.Updater.Complete(next, nextTicket, 0, 0, Accept));
    }

    /// <summary>A begin that is refused describes nothing, and leaves the
    /// step its caller is already holding alone.</summary>
    [Fact]
    public void ARefusedBeginDoesNotSupersedeTheStepInFlight()
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Begin(
            out RuntimeOrdinaryPhysicsCommit commit,
            out RuntimeOrdinaryPhysicsTicket ticket));

        Assert.False(fixture.Begin(
            out RuntimeOrdinaryPhysicsCommit refused,
            out RuntimeOrdinaryPhysicsTicket refusedTicket,
            objectClockEpoch: fixture.Record.ObjectClockEpoch + 1ul));
        Assert.Null(refused);
        Assert.Equal(default, refusedTicket);

        Assert.True(fixture.Updater.Complete(commit, ticket, 0, 0, Accept));
    }

    [Fact]
    public void AnAcknowledgementThatRefusesRefusesTheStep()
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Begin(
            out RuntimeOrdinaryPhysicsCommit commit,
            out RuntimeOrdinaryPhysicsTicket ticket));

        Assert.False(fixture.Updater.Complete(commit, ticket, 0, 0, _ => false));
    }

    private static bool Accept(RuntimePhysicsFrameSnapshot snapshot) => true;

    private sealed class Fixture : IDisposable
    {
        private readonly RuntimeEntityObjectLifetime _lifetime = new();
        private readonly Func<bool> _ownerValid;

        public Fixture()
        {
            Record = _lifetime.Entities.AddActive(Spawn(0x70000101u, 1));
            Body = new PhysicsBody();
            _lifetime.Entities.SetPhysicsBody(Record, Body);
            _lifetime.Physics.AcknowledgeSpatialProjection(Record, spatial: true);
            Updater = new RuntimeOrdinaryPhysicsUpdater(_lifetime.Physics);
            _ownerValid = () => OwnerValid;
        }

        public RuntimeEntityRecord Record { get; }

        public PhysicsBody Body { get; }

        public RuntimeOrdinaryPhysicsUpdater Updater { get; }

        public RuntimePhysicsState Physics => _lifetime.Physics;

        public bool OwnerValid { get; set; } = true;

        public bool Begin(
            out RuntimeOrdinaryPhysicsCommit commit,
            out RuntimeOrdinaryPhysicsTicket ticket,
            RuntimeOrdinaryPhysicsUpdater? updater = null,
            ulong? objectClockEpoch = null) =>
            (updater ?? Updater).TryBegin(
                Record,
                new Frame(),
                objectScale: 1f,
                quantum: 1f / 30f,
                radius: 0.48f,
                height: 1.2f,
                objectClockEpoch ?? Record.ObjectClockEpoch,
                sequencer: null,
                captureAnimationHooks: static (_, _) => { },
                _ownerValid,
                out commit,
                out ticket);

        public void Dispose() => _lifetime.Dispose();
    }

    private static WorldSession.EntitySpawn Spawn(uint guid, ushort instance)
    {
        var position = new CreateObject.ServerPosition(
            0x0101FFFFu, 10f, 20f, 5f, 1f, 0f, 0f, 0f);
        var timestamps = new PhysicsTimestamps(
            Position: 1,
            Movement: 1,
            State: 1,
            Vector: 1,
            Teleport: 0,
            ServerControlledMove: 1,
            ForcePosition: 0,
            ObjDesc: 1,
            Instance: instance);
        var physics = new PhysicsSpawnData(
            RawState: 0x408u,
            Position: position,
            Movement: null,
            AnimationFrame: null,
            SetupTableId: 0x02000001u,
            MotionTableId: 0x09000001u,
            SoundTableId: null,
            PhysicsScriptTableId: null,
            Parent: null,
            Children: null,
            Scale: null,
            Friction: null,
            Elasticity: null,
            Translucency: null,
            Velocity: null,
            Acceleration: null,
            AngularVelocity: null,
            DefaultScriptType: null,
            DefaultScriptIntensity: null,
            Timestamps: timestamps);
        return new WorldSession.EntitySpawn(
            guid,
            position,
            0x02000001u,
            Array.Empty<CreateObject.AnimPartChange>(),
            Array.Empty<CreateObject.TextureChange>(),
            Array.Empty<CreateObject.SubPaletteSwap>(),
            null,
            null,
            "fixture",
            null,
            null,
            0x09000001u,
            PhysicsState: 0x408u,
            InstanceSequence: instance,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: physics);
    }
}
