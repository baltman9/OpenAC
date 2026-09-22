using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Navigation;
using AcDream.Runtime.Plugins;
using AcDream.Runtime.Tests.Maps;

namespace AcDream.Runtime.Tests.Plugins;

/// <summary>
/// The dungeon map area of the plugin surface: unbound it answers the inert
/// defaults, bound to the game files it tells a sealed cell and hands out
/// the plan the builder makes, and bound to a runtime it names the landblock
/// the character stands in.
/// </summary>
public sealed class DungeonMapAutomationTests
{
    [Fact]
    public void UnboundTheAreaAnswersTheDefaults()
    {
        using var surface = new RuntimeAutomationSurface();
        IDungeonMapAutomation map = surface.DungeonMap;

        Assert.False(map.IsSealedDungeon(TwoRoomLandblock.TurnedRoom));
        Assert.Equal(0u, map.CurrentLandblockId);
        Assert.True(map.CaptureFloorplan(TwoRoomLandblock.Landblock).IsEmpty);
    }

    [Fact]
    public void BoundToTheFilesItTellsASealedCellFromTheRest()
    {
        var dats = new TwoRoomLandblock();
        using var surface = new RuntimeAutomationSurface();
        surface.BindDungeonMap(dats, dats.Lock);
        IDungeonMapAutomation map = surface.DungeonMap;

        Assert.True(map.IsSealedDungeon(TwoRoomLandblock.TurnedRoom));
        Assert.False(map.IsSealedDungeon(TwoRoomLandblock.CellSeeingOutside));
        Assert.False(map.IsSealedDungeon(TwoRoomLandblock.Landblock | 0x0001u));
        Assert.False(map.IsSealedDungeon(TwoRoomLandblock.Landblock | 0x0104u));
        Assert.Equal(0, dats.ReadsOutsideTheLock);
    }

    [Fact]
    public void BoundToTheFilesItHandsOutThePlanAsPlainRecordsOnce()
    {
        var dats = new TwoRoomLandblock();
        using var surface = new RuntimeAutomationSurface();
        surface.BindDungeonMap(dats, dats.Lock);
        IDungeonMapAutomation map = surface.DungeonMap;

        PluginDungeonFloorplan plan = map.CaptureFloorplan(TwoRoomLandblock.UpstairsRoom);

        Assert.False(plan.IsEmpty);
        Assert.Equal(TwoRoomLandblock.Landblock, plan.LandblockId);
        Assert.Equal([0f, 6f], plan.Layers.Select(static layer => layer.Z).ToArray());
        Assert.Single(plan.Layers[0].Floors);
        Assert.Equal(3, plan.Layers[0].Walls.Count);
        Assert.Equal(4, plan.Layers[1].Walls.Count);
        Assert.Equal(
            [TwoRoomLandblock.TurnedRoom, TwoRoomLandblock.UpstairsRoom],
            plan.Cells.Select(static cell => cell.CellId).ToArray());
        Assert.Equal(new Vector3(12f, 30f, 0.5f), plan.BoundsMin);
        Assert.True(Vector3.Distance(new Vector3(50f, 40f, 10.2f), plan.BoundsMax) < 0.01f);

        Assert.Same(plan, map.CaptureFloorplan(TwoRoomLandblock.Landblock));
        Assert.True(map.CaptureFloorplan(TwoRoomLandblock.EmptyLandblock).IsEmpty);
        Assert.True(map.CaptureFloorplan(0x45670000u).IsEmpty);
        Assert.Equal(0, dats.ReadsOutsideTheLock);
    }

    [Fact]
    public void BoundToARuntimeItNamesTheLandblockTheCharacterStandsIn()
    {
        using GameRuntime runtime = GameRuntimeTestFactory.Create();
        using var surface = new RuntimeAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        IDungeonMapAutomation map = surface.DungeonMap;

        Assert.Equal(0u, map.CurrentLandblockId);

        var controller = new PlayerMovementController(new PhysicsEngine());
        controller.SeedPlacementForTest(
            new Vector3(16f, 35f, 0.5f),
            TwoRoomLandblock.TurnedRoom,
            new Vector3(16f, 35f, 0.5f));
        runtime.MovementOwner.Controller = controller;

        Assert.Equal(TwoRoomLandblock.Landblock, map.CurrentLandblockId);
    }

    [Fact]
    public void APositionConvertsToTheLandblockFrameTheSameWayTheRuntimeDoes()
    {
        var position = new PluginNavigationPosition(
            0xA9B40123u, 12.345d, -4.5d, 0.2d, 90f, false);

        Vector3 expected = RuntimeNavigationProjection.LandblockLocal(position);
        Vector3 actual = PluginDungeonFloorplan.ToLandblockLocal(position);

        Assert.True(Vector3.Distance(expected, actual) < 0.001f, $"{expected} vs {actual}");
    }
}
