using AcDream.App.Rendering;
using AcDream.App.UI;

namespace AcDream.App.Tests.UI;

public sealed class RetailCursorManagerTests
{
    private static readonly UiCursorMedia WidgetA = new(0x06001001u, 1, 2);
    private static readonly UiCursorMedia WidgetB = new(0x06001002u, 3, 4);

    [Fact]
    public void InitialState_appliesGlobalDefaultThenWidgetOverride()
    {
        var plan = RetailCursorManager.PlanApplication(
            hasLayerState: false,
            previousGlobal: default,
            previousWidget: default,
            currentGlobal: RetailGlobalCursorKind.Default,
            currentWidget: WidgetA);

        Assert.Equal(new[] { RetailCursorLayer.Global, RetailCursorLayer.Widget }, plan);
    }

    [Fact]
    public void GlobalChange_reassertsDefaultWithoutReapplyingUnchangedWidget()
    {
        var plan = RetailCursorManager.PlanApplication(
            hasLayerState: true,
            previousGlobal: RetailGlobalCursorKind.Default,
            previousWidget: WidgetA,
            currentGlobal: RetailGlobalCursorKind.TargetPending,
            currentWidget: WidgetA);

        Assert.Equal(new[] { RetailCursorLayer.Global }, plan);
    }

    [Fact]
    public void LaterWidgetTransition_overridesUnchangedGlobalDefault()
    {
        var plan = RetailCursorManager.PlanApplication(
            hasLayerState: true,
            previousGlobal: RetailGlobalCursorKind.TargetPending,
            previousWidget: WidgetA,
            currentGlobal: RetailGlobalCursorKind.TargetPending,
            currentWidget: WidgetB);

        Assert.Equal(new[] { RetailCursorLayer.Widget }, plan);
    }

    [Fact]
    public void ClearingWidgetCursor_restoresGlobalDefault()
    {
        var plan = RetailCursorManager.PlanApplication(
            hasLayerState: true,
            previousGlobal: RetailGlobalCursorKind.TargetPending,
            previousWidget: WidgetA,
            currentGlobal: RetailGlobalCursorKind.TargetPending,
            currentWidget: default);

        Assert.Equal(new[] { RetailCursorLayer.Global }, plan);
    }
}

public sealed class RetailCursorKeyColorTests
{
    [Fact]
    public void OpaqueSurface_PureWhiteBecomesTransparent_OtherPixelsUntouched()
    {
        byte[] rgba =
        [
            255, 255, 255, 255,   // key
            200, 160,  40, 255,   // gold
              0,   0,   0, 255,   // black
            255, 255, 254, 255,   // nearly white - not the key
        ];

        AcDream.App.Rendering.RetailCursorManager.KnockOutKeyColor(rgba);

        Assert.Equal(0, rgba[3]);
        Assert.Equal(255, rgba[7]);
        Assert.Equal(255, rgba[11]);
        Assert.Equal(255, rgba[15]);
    }

    [Fact]
    public void SurfaceWithRealAlpha_IsLeftAlone()
    {
        byte[] rgba =
        [
            255, 255, 255, 255,   // a real white pixel
            255, 255, 255,   0,   // already transparent
        ];

        AcDream.App.Rendering.RetailCursorManager.KnockOutKeyColor(rgba);

        Assert.Equal(255, rgba[3]);
    }
}
