using AcDream.App.Rendering.Vfx;

namespace AcDream.App.Rendering.Scene;

internal enum DirectionalShadowCasterKind : byte
{
    OutdoorStatic,
    Building,
    AnimatedStatic,
    LiveDynamic,
    EquippedChild,
}

internal readonly record struct DirectionalShadowCaster(
    RenderProjectionRecord Projection,
    DirectionalShadowCasterKind Kind)
{
    public bool UsesCurrentAnimatedTransforms =>
        Projection.ProjectionClass
            is RenderProjectionClass.ActiveAnimatedStatic
            or RenderProjectionClass.LiveDynamicRoot
            or RenderProjectionClass.EquippedChild;
}

internal readonly struct DirectionalShadowChangedPose
{
    internal DirectionalShadowChangedPose(
        int casterIndex,
        in DirectionalShadowTransformSnapshot snapshot)
    {
        CasterIndex = casterIndex;
        Snapshot = snapshot;
    }

    internal readonly int CasterIndex;
    internal readonly DirectionalShadowTransformSnapshot Snapshot;
}

internal readonly record struct DirectionalShadowCasterClassDiagnostics(
    int TerrainCommands,
    int OutdoorStatics,
    int Buildings,
    int AnimatedStatics,
    int LocalPlayers,
    int RemotePlayers,
    int NonPlayerCreatures,
    int OtherLiveDynamics,
    int EquippedChildren);

internal readonly record struct DirectionalShadowCasterBuildStats(
    int SourceOutdoorStatics,
    int SourceOutdoorDynamics,
    int Accepted,
    int RejectedNotDrawable,
    int RejectedNotResident,
    int RejectedTransparent,
    int RejectedIndoor,
    int RejectedMissingMesh,
    int IndexCopies,
    int Classifications,
    int DynamicTransformRefreshes,
    bool TopologyRebuilt,
    int CopiedTransformChanges = 0,
    int DedupedChangedCasterSlots = 0,
    bool TransformJournalFullRefresh = false,
    int UpdateTransformChanges = 0,
    int UpdateAppearanceChanges = 0,
    int DynamicSynchronizationChanges = 0,
    int ActiveAnimatedStaticChanges = 0,
    int LiveDynamicRootChanges = 0,
    int EquippedChildChanges = 0,
    bool DensityBulkRefresh = false,
    int BatchedProjectionCopyCalls = 0,
    int ActiveSelected = 0)
{
    public DirectionalShadowCasterClassDiagnostics CasterClasses { get; init; }
}

internal sealed class DirectionalShadowCasterFrame
{
    private RenderProjectionRecord[] _outdoorStaticScratch = [];
    private RenderProjectionRecord[] _outdoorDynamicScratch = [];
    private DirectionalShadowCaster[] _casters = [];
    private bool[] _selectedCasters = [];

    // Each non-building caster's retail cell array, fetched once per
    // (caster set, membership revision) instead of once per caster per frame.
    private IReadOnlyList<uint>?[] _casterCells = [];
    private ulong _casterCellsBuildSequence;
    private ulong _casterCellsRevision;
    private IDirectionalShadowCellMembership? _casterCellsMembership;

    // The inputs of the last selection; identical inputs select identically.
    private readonly HashSet<uint> _lastVisibleCells = new();
    private bool _lastSelectionValid;
    private bool _lastHasCompletedWorldView;
    private int[] _refreshCasterSlots = [];
    private DirectionalShadowChangedPose[] _changedCasterPoses = [];
    private bool[] _changedCasterFlags = [];
    private RenderProjectionId[] _casterIds = [];
    private RenderProjectionClass[] _casterClasses = [];
    private RenderProjectionId[] _denseIdScratch = [];
    private RenderProjectionRecord[] _denseRecordScratch = [];
    private int[] _sortIndices = [];
    private ulong[] _sortKeys = [];
    private DirectionalShadowCaster[] _sortScratch = [];
    private readonly DirectionalShadowTransformSnapshot[] _transformChangeScratch =
        new DirectionalShadowTransformSnapshot[
            DirectionalShadowTransformChangeJournal.Capacity];
    private readonly Dictionary<RenderProjectionId, int> _refreshCasterSlotById = [];
    private int _casterCount;
    private int _refreshCasterSlotCount;
    private int _changedCasterPoseCount;
    private ulong _topologyRevision;
    private ulong _transformRevision;
    private DirectionalShadowTransformChanges _lastTransformChanges;
    private bool _lastDensityBulkRefresh;
    private int _lastBatchedProjectionCopyCalls;

