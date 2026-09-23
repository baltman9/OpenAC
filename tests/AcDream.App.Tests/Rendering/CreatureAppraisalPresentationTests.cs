using System.Numerics;
using AcDream.App.Rendering;
using AcDream.Core.World;

namespace AcDream.App.Tests.Rendering;

public sealed class CreatureAppraisalPresentationTests
{
    [Fact]
    public void CameraFitsRetailVerticalSpanAndKeepsIdentityLookDirection()
    {
        var camera = new CreatureAppraisalCamera { Aspect = 2f };

        camera.Fit(
            new Vector3(-1f, -0.5f, 0f),
            new Vector3(1f, 0.5f, 4f));

        Assert.Equal(0f, camera.Eye.X, 5);
        Assert.Equal(-(4f * 1.20710683f + 0.5f), camera.Eye.Y, 5);
        Assert.Equal(2f, camera.Eye.Z, 5);
        Vector3 forward = -new Vector3(
            camera.View.M13,
            camera.View.M23,
            camera.View.M33);
        Assert.Equal(Vector3.UnitY, forward);
    }

    [Fact]
    public void CameraUsesViewportAspectWhenCreatureIsWiderThanTall()
    {
        var camera = new CreatureAppraisalCamera { Aspect = 1f };

        camera.Fit(
            new Vector3(-4f, -0.5f, 0f),
            new Vector3(4f, 0.5f, 2f));

        Assert.Equal(-(8f * 1.20710683f + 0.5f), camera.Eye.Y, 5);
    }

    [Fact]
    public void CloneFactoryReusesIdentityAndFollowsCurrentAnimatedMeshRefs()
    {
        var firstMeshes = new[]
        {
            new MeshRef(0x01000001u, Matrix4x4.Identity),
        };
        var source = Entity(
            id: 17u,
            setup: 0x02000001u,
            firstMeshes);
        source.SetLocalBounds(
            new Vector3(-1f, -0.5f, 0f),
            new Vector3(1f, 0.5f, 2f));
        var lookup = new Lookup { Entity = source };
        var factory = new RetailCreatureAppraisalCloneFactory(lookup);

        Assert.True(factory.TrySynchronize(
            17u,
            currentClone: null,
            out WorldEntity? firstClone,
            out Vector3 firstMin,
            out Vector3 firstMax));
        var nextMeshes = new[]
        {
            new MeshRef(
                0x01000001u,
                Matrix4x4.CreateTranslation(0f, 0f, 0.25f)),
        };
        source.MeshRefs = nextMeshes;
        Assert.True(factory.TrySynchronize(
            17u,
            firstClone,
            out WorldEntity? nextClone,
            out Vector3 nextMin,
            out Vector3 nextMax));

        Assert.Same(firstClone, nextClone);
        Assert.Same(nextMeshes, nextClone!.MeshRefs);
        Assert.Equal(CreatureAppraisalEntityBuilder.RenderId, nextClone.Id);
        Assert.Equal(firstMin, nextMin);
        Assert.Equal(firstMax, nextMax);
        Assert.Equal([17u, 17u], lookup.Requests);
    }

    [Fact]
    public void PresenterRendersVisibleTargetAndClearsWhenProjectionDisappears()
    {
        WorldEntity clone = Entity(
            CreatureAppraisalEntityBuilder.RenderId,
            0x02000001u,
            [new MeshRef(0x01000001u, Matrix4x4.Identity)]);
        var renderer = new Renderer { Texture = 73u };
        var view = new View();
        var factory = new Factory { Clone = clone };
        var presenter = new CreatureAppraisalFramePresenter(
            renderer,
            view,
            factory);

        presenter.Render();
        factory.Available = false;
        presenter.Render();

        Assert.Equal([17u, 17u], factory.Requests);
        Assert.Same(clone, renderer.Creatures[0]);
        Assert.Null(renderer.Creatures[1]);
        Assert.Equal([(300, 265)], renderer.RenderSizes);
        Assert.Equal([73u, 0u], view.Textures);
    }

