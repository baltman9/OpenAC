using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using AcDream.Content;
using AcDream.Content.Navigation;
using AcDream.Content.Pak;
using AcDream.Core.Physics;
using AcDream.Core.World;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Options;
using DatReaderWriter.Types;
using DatEnvironment = DatReaderWriter.DBObjs.Environment;

namespace AcDream.Bake;

public sealed record NavigationBakeOptions
{
    public required string DatDirectory { get; init; }
    public required string OutputPath { get; init; }
    public int Threads { get; init; } = System.Environment.ProcessorCount;
    public HashSet<uint>? LandblockFilter { get; init; }
    public IBakeProgressSink? Progress { get; init; }
}

public sealed record NavigationBakeReport
{
    public required PakHeader Header { get; init; }
    public required int LandblockKeys { get; init; }
    public required int TileBlobs { get; init; }
    public required int EmptyLandblocks { get; init; }
    public required TimeSpan Elapsed { get; init; }
    public required long OutputBytes { get; init; }
    public IReadOnlyDictionary<PakAssetType, int> TypeCounts =>
        new Dictionary<PakAssetType, int>
        {
            [PakAssetType.NavigationTile] = LandblockKeys,
        };
}

/// <summary>Builds and transactionally publishes a navigation-only pak overlay.</summary>
public static class NavigationBakeRunner
{
    private const int BatchSize = 4;

    public static int Run(
        NavigationBakeOptions options,
        CancellationToken cancellationToken = default)
    {
        RunDetailed(options, cancellationToken);
        return 0;
    }