    public RenderSceneGeneration Generation { get; private set; }

    public ulong BuildSequence { get; private set; }

    public ulong SelectionSequence { get; private set; }

    public ReadOnlySpan<DirectionalShadowCaster> Casters =>
        _casters.AsSpan(0, _casterCount);

    internal ReadOnlySpan<bool> SelectedCasters =>
        _selectedCasters.AsSpan(0, _casterCount);

    internal ReadOnlySpan<int> RefreshCasterSlots =>
        _refreshCasterSlots.AsSpan(0, _refreshCasterSlotCount);

    internal ReadOnlySpan<DirectionalShadowChangedPose> ChangedCasterPoses =>
        _changedCasterPoses.AsSpan(0, _changedCasterPoseCount);

    internal ulong TransformRevision => _transformRevision;

    public DirectionalShadowCasterBuildStats Stats { get; private set; }

    public long RetainedScratchBytes =>
        checked(
            (long)_outdoorStaticScratch.Length
                * System.Runtime.CompilerServices.Unsafe.SizeOf<RenderProjectionRecord>()
            + (long)_outdoorDynamicScratch.Length
                * System.Runtime.CompilerServices.Unsafe.SizeOf<RenderProjectionRecord>()
            + (long)_casters.Length
                * System.Runtime.CompilerServices.Unsafe.SizeOf<DirectionalShadowCaster>()
            + _selectedCasters.Length
            + (long)_casterCells.Length * IntPtr.Size
            + (long)_refreshCasterSlots.Length * sizeof(int)
            + (long)_changedCasterPoses.Length
                * System.Runtime.CompilerServices.Unsafe.SizeOf<
                    DirectionalShadowChangedPose>()
            + _changedCasterFlags.Length
            + (long)_casterIds.Length
                * System.Runtime.CompilerServices.Unsafe.SizeOf<
                    RenderProjectionId>()
            + (long)_casterClasses.Length
                * System.Runtime.CompilerServices.Unsafe.SizeOf<
                    RenderProjectionClass>()
            + (long)_denseIdScratch.Length
                * System.Runtime.CompilerServices.Unsafe.SizeOf<RenderProjectionId>()
            + (long)_denseRecordScratch.Length
                * System.Runtime.CompilerServices.Unsafe.SizeOf<RenderProjectionRecord>()
            + (long)_transformChangeScratch.Length
                * System.Runtime.CompilerServices.Unsafe.SizeOf<
                    DirectionalShadowTransformSnapshot>()
            + (long)_refreshCasterSlotById.EnsureCapacity(0)
                * (sizeof(int)
                    + System.Runtime.CompilerServices.Unsafe.SizeOf<
                        KeyValuePair<RenderProjectionId, int>>()));

