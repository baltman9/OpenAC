using AcDream.Core.Plugins;
using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Tests.Plugins;

/// <summary>
/// A plugin handler that throws is skipped, never propagated -- one plugin's
/// failure is its own. It is said out loud all the same: a client that says
/// nothing leaves a plugin author with an overlay that silently stopped
/// updating and no idea why. These pin that every event this type raises
/// names the failure, in the same words whichever client is running.
/// </summary>
public class WorldEventsReportTests
{
    private static (WorldEvents Events, List<string> Reported) Build()
    {
        var reported = new List<string>();
        return (new WorldEvents(reported.Add), reported);
    }

    [Fact]
    public void ALifecycleHandlerThatThrowsIsNamed()
    {
        (WorldEvents events, List<string> reported) = Build();
        events.LoginComplete += () => throw new InvalidOperationException("boom");
        events.Logoff += () => throw new InvalidOperationException("boom");

        events.FireLoginComplete();
        events.FireLogoff();

        Assert.Equal(2, reported.Count);
        Assert.All(reported, line =>
            Assert.StartsWith("Plugin lifecycle handler threw: ", line));
    }

    [Fact]
    public void ATickHandlerThatThrowsIsNamed()
    {
        (WorldEvents events, List<string> reported) = Build();
        events.Tick += _ => throw new InvalidOperationException("boom");

        events.FireTick(0.05);

        Assert.StartsWith(
            "Plugin tick handler threw: ",
            Assert.Single(reported));
    }

    [Fact]
    public void ADeathHandlerThatThrowsIsNamed()
    {
        (WorldEvents events, List<string> reported) = Build();
        events.LocalPlayerDied += _ => throw new InvalidOperationException("boom");

        events.FireLocalPlayerDied("you died");

        Assert.StartsWith(
            "Plugin death handler threw: ",
            Assert.Single(reported));
    }

    [Fact]
    public void AnObjectChangeHandlerThatThrowsIsNamed()
    {
        (WorldEvents events, List<string> reported) = Build();
        events.ObjectChanged += _ => throw new InvalidOperationException("boom");

        events.FireObjectChanged(
            new PluginObjectChange(0x50000001u, PluginObjectChangeKind.Created));

        Assert.StartsWith(
            "Plugin object-change handler threw: ",
            Assert.Single(reported));
    }

    [Fact]
    public void AContainerHandlerThatThrowsIsNamed()
    {
        (WorldEvents events, List<string> reported) = Build();
        events.ContainerOpened += _ => throw new InvalidOperationException("boom");
        events.ContainerClosed += _ => throw new InvalidOperationException("boom");

        events.FireContainerOpened(0x50000002u);
        events.FireContainerClosed(0x50000002u);

        Assert.Equal(2, reported.Count);
        Assert.All(reported, line =>
            Assert.StartsWith("Plugin container handler threw: ", line));
    }

    [Fact]
    public void AConfirmationHandlerThatThrowsIsNamed()
    {
        (WorldEvents events, List<string> reported) = Build();
        events.ConfirmationRequested += _ =>
            throw new InvalidOperationException("boom");

        events.FireConfirmationRequested(
            new PluginConfirmation(1, 0, "are you sure?"));

        Assert.StartsWith(
            "Plugin confirmation handler threw: ",
            Assert.Single(reported));
    }

    /// <summary>
    /// A failing handler does not stop the next one hearing about the same
    /// event, and the failure never reaches the caller that raised it.
    /// </summary>
    [Fact]
    public void AFailingHandlerDoesNotStopTheNextOneOrReachTheCaller()
    {
        (WorldEvents events, List<string> reported) = Build();
        var seen = new List<uint>();
        events.ContainerOpened += _ => throw new InvalidOperationException("boom");
        events.ContainerOpened += seen.Add;

        events.FireContainerOpened(0x50000003u);

        Assert.Equal([0x50000003u], seen);
        Assert.Single(reported);
    }

    /// <summary>
    /// A host that names no sink still runs: the handler is skipped exactly
    /// as before and nothing is said.
    /// </summary>
    [Fact]
    public void WithNoSinkAFailingHandlerIsStillSkipped()
    {
        var events = new WorldEvents();
        var seen = new List<uint>();
        events.ContainerOpened += _ => throw new InvalidOperationException("boom");
        events.ContainerOpened += seen.Add;

        events.FireContainerOpened(0x50000004u);

        Assert.Equal([0x50000004u], seen);
    }
}
