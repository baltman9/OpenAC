using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Reflection;
using AcDream.App.Streaming;
using AcDream.App.Tests.Architecture;
using AcDream.Content;
using AcDream.Core.World;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using SysEnvironment = System.Environment;
using DatReaderWriter.Enums;
using DatReaderWriter.Lib.IO;
using Xunit;

namespace AcDream.App.Tests.Streaming;

/// <summary>
/// The streaming worker and the frame thread share one content source that is
/// not safe to enter twice at once, so every read has to hold the content
/// gate. The worker used to hold the gate around a whole landblock build,
/// which made a creation message on the frame thread wait for the build; it
/// now holds the gate around each read. These pin both halves of that: no read
/// escapes the gate, and the build is the same either way.
/// </summary>
public sealed class LandblockBuildContentGateTests
{
    private static readonly float[] HeightTable = BuildHeightTable();

    [Fact]
    public void EveryReadOfABuildHoldsTheContentGate()
    {
        var gate = new object();
        var content = new GateAssertingContent(gate);
        var factory = new LandblockBuildFactory(
            content,
            NullPreparedCollisions.Instance,
            gate,
            HeightTable);

        LandblockBuild? far = factory.Build(new LandblockBuildRequest(
            0xAABBFFFFu,
            LandblockStreamJobKind.LoadFar,
            Generation: 1,
            new LandblockBuildOrigin(0xAA, 0xBB)));
        _ = factory.Build(new LandblockBuildRequest(
            0xAABBFFFFu,
            LandblockStreamJobKind.LoadNear,
            Generation: 1,
            new LandblockBuildOrigin(0xAA, 0xBB)));

        Assert.NotNull(far);
        Assert.True(content.Reads > 0, $"the build made {content.Reads} reads");
        Assert.Null(content.Ungated);
    }

    [Fact]
    public void EveryMemberOfTheGatedSourceTakesTheGate()
    {
        var gate = new object();
        var content = new GateAssertingContent(gate);
        var gated = (IDatReaderWriter)Activator.CreateInstance(
            typeof(LandblockBuildFactory).Assembly.GetType(
                "AcDream.App.Streaming.LockedContentReads",
                throwOnError: true)!,
            System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            args: [content, gate],
            culture: null)!;

        _ = gated.Get<LandBlock>(0xAABBFFFFu);
        _ = gated.TryGet<LandBlock>(0xAABBFFFFu, out _);
        _ = gated.GetAllIdsOfType<LandBlock>().ToList();
        byte[] buffer = new byte[4];
        _ = gated.TryGetFileBytes(0u, 0xAABBFFFFu, ref buffer, out _);
        _ = gated.ResolveId(0xAABBFFFFu).ToList();

        Assert.Equal(5, content.Reads);
        Assert.Null(content.Ungated);
    }

    [Fact]
    public void NoMethodOfTheBuildFactoryHoldsTheGateItself()
    {
        // The frame thread waits for one record, not for a build, because the
        // build never enters the gate on its own: only each read does.
        MethodInfo[] methods = typeof(LandblockBuildFactory).GetMethods(
            BindingFlags.Instance | BindingFlags.Static
            | BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.DeclaredOnly);
        Assert.NotEmpty(methods);
        foreach (MethodInfo method in methods)
        {
            Assert.DoesNotContain(
                CompiledCallGraph.Read(method),
                call => call.Target.DeclaringType == typeof(Monitor)
                    && call.Target.Name is nameof(Monitor.Enter)
                        or nameof(Monitor.TryEnter));
        }
    }

