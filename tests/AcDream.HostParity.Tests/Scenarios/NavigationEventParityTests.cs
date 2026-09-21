using AcDream.Core.Plugins;
using AcDream.Plugin.Abstractions;

namespace AcDream.HostParity.Tests;

/// <summary>
/// The walk a plugin asked for, reported back to it, on both clients.
///
/// A bot that walks somewhere and then does something there is written
/// against this event: it asks for the walk and waits to be told the
/// character arrived. A client where the event never arrives runs that bot
/// up to the walk and no further, and the bot cannot tell the difference
/// between a walk still under way and a client that will never speak. The
/// event arrived on this branch raised by hand on the windowless client
/// alone, which is exactly that failure on the other one.
///
/// What is proven here is the half the harness can reach: the report goes in
/// through the one sink both clients supply and comes out at a subscribed
/// plugin, identically, including a plugin that throws and a plugin that has
/// unsubscribed. Each report is also asserted outright, since two clients
/// that both delivered nothing would write identical transcripts.
///
/// What is NOT proven here: that a real walk drives those reports. Driving
/// one needs the navigation walk owner, which needs a built world, and the
/// harness builds none -- <c>BindNavigationWalk</c> is in the binding pass's
/// "needs a world" list. The drive itself is one shared method on the plugin
/// surface, reached from the shared tick on both clients, so it cannot
/// differ between them; but nothing here would notice if it stopped running
/// on both at once. Closing that needs a harness world, not another
/// scenario.
///
/// Mutation check (2026-09-21), run: making the windowless client's
/// navigation raise a no-op turned both scenarios red on that arm and left
/// the rest of the parity suite green. Restoring it turned them green.
/// </summary>
public sealed class NavigationEventParityTests
{
    private static PluginGoToReport Report(
        PluginGoToState state, long revision) =>
        new(
            Sequence: 7L,
            State: state,
            ObjectId: ParityWorld.Corpse,
            RemainingMeters: 3.5f,
            Replans: 0,
            Reason: state.ToString())
        {
            Revision = revision,
            Owner = "example.plugin",
        };

    [Fact]
    public void AWalkIsReportedToAPluginOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            IPluginEventSink sink = Sink(arm);
            var reports = new List<PluginGoToReport>();
            arm.Host.Events.NavigationChanged += reports.Add;

            transcript.Step("the walk starts");
            sink.FireNavigationChanged(Report(PluginGoToState.Walking, 1L));
            Record(transcript, "walking", reports);
            Assert.Equal(PluginGoToState.Walking, Assert.Single(reports).State);

            transcript.Step("and the character arrives");
            reports.Clear();
            sink.FireNavigationChanged(Report(PluginGoToState.Arrived, 2L));
            Record(transcript, "arrived", reports);
            PluginGoToReport arrived = Assert.Single(reports);
            // The whole report survives the crossing, not just the state: a
            // bot decides on the object and the revision as well.
            Assert.Equal(PluginGoToState.Arrived, arrived.State);
            Assert.Equal(PluginGoToState.Arrived, arrived.CurrentState);
            Assert.Equal(ParityWorld.Corpse, arrived.ObjectId);
            Assert.Equal(2L, arrived.Revision);
            Assert.Equal("example.plugin", arrived.Owner);
        });

    /// <summary>
    /// One plugin's bad handler must not cost another plugin the news, and a
    /// plugin that has gone away must stop hearing it. Both clients owe the
    /// same answer: a client that let the throw through would take the whole
    /// frame down with it.
    /// </summary>
    [Fact]
    public void AThrowingPluginCostsNoOtherPluginTheNewsOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            IPluginEventSink sink = Sink(arm);
            var heard = new List<PluginGoToState>();
            Action<PluginGoToReport> throwing =
                static _ => throw new InvalidOperationException("bad plugin");
            Action<PluginGoToReport> leaving = _ => heard.Add(PluginGoToState.None);

            arm.Host.Events.NavigationChanged += throwing;
            arm.Host.Events.NavigationChanged += leaving;
            arm.Host.Events.NavigationChanged += report => heard.Add(report.State);

            transcript.Step("one handler throws");
            sink.FireNavigationChanged(Report(PluginGoToState.Walking, 1L));
            transcript.Record("heard", string.Join(",", heard));
            Assert.Contains(PluginGoToState.Walking, heard);

            transcript.Step("a plugin unsubscribes");
            heard.Clear();
            arm.Host.Events.NavigationChanged -= leaving;
            sink.FireNavigationChanged(Report(PluginGoToState.Arrived, 2L));
            transcript.Record("heard", string.Join(",", heard));
            Assert.Equal([PluginGoToState.Arrived], heard);
        });

    /// <summary>
    /// The one object the runtime speaks navigation into, on this client. It
    /// is the host itself where there is no window and the events object
    /// where there is one; a client that supplied neither could not be
    /// spoken into at all.
    /// </summary>
    private static IPluginEventSink Sink(ParityArm arm) =>
        arm.Host.Events as IPluginEventSink
        ?? throw new InvalidOperationException(
            $"{arm.Host.GetType().Name} does not supply the shared event "
            + "sink, so the runtime cannot report a walk on it.");

    private static void Record(
        ParityTranscript transcript,
        string key,
        IReadOnlyList<PluginGoToReport> reports)
    {
        transcript.Record($"{key}.count", reports.Count);
        transcript.Record(
            $"{key}.states",
            string.Join(
                ",",
                reports.Select(static report => report.State.ToString())));
        transcript.Record(
            $"{key}.revisions",
            string.Join(
                ",",
                reports.Select(static report => report.Revision)));
    }
}