    public static NavigationBakeReport RunDetailed(
        NavigationBakeOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DatDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.OutputPath);
        if (options.Threads <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.Threads));
        ValidateFilter(options.LandblockFilter);
        cancellationToken.ThrowIfCancellationRequested();

        options.Progress?.Started(PakFormat.CurrentBakeToolVersion, options.OutputPath);
        var stopwatch = Stopwatch.StartNew();
        NavigationBakeReport report = BakeOutputTransaction.WriteValidateAndPublish(
            options.OutputPath,
            temporaryPath => RunCore(options, temporaryPath, cancellationToken),
            (temporaryPath, result) => BakeArtifactValidator.Validate(
                temporaryPath,
                result.Header,
                result.LandblockKeys,
                result.TypeCounts),
            cancellationToken);
        stopwatch.Stop();

        report = report with
        {
            Elapsed = stopwatch.Elapsed,
            OutputBytes = new FileInfo(options.OutputPath).Length,
        };
        options.Progress?.Completed(
            report.Header.BakeToolVersion,
            report.OutputBytes,
            failures: 0);
        return report;
    }

    private static NavigationBakeReport RunCore(
        NavigationBakeOptions options,
        string temporaryPath,
        CancellationToken cancellationToken)
    {
        using var dats = new DatCollection(options.DatDirectory, DatAccessType.Read);
        using var source = new DatCollectionAdapter(dats);
        Region region = source.Get<Region>(0x1300_0000u)
            ?? throw new InvalidDataException("region 0x13000000 is missing");
        float[] heightTable = region.LandDefs.LandHeightTable;
        if (heightTable.Length < 256)
            throw new InvalidDataException("the terrain height table is incomplete");

        uint[] available = dats.Cell.Tree
            .Where(static file => (file.Id & 0xFFFFu) == 0xFFFFu)
            .Select(static file => file.Id & 0xFFFF_0000u)
            .Distinct()
            .Order()
            .ToArray();
        var availableSet = available.ToHashSet();
        uint[] targets = options.LandblockFilter is null
            ? available
            : available.Where(options.LandblockFilter.Contains).ToArray();
        if (options.LandblockFilter is not null
            && targets.Length != options.LandblockFilter.Count)
        {
            uint missing = options.LandblockFilter.First(id => !availableSet.Contains(id));
            throw new InvalidDataException(
                $"landblock 0x{missing:X8} is absent from the cell DAT");
        }

        var header = new PakHeader
        {
            FormatVersion = PakFormat.CurrentFormatVersion,
            PortalIteration = (uint)dats.Portal.Iteration!.CurrentIteration,
            CellIteration = (uint)dats.Cell.Iteration!.CurrentIteration,
            HighResIteration = (uint)dats.HighRes.Iteration!.CurrentIteration,
            LanguageIteration = (uint)dats.Local.Iteration!.CurrentIteration,
            BakeToolVersion = PakFormat.CurrentBakeToolVersion,
        };

        int tileBlobs = 0;
        int emptyLandblocks = 0;
        long completed = 0;
        var stopwatch = Stopwatch.StartNew();
        using var writer = new PakWriter(temporaryPath, header);
        using var collisionSource = new DatPreparedCollisionSource(source);

        for (int batchStart = 0; batchStart < targets.Length; batchStart += BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            uint[] batch = targets.Skip(batchStart).Take(BatchSize).ToArray();
            uint[] haloIds = batch
                .SelectMany(NavigationDatGeometry.GetHaloIds)
                .Where(availableSet.Contains)
                .Distinct()
                .Order()
                .ToArray();
            var prepared = new ConcurrentDictionary<
                uint,
                NavigationGeometryLandblockInput>();

            Parallel.ForEach(
                haloIds,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = options.Threads,
                    CancellationToken = cancellationToken,
                },
                id => prepared[id] = NavigationDatGeometry.PrepareLandblock(
                    source,
                    collisionSource,
                    id,
                    heightTable,
                    cancellationToken));

            var results = new ConcurrentBag<(uint Id, PreparedNavigationTileSet Set)>();
            Parallel.ForEach(
                batch,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = options.Threads,
                    CancellationToken = cancellationToken,
                },
                id =>
                {
                    NavigationGeometryLandblockInput[] halo = NavigationDatGeometry.GetHaloIds(id)
                        .Where(prepared.ContainsKey)
                        .Select(haloId => prepared[haloId])
                        .OrderBy(static input => input.Landblock.LandblockId)
                        .ToArray();
                    NavigationGeometryChunk geometry = NavigationGeometryBuilder.Build(
                        id,
                        halo,
                        cancellationToken);
                    PreparedNavigationTileSet set = NavigationTileBuilder.Build(
                        geometry,
                        cancellationToken: cancellationToken);
                    results.Add((id, set));
                });

            foreach ((uint id, PreparedNavigationTileSet set) in results.OrderBy(r => r.Id))
            {
                byte[] payload = PreparedNavigationTileCodec.Serialize(set);
                writer.AddBlob(
                    PakKey.Compose(PakAssetType.NavigationTile, id),
                    payload);
                tileBlobs += set.Tiles.Count;
                if (set.Tiles.Count == 0)
                    emptyLandblocks++;
                completed++;
            }

            ReportProgress(options.Progress, completed, targets.Length, stopwatch.Elapsed);
        }

        writer.Finish();
        return new NavigationBakeReport
        {
            Header = header,
            LandblockKeys = targets.Length,
            TileBlobs = tileBlobs,
            EmptyLandblocks = emptyLandblocks,
            Elapsed = stopwatch.Elapsed,
            OutputBytes = 0,
        };
    }

    private static void ValidateFilter(HashSet<uint>? filter)
    {
        if (filter is null)
            return;
        foreach (uint id in filter)
        {
            if ((id & 0xFFFFu) != 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(filter),
                    id,
                    "navigation landblock filters must have a zero cell suffix");
            }
        }
    }

    private static void ReportProgress(
        IBakeProgressSink? progress,
        long completed,
        long total,
        TimeSpan elapsed)
    {
        double eta = completed == 0
            ? 0
            : elapsed.TotalSeconds / completed * (total - completed);
        progress?.Progress(
            "navigation",
            completed,
            total,
            failures: 0,
            elapsed.TotalSeconds,
            eta,
            Process.GetCurrentProcess().PrivateMemorySize64,
            GC.GetTotalMemory(forceFullCollection: false));
    }
}
