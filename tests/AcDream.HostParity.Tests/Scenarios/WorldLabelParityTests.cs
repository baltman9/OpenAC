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
            // The two unusable entries are dropped; the two usable ones show.
            AssertShown(transcript, arm,
                $"{ParityWorld.Monster:X8} 'Drudge' line 0",
                $"{ParityWorld.Monster:X8} '12 m' line 1");

            transcript.Step("a set past the cap");
            PluginWorldLabel[] tooMany = Enumerable
                .Range(1, IWorldLabelAutomation.MaximumLabels + 1)
                .Select(static index => new PluginWorldLabel(
                    (uint)index, "n", Vector4.One))
                .ToArray();
            bool refused = labels.ShowLabels(tooMany);
            transcript.Record("taken", refused);
            Assert.False(refused);
            // A refused set leaves the labels already showing as they were.
            AssertShown(transcript, arm,
                $"{ParityWorld.Monster:X8} 'Drudge' line 0",
                $"{ParityWorld.Monster:X8} '12 m' line 1");

            transcript.Step("an empty set clears");
            bool cleared = labels.ShowLabels([]);
            transcript.Record("taken", cleared);
            Assert.True(cleared);
            AssertShown(transcript, arm);
        });

    /// <summary>
    /// What the drawing side of either client would read back: the same
    /// surface object a plugin pushed into, on both. Each arm is held to
    /// the expected set on its own; two clients agreeing on the wrong set
    /// would not be parity worth having.
    /// </summary>
    private static void AssertShown(
        ParityTranscript transcript,
        ParityArm arm,
        params string[] expected)
    {
        var surface = Assert.IsType<RuntimeAutomationSurface>(arm.Host.Automation);
        IReadOnlyList<PluginWorldLabel> shown = surface.CaptureWorldLabels();
        transcript.Record("shown.count", shown.Count);
        var lines = new string[shown.Count];
        for (int index = 0; index < shown.Count; index++)
        {
            lines[index] =
                $"{shown[index].ObjectId:X8} '{shown[index].Text}' line {shown[index].Line}";
            transcript.Record($"shown[{index}]", lines[index]);
        }
        Assert.Equal(expected, lines);
    }
}
