using System.Collections.Immutable;
using System.Numerics;
using AcDream.Content;
using AcDream.Content.Pak;
using AcDream.Core.Physics;
using AcDream.Runtime;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Physics;
using AcDream.Runtime.Session;

namespace AcDream.HostParity.Tests;

/// <summary>
/// Gives an arm a local player with a body standing on the ground.
///
/// Everything here is runtime code both clients run: the ground goes into the
/// shared physics engine, the character arrives through the ordinary
/// create-object path marked as this session's own login, and the body is made
/// by the runtime's first-entry drive -- the same class each host builds when
/// a real character logs in. Nothing is stood in for except the two things a
/// test machine has no way to supply: the installed data files a body's shape
/// is read from (a plain capsule stands in) and the presentation that would
/// acknowledge the placement (acknowledged straight away).
///
/// Without this the two clients have no character to move or swing, so every
/// combat and movement answer is "no body" on both arms and a scenario proves
/// nothing by agreeing.
/// </summary>
internal sealed class ParityPlayerBody : IDisposable
{
    /// <summary>The flat square of ground the scenarios stand on.</summary>
    internal const uint Landblock = 0xA9B40000u;

    /// <summary>The cell in that ground the character lives in.</summary>
    internal const uint Cell = Landblock | 0x0001u;

    /// <summary>Ground height, in metres.</summary>
    internal const float GroundHeight = 50f;

    private readonly GameRuntime _runtime;
    private readonly RuntimeFirstEntryDriveController _firstEntry;
    private readonly RuntimePlacementProjectionSubscription _placements;

    private ParityPlayerBody(
        GameRuntime runtime,
        RuntimeFirstEntryDriveController firstEntry,
        RuntimePlacementProjectionSubscription placements)
    {
        _runtime = runtime;
        _firstEntry = firstEntry;
        _placements = placements;
    }

    /// <summary>Lays the ground down and stands the first-entry drive up.</summary>
    internal static ParityPlayerBody Prepare(GameRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        RuntimePhysicsState physics = runtime.EntityObjects.Physics;
        physics.SetPosition.BeginCollisionGeneration(Landblock, 1UL);
        AddFlatGround(physics.Engine);
        physics.SetPosition.CommitCollisionGeneration(Landblock, 1UL, ready: true);
        physics.ObserveLocalWorldFrame(Cell, teleportAdvanced: false);

        var placements = new RuntimePlacementProjectionSubscription(
            runtime,
            new AcceptingPlacementSink(runtime));
        var firstEntry = new RuntimeFirstEntryDriveController(
            runtime.EntityObjects,
            runtime.Clock,
            new CapsuleCollisionSource(),
            () => PlayerMovementConstructionOptions.From(
                runtime.CharacterOwner.MovementSkills.Snapshot),
            // With no installed data files neither host reads an authored
            // shape here; both fall back to the same plain capsule.
            static _ => new RuntimeLocalPlayerPhysicsActivationPreparation(
                Radius: 0.48f,
                Height: 1.835f,
                RuntimeLocalPlayerShadowDisposition.ProvenShapeless));
        return new ParityPlayerBody(runtime, firstEntry, placements);
    }

    /// <summary>
    /// Brings the character in as its own login and lets the first-entry
    /// drive make its body. Returns the record the runtime keeps for it.
    /// </summary>
    internal RuntimeEntityRecord SpawnLocalPlayer(uint guid, float x, float y)
    {
        _runtime.PlayerIdentity.ServerGuid = guid;
        RuntimeEntityRecord record = _runtime.EntityObjects
            .RegisterEntityWithInitialResidence(
                ParityWorld.Spawn(guid, x, y, Cell, state: 0),
                isLocalPlayer: true)
            .Canonical!;
        _ = _runtime.EntityObjects.ApplyAcceptedSpawn(
            record,
            record.CreateIntegrationVersion,
            record.Snapshot,
            replaceGeneration: false);
        Drive();
        return record;
    }

    /// <summary>Lets the first-entry drive take every pending arrival a step on.</summary>
    internal void Drive() => _firstEntry.DriveAll();

    /// <summary>Whether the character has a body that can be moved.</summary>
    internal bool HasLiveBody =>
        _runtime.MovementOwner.Controller is { CanExecuteLiveMovement: true };

    public void Dispose() => _placements.Dispose();

    private static void AddFlatGround(PhysicsEngine engine)
    {
        var heights = new byte[81];
        Array.Fill(heights, (byte)GroundHeight);
        var heightTable = new float[256];
        for (int index = 0; index < heightTable.Length; index++)
            heightTable[index] = index;
        engine.AddLandblock(
            Landblock | 0xFFFFu,
            new TerrainSurface(heights, heightTable),
            [],
            [],
            worldOffsetX: 0f,
            worldOffsetY: 0f);
    }

    /// <summary>
    /// Nothing is drawn here, so a placement the runtime offers is taken as
    /// shown at once -- except while the character is still arriving, which
    /// both clients leave to the arrival itself to acknowledge in step with
    /// the body it is building. Taking it earlier strands the arrival.
    /// Both arms share this, so it cannot hide a difference.
    /// </summary>
    private sealed class AcceptingPlacementSink(GameRuntime runtime)
        : IRuntimePlacementProjectionSink
    {
        public bool TryApply(in RuntimePlacementProjectionSnapshot projection)
        {
            RuntimePlacementProjectionToken token = projection.Token;
            if (projection.Kind
                    is RuntimePlacementProjectionKind.Place
                    or RuntimePlacementProjectionKind.Withdraw
                && token.IsValid
                && runtime.EntityObjects.Entities.TryGetByLocalId(
                    token.Entity.LocalEntityId,
                    out RuntimeEntityRecord arriving)
                && runtime.EntityObjects.Entities.IsCurrent(arriving)
                && arriving.Key == token.Entity
                && runtime.EntityObjects.TryGetInitialCreateResidence(
                    arriving,
                    out _))
            {
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// The body shape a character gets with no installed data files: one
    /// sphere of the usual radius, which is what both hosts fall back to.
    /// </summary>
    private sealed class CapsuleCollisionSource : IPreparedCollisionSource
    {
        public PreparedAssetPresence ProbeCollision(
            PakAssetType type,
            uint sourceFileId) => PreparedAssetPresence.Available;

        public PreparedCollisionReadResult<FlatSetupCollision> ReadSetupCollision(
            uint sourceFileId,
            CancellationToken cancellationToken = default) =>
            PreparedCollisionReadResult<FlatSetupCollision>.Loaded(
                new FlatSetupCollision(
                    ImmutableArray<FlatCollisionCylinder>.Empty,
                    [new FlatCollisionSphere(Vector3.Zero, 0.48f)],
                    height: 0f,
                    radius: 0f,
                    stepUpHeight: 0.4f,
                    stepDownHeight: 0.4f));

        public PreparedCollisionReadResult<FlatGfxObjCollisionAsset>
            ReadGfxObjCollision(
                uint sourceFileId,
                CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public PreparedCollisionReadResult<FlatCellStructureCollisionAsset>
            ReadCellStructureCollision(
                uint sourceFileId,
                CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public PreparedCollisionReadResult<FlatEnvCellTopology>
            ReadEnvCellTopology(
                uint sourceFileId,
                CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public PreparedCollisionSourceStats CollisionStats => default;

        public void Dispose()
        {
        }
    }
}
