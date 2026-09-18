using System.Numerics;
using System.Buffers.Binary;
using AcDream.Content;
using AcDream.Content.Navigation;
using DotRecast.Core.Numerics;
using DotRecast.Detour;
using DotRecast.Detour.Io;

namespace AcDream.Runtime.Gameplay;

/// <summary>Session-owned navigation mesh; the prepared source is borrowed from the host.</summary>
internal sealed class RuntimeNavigationState
{
    private IPreparedNavigationSource? _source;
    private IPreparedNavigationSource? _boundSource;
    private string? _datDirectory;
    private string? _cacheDirectory;
    private Task? _generationTask;
    private CancellationTokenSource? _generationCancellation;
    private int _generatedCount;
    private int _generationTotal;
    private string _generationMessage = "Not generated.";
    private RuntimeNavigationState? _preview;
    private RuntimeGenerationToken _generation;
    private NavigationPathQuery? _query;
    private (int MinX, int MinY, int MaxX, int MaxY)? _window;
    private (int MinX, int MinY, int MaxX, int MaxY)? _loadingWindow;
    private (int MinX, int MinY, int MaxX, int MaxY)? _failedWindow;
    private NavigationPathStatus _loadFailure;
    private Task<LoadedMesh>? _load;
    private CancellationTokenSource? _loadCancellation;
    private readonly List<Task> _retiringLoads = [];
    public long Revision { get; private set; }

    public bool IsQuiescent
    {
        get
        {
            for (int index = _retiringLoads.Count - 1; index >= 0; index--)
            {
                if (!_retiringLoads[index].IsCompleted)
                    continue;
                _ = _retiringLoads[index].Exception;
                _retiringLoads.RemoveAt(index);
            }
            return _retiringLoads.Count == 0 && (_load is null || _load.IsCompleted)
                && (_preview?.IsQuiescent ?? true);
        }
    }

    public void Bind(IPreparedNavigationSource? source)
    {
        if (ReferenceEquals(source, _boundSource))
            return;
        Reset();
        _boundSource = source;
        _source = source is null || _cacheDirectory is null ? source
            : new LocalNavigationSource(_cacheDirectory, source);
        _preview?.Bind(_source);
    }

    public void ConfigureLocalGeneration(string datDirectory, string? cacheDirectory = null)
    {
        _datDirectory = datDirectory;
        _cacheDirectory = cacheDirectory ?? LocalNavigationSource.CacheDirectory(datDirectory);
    }

    public bool GenerateNearby(RuntimeGenerationToken generation, uint cell)
    {
        PollGeneration();
        if (_generationTask is not null || !IsQuiescent || _datDirectory is null || _cacheDirectory is null || cell == 0)
            return false;
        if (_generation != generation) { Reset(); _generation = generation; }
        _generatedCount = 0;
        _generationTotal = 0;
        _generationMessage = "Generating nearby navigation data…";
        _generationCancellation = new CancellationTokenSource();
        var token = _generationCancellation.Token;
        string dat = _datDirectory, cache = _cacheDirectory;
        uint center = cell & 0xFFFF0000u;
        _generationTask = Task.Run(() => LocalNavigationSource.GenerateNearby(dat, cache, center,
            (done, total) => { Volatile.Write(ref _generatedCount, done); Volatile.Write(ref _generationTotal, total); }, token), token);
        return true;
    }

    public (bool Running, int Completed, int Total, string Message) GenerationStatus()
    {
        PollGeneration();
        return (_generationTask is not null, Volatile.Read(ref _generatedCount),
            Volatile.Read(ref _generationTotal), _generationMessage);
    }

    private void PollGeneration()
    {
        if (_generationTask is not { IsCompleted: true } task) return;
        try
        {
            task.GetAwaiter().GetResult();
            _generationMessage = "Nearby navdata saved and ready.";
            CancelLoad();
            _query = null; _window = null; _failedWindow = null;
            _preview?.Reset();
            Revision++;
        }
        catch (OperationCanceledException) { _generationMessage = "Navigation generation cancelled."; }
        catch (Exception error) { _generationMessage = $"Navigation generation failed: {error.Message}"; }
        _generationTask = null;
        _generationCancellation?.Dispose();
        _generationCancellation = null;
    }

    public void CancelGeneration()
    {
        if (_generationTask is not { } task) return;
        var cancellation = _generationCancellation;
        cancellation?.Cancel();
        _retiringLoads.Add(task);
        _ = task.ContinueWith(_ => cancellation?.Dispose(), TaskScheduler.Default);
        _generationTask = null;
        _generationCancellation = null;
        _generationMessage = "Navigation generation cancelled.";
    }

