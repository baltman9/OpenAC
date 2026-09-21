using AcDream.Plugin.Abstractions;

namespace AcDream.HostParity.Tests;

/// <summary>
/// A plugin asking where the character is and what shape the place has.
/// The area is the runtime's own on both clients, so the landblock comes
/// from the same body and, with no lease on the game files in either arm,
/// the sealed-cell question and the plan are answered the same inert way.
/// </summary>
public sealed class DungeonMapParityTests
{
    [Fact]
    public void TheDungeonMapIsAnsweredTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            _ = ParityWorld.Stage(arm);
            IDungeonMapAutomation map = arm.Host.Automation.DungeonMap;

            transcript.Step("the landblock the character stands in");
            uint landblock = map.CurrentLandblockId;
            transcript.Record("landblock", landblock);
            Assert.Equal(ParityPlayerBody.Landblock, landblock);

            transcript.Step("a sealed cell, without the files to say");
            transcript.Record("sealed", map.IsSealedDungeon(ParityPlayerBody.Cell));

            transcript.Step("the plan, without the files to read");
            PluginDungeonFloorplan plan = map.CaptureFloorplan(landblock);
            transcript.Record("empty", plan.IsEmpty);
            transcript.Record("layers", plan.Layers.Count);
        });
}
