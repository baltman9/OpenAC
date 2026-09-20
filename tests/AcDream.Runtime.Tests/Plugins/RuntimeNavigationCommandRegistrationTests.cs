using AcDream.Runtime;
using AcDream.Runtime.Plugins;
using AcDream.Runtime.Tests.Gameplay;

namespace AcDream.Runtime.Tests.Plugins;

/// <summary>
/// The client's own navigation verbs are built once, by the binding pass both
/// clients run, on the one command registry the plugin surface owns. Before
/// this each host built its own <c>/nav</c> with its own extras on its own
/// registry, so the two answered differently and a plugin verb registered on
/// one registry was invisible to the other.
/// </summary>
public sealed class RuntimeNavigationCommandRegistrationTests
{
    private static GameRuntime Create() =>
        RuntimeCreatureDeathStateTests.Create();

    private static RuntimeAutomationHostCapabilities Capabilities(
        Func<bool>? grid = null,
        Func<uint, bool>? route = null) => new()
    {
        HostName = "a client under test",
        Declared = new HashSet<string>(StringComparer.Ordinal),
        NavigationGrid = grid,
        NavigationRoutePreview = route,
    };

    private static string[] Lines(GameRuntime runtime) =>
        runtime.CommunicationOwner.Chat.Snapshot()
            .Select(static entry => entry.Text)
            .ToArray();

    [Fact]
    public void TheBindingPassPutsTheNavigationVerbsOnTheSurfacesOwnRegistry()
    {
        using GameRuntime runtime = Create();
        using var surface = new RuntimeAutomationSurface();

        IReadOnlySet<string> bound = RuntimeAutomationBindings.Apply(
            surface, runtime, Capabilities());

        Assert.Contains("BindNavigationCommands", bound);
        Assert.True(surface.TryHandlePluginCommand("/nav status"));
        Assert.True(surface.TryHandlePluginCommand("/motor status"));
        Assert.Contains(
            Lines(runtime),
            line => line.Contains(
                "Navigation: no walk has been asked for",
                StringComparison.Ordinal));
    }

    /// <summary>
    /// The two verbs that need something drawn are the only difference, and a
    /// client with nothing to draw on says so rather than leaving them out.
    /// </summary>
    [Fact]
    public void AClientWithNothingToDrawOnSaysSoForTheGridAndTheRoute()
    {
        using GameRuntime runtime = Create();
        using var surface = new RuntimeAutomationSurface();
        RuntimeAutomationBindings.Apply(surface, runtime, Capabilities());

        Assert.True(surface.TryHandlePluginCommand("/nav grid"));
        Assert.True(surface.TryHandlePluginCommand("/nav route 0x80000001"));

        Assert.Contains(
            Lines(runtime),
            line => line.Contains(
                "Navigation: this client has nothing to draw the grid on",
                StringComparison.Ordinal));
        Assert.Contains(
            Lines(runtime),
            line => line.Contains(
                "Navigation: this client has nothing to draw a route on",
                StringComparison.Ordinal));
    }

    [Fact]
    public void AClientThatCanDrawAnswersTheGridAndTheRouteWithWhatItDid()
    {
        using GameRuntime runtime = Create();
        using var surface = new RuntimeAutomationSurface();
        bool shown = false;
        uint routed = 0u;
        RuntimeAutomationBindings.Apply(
            surface,
            runtime,
            Capabilities(
                grid: () => shown = !shown,
                route: objectId =>
                {
                    routed = objectId;
                    return true;
                }));

        Assert.True(surface.TryHandlePluginCommand("/nav grid"));
        Assert.True(surface.TryHandlePluginCommand("/nav route 0x80000001"));

        Assert.True(shown);
        Assert.Equal(0x80000001u, routed);
        Assert.Contains(
            Lines(runtime),
            line => line.Contains(
                "Navigation: grid shown", StringComparison.Ordinal));
    }

    /// <summary>
    /// A second pass over the same surface must not register the verbs twice;
    /// the registry refuses a duplicate verb outright.
    /// </summary>
    [Fact]
    public void ASecondBindingPassOverTheSameSurfaceRegistersNothingTwice()
    {
        using GameRuntime runtime = Create();
        using var surface = new RuntimeAutomationSurface();

        RuntimeAutomationBindings.Apply(surface, runtime, Capabilities());
        IReadOnlySet<string> again = RuntimeAutomationBindings.Apply(
            surface, runtime, Capabilities());

        Assert.Contains("BindNavigationCommands", again);
        Assert.True(surface.TryHandlePluginCommand("/nav status"));
    }
}