    public void Reset()
    {
        CancelGeneration();
        _preview?.Reset();
        CancelLoad();
        _query = null;
        _window = null;
        _failedWindow = null;
        _generation = default;
        Revision++;
    }

    public IReadOnlyList<(Vector3 Start, Vector3 End)> CapturePreview(
        RuntimeGenerationToken generation, uint cell, Vector3 position, bool enabled)
    {
        if (!enabled)
        {
            _preview?.Bind(null);
            return [];
        }
        _preview ??= new RuntimeNavigationState();
        _preview.Bind(_source);
        _preview.Find(generation, cell, cell, position, position, 1f, 1.5f);
        return _preview._query?.CaptureEdges(position) ?? [];
    }

    public NavigationPathResult Find(RuntimeGenerationToken generation,
        uint startCell, uint goalCell, Vector3 start, Vector3 goal,
        float horizontalTolerance, float verticalTolerance)
    {
        PollGeneration();
        if (startCell == 0 || !Finite(start) || !Finite(goal)
            || goal.X < 0 || goal.X >= 256 * 192 || goal.Y < 0 || goal.Y >= 256 * 192
            || !float.IsFinite(horizontalTolerance) || !float.IsFinite(verticalTolerance)
            || horizontalTolerance <= 0 || verticalTolerance <= 0)
            return Failed(NavigationPathStatus.InvalidPosition);
        if (_source is null)
            return Failed(NavigationPathStatus.Unavailable);
        if (_generation != generation)
        {
            Reset();
            _generation = generation;
        }
        int sx = (int)(startCell >> 24), sy = (int)((startCell >> 16) & 255u);
        // Coordinate-only route points have no asserted cell membership.
        int gx = goalCell == 0 ? (int)(goal.X / 192f) : (int)(goalCell >> 24);
        int gy = goalCell == 0 ? (int)(goal.Y / 192f) : (int)((goalCell >> 16) & 255u);
        var window = (MinX: Math.Max(0, Math.Min(sx, gx) - 1),
            MinY: Math.Max(0, Math.Min(sy, gy) - 1),
            MaxX: Math.Min(255, Math.Max(sx, gx) + 1),
            MaxY: Math.Min(255, Math.Max(sy, gy) + 1));
        if ((window.MaxX - window.MinX + 1) * (window.MaxY - window.MinY + 1) > 49)
            return Failed(NavigationPathStatus.CapacityExceeded);
        if (_failedWindow == window)
            return Failed(_loadFailure);
        if (_query is null || _window != window)
        {
            if (_load is not null && _loadingWindow != window)
                CancelLoad();
            if (_load is null)
            {
                if (!IsQuiescent)
                    return Failed(NavigationPathStatus.Loading);
                _query = null;
                _window = null;
                _loadingWindow = window;
                Revision++;
                _loadCancellation = new CancellationTokenSource();
                CancellationToken cancellation = _loadCancellation.Token;
                IPreparedNavigationSource source = _source;
                _load = Task.Run(() => Load(source, window, cancellation), cancellation);
                return Failed(NavigationPathStatus.Loading);
            }
            if (!_load.IsCompleted)
                return Failed(NavigationPathStatus.Loading);
            LoadedMesh loaded;
            try { loaded = _load.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { loaded = NavigationPathStatus.Unavailable; }
            catch (Exception error) when (error is IOException or ArgumentException
                or IndexOutOfRangeException or ObjectDisposedException or OverflowException)
            { loaded = NavigationPathStatus.CorruptTiles; }
            _load = null;
            _loadCancellation?.Dispose();
            _loadCancellation = null;
            _loadingWindow = null;
            if (loaded.Status != NavigationPathStatus.Complete)
            {
                _failedWindow = window;
                _loadFailure = loaded.Status;
                return Failed(loaded.Status);
            }
            _query = loaded.Query;
            _window = window;
        }
        NavigationPathResult result = _query!.Find(start, goal, horizontalTolerance, verticalTolerance);
        // Failure inside this bounded window does not establish global disconnection.
        return result.Status == NavigationPathStatus.Unreachable
            ? Failed(NavigationPathStatus.SearchLimitReached)
            : result;
    }

    private void CancelLoad()
    {
        _loadCancellation?.Cancel();
        if (_load is { } load)
        {
            _retiringLoads.Add(load);
            CancellationTokenSource? cancellation = _loadCancellation;
            _ = load.ContinueWith(_ => cancellation?.Dispose(), TaskScheduler.Default);
        }
        else
            _loadCancellation?.Dispose();
        _load = null;
        _loadCancellation = null;
        _loadingWindow = null;
    }

    private readonly record struct LoadedMesh(NavigationPathStatus Status, NavigationPathQuery? Query)
    {
        public static implicit operator LoadedMesh(NavigationPathStatus status) => new(status, null);
    }

    private static LoadedMesh Load(IPreparedNavigationSource source,
        (int MinX, int MinY, int MaxX, int MaxY) window, CancellationToken cancellation)
    {
        var sets = new List<PreparedNavigationTileSet>();
        for (int x = window.MinX; x <= window.MaxX; x++)
        for (int y = window.MinY; y <= window.MaxY; y++)
        {
            uint key = ((uint)x << 24) | ((uint)y << 16);
            cancellation.ThrowIfCancellationRequested();
            PreparedNavigationReadResult read = source.ReadNavigation(key, cancellation);
            if (read.Status == PreparedAssetReadStatus.Corrupt)
                return NavigationPathStatus.CorruptTiles;
            if (read.Status != PreparedAssetReadStatus.Loaded || read.Data is null)
                return NavigationPathStatus.MissingTiles;
            sets.Add(read.Data);
        }
        PreparedNavigationBuildParameters parameters = sets[0].Parameters;
        if (Math.Abs(parameters.CellSize * parameters.TileSizeCells - 192f) > 0.01f)
            return NavigationPathStatus.CorruptTiles;
        var mesh = new DtNavMesh();
        var config = new DtNavMeshParams
        {
            orig = new RcVec3f(0, 0, 0), tileWidth = 192f, tileHeight = 192f,
            maxTiles = 4096, maxPolys = 65536,
        };
        if (!mesh.Init(in config, parameters.VerticesPerPolygon).Succeeded())
            return NavigationPathStatus.CorruptTiles;
        try
        {
            foreach (PreparedNavigationTileSet set in sets)
            {
                if (set.Parameters != parameters)
                    return NavigationPathStatus.CorruptTiles;
                foreach (PreparedNavigationTileBlob blob in set.Tiles)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (!BoundedHeader(blob.Data.Span, parameters.VerticesPerPolygon))
                        return NavigationPathStatus.CorruptTiles;
                    using var stream = new MemoryStream(blob.Data.ToArray(), writable: false);
                    using var reader = new BinaryReader(stream);
                    DtMeshData data = new DtMeshDataReader().Read(reader, parameters.VerticesPerPolygon);
                    if (data.header.x != (int)(set.CanonicalLandblockId >> 24)
                        || data.header.y != (int)((set.CanonicalLandblockId >> 16) & 255u)
                        || data.header.layer < 0 || data.verts.Any(value => !float.IsFinite(value))
                        || !mesh.AddTile(data, 0, 0, out _).Succeeded())
                        return NavigationPathStatus.CorruptTiles;
                }
            }
        }
        catch (Exception error) when (error is IOException or ArgumentException or IndexOutOfRangeException)
        {
            return NavigationPathStatus.CorruptTiles;
        }
        cancellation.ThrowIfCancellationRequested();
        return new(NavigationPathStatus.Complete, new NavigationPathQuery(mesh));
    }