    public void Build(in RenderSceneQuery query, bool allowTopologyRebuild = true)
    {
        ulong topologyRevision = query.DirectionalShadowTopologyRevision;
        bool current = BuildSequence != 0
            && Generation == query.Generation
            && _topologyRevision == topologyRevision;
        bool deferStale = !allowTopologyRebuild
            && !current
            && BuildSequence != 0
            && Generation == query.Generation
            && !RefreshRequiresFullCopy(in query);
        if (current || deferStale)
        {
            int refreshes = RefreshChangedTransforms(
                in query,
                tolerateStaleTopology: deferStale);
            Stats = Stats with
            {
                IndexCopies = 0,
                Classifications = 0,
                DynamicTransformRefreshes = refreshes,
                TopologyRebuilt = false,
                CopiedTransformChanges = _lastTransformChanges.Count,
                DedupedChangedCasterSlots = _changedCasterPoseCount,
                TransformJournalFullRefresh =
                    _lastTransformChanges.RequiresFullRefresh,
                DensityBulkRefresh = _lastDensityBulkRefresh,
                BatchedProjectionCopyCalls = _lastBatchedProjectionCopyCalls,
                UpdateTransformChanges =
                    _lastTransformChanges.UpdateTransformCount,
                UpdateAppearanceChanges =
                    _lastTransformChanges.UpdateAppearanceCount,
                DynamicSynchronizationChanges =
                    _lastTransformChanges.DynamicSynchronizationCount,
                ActiveAnimatedStaticChanges =
                    _lastTransformChanges.ActiveAnimatedStaticCount,
                LiveDynamicRootChanges =
                    _lastTransformChanges.LiveDynamicRootCount,
                EquippedChildChanges =
                    _lastTransformChanges.EquippedChildCount,
            };
            return;
        }

        RenderSceneIndexCounts counts = query.IndexCounts;
        EnsureCapacity(ref _outdoorStaticScratch, counts.OutdoorStatic);
        EnsureCapacity(ref _outdoorDynamicScratch, counts.OutdoorDynamic);
        int staticCount = query.CopyIndexTo(
            RenderSceneIndex.OutdoorStatic,
            _outdoorStaticScratch.AsSpan(0, counts.OutdoorStatic));
        int dynamicCount = query.CopyIndexTo(
            RenderSceneIndex.OutdoorDynamic,
            _outdoorDynamicScratch.AsSpan(0, counts.OutdoorDynamic));
        EnsureCapacity(ref _casters, checked(staticCount + dynamicCount));
        EnsureCapacity(ref _selectedCasters, checked(staticCount + dynamicCount));
        _casterCount = 0;

        int rejectedNotDrawable = 0;
        int rejectedNotResident = 0;
        int rejectedTransparent = 0;
        int rejectedIndoor = 0;
        int rejectedMissingMesh = 0;
        int outdoorStatics = 0;
        int buildings = 0;
        int animatedStatics = 0;
        int localPlayers = 0;
        int remotePlayers = 0;
        int nonPlayerCreatures = 0;
        int otherLiveDynamics = 0;
        int equippedChildren = 0;
        for (int i = 0; i < staticCount; i++)
            Add(_outdoorStaticScratch[i]);
        for (int i = 0; i < dynamicCount; i++)
            Add(_outdoorDynamicScratch[i]);

        SortCasters();
        int refreshCasterCount = 0;
        for (int casterIndex = 0; casterIndex < _casterCount; casterIndex++)
        {
            if (_casters[casterIndex].UsesCurrentAnimatedTransforms)
                refreshCasterCount++;
        }
        EnsureCapacity(ref _refreshCasterSlots, refreshCasterCount);
        EnsureCapacity(ref _changedCasterPoses, refreshCasterCount);
        EnsureCapacity(ref _changedCasterFlags, _casterCount);
        EnsureCapacity(ref _casterIds, _casterCount);
        EnsureCapacity(ref _casterClasses, _casterCount);
        EnsureCapacity(ref _denseIdScratch, refreshCasterCount);
        EnsureCapacity(ref _denseRecordScratch, refreshCasterCount);
        _refreshCasterSlotCount = 0;
        _changedCasterPoseCount = 0;
        _refreshCasterSlotById.Clear();
        _refreshCasterSlotById.EnsureCapacity(refreshCasterCount);
        for (int casterIndex = 0; casterIndex < _casterCount; casterIndex++)
        {
            _casterIds[casterIndex] = _casters[casterIndex].Projection.Id;
            _casterClasses[casterIndex] =
                _casters[casterIndex].Projection.ProjectionClass;
            if (_casters[casterIndex].UsesCurrentAnimatedTransforms)
            {
                _refreshCasterSlots[_refreshCasterSlotCount++] = casterIndex;
                _refreshCasterSlotById.Add(
                    _casterIds[casterIndex],
                    casterIndex);
            }
        }
        Generation = query.Generation;
        _topologyRevision = topologyRevision;
        _transformRevision = query.DirectionalShadowTransformRevision;
        _lastTransformChanges = default;
        _lastDensityBulkRefresh = false;
        _lastBatchedProjectionCopyCalls = 0;
        BuildSequence = checked(BuildSequence + 1);
        Stats = new DirectionalShadowCasterBuildStats(
            staticCount,
            dynamicCount,
            _casterCount,
            rejectedNotDrawable,
            rejectedNotResident,
            rejectedTransparent,
            rejectedIndoor,
            rejectedMissingMesh,
            IndexCopies: 2,
            Classifications: _casterCount,
            DynamicTransformRefreshes: 0,
            TopologyRebuilt: true)
        {
            CasterClasses = new DirectionalShadowCasterClassDiagnostics(
                TerrainCommands: 0,
                outdoorStatics,
                buildings,
                animatedStatics,
                localPlayers,
                remotePlayers,
                nonPlayerCreatures,
                otherLiveDynamics,
                equippedChildren),
        };
        return;

        void Add(in RenderProjectionRecord projection)
        {
            if ((projection.Flags & RenderProjectionFlags.Draw) == 0)
            {
                rejectedNotDrawable++;
                return;
            }
            if ((projection.Flags & RenderProjectionFlags.SpatiallyResident) == 0)
            {
                rejectedNotResident++;
                return;
            }
            if ((projection.Flags & RenderProjectionFlags.Translucent) != 0)
            {
                rejectedTransparent++;
                return;
            }
            if (projection.Source.ParentCellId != 0
                && InteriorEntityPartition.IsIndoorCellId(
                    projection.Source.ParentCellId))
            {
                rejectedIndoor++;
                return;
            }
            if (projection.MeshSet.MeshCount <= 0
                || projection.EntityPayload.MeshRefs is null
                || projection.EntityPayload.MeshRefs.Count == 0)
            {
                rejectedMissingMesh++;
                return;
            }

            DirectionalShadowCasterKind kind = Classify(in projection);
            _casters[_casterCount++] = new DirectionalShadowCaster(
                projection,
                kind);
            switch (kind)
            {
                case DirectionalShadowCasterKind.OutdoorStatic:
                    outdoorStatics++;
                    break;
                case DirectionalShadowCasterKind.Building:
                    buildings++;
                    break;
                case DirectionalShadowCasterKind.AnimatedStatic:
                    animatedStatics++;
                    break;
                case DirectionalShadowCasterKind.EquippedChild:
                    equippedChildren++;
                    break;
                case DirectionalShadowCasterKind.LiveDynamic:
                    switch (projection.EntityPayload.CasterIdentity)
                    {
                        case RenderCasterIdentityKind.LocalPlayer:
                            localPlayers++;
                            break;
                        case RenderCasterIdentityKind.RemotePlayer:
                            remotePlayers++;
                            break;
                        case RenderCasterIdentityKind.NonPlayerCreature:
                            nonPlayerCreatures++;
                            break;
                        default:
                            otherLiveDynamics++;
                            break;
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(kind), kind, "Unknown shadow caster kind.");
            }
        }
    }

