using System.Numerics;
using System.Runtime.CompilerServices;
using AcDream.App.Rendering.Scene;
using AcDream.App.Rendering.Walk;
using AcDream.App.Rendering.Wb;
using AcDream.Core.Meshing;

namespace AcDream.App.Tests.Rendering.Walk;

public sealed partial class WalkStaticStreamPopulatorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetainedCells_ChangingOneEntityPreservesStaticGeometryWithoutWarmedAllocations(
        bool liveDynamic)
    {
        RenderProjectionClass projectionClass = liveDynamic
            ? RenderProjectionClass.LiveDynamicRoot : RenderProjectionClass.ActiveAnimatedStatic;
        using var fx = new DispatcherFixture();
        InstallRetainedMesh(fx);
        InstallRetainedMesh(fx, RetainedMesh + 1, 12);
        RenderProjectionRecord moving = RetainedRecord(9, RetainedMesh + 1) with
        {
            ProjectionClass = projectionClass,
        };
        var world = new RetainedWorld();
        world.Set(RetainedRecord(1), moving, RetainedRecord(2));
        var cache = new FarLandscapeDrawCache(fx.Dispatcher, world);
        var views = new RetainedViews();
        var stream = new OrderedDrawStream();
        var alpha = new List<WbDrawDispatcher.WalkClassifiedBatch>();
        for (int warmup = 0; warmup < 16; warmup++)
            _ = MeasureChangingRetainedReplayAllocations(
                fx.Dispatcher, cache, world, views, stream, alpha, ref moving, 0, 256);
        int reads = world.Reads;
        int classified = cache.EntityClassificationCount;
        long allocated = MeasureChangingRetainedReplayAllocations(
            fx.Dispatcher, cache, world, views, stream, alpha, ref moving, 256, 256);

        Assert.Equal(0, allocated);
        Assert.Equal(reads, world.Reads);
        Assert.Equal(1, cache.RebuildCount);
        Assert.Equal(256, cache.EntityClassificationCount - classified);
        Assert.Equal(new[] { 3u, 3u, 12u }, stream.Keys.Select(key => key.FirstIndex));
        Assert.Equal(new[] { 1f, 2f, 511f }, stream.Transforms.Select(transform => transform.M41));
        Assert.Equal(projectionClass == RenderProjectionClass.LiveDynamicRoot ? WalkDrawStage.Dynamic : WalkDrawStage.OutdoorStatic,
            stream.Stages[2]);
    }

    // Keep setup, disposal and assertion work outside the measured method's optimization boundary.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureChangingRetainedReplayAllocations(
        WbDrawDispatcher dispatcher,
        FarLandscapeDrawCache cache,
        RetainedWorld world,
        RetainedViews views,
        OrderedDrawStream stream,
        List<WbDrawDispatcher.WalkClassifiedBatch> alpha,
        ref RenderProjectionRecord moving,
        int firstFrame,
        int iterations)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int frame = firstFrame; frame < firstFrame + iterations; frame++)
        {
            moving = moving with
            {
                PreviousTransform = new PreviousRenderTransform(moving.Transform.LocalToWorld),
                Transform = new RenderTransform(Matrix4x4.CreateTranslation(frame, 0, 0)),
                MeshSet = moving.MeshSet with { Revision = (ulong)frame },
            };
            world.Current[9] = moving;
            stream.Reset();
            alpha.Clear();
            dispatcher.BeginWalkPartFrame();
            cache.BeginFrame();
            cache.TryAppend(RetainedBlock, 4, 0, stream, views, 0, Vector3.Zero, alpha);
            dispatcher.EndWalkPartFrame();
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void RetainedCells_MovedTranslucentEntityRefreshesItsAlphaSortCentres()
    {
        using var fx = new DispatcherFixture();
        InjectRenderData(
            fx.Manager,
            RetainedMesh,
            MakeFlatMesh(MakeBatch(1, TranslucencyKind.AlphaBlend, 3, 0, 3, 1)));
        var world = new RetainedWorld();
        world.Set(RetainedRecord(1), RetainedRecord(9));
        var cache = new FarLandscapeDrawCache(fx.Dispatcher, world);
        Assert.Equal(
            new[] { 81f, 1f },
            AppendRetainedAlpha(fx, cache).Select(batch => batch.SortDistanceSq));
        int regroups = cache.RegroupCount;

        // Moving a translucent entity keeps the entry's grouping, so the only
        // thing that has to be recomputed is where its alpha batches sort.
        world.Current[1] = RetainedRecord(1) with
        {
            Transform = new RenderTransform(Matrix4x4.CreateTranslation(30f, 0f, 0f)),
        };
        List<WbDrawDispatcher.WalkClassifiedBatch> moved = AppendRetainedAlpha(fx, cache);
        Assert.Equal(regroups, cache.RegroupCount);
        Assert.Equal(1, cache.RebuildCount);
        Assert.Equal(new[] { 900f, 81f }, moved.Select(batch => batch.SortDistanceSq));
        Assert.Equal(new[] { 30f, 9f }, moved.Select(batch => batch.Transform.M41));
    }

    private static List<WbDrawDispatcher.WalkClassifiedBatch> AppendRetainedAlpha(
        DispatcherFixture fx, FarLandscapeDrawCache cache)
    {
        fx.Dispatcher.BeginWalkPartFrame();
        try
        {
            cache.BeginFrame();
            var alpha = new List<WbDrawDispatcher.WalkClassifiedBatch>();
            Assert.True(cache.TryAppend(
                RetainedBlock, 4, 0, new OrderedDrawStream(), new RetainedViews(), 0,
                Vector3.Zero, alpha));
            return alpha;
        }
        finally { fx.Dispatcher.EndWalkPartFrame(); }
    }

    [Fact]
    public void RetainedCells_FadingDynamicKeepsFadingWithoutSceneWrites()
    {
        using var fx = new DispatcherFixture();
        InstallRetainedMesh(fx);
        var world = new RetainedWorld { LandblockRevision = 11UL };
        RenderProjectionRecord dynamic = RetainedRecord(1) with
        {
            ProjectionClass = RenderProjectionClass.LiveDynamicRoot,
        };
        world.Set(dynamic);
        var cache = new FarLandscapeDrawCache(fx.Dispatcher, world);

        // A part fade runs on its own clock. Nothing writes the scene while it
        // runs, so the landblock stamp never moves, and an entry holding a
        // dynamic would keep its first classification and freeze the fade.
        fx.Fades.StartPartFade(
            dynamic.Source.LocalEntityId, partIndex: 0, start: 0.25f, end: 0.75f, time: 1f);
        float before = Assert.Single(AppendRetainedAlpha(fx, cache)).Alpha;

        fx.Fades.AdvanceAll(0.5f);
        float after = Assert.Single(AppendRetainedAlpha(fx, cache)).Alpha;

        Assert.Equal(0.75f, before, 3);
        Assert.True(
            after < before,
            $"the dynamic opacity froze at {before} instead of fading to {after}.");
    }

    [Fact]
    public void RetainedCells_UnchangedLandblockSkipsThePerEntityRefresh()
    {
        using var fx = new DispatcherFixture();
        InstallRetainedMesh(fx);
        var world = new RetainedWorld { LandblockRevision = 7UL };
        world.Set(RetainedRecord(1), RetainedRecord(2), RetainedRecord(3));
        var cache = new FarLandscapeDrawCache(fx.Dispatcher, world);
        Assert.Equal(
            new[] { 3u, 3u, 3u },
            AppendRetainedFrame(fx, cache).Keys.Select(key => key.FirstIndex));
        int reads = world.CurrentReads;

        // The landblock stamp did not move, so the entry is not re-read at all.
        world.Current[2] = RetainedRecord(2, RetainedMesh + 1);
        Assert.Equal(
            new[] { 3u, 3u, 3u },
            AppendRetainedFrame(fx, cache).Keys.Select(key => key.FirstIndex));
        Assert.Equal(reads, world.CurrentReads);

        // Once the scene stamps a write to the landblock, the entry is read
        // again and picks the change up.
        InstallRetainedMesh(fx, RetainedMesh + 1, 12);
        world.LandblockRevision = 8UL;
        Assert.Equal(
            new[] { 3u, 3u, 12u },
            AppendRetainedFrame(fx, cache).Keys.Select(key => key.FirstIndex));
        Assert.True(world.CurrentReads > reads);
    }

    [Fact]
    public void RetainedCells_MovedEntityKeepsTheCachedGrouping()
    {
        using var fx = new DispatcherFixture();
        InstallRetainedMesh(fx);
        InstallRetainedMesh(fx, RetainedMesh + 1, 12);
        var world = new RetainedWorld();
        world.Set(RetainedRecord(1), RetainedRecord(2, RetainedMesh + 1), RetainedRecord(3));
        var cache = new FarLandscapeDrawCache(fx.Dispatcher, world);
        Assert.Equal(
            new[] { 3u, 3u, 12u },
            AppendRetainedFrame(fx, cache).Keys.Select(key => key.FirstIndex));
        int regroups = cache.RegroupCount;

        // Moving the entity reclassifies it but reproduces the same batches,
        // so the cached groups and alpha partition still describe the entry.
        world.Current[2] = RetainedRecord(2, RetainedMesh + 1) with
        {
            Transform = new RenderTransform(Matrix4x4.CreateTranslation(7f, 0f, 0f)),
        };
        OrderedDrawStream moved = AppendRetainedFrame(fx, cache);
        Assert.Equal(regroups, cache.RegroupCount);
        Assert.Equal(new[] { 3u, 3u, 12u }, moved.Keys.Select(key => key.FirstIndex));
        Assert.Equal(new[] { 1f, 3f, 7f }, moved.Transforms.Select(transform => transform.M41));

        // Changing its geometry changes the shape, which does regroup.
        world.Current[2] = RetainedRecord(2);
        OrderedDrawStream regrouped = AppendRetainedFrame(fx, cache);
        Assert.Equal(regroups + 1, cache.RegroupCount);
        Assert.Equal(new[] { 3u, 3u, 3u }, regrouped.Keys.Select(key => key.FirstIndex));
        Assert.Equal(new[] { 1f, 2f, 3f }, regrouped.Transforms.Select(transform => transform.M41));
    }

    [Fact]
    public void RetainedCells_ChangedGeometryRegroupsOnlyTheChangedEntity()
    {
        using var fx = new DispatcherFixture();
        InstallRetainedMesh(fx);
        InstallRetainedMesh(fx, RetainedMesh + 1, 12);
        var world = new RetainedWorld();
        world.Set(RetainedRecord(1), RetainedRecord(2, RetainedMesh + 1), RetainedRecord(3));
        var cache = new FarLandscapeDrawCache(fx.Dispatcher, world);
        Assert.Equal(new[] { 3u, 3u, 12u }, AppendRetainedFrame(fx, cache).Keys.Select(key => key.FirstIndex));
        world.Current[2] = RetainedRecord(2);
        var updated = AppendRetainedFrame(fx, cache);
        Assert.Equal(new[] { 3u, 3u, 3u }, updated.Keys.Select(key => key.FirstIndex));
        Assert.Equal(new[] { 1f, 2f, 3f }, updated.Transforms.Select(transform => transform.M41));
        Assert.Equal(4, cache.EntityClassificationCount);
        Assert.Equal(1, cache.RebuildCount);
    }
}
