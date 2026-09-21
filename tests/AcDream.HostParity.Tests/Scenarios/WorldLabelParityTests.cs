using System.Numerics;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Plugins;

namespace AcDream.HostParity.Tests;

/// <summary>
/// A plugin hanging labels over things in the world. The set is kept by the
/// runtime's own surface on both clients, so a plugin gets the same answer
/// -- taken, refused past the cap, unusable entries dropped -- whether or
/// not there is a window to draw them in. A windowless client keeps the set
/// and never draws it; that is the whole of the difference, and it is not a
/// difference a plugin can see.
/// </summary>
public sealed class WorldLabelParityTests
{
    [Fact]
    public void PushingLabelsIsAnsweredTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            _ = ParityWorld.Stage(arm);
            IWorldLabelAutomation labels = arm.Host.Automation.Labels;

            transcript.Step("a usable set, with two entries to drop");
            bool taken = labels.ShowLabels(
            [
                new PluginWorldLabel(ParityWorld.Monster, "Drudge", Vector4.One),
                new PluginWorldLabel(ParityWorld.Monster, "12 m", Vector4.One, Line: 1),
                new PluginWorldLabel(0u, "nobody", Vector4.One),
                new PluginWorldLabel(ParityWorld.SecondMonster, "", Vector4.One),
            ]);
            transcript.Record("taken", taken);
            Assert.True(taken);
            Record(transcript, arm);

            transcript.Step("a set past the cap");
            PluginWorldLabel[] tooMany = Enumerable
                .Range(1, IWorldLabelAutomation.MaximumLabels + 1)
                .Select(static index => new PluginWorldLabel(
                    (uint)index, "n", Vector4.One))
                .ToArray();
            bool refused = labels.ShowLabels(tooMany);
            transcript.Record("taken", refused);
            Assert.False(refused);
            Record(transcript, arm);

            transcript.Step("an empty set clears");
            transcript.Record("taken", labels.ShowLabels([]));
            Record(transcript, arm);
        });

    /// <summary>
    /// What the drawing side of either client would read back: the same
    /// surface object a plugin pushed into, on both.
    /// </summary>
    private static void Record(ParityTranscript transcript, ParityArm arm)
    {
        var surface = Assert.IsType<RuntimeAutomationSurface>(arm.Host.Automation);
        IReadOnlyList<PluginWorldLabel> shown = surface.CaptureWorldLabels();
        transcript.Record("shown.count", shown.Count);
        for (int index = 0; index < shown.Count; index++)
        {
            transcript.Record(
                $"shown[{index}]",
                $"{shown[index].ObjectId:X8} '{shown[index].Text}' line {shown[index].Line}");
        }
    }
}
