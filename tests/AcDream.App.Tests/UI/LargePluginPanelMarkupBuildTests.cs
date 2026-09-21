using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.App.UI;
using Xunit;

namespace AcDream.App.Tests.UI;

/// <summary>
/// The markup engine over a realistic, large plugin panel set: nine tabs, a
/// wide multi-column table, anchored resize, and two resizable popups. The
/// fixture in <c>UI/fixtures/plugin-panel</c> and its binding source
/// (<see cref="SamplePluginPanelBindings"/>) belong to this test project, so
/// what is pinned here is the client's own engine rather than any plugin's
/// current panel.
/// </summary>
public sealed class LargePluginPanelMarkupBuildTests
{
    private static string FixtureDirectory =>
        Path.Combine(AppContext.BaseDirectory, "PluginPanel");

    public static IEnumerable<object[]> FixtureFiles() =>
        Directory.GetFiles(FixtureDirectory, "sample-panel*.xml")
            .OrderBy(static path => path, StringComparer.Ordinal)
            .Select(static path => new object[] { path });

    private static UiNineSlicePanel Build(string fileName, bool withIcons = false) =>
        MarkupDocument.Build(
            File.ReadAllText(Path.Combine(FixtureDirectory, fileName)),
            new SamplePluginPanelBindings(),
            static id => (id, 32, 32),
            icons: withIcons ? new IdentityIconResolver() : null);

    [Theory]
    [MemberData(nameof(FixtureFiles))]
    public void EveryPanelFileBuildsAgainstARealPanelWithNoException(string path)
    {
        string xml = File.ReadAllText(path);

        UiNineSlicePanel built = MarkupDocument.Build(
            xml, new SamplePluginPanelBindings(), static id => (id, 32, 32));

        Assert.NotNull(built);
        Assert.NotEmpty(built.Children);
    }

    [Fact]
    public void WideningTheMainPanelWidensTheWideTable()
    {
        UiNineSlicePanel built = Build("sample-panel.xml");

        UiPanel[] tabGroups = TabGroups(built);
        foreach (UiPanel group in tabGroups)
            group.Visible = false;
        UiPanel tableGroup = tabGroups[3];
        tableGroup.Visible = true;
        UiMarkupList table = Assert.Single(tableGroup.Children.OfType<UiMarkupList>());

        var device = new RecordingGpuDevice();
        var renderer = new TextRenderer(device, new NullGpuFrameSource(), "unused");
        renderer.Begin(new Vector2(1400f, 900f));
        var ctx = new UiRenderContext(renderer, new Vector2(1400f, 900f));

        built.DrawSelfAndChildren(ctx);
        float widthAtAuthoredDefault = table.Width;

        built.Width += 100f;
        built.DrawSelfAndChildren(ctx);

        Assert.True(
            table.Width > widthAtAuthoredDefault,
            $"Table width did not grow: {widthAtAuthoredDefault} -> {table.Width}");
    }

    [Fact]
    public void WideningTheResizablePopupGrowsItsOptionListWithoutOverlappingItsSibling()
    {
        UiNineSlicePanel built = Build("sample-panel-advanced.xml");

        Assert.True(built.Resizable);
        Assert.Equal(392f, built.MinWidth);
        Assert.Equal(300f, built.MinHeight);

        // The popup's own visibility is bound; force it visible, because
        // DrawSelfAndChildren's anchor pass never runs for an invisible
        // element.
        built.Visible = true;

        UiMarkupList optionList = Assert.Single(
            built.Children.OfType<UiMarkupList>(), static list => list.Width == 256f);
        UiMarkupList categoryList = Assert.Single(
            built.Children.OfType<UiMarkupList>(), static list => list.Width == 120f);

        var device = new RecordingGpuDevice();
        var renderer = new TextRenderer(device, new NullGpuFrameSource(), "unused");
        renderer.Begin(new Vector2(1400f, 900f));
        var ctx = new UiRenderContext(renderer, new Vector2(1400f, 900f));

        built.DrawSelfAndChildren(ctx);
        float optionListWidthBefore = optionList.Width;
        float optionListHeightBefore = optionList.Height;
        float categoryListLeftBefore = categoryList.Left;
        float categoryListWidthBefore = categoryList.Width;

        built.Width += 100f;
        built.DrawSelfAndChildren(ctx);

        Assert.True(
            optionList.Width > optionListWidthBefore,
            $"Option list width did not grow: {optionListWidthBefore} -> {optionList.Width}");
        Assert.Equal(optionListHeightBefore, optionList.Height); // height fixed
        Assert.True(
            categoryList.Left > categoryListLeftBefore,
            $"Category list did not track the growing right edge: {categoryListLeftBefore} -> {categoryList.Left}");
        Assert.Equal(categoryListWidthBefore, categoryList.Width); // width fixed, only repositions
        Assert.True(
            optionList.Left + optionList.Width <= categoryList.Left,
            $"Widened option list (right edge {optionList.Left + optionList.Width}) overlaps "
            + $"the repositioned category list (left edge {categoryList.Left}).");
    }