    internal void Select(
        in RetailLandscapeVisibilityFrame visibility,
        IDirectionalShadowCellMembership membership)
    {
        ArgumentNullException.ThrowIfNull(membership);
        IReadOnlySet<uint> visible = visibility.CellIds
            ?? RetailLandscapeVisibilityFrame.None.CellIds;

        bool cellsCurrent = RefreshCasterCells(membership);
        if (cellsCurrent
            && _lastSelectionValid
            && _lastHasCompletedWorldView == visibility.HasCompletedWorldView
            && SameCells(visible, _lastVisibleCells))
        {
            // Same casters, same cell arrays, same visible cells: the
            // selection this would compute is the one already stored.
            SelectionSequence = checked(SelectionSequence + 1);
            return;
        }

        int selected = 0;
        for (int casterIndex = 0; casterIndex < _casterCount; casterIndex++)
        {
            ref readonly DirectionalShadowCaster caster =
                ref _casters[casterIndex];
            bool active = visibility.HasCompletedWorldView
                && SelectsCaster(in caster, _casterCells[casterIndex], visible);
            _selectedCasters[casterIndex] = active;
            if (active)
                selected++;
        }

        _lastVisibleCells.Clear();
        CopyCells(visible, _lastVisibleCells);
        _lastHasCompletedWorldView = visibility.HasCompletedWorldView;
        _lastSelectionValid = true;
        SelectionSequence = checked(SelectionSequence + 1);
        Stats = Stats with { ActiveSelected = selected };
    }

