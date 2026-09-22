using System.Numerics;
using AcDream.Core.Plugins;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Plugins;

namespace AcDream.Runtime.Tests.Plugins;

/// <summary>
/// The label push, as the projectile markers are pushed: a plugin hands over
/// a whole set, the surface keeps a detached and validated copy, and the
/// drawing side reads the merged view back. The set is per owner, capped
/// per owner, and gone with the session.
/// </summary>
public sealed class WorldLabelAutomationTests
{
    private static PluginWorldLabel Label(uint id, string text = "name") =>
        new(id, text, Vector4.One);

    [Fact]
    public void ThePushedSetIsDetachedAndUnusableLabelsAreDropped()
    {
        using var surface = new RuntimeAutomationSurface();
        PluginWorldLabel[] source =
        [
            Label(0x7000_0001u),
            Label(0u),
            Label(0x7000_0002u, ""),
            Label(0x7000_0003u) with { MaxRange = 0f },
            Label(0x7000_0004u) with { MaxRange = float.PositiveInfinity },
            Label(0x7000_0005u) with { MaxRange = float.NaN },
            Label(0x7000_0006u) with { HeightOffset = float.NaN },
            Label(0x7000_0007u) with { MaxRange = 12f, Line = 1 },
            // Text is laid out every frame, so it is capped at the boundary.
            Label(0x7000_0008u, new string('x', IWorldLabelAutomation.MaximumTextLength + 1)),
            Label(0x7000_0009u, new string('x', IWorldLabelAutomation.MaximumTextLength)),
            Label(0x7000_000Au) with { Color = new Vector4(float.NaN, 1f, 1f, 1f) },
            Label(0x7000_000Bu) with { Color = new Vector4(1f, 1f, 1f, float.PositiveInfinity) },
        ];

        Assert.True(surface.Labels.ShowLabels(source));
        source[0] = default;

        IReadOnlyList<PluginWorldLabel> shown = surface.CaptureWorldLabels();
        Assert.Equal(
            [0x7000_0001u, 0x7000_0007u, 0x7000_0009u],
            shown.Select(static label => label.ObjectId).ToArray());
        Assert.Equal(1, shown[1].Line);
    }

    [Fact]
    public void ASetPastTheCapIsRefusedAndTheOldSetStays()
    {
        using var surface = new RuntimeAutomationSurface();
        Assert.True(surface.Labels.ShowLabels([Label(0x7000_0001u)]));

        PluginWorldLabel[] tooMany = Enumerable
            .Range(1, IWorldLabelAutomation.MaximumLabels + 1)
            .Select(static index => Label((uint)index))
            .ToArray();
        Assert.False(surface.Labels.ShowLabels(tooMany));

        PluginWorldLabel only = Assert.Single(surface.CaptureWorldLabels());
        Assert.Equal(0x7000_0001u, only.ObjectId);

        PluginWorldLabel[] atTheCap = tooMany[..IWorldLabelAutomation.MaximumLabels];
        Assert.True(surface.Labels.ShowLabels(atTheCap));
        Assert.Equal(IWorldLabelAutomation.MaximumLabels, surface.CaptureWorldLabels().Count);
    }

    [Fact]
    public void EachOwnerReplacesOnlyItsOwnSetAndTheCapIsPerOwner()
    {
        using var surface = new RuntimeAutomationSurface();
        IScopedWorldLabelSource scoped = surface;
        IWorldLabelAutomation first = scoped.ScopeTo("first.plugin");
        IWorldLabelAutomation second = scoped.ScopeTo("second.plugin");

        PluginWorldLabel[] full = Enumerable
            .Range(1, IWorldLabelAutomation.MaximumLabels)
            .Select(static index => Label((uint)index))
            .ToArray();
        Assert.True(first.ShowLabels(full));
        Assert.True(second.ShowLabels(full));
        Assert.Equal(
            2 * IWorldLabelAutomation.MaximumLabels,
            surface.CaptureWorldLabels().Count);

        Assert.True(first.ShowLabels([Label(0x7000_0001u, "one")]));
        IReadOnlyList<PluginWorldLabel> shown = surface.CaptureWorldLabels();
        Assert.Equal(1 + IWorldLabelAutomation.MaximumLabels, shown.Count);
        Assert.Contains(shown, static label => label.Text == "one");

        Assert.True(second.ShowLabels([]));
        PluginWorldLabel only = Assert.Single(surface.CaptureWorldLabels());
        Assert.Equal("one", only.Text);
    }

    [Fact]
    public void ReleasingAnOwnerTakesItsLabelsDownAndUnbindTakesEveryOnesDown()
    {
        using var surface = new RuntimeAutomationSurface();
        IScopedWorldLabelSource scoped = surface;
        Assert.True(scoped.ScopeTo("first.plugin").ShowLabels([Label(0x7000_0001u)]));
        Assert.True(scoped.ScopeTo("second.plugin").ShowLabels([Label(0x7000_0002u)]));
        Assert.True(surface.Labels.ShowLabels([Label(0x7000_0003u)]));
        Assert.Equal(3, surface.CaptureWorldLabels().Count);

        scoped.Release("first.plugin");
        Assert.DoesNotContain(
            surface.CaptureWorldLabels(),
            static label => label.ObjectId == 0x7000_0001u);
        Assert.Equal(2, surface.CaptureWorldLabels().Count);

        surface.Unbind();
        Assert.Empty(surface.CaptureWorldLabels());
    }

    [Fact]
    public void TheMergedViewIsSharedUntilASetChanges()
    {
        using var surface = new RuntimeAutomationSurface();
        Assert.True(surface.Labels.ShowLabels([Label(0x7000_0001u)]));

        IReadOnlyList<PluginWorldLabel> once = surface.CaptureWorldLabels();
        IReadOnlyList<PluginWorldLabel> again = surface.CaptureWorldLabels();
        Assert.Same(once, again);

        Assert.True(surface.Labels.ShowLabels([Label(0x7000_0002u)]));
        Assert.NotSame(once, surface.CaptureWorldLabels());
    }
}