    /// <summary>A file that authors no <c>tooltip</c> anywhere must produce a
    /// tree with no tooltip anywhere: the engine never invents one.</summary>
    [Fact]
    public void APanelFileWithNoAuthoredTooltipBuildsNoTooltip()
    {
        UiNineSlicePanel built = Build("sample-panel-advanced.xml");

        AssertNoElementHasATooltip(built);
    }

    private static void AssertNoElementHasATooltip(UiElement element)
    {
        Assert.True(
            element.RuntimeTooltipTextSource is null,
            $"{element.GetType().Name} carries a tooltip the markup never authored.");
        foreach (UiElement child in element.Children)
            AssertNoElementHasATooltip(child);
    }

    [Theory]
    [InlineData(856f, 236f)] // the panel's own minw/minh floor
    [InlineData(1100f, 320f)] // one enlarged size past the authored default
    public void ResolvedMainPanelHasNoOverlapOrOutOfBoundsChildAtThisSize(
        float width, float height)
    {
        var device = new RecordingGpuDevice();
        var renderer = new TextRenderer(device, new NullGpuFrameSource(), "unused");
        renderer.Begin(new Vector2(1400f, 900f));
        var ctx = new UiRenderContext(renderer, new Vector2(1400f, 900f));

        for (int tabIndex = 0; tabIndex < 9; tabIndex++)
        {
            UiNineSlicePanel built = Build("sample-panel.xml");
            built.Visible = true;

            UiPanel[] tabGroups = TabGroups(built);
            foreach (UiPanel group in tabGroups)
                group.Visible = false;
            tabGroups[tabIndex].Visible = true;

            built.DrawSelfAndChildren(ctx);
            built.Width = width;
            built.Height = height;
            built.DrawSelfAndChildren(ctx);

            AssertResolvedWithinParent(built);
            AssertResolvedNoSiblingOverlap(built);
        }
    }

    private static UiPanel[] TabGroups(UiNineSlicePanel built)
    {
        UiPanel[] tabGroups = built.Children
            .Where(static child => child.GetType() == typeof(UiPanel))
            .Cast<UiPanel>()
            .ToArray();
        Assert.Equal(9, tabGroups.Length);
        return tabGroups;
    }

    private static void AssertResolvedWithinParent(UiElement parent)
    {
        foreach (UiElement child in parent.Children)
        {
            if (!child.Visible)
                continue;
            if (child is not UiLabel)
            {
                if (child.Width > 0f)
                {
                    Assert.True(
                        child.Left + child.Width <= parent.Width + 0.01f,
                        $"{child.GetType().Name} @ ({child.Left},{child.Top},{child.Width},"
                        + $"{child.Height}) crosses the right edge of {parent.GetType().Name} "
                        + $"(w={parent.Width}).");
                }
                if (child.Height > 0f)
                {
                    Assert.True(
                        child.Top + child.Height <= parent.Height + 0.01f,
                        $"{child.GetType().Name} @ ({child.Left},{child.Top},{child.Width},"
                        + $"{child.Height}) crosses the bottom edge of {parent.GetType().Name} "
                        + $"(h={parent.Height}).");
                }
            }
            AssertResolvedWithinParent(child);
        }
    }

