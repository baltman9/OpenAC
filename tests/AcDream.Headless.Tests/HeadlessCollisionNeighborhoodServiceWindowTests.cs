using System.Collections.Immutable;
using System.Reflection;
using AcDream.Content;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Physics;
using AcDream.Core.Spells;
using AcDream.Headless.Configuration;
using AcDream.Headless.Hosting;
using AcDream.Runtime;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Session;

namespace AcDream.Headless.Tests;

public sealed class HeadlessCollisionNeighborhoodServiceWindowTests
{
    [Fact]
    public void ServiceWindowIsResidencyNotGeometry_UnpublishedLandblockIsRefusedDespiteGeometricMembership()
    {
        var factory = new FixtureContentFactory();
        using var owner = new HeadlessProcessContentOwner(
            ContentDescriptor(),
            _ => { },
            factory);
        using HeadlessProcessContentOwner.HeadlessProcessContentLease lease =
            owner.AcquireLease("fixture");
        var operations = new FixtureGameplayOperations();
        using var runtime = new GameRuntime(new GameRuntimeDependencies(
            operations, operations, operations, operations));
        var neighborhood = new HeadlessCollisionNeighborhood(runtime, lease);

        const uint cell = 0xA9B40001u;
        Assert.True(
            ((IHeadlessCollisionNeighborhood)neighborhood)
                .IsWithinServiceWindow(cell));
        Assert.False(
            ((IRuntimeRemotePlacementServiceWindow)neighborhood)
                .IsWithinServiceWindow(cell));
    }

    [Fact]
    public void ServiceWindowCoversAPublishedNeighborLandblockNotOnlyTheExactCenter()
    {
        var factory = new FixtureContentFactory();
        using var owner = new HeadlessProcessContentOwner(
            ContentDescriptor(),
            _ => { },
            factory);
        using HeadlessProcessContentOwner.HeadlessProcessContentLease lease =
            owner.AcquireLease("fixture");
        var operations = new FixtureGameplayOperations();
        using var runtime = new GameRuntime(new GameRuntimeDependencies(
            operations, operations, operations, operations));
        var neighborhood = new HeadlessCollisionNeighborhood(runtime, lease);

        const uint neighborLandblock = 0xA9B5FFFFu;
        const uint neighborCell = 0xA9B50001u;
        runtime.EntityObjects.Physics.Engine.AddLandblock(
            neighborLandblock,
            new TerrainSurface(new byte[81], new float[256]),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 0f);
        SeedResident(neighborhood, neighborLandblock);

        Assert.True(
            ((IRuntimeRemotePlacementServiceWindow)neighborhood)
                .IsWithinServiceWindow(neighborCell));
    }

    [Fact]
    public void AWalkNorthPublishesOnlyTheNewRowMeasuredFromTheSameFrame()
    {
        // Arrived in 0x282A; the window is 0x27..0x29 by 0x29..0x2B. The
        // player walks into 0x282B, so the row y = 0x2C is new.
        const uint frame = 0x282AFFFFu;
        var resident = new HashSet<uint>();
        for (uint x = 0x27; x <= 0x29; x++)
        {
            for (uint y = 0x29; y <= 0x2B; y++)
                resident.Add((x << 24) | (y << 16) | 0xFFFFu);
        }
        var plan = new Queue<HeadlessCollisionNeighborhood.PublicationSpec>();

        HeadlessCollisionNeighborhood.BuildPublicationPlan(
            0x282BFFFFu,
            frame,
            resident,
            plan);

        Assert.Equal(
            [
                (0x272CFFFFu, new System.Numerics.Vector3(-192f, 384f, 0f)),
                (0x282CFFFFu, new System.Numerics.Vector3(0f, 384f, 0f)),
                (0x292CFFFFu, new System.Numerics.Vector3(192f, 384f, 0f)),
            ],
            plan.Select(spec => (spec.LandblockId, spec.Origin)));
        Assert.False(
            HeadlessCollisionNeighborhood.IsInWindow(0x282BFFFFu, 0x2829FFFFu));
        Assert.True(
            HeadlessCollisionNeighborhood.IsInWindow(0x282BFFFFu, 0x292AFFFFu));
    }