    /// <summary>Fetches every caster's retail cell array when the caster set
    /// or the membership changed; returns whether they were already current.</summary>
    private bool RefreshCasterCells(IDirectionalShadowCellMembership membership)
    {
        ulong revision = membership.Revision;
        if (BuildSequence != 0
            && _casterCellsBuildSequence == BuildSequence
            && _casterCellsRevision == revision
            && ReferenceEquals(_casterCellsMembership, membership))
        {
            return true;
        }

        EnsureCapacity(ref _casterCells, _casterCount);
        for (int casterIndex = 0; casterIndex < _casterCount; casterIndex++)
        {
            ref readonly DirectionalShadowCaster caster = ref _casters[casterIndex];
            _casterCells[casterIndex] =
                caster.Kind is not DirectionalShadowCasterKind.Building
                && membership.TryGetRetailCellArray(
                    caster.Projection.Source.LocalEntityId,
                    out IReadOnlyList<uint>? cells)
                && cells.Count != 0
                    ? cells
                    : null;
        }
        _casterCellsBuildSequence = BuildSequence;
        _casterCellsRevision = revision;
        _casterCellsMembership = membership;
        _lastSelectionValid = false;
        return false;
    }

    private static bool SameCells(IReadOnlySet<uint> visible, HashSet<uint> previous)
    {
        if (visible.Count != previous.Count)
            return false;
        if (visible is HashSet<uint> set)
        {
            foreach (uint cellId in set)
            {
                if (!previous.Contains(cellId))
                    return false;
            }
            return true;
        }
        foreach (uint cellId in visible)
        {
            if (!previous.Contains(cellId))
                return false;
        }
        return true;
    }

    private static void CopyCells(IReadOnlySet<uint> visible, HashSet<uint> destination)
    {
        if (visible is HashSet<uint> set)
        {
            foreach (uint cellId in set)
                destination.Add(cellId);
            return;
        }
        foreach (uint cellId in visible)
            destination.Add(cellId);
    }

    private static bool SelectsCaster(
        in DirectionalShadowCaster caster,
        IReadOnlyList<uint>? cells,
        IReadOnlySet<uint> visible)
    {
        if (caster.Kind is DirectionalShadowCasterKind.Building)
        {
            uint effectCellId = caster.Projection.Source.EffectCellId;
            return IsOutdoorLandCell(effectCellId)
                && visible.Contains(effectCellId);
        }

        if (cells is null)
            return false;

        for (int cellIndex = 0; cellIndex < cells.Count; cellIndex++)
        {
            uint cellId = cells[cellIndex];
            if (IsOutdoorLandCell(cellId) && visible.Contains(cellId))
                return true;
        }
        return false;
    }

    private static bool IsOutdoorLandCell(uint cellId)
    {
        uint low = cellId & 0xFFFFu;
        return low != 0u && low < 0x0100u;
    }

