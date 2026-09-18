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

namespace AcDream.Content.Navigation;

public static class NavigationDatGeometry
{
    public static NavigationGeometryLandblockInput PrepareLandblock(
        IDatReaderWriter dats,
        IPreparedCollisionSource collisionSource,
        uint landblockId,
        float[] heightTable,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        uint terrainRecordId = landblockId | 0xFFFFu;
        LoadedLandblock sourceLandblock = LandblockLoader.Load(dats, terrainRecordId)
            ?? throw new InvalidDataException(
                $"landblock terrain record 0x{terrainRecordId:X8} could not be loaded");
        LoadedLandblock baseLandblock = sourceLandblock with
        {
            LandblockId = landblockId,
        };
        IReadOnlyList<WorldEntity> statics =
            LandblockPhysicsContentBuilder.HydrateStaticEntities(
                dats,
                baseLandblock,
                Vector3.Zero);
        IReadOnlyList<WorldEntity> scenery =
            LandblockPhysicsContentBuilder.HydrateProceduralScenery(
                dats,
                baseLandblock,
                Vector3.Zero,
                heightTable);
        WorldEntity[] entities = statics.Concat(scenery).ToArray();
        PhysicsDatBundle bundle = LandblockPhysicsContentBuilder.BuildDatBundle(
            dats,
            landblockId,
            entities);
        LoadedLandblock loaded = baseLandblock with
        {
            Entities = entities,
            PhysicsDats = bundle,
        };
        TerrainSurface terrain = LandblockPhysicsContentBuilder.BuildTerrainSurface(
            loaded,
            heightTable);
        LandblockCollisionBuild collisions =
            LandblockPhysicsContentBuilder.BuildPreparedCollisionClosure(
                collisionSource,
                loaded);
        float originX = ((landblockId >> 24) & 0xFFu) * 192f;
        float originY = ((landblockId >> 16) & 0xFFu) * 192f;
        return new NavigationGeometryLandblockInput(
            loaded,
            terrain,
            entities,
            collisions,
            new Vector3(originX, originY, 0f));
    }

    public static IEnumerable<uint> GetHaloIds(uint center)
    {
        int centerX = (int)(center >> 24);
        int centerY = (int)((center >> 16) & 0xFFu);
        for (int x = Math.Max(0, centerX - 1); x <= Math.Min(255, centerX + 1); x++)
        {
            for (int y = Math.Max(0, centerY - 1); y <= Math.Min(255, centerY + 1); y++)
                yield return ((uint)x << 24) | ((uint)y << 16);
        }
    }

}

public sealed class DatPreparedCollisionSource(IDatReaderWriter dats)
    : IPreparedCollisionSource
{
    public PreparedCollisionSourceStats CollisionStats => default;

    public PreparedAssetPresence ProbeCollision(PakAssetType type, uint sourceFileId) =>
        PreparedAssetPresence.Available;

    public PreparedCollisionReadResult<FlatGfxObjCollisionAsset> ReadGfxObjCollision(
        uint sourceFileId,
        CancellationToken cancellationToken = default) =>
        Read(
            () => dats.Get<GfxObj>(sourceFileId) is { } value
                ? FlatCollisionAssetBuilder.FlattenGfxObj(value)
                : null,
            cancellationToken);

    public PreparedCollisionReadResult<FlatSetupCollision> ReadSetupCollision(
        uint sourceFileId,
        CancellationToken cancellationToken = default) =>
        Read(
            () => dats.Get<Setup>(sourceFileId) is { } value
                ? FlatCollisionAssetBuilder.FlattenSetup(value)
                : null,
            cancellationToken);

    public PreparedCollisionReadResult<FlatCellStructureCollisionAsset>
        ReadCellStructureCollision(
            uint sourceFileId,
            CancellationToken cancellationToken = default) =>
        Read(
            () => TryGetCellStructure(sourceFileId, out _, out CellStruct? structure)
                ? FlatCollisionAssetBuilder.FlattenCellStructure(structure!)
                : null,
            cancellationToken);

    public PreparedCollisionReadResult<FlatEnvCellTopology> ReadEnvCellTopology(
        uint sourceFileId,
        CancellationToken cancellationToken = default) =>
        Read(
            () => TryGetCellStructure(
                    sourceFileId,
                    out EnvCell? envCell,
                    out CellStruct? structure)
                ? FlatCollisionAssetBuilder.FlattenEnvCellTopology(
                    sourceFileId,
                    envCell!,
                    FlatCollisionAssetBuilder
                        .FlattenCellStructure(structure!)
                        .PortalPolygons)
                : null,
            cancellationToken);

    public void Dispose()
    {
    }

    private bool TryGetCellStructure(
        uint sourceFileId,
        out EnvCell? envCell,
        out CellStruct? structure)
    {
        envCell = dats.Get<EnvCell>(sourceFileId);
        structure = null;
        if (envCell is null)
            return false;
        uint environmentId = 0x0D00_0000u | envCell.EnvironmentId;
        DatEnvironment? environment = dats.Get<DatEnvironment>(environmentId);
        return environment is not null
            && environment.Cells.TryGetValue(envCell.CellStructure, out structure)
            && structure is not null;
    }

    private static PreparedCollisionReadResult<T> Read<T>(
        Func<T?> read,
        CancellationToken cancellationToken)
        where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            T? value = read();
            cancellationToken.ThrowIfCancellationRequested();
            return value is null
                ? PreparedCollisionReadResult<T>.Missing
                : PreparedCollisionReadResult<T>.Loaded(value);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return PreparedCollisionReadResult<T>.Corrupt;
        }
    }
}
