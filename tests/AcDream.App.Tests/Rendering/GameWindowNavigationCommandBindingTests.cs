using AcDream.App.Plugins;
using AcDream.App.Rendering;
using AcDream.App.Tests.Architecture;
using AcDream.Runtime.Navigation;
using AcDream.Runtime.Plugins;

namespace AcDream.App.Tests.Rendering;

/// <summary>
/// The window used to build its own <c>/nav</c> and <c>/motor</c> on its own
/// command registry while the windowless client built another pair on another
/// registry. Both are now built once by the shared binding pass; the window
/// only lends the two verbs that need something drawn.
/// </summary>
public sealed class GameWindowNavigationCommandBindingTests
{
    [Fact]
    public void TheWindowNoLongerBuildsOrRegistersTheNavigationVerbsItself()
    {
        var window = CompiledCallGraph.ReadOwned(typeof(GameWindow));

        Assert.DoesNotContain(
            window,
            call => call.Target.DeclaringType == typeof(NavigationChatCommands));
    }

    [Fact]
    public void TheWindowLendsTheGridAndTheRoutePreviewThroughTheCapabilityRecord()
    {
        bool shown = false;
        uint routed = 0u;
        RuntimeAutomationHostCapabilities record =
            GraphicalAutomationCapabilities.Build(new GraphicalAutomationParts
            {
                Runtime = null!,
                NavigationGrid = () => shown = true,
                NavigationRoutePreview = objectId =>
                {
                    routed = objectId;
                    return true;
                },
            });

        Assert.NotNull(record.NavigationGrid);
        Assert.NotNull(record.NavigationRoutePreview);
        Assert.True(record.NavigationGrid!());
        Assert.True(shown);
        Assert.True(record.NavigationRoutePreview!(0x80000001u));
        Assert.Equal(0x80000001u, routed);

        // The two are lent, not declared: a client with nothing to draw on is
        // not a client missing a plugin capability, so the seam census never
        // sees them and no allow-list row has to excuse them.
        Assert.DoesNotContain(
            nameof(RuntimeAutomationHostCapabilities.NavigationGrid),
            RuntimeAutomationHostCapabilities.AllCapabilityNames);
        Assert.DoesNotContain(
            nameof(RuntimeAutomationHostCapabilities.NavigationRoutePreview),
            RuntimeAutomationHostCapabilities.AllCapabilityNames);
    }
}