    private bool RefreshRequiresFullCopy(in RenderSceneQuery query)
    {
        if (query.DirectionalShadowTransformRevision == _transformRevision)
            return false;
        DirectionalShadowTransformChanges changes =
            query.CopyDirectionalShadowTransformChanges(
                _transformRevision,
                _transformChangeScratch);
        return changes.RequiresFullRefresh;
    }

    private int RefreshChangedTransforms(
        in RenderSceneQuery query,
        bool tolerateStaleTopology = false)
    {
        _changedCasterPoseCount = 0;
        ulong latest = query.DirectionalShadowTransformRevision;
        if (latest == _transformRevision)
        {
            _lastTransformChanges = new DirectionalShadowTransformChanges(
                latest,
                0,
                false);
            _lastDensityBulkRefresh = false;
            _lastBatchedProjectionCopyCalls = 0;
            return 0;
        }

        DirectionalShadowTransformChanges changes =
            query.CopyDirectionalShadowTransformChanges(
                _transformRevision,
                _transformChangeScratch);
        _lastTransformChanges = changes;
        if (changes.RequiresFullRefresh)
        {
            if (tolerateStaleTopology)
            {
                throw new InvalidOperationException(
                    "A stale-topology refresh cannot perform the dense full re-copy.");
            }
            _lastBatchedProjectionCopyCalls = 1;
            for (int index = 0; index < _refreshCasterSlotCount; index++)
            {
                int casterIndex = _refreshCasterSlots[index];
                _denseIdScratch[index] = _casters[casterIndex].Projection.Id;
            }
            int copied = query.CopyById(
                _denseIdScratch.AsSpan(0, _refreshCasterSlotCount),
                _denseRecordScratch.AsSpan(0, _refreshCasterSlotCount));
            if (copied != _refreshCasterSlotCount)
            {
                throw new InvalidOperationException(
                    "Dense directional-shadow refresh returned an incomplete record batch.");
            }
            for (int index = 0; index < _refreshCasterSlotCount; index++)
            {
                int casterIndex = _refreshCasterSlots[index];
                RefreshOne(in _denseRecordScratch[index], casterIndex);
                DirectionalShadowTransformSnapshot snapshot =
                    DirectionalShadowTransformSnapshot.Capture(
                        in _denseRecordScratch[index]);
                _changedCasterPoses[_changedCasterPoseCount++] =
                    new DirectionalShadowChangedPose(casterIndex, in snapshot);
            }
            _lastDensityBulkRefresh = false;
            _transformRevision = changes.LatestRevision;
            return _changedCasterPoseCount;
        }
        _lastBatchedProjectionCopyCalls = 0;

        try
        {
            ReadOnlySpan<DirectionalShadowTransformSnapshot> records =
                _transformChangeScratch.AsSpan(0, changes.Count);
            // Newest-first makes repeated publications of the same projection
            // resolve to the latest exact root/part payload without an ECS read.
            for (int index = records.Length - 1; index >= 0; index--)
            {
                if (!_refreshCasterSlotById.TryGetValue(
                        records[index].Id,
                        out int casterIndex)
                    || _changedCasterFlags[casterIndex])
                {
                    continue;
                }
                if (tolerateStaleTopology
                    && (records[index].Id != _casterIds[casterIndex]
                        || records[index].ProjectionClass
                            != _casterClasses[casterIndex]))
                {
                    continue;
                }
                _changedCasterFlags[casterIndex] = true;
                ValidateStablePose(in records[index], casterIndex);
                _changedCasterPoses[_changedCasterPoseCount++] =
                    new DirectionalShadowChangedPose(
                        casterIndex,
                        in records[index]);
            }
        }
        finally
        {
            for (int index = 0; index < _changedCasterPoseCount; index++)
            {
                _changedCasterFlags[
                    _changedCasterPoses[index].CasterIndex] = false;
            }
        }
        _lastDensityBulkRefresh = _refreshCasterSlotCount >= 64
            && _changedCasterPoseCount
                >= checked((_refreshCasterSlotCount * 3) / 4);
        _transformRevision = changes.LatestRevision;
        return _changedCasterPoseCount;
    }