    [Fact]
    [Trait("Lane", "InstalledDat")]
    public void APerReadGatedBuildEqualsABuildUnderOneWholeBuildGate()
    {
        Assert.Equal(
            "1",
            SysEnvironment.GetEnvironmentVariable("ACDREAM_RUN_INSTALLED_DAT_TESTS"));
        string? directory = SysEnvironment.GetEnvironmentVariable("ACDREAM_DAT_DIR");
        string? package = SysEnvironment.GetEnvironmentVariable("ACDREAM_PAK_PATH");
        Assert.True(
            Directory.Exists(directory),
            "An explicit installed ACDREAM_DAT_DIR is required.");
        Assert.True(
            File.Exists(package),
            "An explicit validated ACDREAM_PAK_PATH is required.");

        using var dats = new BoundedTestDatCollection(directory!);
        var content = (IDatReaderWriter)dats;
        using var prepared = new AcDream.Content.PakPreparedAssetSource(package!, content);
        float[] heights = Assert.IsType<Region>(content.Get<Region>(0x13000000u))
            .LandDefs.LandHeightTable;
        var request = new LandblockBuildRequest(
            0xF418FFFFu,
            LandblockStreamJobKind.LoadNear,
            Generation: 1,
            new LandblockBuildOrigin(0xF4, 0x18));

        var perReadGate = new object();
        var perRead = new LandblockBuildFactory(content, prepared, perReadGate, heights);
        LandblockBuild first = Assert.IsType<LandblockBuild>(perRead.Build(request));

        // Monitor is re-entrant, so holding the gate around the whole call is
        // exactly the behaviour this replaced: every read still gated, and the
        // gate never released between them.
        var wholeBuildGate = new object();
        var wholeBuild = new LandblockBuildFactory(
            content,
            prepared,
            wholeBuildGate,
            heights);
        LandblockBuild second;
        lock (wholeBuildGate)
            second = Assert.IsType<LandblockBuild>(wholeBuild.Build(request));

        Assert.Equal(Digest(first), Digest(second));
    }

    private static string Digest(LandblockBuild build)
    {
        var text = new StringBuilder();
        var ci = CultureInfo.InvariantCulture;
        text.Append(ci, $"lb=0x{build.LandblockId:X8}");
        text.Append(ci, $" bounds={build.TerrainBounds.MaxZ:R}/{build.TerrainBounds.MinZ:R}");
        text.Append(ci, $" heights={Convert.ToHexString(build.Landblock.Heightmap.Height)}");
        text.Append(ci, $" entities={build.Landblock.Entities.Count}");
        foreach (WorldEntity entity in build.Landblock.Entities)
        {
            text.Append(ci, $"\n  id=0x{entity.Id:X8} src=0x{entity.SourceGfxObjOrSetupId:X8}");
            text.Append(ci, $" pos={entity.Position.X:R},{entity.Position.Y:R},{entity.Position.Z:R}");
            text.Append(ci, $" rot={entity.Rotation.X:R},{entity.Rotation.Y:R},{entity.Rotation.Z:R},{entity.Rotation.W:R}");
            text.Append(ci, $" scale={entity.Scale:R} cell=0x{entity.EffectCellId:X8}");
            foreach (MeshRef mesh in entity.MeshRefs)
                text.Append(ci, $" mesh=0x{mesh.GfxObjId:X8}@{mesh.PartTransform.GetHashCode()}");
        }

        if (build.EnvCells is { } cells)
        {
            text.Append(ci, $"\n envcells={string.Join(",", cells.VisibilityCells.Select(cell => cell.CellId.ToString("X8", ci)))}");
            text.Append(ci, $"\n shells={string.Join(",", cells.Shells.Select(shell => shell.CellId.ToString("X8", ci)))}");
            text.Append(ci, $"\n walkbuildings={cells.WalkBuildings.Length} walkz={cells.WalkMaxZ:R}/{cells.WalkMinZ:R}");
        }

        if (build.Collisions is { } collisions)
        {
            text.Append(ci, $"\n gfx={string.Join(",", collisions.GfxObjIds.Select(id => id.ToString("X8", ci)))}");
            text.Append(ci, $"\n setups={string.Join(",", collisions.SetupIds.Select(id => id.ToString("X8", ci)))}");
            text.Append(ci, $"\n cellstructs={string.Join(",", collisions.EnvCellIds.Select(id => id.ToString("X8", ci)))}");
        }

        return text.ToString();
    }

    private static float[] BuildHeightTable()
    {
        var table = new float[256];
        for (int i = 0; i < table.Length; i++)
            table[i] = i * 2f;
        return table;
    }

    private sealed class NullPreparedCollisions : IPreparedCollisionSource
    {
        public static readonly NullPreparedCollisions Instance = new();

        public PreparedAssetPresence ProbeCollision(
            AcDream.Content.Pak.PakAssetType type,
            uint sourceFileId) => PreparedAssetPresence.Missing;

