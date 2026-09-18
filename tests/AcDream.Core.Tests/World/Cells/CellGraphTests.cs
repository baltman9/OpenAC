using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.World.Cells;
using Xunit;

namespace AcDream.Core.Tests.World.Cells;

public class CellGraphTests
{
    private static TerrainSurface FlatTerrain() => new TerrainSurface(new byte[81], new float[256]);

    private static EnvCell Env(uint id) => new EnvCell(id, Matrix4x4.Identity, Matrix4x4.Identity,
        Vector3.Zero, new Vector3(10,10,10), System.Array.Empty<CellPortal>(),
        System.Array.Empty<uint>(), false, null);

    [Fact]
    public void GetVisible_ZeroId_ReturnsNull()
        => Assert.Null(new CellGraph().GetVisible(0u));

    [Fact]
    public void GetVisible_EnvId_ReturnsAddedEnvCell()
    {
        var g = new CellGraph();
        var env = Env(0xA9B40174u);
        g.Add(env);
        Assert.Same(env, g.GetVisible(0xA9B40174u));
    }

    [Fact]
    public void GetVisible_UnknownEnvId_ReturnsNull()
        => Assert.Null(new CellGraph().GetVisible(0xA9B40174u));

    [Fact]
    public void GetVisible_LandId_SynthesizesFromRegisteredTerrain()
    {
        var g = new CellGraph();
        g.RegisterTerrain(0xA9B40000u, FlatTerrain(), new Vector3(1000,2000,0));
        var cell = g.GetVisible(0xA9B40014u);
        var land = Assert.IsType<LandCell>(cell);
        Assert.Equal(2, land.Cx);
        Assert.Equal(3, land.Cy);
    }

    /// <summary>
    /// The land cells of a landblock are built on the first lookup that asks
    /// for them and follow the terrain currently registered: nothing before
    /// the terrain arrives, the same cell on every later lookup, nothing again
    /// once the landblock retires, and a cell built from the new terrain after
    /// a re-register. This fails if a lookup stops building the cell, if the
    /// cell is not cached, or if a replaced terrain leaves its cell behind.
    /// </summary>
    [Fact]
    public void GetVisible_LandCell_IsBuiltOnFirstLookupAndFollowsTheTerrain()
    {
        const uint prefix = 0xA9B40000u;
        const uint cellId = prefix | 0x14u;
        var origin = new Vector3(1000f, 2000f, 0f);
        var replacementOrigin = new Vector3(3000f, 4000f, 0f);
        var g = new CellGraph();

        Assert.Null(g.GetVisible(cellId));

        g.RegisterTerrain(prefix, FlatTerrain(), origin);
        ObjCell? first = g.GetVisible(cellId);
        LandCell built = Assert.IsType<LandCell>(first);
        Assert.Equal(2, built.Cx);
        Assert.Equal(3, built.Cy);
        Assert.Equal(origin, built.WorldTransform.Translation);
        Assert.Same(first, g.GetVisible(cellId));

        g.RemoveLandblock(prefix);
        Assert.Null(g.GetVisible(cellId));

        g.RegisterTerrain(prefix, FlatTerrain(), replacementOrigin);
        LandCell rebuilt = Assert.IsType<LandCell>(g.GetVisible(cellId));
        Assert.NotSame(built, rebuilt);
        Assert.Equal(replacementOrigin, rebuilt.WorldTransform.Translation);
    }

    /// <summary>
    /// Replacing a landblock's terrain without retiring it first must drop the
    /// cells cached from the terrain it replaced.
    /// </summary>
    [Fact]
    public void RegisterTerrain_Again_DropsTheCellCachedFromTheOldTerrain()
    {
        const uint prefix = 0xA9B40000u;
        const uint cellId = prefix | 0x01u;
        var g = new CellGraph();

        g.RegisterTerrain(prefix, FlatTerrain(), new Vector3(10f, 20f, 0f));
        LandCell before = Assert.IsType<LandCell>(g.GetVisible(cellId));

        g.RegisterTerrain(prefix, FlatTerrain(), new Vector3(50f, 60f, 0f));
        LandCell after = Assert.IsType<LandCell>(g.GetVisible(cellId));

        Assert.NotSame(before, after);
        Assert.Equal(new Vector3(50f, 60f, 0f), after.WorldTransform.Translation);
    }

    /// <summary>Every outdoor cell of a registered landblock answers, and only
    /// the sixty-four the landblock owns.</summary>
    [Fact]
    public void GetVisible_LandCells_CoverExactlyTheLandblocksSixtyFour()
    {
        const uint prefix = 0xA9B40000u;
        var g = new CellGraph();
        g.RegisterTerrain(prefix, FlatTerrain(), Vector3.Zero);

        for (uint low = 1u; low <= 0x40u; low++)
            Assert.IsType<LandCell>(g.GetVisible(prefix | low));

        Assert.Null(g.GetVisible(prefix | 0x00u));
        Assert.Null(g.GetVisible(prefix | 0x41u));
    }

    [Fact]
    public void GetVisible_LandId_NoTerrain_ReturnsNull()
        => Assert.Null(new CellGraph().GetVisible(0xA9B40014u));

    [Fact]
    public void RemoveLandblock_EvictsEnvAndTerrain()
    {
        var g = new CellGraph();
        g.Add(Env(0xA9B40174u));
        g.RegisterTerrain(0xA9B40000u, FlatTerrain(), Vector3.Zero);
        g.RemoveLandblock(0xA9B40000u);
        Assert.Null(g.GetVisible(0xA9B40174u));
        Assert.Null(g.GetVisible(0xA9B40014u));
    }

    [Fact]
    public void RemoveLandblock_ClearsCurrentCellForRetiredPrefixOnly()
    {
        var g = new CellGraph();
        var current = Env(0xA9B40174u);
        g.Add(current);
        g.CurrCell = current;

        g.RemoveLandblock(0xAAB40000u);
        Assert.Same(current, g.CurrCell);

        g.RemoveLandblock(0xA9B40000u);
        Assert.Null(g.CurrCell);
    }

    [Fact]
    public void RemoveEnvCellsForLandblock_PreservesTerrain()
    {
        var g = new CellGraph();
        g.Add(Env(0xA9B40174u));
        g.RegisterTerrain(0xA9B40000u, FlatTerrain(), Vector3.Zero);

        g.RemoveEnvCellsForLandblock(0xA9B4FFFFu);

        Assert.Null(g.GetVisible(0xA9B40174u));
        Assert.IsType<LandCell>(g.GetVisible(0xA9B40014u));
    }

    [Fact]
    public void RemoveEnvCellsForLandblock_ClearsOnlyMatchingIndoorCurrentCell()
    {
        var g = new CellGraph();
        var current = Env(0xA9B40174u);
        g.Add(current);
        g.CurrCell = current;

        g.RemoveEnvCellsForLandblock(0xAAB4FFFFu);
        Assert.Same(current, g.CurrCell);

        g.RemoveEnvCellsForLandblock(0xA9B4FFFFu);
        Assert.Null(g.CurrCell);
    }

    [Fact]
    public void Neighbor_ResolvesPortalOtherCellId()
    {
        var g = new CellGraph();
        var target = Env(0xA9B40175u);
        g.Add(target);
        var portal = new CellPortal(0xA9B40175u, 0, 0, 0);
        Assert.Same(target, g.Neighbor(Env(0xA9B40174u), portal));
    }

    [Fact]
    public void CurrCell_IsNull_InStage1()
        => Assert.Null(new CellGraph().CurrCell);
}