    /// <summary>Two fixed-width icon columns side by side keep their authored
    /// pitch however wide the panel gets: only the auto-width column absorbs
    /// the extra room.</summary>
    [Theory]
    [InlineData(984f)]
    [InlineData(1100f)]
    public void AdjacentFixedWidthIconColumnsKeepTheirPitchAtEveryWidth(float width)
    {
        UiNineSlicePanel built = Build("sample-panel.xml", withIcons: true);

        UiPanel[] tabGroups = TabGroups(built);
        foreach (UiPanel group in tabGroups)
            group.Visible = false;
        tabGroups[3].Visible = true;

        var device = new RecordingGpuDevice();
        var renderer = new TextRenderer(device, new NullGpuFrameSource(), "unused");
        renderer.Begin(new Vector2(1400f, 900f));
        var ctx = new UiRenderContext(renderer, new Vector2(1400f, 900f));

        built.DrawSelfAndChildren(ctx);
        built.Width = width;
        built.DrawSelfAndChildren(ctx);

        var firstColumnQuad = renderer.DebugSpriteSegmentVerts
            .Last(static s => s.Texture == SamplePluginPanelBindings.FirstRowIconId);
        var secondColumnQuad = renderer.DebugSpriteSegmentVerts
            .Last(static s => s.Texture == SamplePluginPanelBindings.SecondRowIconId);

        float gap = secondColumnQuad.Verts[0] - firstColumnQuad.Verts[0];
        Assert.True(
            gap is >= 20f and <= 26f,
            $"The two icon columns are {gap}px apart at width {width} - expected the "
            + "authored ~23px column pitch, not the growing gap a still-last, "
            + "still-auto second column would produce.");
    }

    private static void AssertResolvedNoSiblingOverlap(UiElement container)
    {
        UiElement[] children = container.Children
            .Where(static child => child.Visible)
            .ToArray();
        for (int i = 0; i < children.Length; i++)
        {
            for (int j = i + 1; j < children.Length; j++)
            {
                UiElement a = children[i], b = children[j];
                if (a.GetType() == typeof(UiPanel) && b.GetType() == typeof(UiPanel))
                    continue;
                if (a is UiLabel || b is UiLabel)
                    continue;
                Assert.True(
                    !ResolvedRectanglesOverlap(a, b),
                    $"{a.GetType().Name} @ ({a.Left},{a.Top},{a.Width},{a.Height}) overlaps "
                    + $"sibling {b.GetType().Name} @ ({b.Left},{b.Top},{b.Width},{b.Height}).");
            }
        }
        foreach (UiElement child in children)
            AssertResolvedNoSiblingOverlap(child);
    }

    private static bool ResolvedRectanglesOverlap(UiElement a, UiElement b)
    {
        if (a.Width <= 0f || a.Height <= 0f || b.Width <= 0f || b.Height <= 0f)
            return false; // an element with no resolved size never "occupies" space
        return a.Left < b.Left + b.Width && b.Left < a.Left + a.Width
            && a.Top < b.Top + b.Height && b.Top < a.Top + a.Height;
    }

    private sealed class NullGpuFrameSource : ICurrentGpuFrameSource
    {
        public IGpuFrame? CurrentFrame => null;
    }

    private sealed class IdentityIconResolver : IMarkupIconResolver
    {
        public (uint tex, int w, int h) ResolveDid(uint did) =>
            did == 0u ? (0u, 0, 0) : (did, 16, 16);
        public (uint tex, int w, int h) ResolveSpell(uint spellId) =>
            spellId == 0u ? (0u, 0, 0) : (spellId, 16, 16);
        public (uint tex, int w, int h) ResolveItem(uint objectId) =>
            objectId == 0u ? (0u, 0, 0) : (objectId, 16, 16);
    }
}
