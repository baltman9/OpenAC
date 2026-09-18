using System.Numerics;
using System.Runtime.CompilerServices;
using AcDream.App.Rendering.Scene;
using AcDream.App.Rendering.Walk;
using AcDream.App.Rendering.Wb;
using AcDream.App.Rendering.Selection;
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

    /// <summary>
    /// The block arrays are sized to what they hold and reused across
    /// rebuilds of the same entry. Eleven arrays per entry growing by
    /// doubling left gen2 garbage behind every entry that came and went on a
    /// route, which only a rare gen2 collection reclaims.
    /// </summary>
    [Fact]
    public void RetainedCells_RebuildingAnEntryAtAStableCountTakesNoNewBlockArrays()
    {
        using var fx = new DispatcherFixture();
        InstallRetainedMesh(fx);
        InstallRetainedMesh(fx, RetainedMesh + 1, 12);
        var world = new RetainedWorld();
        world.Set(RetainedRecord(1), RetainedRecord(2), RetainedRecord(3));
        var cache = new FarLandscapeDrawCache(fx.Dispatcher, world);

        _ = AppendRetainedFrame(fx, cache);
        Assert.Equal(1, cache.BlockArrayAllocationCount);
        int builds = cache.BlockBuildCount;
        Assert.Equal(3, cache.TotalBlockCommands);
        Assert.Equal(cache.TotalBlockCommands, cache.TotalBlockCapacity);

        // Reclassify the entry back and forth: the command count never moves,
        // so no rebuild may take arrays again.
        for (int i = 0; i < 8; i++)
        {
            world.Current[2] = RetainedRecord(2, (i & 1) == 0 ? RetainedMesh + 1 : RetainedMesh);
            _ = AppendRetainedFrame(fx, cache);
        }

        Assert.True(cache.BlockBuildCount > builds, "the entry was never rebuilt.");
        Assert.Equal(1, cache.BlockArrayAllocationCount);
        Assert.Equal(3, cache.TotalBlockCommands);
        Assert.Equal(cache.TotalBlockCommands, cache.TotalBlockCapacity);
    }

    /// <summary>
    /// A generation change replaces every cached entry at once. The arrays
    /// the departing entries held are the arrays the arriving ones want, so
    /// a replacement at stable totals allocates nothing after the first
    /// generation; without the pool each generation allocated its own, and
    /// the departed sets became garbage no frame ever reclaims cheaply.
    /// </summary>
    [Fact]
    public void RetainedCells_ReplacingAGenerationTakesTheDepartedEntriesBlockArrays()
    {
        using var fx = new DispatcherFixture();
        InstallRetainedMesh(fx);
        var world = new RetainedWorld();
        world.Set(RetainedRecord(1), RetainedRecord(2), RetainedRecord(3));
        var cache = new FarLandscapeDrawCache(fx.Dispatcher, world);

        _ = AppendRetainedFrame(fx, cache);
        Assert.Equal(1, cache.BlockArrayAllocationCount);
        Assert.Equal(0, cache.BlockArrayReuseCount);
        Assert.Equal(3, cache.TotalBlockCommands);

        for (int generation = 2; generation <= 5; generation++)
        {
            world.RetainedContext = (
                RenderSceneGeneration.FromRaw((ulong)generation),
                world.RetainedContext.TupleLandblockId);
            _ = AppendRetainedFrame(fx, cache);

            Assert.Equal(1, cache.BlockArrayAllocationCount);
            Assert.Equal(generation - 1, cache.BlockArrayReuseCount);
            Assert.Equal(3, cache.TotalBlockCommands);
            Assert.Equal(3, cache.TotalBlockCapacity);
            Assert.Equal(0, cache.PooledBlockCapacity);
        }
    }

    /// <summary>
    /// A set taken from the pool can be larger than its new owner needs and
    /// still carries the commands of the entry that released it. What the
    /// entry hands the stream is bounded by its own command count, so the
    /// stream is the one a cache built from scratch produces.
    /// </summary>
    [Fact]
    public void RetainedCells_AnEntryOnAPooledArraySetEmitsOnlyItsOwnCommands()
    {
        using var fx = new DispatcherFixture();
        InstallRetainedMesh(fx);
        var world = new RetainedWorld();
        world.Set(RetainedRecord(1), RetainedRecord(2), RetainedRecord(3));
        var cache = new FarLandscapeDrawCache(fx.Dispatcher, world);
        var views = new RetainedViews();

        _ = AppendRetainedFrame(fx, cache, views);
        Assert.Equal(3, cache.TotalBlockCommands);

        world.RetainedContext = (
            RenderSceneGeneration.FromRaw(2ul),
            world.RetainedContext.TupleLandblockId);
        world.Set(RetainedRecord(4));
        AssertMatchesFullRebuild(fx, world, cache, views, "a smaller generation");

        Assert.Equal(1, cache.BlockArrayAllocationCount);
        Assert.Equal(1, cache.BlockArrayReuseCount);
        Assert.Equal(1, cache.TotalBlockCommands);
        Assert.Equal(3, cache.TotalBlockCapacity);
    }

    /// <summary>
    /// The exactness pin for the retained command block: a cache that keeps
    /// and patches its commands must produce, every frame, the stream a cache
    /// built from scratch that frame produces -- through moves, a geometry
    /// swap, a retirement, a re-admission on a reused entity id, a fade, a
    /// cell change, a translucency change and a visibility flip.
    /// </summary>
    [Fact]
    public void RetainedCells_PatchedStreamMatchesAFullRebuildThroughAScriptedSequence()
    {
        const uint secondCell = RetainedBlock | 2u;
        var lighting = new RetainedLighting();
        using var fx = new DispatcherFixture(selectionSink: lighting);
        InstallRetainedMesh(fx);
        InstallRetainedMesh(fx, RetainedMesh + 1, 12);
        InjectRenderData(
            fx.Manager,
            RetainedMesh + 2,
            MakeFlatMesh(MakeBatch(2, TranslucencyKind.AlphaBlend, 21, 0, 3, 1)));

        var world = new RetainedWorld();
        world.Set(RetainedRecord(1), RetainedRecord(2), RetainedRecord(3));
        var retained = new FarLandscapeDrawCache(fx.Dispatcher, world);
        var views = new RetainedViews();
        AssertMatchesFullRebuild(fx, world, retained, views, "first frame");
        AssertMatchesFullRebuild(fx, world, retained, views, "idle frame");

        // A move: the same batches with a new transform.
        UpdateInPlace(
            world,
            RetainedRecord(1) with
            {
                Transform = new RenderTransform(Matrix4x4.CreateTranslation(17f, 0f, 0f)),
            });
        AssertMatchesFullRebuild(fx, world, retained, views, "moved");

        // A geometry swap: different batches, so a different grouping.
        UpdateInPlace(world, RetainedRecord(2, RetainedMesh + 1));
        AssertMatchesFullRebuild(fx, world, retained, views, "geometry swapped");

        // A retirement.
        world.Set(RetainedRecord(1), RetainedRecord(2, RetainedMesh + 1));
        AssertMatchesFullRebuild(fx, world, retained, views, "retired");

        // A re-admission that reuses the entity id under a new incarnation.
        world.Set(
            RetainedRecord(1),
            RetainedRecord(2, RetainedMesh + 1),
            RetainedRecord(3) with
            {
                OwnerIncarnation = RenderOwnerIncarnation.FromRaw(2),
            });
        AssertMatchesFullRebuild(fx, world, retained, views, "re-admitted");

        // A translucent entity, then a fade advancing on its own clock.
        world.Set(
            RetainedRecord(1),
            RetainedRecord(2, RetainedMesh + 2) with
            {
                ProjectionClass = RenderProjectionClass.LiveDynamicRoot,
            },
            RetainedRecord(3));
        AssertMatchesFullRebuild(fx, world, retained, views, "translucent dynamic");
        fx.Fades.StartPartFade(2u, partIndex: 0, start: 0.2f, end: 0.8f, time: 1f);
        AssertMatchesFullRebuild(fx, world, retained, views, "fade started");
        fx.Fades.AdvanceAll(0.4f);
        AssertMatchesFullRebuild(fx, world, retained, views, "fade advanced");
        fx.Fades.AdvanceAll(0.4f);
        AssertMatchesFullRebuild(fx, world, retained, views, "fade advanced again");

        // A cell change inside the same entry.
        world.Cells[RetainedCell] = [RetainedRecord(1), RetainedRecord(2)];
        world.Cells[secondCell] = [RetainedRecord(3)];
        world.Revisions[RetainedCell] = world.Revisions.GetValueOrDefault(RetainedCell) + 1;
        world.Revisions[secondCell] = world.Revisions.GetValueOrDefault(secondCell) + 1;
        world.Current.Clear();
        foreach (RenderProjectionRecord[] cellRecords in world.Cells.Values)
        {
            foreach (RenderProjectionRecord cellRecord in cellRecords)
                world.Current[cellRecord.Source.LocalEntityId] = cellRecord;
        }
        AssertMatchesFullRebuild(fx, world, retained, views, "cell changed");

        // Selection lighting, which is resolved per frame per entity and is
        // not part of any record.
        lighting.Value = new RetailSelectionLighting(0.6f, 0.4f);
        AssertMatchesFullRebuild(fx, world, retained, views, "selection lighting changed");
        lighting.Value = new RetailSelectionLighting(0.1f, 0.9f);
        AssertMatchesFullRebuild(fx, world, retained, views, "selection lighting restored");

        // A visibility flip, which selects runs out of the block.
        views.Visible = false;
        AssertMatchesFullRebuild(fx, world, retained, views, "nothing visible");
        views.Visible = true;
        AssertMatchesFullRebuild(fx, world, retained, views, "visible again");
    }

    /// <summary>Writes a record change the way the scene does: the record a
    /// refresh reads and the record a rebuild reads are the same record, and
    /// the cell keeps its membership stamp because membership did not
    /// change.</summary>
    private static void UpdateInPlace(RetainedWorld world, RenderProjectionRecord record)
    {
        world.Current[record.Source.LocalEntityId] = record;
        foreach (RenderProjectionRecord[] records in world.Cells.Values)
        {
            for (int i = 0; i < records.Length; i++)
            {
                if (records[i].Source.LocalEntityId == record.Source.LocalEntityId)
                    records[i] = record;
            }
        }
    }

    private static void AssertMatchesFullRebuild(
        DispatcherFixture fx,
        RetainedWorld world,
        FarLandscapeDrawCache retained,
        RetainedViews views,
        string step)
    {
        (OrderedDrawStream patched, List<WbDrawDispatcher.WalkClassifiedBatch> patchedAlpha) =
            AppendOneFrame(fx, retained, views);
        (OrderedDrawStream rebuilt, List<WbDrawDispatcher.WalkClassifiedBatch> rebuiltAlpha) =
            AppendOneFrame(fx, new FarLandscapeDrawCache(fx.Dispatcher, world), views);

        void Same<T>(string field, IReadOnlyList<T> expected, IReadOnlyList<T> actual)
        {
            if (expected.Count == actual.Count
                && expected.SequenceEqual(actual))
            {
                return;
            }

            Assert.Fail(
                $"step '{step}': {field} diverged from a full rebuild."
                + $" rebuilt=[{string.Join(", ", expected)}]"
                + $" patched=[{string.Join(", ", actual)}]");
        }

        Assert.True(rebuilt.Count == patched.Count, $"step '{step}': command count {patched.Count} against a rebuild count of {rebuilt.Count}. rebuilt=[{string.Join(",", rebuilt.Transforms.Select(m => m.M41))}] patched=[{string.Join(",", patched.Transforms.Select(m => m.M41))}] rk=[{string.Join(",", rebuilt.Keys.Select(k => k.FirstIndex))}] pk=[{string.Join(",", patched.Keys.Select(k => k.FirstIndex))}]");
        Same("keys", rebuilt.Keys, patched.Keys);
        Same("transforms", rebuilt.Transforms, patched.Transforms);
        Same("stages", rebuilt.Stages, patched.Stages);
        Same("cell ids", rebuilt.CellIds, patched.CellIds);
        Same("clip slots", rebuilt.ClipSlots, patched.ClipSlots);
        Same("lights", rebuilt.Lights, patched.Lights);
        Same("indoor flags", rebuilt.IndoorFlags, patched.IndoorFlags);
        Same("alphas", rebuilt.Alphas, patched.Alphas);
        Same("selection lighting", rebuilt.SelectionLighting, patched.SelectionLighting);
        Same("detail categories", rebuilt.DetailCategories, patched.DetailCategories);
        Same("instance merges", rebuilt.AllowInstanceMerges, patched.AllowInstanceMerges);
        Same("alpha batches", rebuiltAlpha, patchedAlpha);
    }

    private static (OrderedDrawStream Stream, List<WbDrawDispatcher.WalkClassifiedBatch> Alpha)
        AppendOneFrame(DispatcherFixture fx, FarLandscapeDrawCache cache, RetainedViews views)
    {
        fx.Dispatcher.BeginWalkPartFrame();
        try
        {
            cache.BeginFrame();
            var stream = new OrderedDrawStream();
            var alpha = new List<WbDrawDispatcher.WalkClassifiedBatch>();
            Assert.True(cache.TryAppend(
                RetainedBlock, 4, 0, stream, views, 0, Vector3.Zero, alpha));
            return (stream, alpha);
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