    [Fact]
    public void AFreshFramePublishesTheCentreFirstAtTheWorldsZero()
    {
        var plan = new Queue<HeadlessCollisionNeighborhood.PublicationSpec>();

        HeadlessCollisionNeighborhood.BuildPublicationPlan(
            0x282AFFFFu,
            0x282AFFFFu,
            new HashSet<uint>(),
            plan);

        HeadlessCollisionNeighborhood.PublicationSpec first = plan.Dequeue();
        Assert.Equal(0x282AFFFFu, first.LandblockId);
        Assert.Equal(System.Numerics.Vector3.Zero, first.Origin);
        Assert.True(first.Required);
        Assert.Equal(8, plan.Count);
        Assert.All(plan, spec => Assert.False(spec.Required));
    }

    private static void SeedResident(
        HeadlessCollisionNeighborhood neighborhood,
        uint landblockId)
    {
        FieldInfo field = typeof(HeadlessCollisionNeighborhood).GetField(
            "_resident",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingFieldException(
                nameof(HeadlessCollisionNeighborhood), "_resident");
        var resident = (HashSet<uint>)field.GetValue(neighborhood)!;
        resident.Add(landblockId);
    }

    private static HeadlessContentDescriptor ContentDescriptor() => new()
    {
        DatDirectory = "fixture-dats",
        PreparedAssetPath = "fixture.pak",
    };

    private sealed class FixtureContentFactory
        : IHeadlessProcessContentFactory
    {
        internal FixtureContentFactory()
        {
            DatsResource =
                DispatchProxy.Create<IDatReaderWriter, TestResourceProxy>();
            PreparedResource =
                DispatchProxy.Create<ITestPreparedSource, TestResourceProxy>();
        }

        internal IDatReaderWriter DatsResource { get; }
        internal ITestPreparedSource PreparedResource { get; }

        public HeadlessOpenedProcessContent Open(
            HeadlessContentDescriptor descriptor,
            Action<string> diagnostic) =>
            new(
                DatsResource,
                PreparedResource,
                MagicCatalog.Empty,
                ImmutableArray.CreateRange(new float[256]));
    }

    private sealed class FixtureGameplayOperations
        : IRuntimeCombatAttackOperations,
          IRuntimeCombatTargetOperations,
          IRuntimeCombatModeOperations,
          IRuntimeSpellCastOperations
    {
        public bool CanStartAttack(bool allowAutoTarget) => false;
        public void PrepareAttackRequest()
        {
        }

        public bool SendAttack(AttackHeight height, float power, bool allowAutoTarget) => false;
        public void SendCancelAttack()
        {
        }

        public bool IsDualWield => false;
        public bool PlayerReadyForAttack => false;
        public bool AutoRepeatAttack => false;
        public bool AutoTarget => false;
        public uint? SelectClosestTarget() => null;
        public bool IsInWorld => false;
        public IReadOnlyList<ClientObject> GetOrderedEquipment() => [];
        public void NotifyExplicitCombatModeRequest()
        {
        }

        public void SendChangeCombatMode(CombatMode mode)
        {
        }

        public uint LocalPlayerId => 0u;
        public bool CanSend => false;
        public bool HasRequiredComponents(uint spellId) => false;

        public bool IsTargetCompatible(
            uint targetId, SpellMetadata spell, bool showMessage) => false;

        public void StopCompletely()
        {
        }

        public void SendUntargeted(uint spellId)
        {
        }

        public void SendTargeted(uint targetId, uint spellId)
        {
        }

        public void DisplayMessage(string message)
        {
        }

        public void IncrementBusy()
        {
        }
    }
}
