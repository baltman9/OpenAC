using System.Numerics;
using System.Runtime.InteropServices;
using AcDream.App.Rendering.Scene;
using AcDream.App.Rendering.Wb;

namespace AcDream.App.Rendering.Walk;

/// <summary>Keeps coarse landscape mesh groups until their source records change.</summary>
internal sealed class FarLandscapeDrawCache(
    WbDrawDispatcher dispatcher, IWalkFrameWorldData world)
{
    private readonly record struct CellKey(uint Block, int Side, int Index);
    private readonly record struct BatchRef(Entity Entity, int PartIndex, int BatchIndex);
    private readonly record struct AlphaOrder(float DistanceSq, int Index);

    /// <summary>What the grouping reads out of a classified batch: which
    /// group the batch joins and where that group sorts. A batch transform is
    /// deliberately not part of it, because the append path reads the
    /// transform from the batch itself, so moving geometry cannot move a
    /// batch between groups.</summary>
    private readonly record struct BatchShape(
        GroupKey Key, uint DetailCategory, bool IsOpaque);

    /// <summary>The batch range a classified part contributes, which decides
    /// which batches the grouping visits and in what order.</summary>
    private readonly record struct PartShape(int BatchStart, int BatchCount);

    private sealed class Entity(RenderProjectionRecord record, uint cellId)
    {
        public RenderProjectionRecord Record = record;
        public readonly uint CellId = cellId;
        /// <summary>The scene's write revision of <see cref="Record"/> when it
        /// was classified; 0 when the world does not report revisions.</summary>
        public ulong Revision;
        /// <summary>Index of <see cref="CellId"/> within the owning entry's cells.</summary>
        public int CellIndex;
        public WalkDrawStage Stage;
        public readonly List<WbDrawDispatcher.WalkClassifiedBatch> Batches = new();
        public readonly List<WbDrawDispatcher.WalkCachedPart> Parts = new();
        /// <summary>The grouping shape the owning entry cached groups and
        /// alpha partition were built from; see <see cref="BatchShape"/>.</summary>
        public readonly List<BatchShape> BatchShapes = new();
        public readonly List<PartShape> PartShapes = new();
        public bool[] Visible = [];
        public WbDrawDispatcher.InstanceLightSet Lights;
        public uint Indoor;
        public Vector2 Selection;
    }

    private sealed class Entry(uint[] cells)
    {
        public readonly uint[] Cells = cells;
        public readonly ulong[] Revisions = new ulong[cells.Length];
        public readonly List<Entity> Entities = new();
        public readonly List<BatchRef> Opaque = new();
        public readonly List<BatchRef> Alpha = new();
        /// <summary><see cref="Alpha"/> partitioned by cell, in the same
        /// relative order; cell c owns [AlphaCellEnds[c], AlphaCellEnds[c + 1]).</summary>
        public readonly List<BatchRef> AlphaByCell = new();
        /// <summary>World-space sort center of each <see cref="AlphaByCell"/> batch.</summary>
        public readonly List<Vector3> AlphaWorldCenters = new();
        public readonly int[] AlphaCellEnds = new int[cells.Length + 1];
        public readonly Dictionary<GroupKey, List<BatchRef>> Groups = new();
        public readonly List<List<BatchRef>> GroupLists = new();
        public readonly List<int> GroupOrder = new();
        public long MeshVersion = -1;
        public bool Retry;
    }

    private readonly Dictionary<CellKey, Entry> _entries = new();
    private readonly List<CellKey> _expired = new();
    private readonly List<WbDrawDispatcher.WalkClassifiedSelectionPart> _selectionScratch = new();
    private readonly List<AlphaOrder> _alphaOrderScratch = new();
    private readonly int[] _alphaEnds = new int[16];
    private (RenderSceneGeneration Generation, uint TupleLandblockId) _context;

    internal int RebuildCount { get; private set; }
    /// <summary>How many times an entry groups and alpha partition were
    /// rebuilt. A reclassification that keeps its shape does not rebuild
    /// them.</summary>
    internal int RegroupCount { get; private set; }
    internal int EntityClassificationCount { get; private set; }
    internal int EntryCount => _entries.Count;
    internal ReadOnlySpan<int> AlphaEnds => _alphaEnds;

    internal void Clear()
    {
        _entries.Clear();
        _expired.Clear();
        _alphaOrderScratch.Clear();
    }

    internal void BeginFrame()
    {
        if (_context != world.RetainedContext)
        {
            Clear();
            _context = world.RetainedContext;
        }

        // Drop departed content even when the camera never revisits its cells.
        _expired.Clear();
        foreach ((CellKey key, Entry entry) in _entries)
        {
            bool populated = false;
            bool wasPopulated = false;
            foreach (ulong revision in entry.Revisions)
                wasPopulated |= revision != 0;
            if (!wasPopulated)
                continue;
            foreach (uint cell in entry.Cells)
            {
                if (world.GetOutdoorCellRenderRevision(cell) is > 0)
                {
                    populated = true;
                    break;
                }
            }
            if (!populated)
                _expired.Add(key);
        }
        foreach (CellKey key in _expired)
            _entries.Remove(key);
    }

    internal bool TryAppend(
        uint block, int side, int index,
        OrderedDrawStream stream, IWalkLookInViewSource views, int route,
        Vector3 camera, List<WbDrawDispatcher.WalkClassifiedBatch> alpha)
    {
        int span = 8 / side;
        int firstX = index / side * span;
        int firstY = index % side * span;
        uint firstCell = block | (uint)(firstX * 8 + firstY + 1);
        if (world.GetOutdoorCellRenderRevision(firstCell) is null)
            return false;

        CellKey key = new(block, side, index);
        if (!_entries.TryGetValue(key, out Entry? entry))
        {
            uint[] cells = new uint[span * span];
            int cursor = 0;
            for (int x = firstX; x < firstX + span; x++)
            for (int y = firstY; y < firstY + span; y++)
                cells[cursor++] = block | (uint)(x * 8 + y + 1);
            entry = new Entry(cells);
            _entries.Add(key, entry);
        }

        if (NeedsRebuild(entry))
            Rebuild(entry);
        else
            RefreshEntities(entry);

        foreach (Entity entity in entry.Entities)
        {
            dispatcher.ResolveCachedWalkLighting(
                in entity.Record, _context.TupleLandblockId,
                out entity.Lights, out entity.Indoor, out entity.Selection);
            for (int i = 0; i < entity.Parts.Count; i++)
            {
                ref readonly WbDrawDispatcher.WalkCachedPart part =
                    ref CollectionsMarshal.AsSpan(entity.Parts)[i];
                bool visible = dispatcher.AdmitCachedWalkPart(
                    in entity.Record, in part, views, route);
                entity.Visible[i] = visible;
                // The selection scene ignores parts without a server guid
                // (DAT statics cannot be picked), so they are not offered.
                if (visible && part.Selection.ServerGuid != 0u)
                {
                    var selection = part.Selection;
                    dispatcher.PublishWalkSelectionPart(in selection);
                }
            }
        }

        foreach (BatchRef item in entry.Opaque)
        {
            Entity entity = item.Entity;
            if (!entity.Visible[item.PartIndex])
                continue;
            ref readonly WbDrawDispatcher.WalkClassifiedBatch batch =
                ref CollectionsMarshal.AsSpan(entity.Batches)[item.BatchIndex];
            stream.Append(new OrderedDrawCommand(
                batch.Key, batch.Transform, entity.Stage, firstCell, batch.ClipSlot,
                entity.Lights, entity.Indoor, batch.Alpha,
                entity.Selection, batch.DetailCategory, AllowInstanceMerge: true));
        }

        // Per cell: order the visible alpha batches far-to-near by their
        // precomputed world sort center, sorting only (distance, index) pairs,
        // then copy each batch into the frame's alpha list exactly once.
        for (int cellIndex = 0; cellIndex < entry.Cells.Length; cellIndex++)
        {
            _alphaOrderScratch.Clear();
            int cellEnd = entry.AlphaCellEnds[cellIndex + 1];
            for (int k = entry.AlphaCellEnds[cellIndex]; k < cellEnd; k++)
            {
                BatchRef item = entry.AlphaByCell[k];
                if (!item.Entity.Visible[item.PartIndex])
                    continue;
                _alphaOrderScratch.Add(new AlphaOrder(
                    Vector3.DistanceSquared(entry.AlphaWorldCenters[k], camera), k));
            }
            _alphaOrderScratch.Sort(static (a, b) => b.DistanceSq.CompareTo(a.DistanceSq));
            foreach (AlphaOrder order in _alphaOrderScratch)
            {
                BatchRef item = entry.AlphaByCell[order.Index];
                Entity entity = item.Entity;
                ref readonly WbDrawDispatcher.WalkClassifiedBatch batch =
                    ref CollectionsMarshal.AsSpan(entity.Batches)[item.BatchIndex];
                alpha.Add(batch with
                {
                    Lights = entity.Lights,
                    IndoorFlag = entity.Indoor,
                    SelectionLighting = entity.Selection,
                    SortDistanceSq = order.DistanceSq,
                });
            }
            _alphaEnds[cellIndex] = alpha.Count;
        }
        return true;
    }

    private bool NeedsRebuild(Entry entry)
    {
        if (entry.Retry || entry.MeshVersion != dispatcher.WalkMeshAvailabilityVersion)
            return true;
        for (int i = 0; i < entry.Cells.Length; i++)
        {
            if (entry.Revisions[i] != world.GetOutdoorCellRenderRevision(entry.Cells[i]))
                return true;
        }
        return false;
    }

    private void Rebuild(Entry entry)
    {
        entry.Entities.Clear();
        entry.Opaque.Clear();
        entry.Alpha.Clear();
        entry.Retry = true;
        bool retry = false;
        entry.MeshVersion = dispatcher.WalkMeshAvailabilityVersion;
        var seen = new HashSet<RenderProjectionId>();
        for (int i = 0; i < entry.Cells.Length; i++)
        {
            uint cellId = entry.Cells[i];
            entry.Revisions[i] = world.GetOutdoorCellRenderRevision(cellId) ?? 0;
            WalkFrameStaticRecords records = world.GetOutdoorObjects(cellId);
            retry |= !records.IsComplete;
            foreach (RenderProjectionRecord record in records.Records)
            {
                if (!seen.Add(record.Id))
                    continue;
                var entity = new Entity(record, cellId)
                {
                    Revision = ReadRevision(record.Source.LocalEntityId),
                    CellIndex = i,
                    Stage = StageFor(in record),
                };
                retry |= ClassifyEntity(entity, records.TupleLandblockId, out _);
                entry.Entities.Add(entity);
            }
        }
        Regroup(entry);
        entry.Retry = retry;
        RebuildCount++;
    }

    private ulong ReadRevision(uint localEntityId) =>
        world.TryGetCurrentProjectionRevision(localEntityId, out _, out _, out ulong revision)
            ? revision
            : 0;

    private void RefreshEntities(Entry entry)
    {
        bool changed = false;
        bool regroup = false;
        bool retry = false;
        foreach (Entity entity in entry.Entities)
        {
            uint localEntityId = entity.Record.Source.LocalEntityId;
            if (entity.Revision != 0)
            {
                // The scene stamps every record write with a revision, so an
                // unchanged revision proves the record is bit-identical to the
                // one classified here; only changed or live records are read.
                if (!world.TryGetCurrentProjectionRevision(
                        localEntityId,
                        out RenderProjectionId id,
                        out RenderOwnerIncarnation ownerIncarnation,
                        out ulong revision)
                    || id != entity.Record.Id
                    || ownerIncarnation != entity.Record.OwnerIncarnation)
                {
                    Rebuild(entry);
                    return;
                }
                if (revision == entity.Revision && !IsDynamic(entity.Record))
                    continue;
                entity.Revision = revision;
            }
            if (!world.TryGetCurrentProjection(localEntityId, out var current)
                || current.Id != entity.Record.Id
                || current.OwnerIncarnation != entity.Record.OwnerIncarnation)
            {
                Rebuild(entry);
                return;
            }
            if (current == entity.Record && !IsDynamic(current))
                continue;
            entry.Retry = true;
            changed = true;
            entity.Record = current;
            entity.Stage = StageFor(in current);
            retry |= ClassifyEntity(
                entity, _context.TupleLandblockId, out bool shapeChanged);
            regroup |= shapeChanged;
        }
        if (changed)
        {
            // Reclassifying a moving entity normally reproduces the same
            // batches in the same order with new transforms: the cached
            // groups and the alpha partition still describe it exactly, and
            // only the alpha sort centres move. Rebuilding them walks every
            // entity in the entry (up to sixty-four cells), so it runs only
            // when a reclassification actually changed the shape.
            if (regroup)
                Regroup(entry);
            else
                RefreshAlphaCenters(entry);
            entry.Retry = retry;
        }
    }

    private static void RefreshAlphaCenters(Entry entry)
    {
        for (int k = 0; k < entry.AlphaByCell.Count; k++)
        {
            BatchRef item = entry.AlphaByCell[k];
            ref readonly WbDrawDispatcher.WalkClassifiedBatch batch =
                ref CollectionsMarshal.AsSpan(item.Entity.Batches)[item.BatchIndex];
            entry.AlphaWorldCenters[k] =
                Vector3.Transform(batch.LocalSortCenter, batch.Transform);
        }
    }

    private bool ClassifyEntity(
        Entity entity, uint tupleLandblockId, out bool shapeChanged)
    {
        entity.Batches.Clear();
        entity.Parts.Clear();
        _selectionScratch.Clear();
        dispatcher.ClassifyEntityForWalk(
            in entity.Record, tupleLandblockId,
            entity.Batches, _selectionScratch, liveDynamic: IsDynamic(entity.Record),
            retainedParts: entity.Parts);
        if (entity.Visible.Length < entity.Parts.Count)
            entity.Visible = new bool[entity.Parts.Count];
        shapeChanged = UpdateShape(entity);
        EntityClassificationCount++;
        return dispatcher.WalkClassificationPending;
    }

    /// <summary>Compares a fresh classification against the shape the entry
    /// grouping was built from, adopting it when it differs.</summary>
    private static bool UpdateShape(Entity entity)
    {
        ReadOnlySpan<WbDrawDispatcher.WalkCachedPart> parts =
            CollectionsMarshal.AsSpan(entity.Parts);
        ReadOnlySpan<WbDrawDispatcher.WalkClassifiedBatch> batches =
            CollectionsMarshal.AsSpan(entity.Batches);
        if (ShapeMatches(entity, parts, batches))
            return false;

        entity.PartShapes.Clear();
        for (int i = 0; i < parts.Length; i++)
        {
            entity.PartShapes.Add(
                new PartShape(parts[i].BatchStart, parts[i].BatchCount));
        }

        entity.BatchShapes.Clear();
        for (int i = 0; i < batches.Length; i++)
        {
            entity.BatchShapes.Add(new BatchShape(
                batches[i].Key, batches[i].DetailCategory, batches[i].IsOpaque));
        }

        return true;
    }

    private static bool ShapeMatches(
        Entity entity,
        ReadOnlySpan<WbDrawDispatcher.WalkCachedPart> parts,
        ReadOnlySpan<WbDrawDispatcher.WalkClassifiedBatch> batches)
    {
        if (entity.PartShapes.Count != parts.Length
            || entity.BatchShapes.Count != batches.Length)
        {
            return false;
        }

        ReadOnlySpan<PartShape> partShapes =
            CollectionsMarshal.AsSpan(entity.PartShapes);
        for (int i = 0; i < parts.Length; i++)
        {
            if (partShapes[i].BatchStart != parts[i].BatchStart
                || partShapes[i].BatchCount != parts[i].BatchCount)
            {
                return false;
            }
        }

        ReadOnlySpan<BatchShape> batchShapes =
            CollectionsMarshal.AsSpan(entity.BatchShapes);
        for (int i = 0; i < batches.Length; i++)
        {
            if (batchShapes[i].DetailCategory != batches[i].DetailCategory
                || batchShapes[i].IsOpaque != batches[i].IsOpaque
                || batchShapes[i].Key != batches[i].Key)
            {
                return false;
            }
        }

        return true;
    }

    private void Regroup(Entry entry)
    {
        RegroupCount++;
        entry.Opaque.Clear();
        entry.Alpha.Clear();
        entry.Groups.Clear();
        foreach (List<BatchRef> group in entry.GroupLists)
            group.Clear();
        int groupCount = 0;
        foreach (Entity entity in entry.Entities)
        {
            for (int partIndex = 0; partIndex < entity.Parts.Count; partIndex++)
            {
                var part = entity.Parts[partIndex];
                for (int b = part.BatchStart; b < part.BatchStart + part.BatchCount; b++)
                {
                    var batch = entity.Batches[b];
                    BatchRef item = new(entity, partIndex, b);
                    if (!batch.IsOpaque)
                    {
                        entry.Alpha.Add(item);
                        continue;
                    }
                    if (!entry.Groups.TryGetValue(batch.Key, out List<BatchRef>? group))
                    {
                        if (groupCount == entry.GroupLists.Count)
                            entry.GroupLists.Add(new List<BatchRef>());
                        group = entry.GroupLists[groupCount++];
                        entry.Groups.Add(batch.Key, group);
                    }
                    group.Add(item);
                }
            }
        }
        // Emit the groups so consecutive draws share a detail category (a
        // pipeline), a cull mode, and a translucency. The ordered stream
        // starts a new merge run whenever one of those changes, and every
        // run costs a bind plus an indirect submission. The groups are
        // opaque and depth-tested, so their order does not change the image.
        entry.GroupOrder.Clear();
        for (int i = 0; i < groupCount; i++)
            entry.GroupOrder.Add(i);
        SortGroupOrder(entry);
        foreach (int groupIndex in entry.GroupOrder)
            entry.Opaque.AddRange(entry.GroupLists[groupIndex]);

        // Partition the alpha batches by cell once, keeping their relative
        // order, so each cell's per-frame sort reads only its own slice.
        entry.AlphaByCell.Clear();
        entry.AlphaWorldCenters.Clear();
        entry.AlphaCellEnds[0] = 0;
        for (int cellIndex = 0; cellIndex < entry.Cells.Length; cellIndex++)
        {
            foreach (BatchRef item in entry.Alpha)
            {
                if (item.Entity.CellIndex != cellIndex)
                    continue;
                entry.AlphaByCell.Add(item);
                ref readonly WbDrawDispatcher.WalkClassifiedBatch batch =
                    ref CollectionsMarshal.AsSpan(item.Entity.Batches)[item.BatchIndex];
                entry.AlphaWorldCenters.Add(
                    Vector3.Transform(batch.LocalSortCenter, batch.Transform));
            }
            entry.AlphaCellEnds[cellIndex + 1] = entry.AlphaByCell.Count;
        }
    }

    // Insertion sort over the group indices: the counts are small, the
    // order is a total order (index breaks ties), and unlike the span sort
    // it never boxes the comparer, which keeps regroup frames allocation-free.
    private static void SortGroupOrder(Entry entry)
    {
        var comparer = new GroupOrderComparer(entry);
        Span<int> order = CollectionsMarshal.AsSpan(entry.GroupOrder);
        for (int i = 1; i < order.Length; i++)
        {
            int value = order[i];
            int j = i - 1;
            while (j >= 0 && comparer.Compare(order[j], value) > 0)
            {
                order[j + 1] = order[j];
                j--;
            }
            order[j + 1] = value;
        }
    }

    private readonly struct GroupOrderComparer(Entry entry)
    {
        public int Compare(int left, int right)
        {
            if (left == right)
                return 0;
            ref readonly WbDrawDispatcher.WalkClassifiedBatch a = ref First(left);
            ref readonly WbDrawDispatcher.WalkClassifiedBatch b = ref First(right);
            int order = a.DetailCategory.CompareTo(b.DetailCategory);
            if (order != 0)
                return order;
            order = ((int)a.Key.CullMode).CompareTo((int)b.Key.CullMode);
            if (order != 0)
                return order;
            order = ((int)a.Key.Translucency).CompareTo((int)b.Key.Translucency);
            return order != 0 ? order : left.CompareTo(right);
        }

        private ref readonly WbDrawDispatcher.WalkClassifiedBatch First(int groupIndex)
        {
            BatchRef item = entry.GroupLists[groupIndex][0];
            return ref CollectionsMarshal.AsSpan(item.Entity.Batches)[item.BatchIndex];
        }
    }

    private static WalkDrawStage StageFor(in RenderProjectionRecord record) =>
        IsDynamic(in record) ? WalkDrawStage.Dynamic : WalkDrawStage.OutdoorStatic;

    private static bool IsDynamic(in RenderProjectionRecord record) =>
        record.ProjectionClass is RenderProjectionClass.LiveDynamicRoot
            or RenderProjectionClass.EquippedChild;
}
