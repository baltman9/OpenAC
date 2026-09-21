using System.Reflection;
using AcDream.Core.Plugins;
using AcDream.Headless.Plugins;
using AcDream.Plugin.Abstractions;

namespace AcDream.HostParity.Tests;

/// <summary>
/// The gate on world events: every event a plugin can subscribe to has one
/// raise on the shared sink, and both clients supply that sink.
///
/// This exists because of the shape an added event tends to arrive in: the
/// event is declared on <see cref="IEvents"/>, one client is taught to raise
/// it by hand, and the other is left with an event that compiles, looks
/// wired and never fires. A bot written against it then waits forever on
/// whichever client the author did not have open. The rule here is that the
/// raise belongs to <see cref="IPluginEventSink"/>, which both clients
/// implement, so teaching one client teaches both.
///
/// Mutation check (2026-09-21), run: adding an event to
/// <see cref="IEvents"/> with an inert default and no raise anywhere --
/// which is how such an event is usually added, and how it compiles while
/// delivering nothing -- turned
/// <see cref="EveryPluginEventHasARaiseOnTheSharedSink"/> red with
/// "SomethingNew has no FireSomethingNew on the shared sink" and left the
/// rest of the parity suite green. Removing it turned the census green.
/// </summary>
public sealed class PluginEventCensusTests
{
    /// <summary>
    /// Events whose raise is not a sink member, each with the reason. Both
    /// of these are raised by something shared rather than by a client, so
    /// neither is a way for the two clients to drift.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> NotSinkRaised =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(IEvents.Tick)] =
                "the client's clock rather than a report; its cadence is "
                + "pinned separately and both clients drive the same step",
            [nameof(IEvents.EntitySpawned)] =
                "handed straight to the one world-entity producer both "
                + "clients bind, which owns the replay as well as the raise",
        };

    private static IEnumerable<EventInfo> PluginEvents() =>
        typeof(IEvents)
            .GetEvents(BindingFlags.Public | BindingFlags.Instance)
            .OrderBy(static e => e.Name, StringComparer.Ordinal);

    [Fact]
    public void EveryPluginEventHasARaiseOnTheSharedSink()
    {
        var missing = new List<string>();
        foreach (EventInfo pluginEvent in PluginEvents())
        {
            if (NotSinkRaised.ContainsKey(pluginEvent.Name))
                continue;
            string raise = "Fire" + pluginEvent.Name;
            if (typeof(IPluginEventSink).GetMethod(raise) is null)
            {
                missing.Add(
                    $"{pluginEvent.Name} has no {raise} on the shared sink");
            }
        }

        Assert.True(
            missing.Count == 0,
            "A plugin event with no raise on the shared sink is raised by "
                + "hand on one client or not at all, which is how an event "
                + "comes to be dead on the other: "
                + string.Join("; ", missing));
    }

    /// <summary>
    /// A row that no longer names a real event is as misleading as a missing
    /// raise, so a removed or renamed event forces its row out.
    /// </summary>
    [Fact]
    public void EveryNotSinkRaisedRowStillNamesAnEvent()
    {
        string[] names = PluginEvents().Select(static e => e.Name).ToArray();
        string[] stale = NotSinkRaised.Keys
            .Where(name => !names.Contains(name, StringComparer.Ordinal))
            .ToArray();

        Assert.True(
            stale.Length == 0,
            "These rows no longer name an event on IEvents: "
                + string.Join(", ", stale));
    }

    [Fact]
    public void BothClientsSupplyTheSharedSink()
    {
        Assert.True(
            typeof(IPluginEventSink).IsAssignableFrom(typeof(WorldEvents)),
            "The windowed client's events object must be the shared sink.");
        Assert.True(
            typeof(IPluginEventSink).IsAssignableFrom(typeof(HeadlessPluginHost)),
            "The windowless client's host must be the shared sink.");
    }

    /// <summary>
    /// The two clients' raises are the same set. A client that grows one of
    /// its own is raising something the shared runtime cannot ask the other
    /// client for, which is the drift this campaign exists to end.
    /// </summary>
    [Fact]
    public void NeitherClientRaisesAnEventTheOtherCannotRaise() =>
        Assert.Equal(
            RaisesOn(typeof(WorldEvents)),
            RaisesOn(typeof(HeadlessPluginHost)));

    private static string[] RaisesOn(Type client) =>
        client.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(static method =>
                method.Name.StartsWith("Fire", StringComparison.Ordinal))
            .Select(static method => method.Name)
            // The tick is the client's own clock, driven rather than raised.
            .Where(static name => name != "FireTick")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
}