    private static NavigationPathResult Failed(NavigationPathStatus status) => new(status, []);
    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool BoundedHeader(ReadOnlySpan<byte> data, int verticesPerPolygon)
    {
        if (data.Length < 100)
            return false;
        // Both byte orders are accepted by the payload reader. Bound allocation
        // counts before calling it, even when the enclosing package CRC is valid.
        const int magic = ('D' << 24) | ('N' << 16) | ('A' << 8) | 'V';
        bool little = BinaryPrimitives.ReadInt32LittleEndian(data) == magic;
        if (!little && BinaryPrimitives.ReadInt32BigEndian(data) != magic)
            return false;
        Span<int> counts = stackalloc int[8];
        for (int index = 0; index < counts.Length; index++)
        {
            ReadOnlySpan<byte> field = data.Slice(24 + index * 4, 4);
            int value = little ? BinaryPrimitives.ReadInt32LittleEndian(field)
                : BinaryPrimitives.ReadInt32BigEndian(field);
            if (value < 0 || value > data.Length / 4)
                return false;
            counts[index] = value;
        }
        long minimumBytes = 100L + counts[1] * 12L
            + counts[0] * (verticesPerPolygon * 4L + 4L)
            + counts[3] * 10L + counts[4] * 12L + counts[5] * 4L
            + counts[6] * 16L + counts[7] * 36L;
        return counts[0] <= 65536 && counts[1] <= 65536 && minimumBytes <= data.Length;
    }
}