    private void ValidateStablePose(
        in DirectionalShadowTransformSnapshot current,
        int casterIndex)
    {
        if (current.Id != _casterIds[casterIndex]
            || current.ProjectionClass != _casterClasses[casterIndex])
        {
            throw new InvalidOperationException(
                $"Stable directional-shadow topology changed caster "
                + $"{_casterIds[casterIndex]} identity or class.");
        }
    }

    private void RefreshOne(
        in RenderProjectionRecord current,
        int casterIndex)
    {
        DirectionalShadowCaster retained = _casters[casterIndex];
        if (current.Id != retained.Projection.Id
            || current.ProjectionClass != retained.Projection.ProjectionClass)
        {
            throw new InvalidOperationException(
                $"Stable directional-shadow topology changed caster "
                + $"{retained.Projection.Id} identity or class.");
        }
        _casters[casterIndex] = retained with { Projection = current };
    }

    private static DirectionalShadowCasterKind Classify(
        in RenderProjectionRecord projection)
    {
        if (projection.EntityPayload.IsBuildingShell)
            return DirectionalShadowCasterKind.Building;
        return projection.ProjectionClass switch
        {
            RenderProjectionClass.OutdoorStatic =>
                DirectionalShadowCasterKind.OutdoorStatic,
            RenderProjectionClass.ActiveAnimatedStatic =>
                DirectionalShadowCasterKind.AnimatedStatic,
            RenderProjectionClass.LiveDynamicRoot =>
                DirectionalShadowCasterKind.LiveDynamic,
            RenderProjectionClass.EquippedChild =>
                DirectionalShadowCasterKind.EquippedChild,
            _ => throw new InvalidOperationException(
                $"Outdoor shadow index carried unsupported {projection.ProjectionClass}."),
        };
    }

    // Every array sized here is rewritten from index zero right after, so
    // the refill policy may shrink it when the window got much smaller.
    private static void EnsureCapacity<T>(ref T[] values, int required) =>
        ScratchArrays.EnsureRefillCapacity(ref values, required, minimum: 4);

    private void SortCasters()
    {
        int count = _casterCount;
        EnsureCapacity(ref _sortIndices, count);
        EnsureCapacity(ref _sortKeys, count);
        EnsureCapacity(ref _sortScratch, count);
        for (int i = 0; i < count; i++)
        {
            _sortKeys[i] = _casters[i].Projection.SortKey.Value;
            _sortIndices[i] = i;
        }
        _sortIndices.AsSpan(0, count).Sort(
            new CasterIndexComparer(_sortKeys, _casters));
        for (int i = 0; i < count; i++)
            _sortScratch[i] = _casters[_sortIndices[i]];
        (_casters, _sortScratch) = (_sortScratch, _casters);
    }

    private readonly struct CasterIndexComparer(
        ulong[] keys,
        DirectionalShadowCaster[] casters) : IComparer<int>
    {
        public int Compare(int x, int y)
        {
            ulong left = keys[x];
            ulong right = keys[y];
            if (left != right)
                return left < right ? -1 : 1;
            int order = DirectionalShadowCasterComparer.Instance.Compare(
                casters[x],
                casters[y]);
            return order != 0 ? order : x.CompareTo(y);
        }
    }

    private sealed class DirectionalShadowCasterComparer
        : IComparer<DirectionalShadowCaster>
    {
        public static DirectionalShadowCasterComparer Instance { get; } = new();

        public int Compare(DirectionalShadowCaster left, DirectionalShadowCaster right)
        {
            int order = left.Projection.SortKey.Value.CompareTo(
                right.Projection.SortKey.Value);
            return order != 0
                ? order
                : left.Projection.Id.CompareTo(right.Projection.Id);
        }
    }
}