    /// <summary>
    /// A held object sits on the copy where it sits on the creature: same
    /// offset from the body and same turn, measured in the creature's own
    /// frame and carried over to the copy's. The copy is reused while the
    /// object is still held, and at most one copy per slot is drawn.
    /// Mutation: place the copy at the held object's world position and the
    /// offset row fails; build a new copy each frame and the reuse row fails.
    /// </summary>
    [Fact]
    public void HeldObjectsAreCopiedAtTheirPlaceOnTheCreature()
    {
        Quaternion creatureTurn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1.1f);
        Quaternion shieldTurn = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.4f);
        Vector3 offset = new(0.3f, 0.1f, 1.2f);
        WorldEntity creature = Entity(17u, 0x02000001u, [new MeshRef(0x01000001u, Matrix4x4.Identity)]);
        creature.Position = new Vector3(100f, 50f, 10f);
        creature.Rotation = creatureTurn;
        WorldEntity shield = Entity(40u, 0x02000002u, [new MeshRef(0x01000002u, Matrix4x4.Identity)]);
        shield.Position = creature.Position + Vector3.Transform(offset, creatureTurn);
        shield.Rotation = Quaternion.Concatenate(shieldTurn, creatureTurn);
        var lookup = new Lookup { Entity = creature };
        lookup.Held.Add(shield);
        var factory = new RetailCreatureAppraisalCloneFactory(lookup);
        Assert.True(factory.TrySynchronize(17u, null, out WorldEntity? copy, out _, out _));

        IReadOnlyList<WorldEntity> first = factory.SynchronizeAttachments(17u);
        WorldEntity held = Assert.Single(first);
        Vector3 expected = Vector3.Transform(offset, copy!.Rotation);
        Assert.Equal(expected.X, held.Position.X, 4);
        Assert.Equal(expected.Y, held.Position.Y, 4);
        Assert.Equal(expected.Z, held.Position.Z, 4);
        Quaternion expectedTurn = Quaternion.Concatenate(shieldTurn, copy.Rotation);
        Assert.True(MathF.Abs(Quaternion.Dot(expectedTurn, held.Rotation)) > 0.9999f);
        Assert.Equal(CreatureAppraisalEntityBuilder.AttachmentRenderIds[0], held.Id);
        Assert.Same(shield.MeshRefs, held.MeshRefs);

        Assert.Same(held, Assert.Single(factory.SynchronizeAttachments(17u)));

        for (uint i = 0; i < 6; i++)
            lookup.Held.Add(Entity(50u + i, 0x02000003u, [new MeshRef(0x01000003u, Matrix4x4.Identity)]));
        Assert.Equal(
            CreatureAppraisalEntityBuilder.AttachmentRenderIds.Length,
            factory.SynchronizeAttachments(17u).Count);

        lookup.Held.Clear();
        Assert.Empty(factory.SynchronizeAttachments(17u));
    }

    private static WorldEntity Entity(
        uint id,
        uint setup,
        IReadOnlyList<MeshRef> meshes)
        => new()
        {
            Id = id,
            ServerGuid = id,
            SourceGfxObjOrSetupId = setup,
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            MeshRefs = meshes,
        };

    private sealed class Lookup : ICreatureAppraisalEntityLookup
    {
        public WorldEntity? Entity { get; init; }
        public List<uint> Requests { get; } = [];
        public List<WorldEntity> Held { get; } = [];

        public void CollectAttachments(WorldEntity parent, List<WorldEntity> into) =>
            into.AddRange(Held);

        public bool TryGet(uint serverGuid, out WorldEntity entity)
        {
            Requests.Add(serverGuid);
            entity = Entity!;
            return Entity is not null;
        }
    }

    private sealed class Renderer : ICreatureAppraisalRenderer
    {
        public uint Texture { get; init; }
        public List<WorldEntity?> Creatures { get; } = [];
        public List<(int Width, int Height)> RenderSizes { get; } = [];

        public void SetCreature(
            WorldEntity? creature,
            Vector3 boundsMin,
            Vector3 boundsMax)
            => Creatures.Add(creature);

        public uint Render(int width, int height)
        {
            RenderSizes.Add((width, height));
            return Texture;
        }
    }

    private sealed class View : ICreatureAppraisalFrameView
    {
        public List<uint> Textures { get; } = [];

        public bool TryGetVisibleTarget(
            out uint serverGuid,
            out int width,
            out int height)
        {
            serverGuid = 17u;
            width = 300;
            height = 265;
            return true;
        }

        public void SetTextureHandle(uint textureHandle) =>
            Textures.Add(textureHandle);
    }

    private sealed class Factory : ICreatureAppraisalCloneFactory
    {
        public bool Available { get; set; } = true;
        public WorldEntity? Clone { get; init; }
        public List<uint> Requests { get; } = [];

        public bool TrySynchronize(
            uint serverGuid,
            WorldEntity? currentClone,
            out WorldEntity? synchronizedClone,
            out Vector3 boundsMin,
            out Vector3 boundsMax)
        {
            Requests.Add(serverGuid);
            synchronizedClone = Available ? Clone : null;
            boundsMin = new Vector3(-1f);
            boundsMax = new Vector3(1f);
            return Available;
        }
    }
}