        public PreparedCollisionReadResult<AcDream.Core.Physics.FlatGfxObjCollisionAsset>
            ReadGfxObjCollision(uint sourceFileId, CancellationToken cancellationToken = default)
            => PreparedCollisionReadResult<AcDream.Core.Physics.FlatGfxObjCollisionAsset>.Missing;

        public PreparedCollisionReadResult<AcDream.Core.Physics.FlatSetupCollision>
            ReadSetupCollision(uint sourceFileId, CancellationToken cancellationToken = default)
            => PreparedCollisionReadResult<AcDream.Core.Physics.FlatSetupCollision>.Missing;

        public PreparedCollisionReadResult<AcDream.Core.Physics.FlatCellStructureCollisionAsset>
            ReadCellStructureCollision(uint sourceFileId, CancellationToken cancellationToken = default)
            => PreparedCollisionReadResult<AcDream.Core.Physics.FlatCellStructureCollisionAsset>.Missing;

        public PreparedCollisionReadResult<AcDream.Core.Physics.FlatEnvCellTopology>
            ReadEnvCellTopology(uint sourceFileId, CancellationToken cancellationToken = default)
            => PreparedCollisionReadResult<AcDream.Core.Physics.FlatEnvCellTopology>.Missing;

        public PreparedCollisionSourceStats CollisionStats => default;

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Refuses any read taken without the content gate held, and answers the
    /// one record a far build needs.
    /// </summary>
    private sealed class GateAssertingContent : IDatReaderWriter
    {
        private readonly object _gate;
        private readonly LandBlock _landblock;

        public GateAssertingContent(object gate)
        {
            _gate = gate;
            var heights = new byte[81];
            for (int i = 0; i < heights.Length; i++)
                heights[i] = (byte)(i % 7);
            _landblock = new LandBlock { Id = 0xAABBFFFFu, Height = heights };
        }

        public int Reads { get; private set; }

        public string? Ungated { get; private set; }

        public Action? OnRead { get; set; }

        private void Enter(string member)
        {
            if (!Monitor.IsEntered(_gate))
                Ungated ??= member;
            Reads++;
            OnRead?.Invoke();
        }

        [return: MaybeNull]
        public T Get<T>(uint fileId) where T : IDBObj
        {
            return TryGet<T>(fileId, out var value) ? value : default;
        }

        public bool TryGet<T>(uint fileId, [MaybeNullWhen(false)] out T value)
            where T : IDBObj
        {
            Enter($"TryGet<{typeof(T).Name}>(0x{fileId:X8})");
            if (typeof(T) == typeof(LandBlock) && fileId == _landblock.Id)
            {
                value = (T)(IDBObj)_landblock;
                return true;
            }

            value = default;
            return false;
        }

        public string SourceDirectory => string.Empty;
        public IDatDatabase Portal => throw new NotSupportedException();
        public IDatDatabase Cell => throw new NotSupportedException();
        public IDatDatabase HighRes => throw new NotSupportedException();
        public IDatDatabase Language => throw new NotSupportedException();
        public IDatDatabase Local => throw new NotSupportedException();
        public ReadOnlyDictionary<uint, IDatDatabase> CellRegions =>
            new(new Dictionary<uint, IDatDatabase>());
        public ReadOnlyDictionary<uint, uint> RegionFileMap =>
            new(new Dictionary<uint, uint>());
        public int PortalIteration => 0;
        public int CellIteration => 0;
        public int HighResIteration => 0;
        public int LanguageIteration => 0;

        public IEnumerable<uint> GetAllIdsOfType<T>() where T : IDBObj
        {
            Enter($"GetAllIdsOfType<{typeof(T).Name}>");
            return Array.Empty<uint>();
        }

        public bool TryGetFileBytes(
            uint regionId,
            uint fileId,
            ref byte[] bytes,
            out int bytesRead)
        {
            Enter($"TryGetFileBytes(0x{fileId:X8})");
            bytesRead = 0;
            return false;
        }

        public IEnumerable<IDatReaderWriter.IdResolution> ResolveId(uint id)
        {
            Enter($"ResolveId(0x{id:X8})");
            return Array.Empty<IDatReaderWriter.IdResolution>();
        }

        public bool TrySave<T>(T obj, int iteration = 0) where T : IDBObj =>
            throw new NotSupportedException();

        public bool TrySave<T>(uint regionId, T obj, int iteration = 0) where T : IDBObj =>
            throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
