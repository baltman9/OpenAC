using AcDream.Plugin.Abstractions;

namespace AcDream.HostParity.Tests;

/// <summary>
/// Selecting things, and the parts of the surface that are meant to be inert
/// without a window. Inert has to mean the same thing on both clients: never
/// throwing, never handing back a null a plugin would walk into, and saying
/// the same "no" -- otherwise a plugin that runs happily under a window falls
/// over in a bot, or the other way round.
///
/// What is compared is behaviour, not which class answers: the two clients
/// legitimately have different no-op implementations, and a plugin cannot
/// tell them apart except by what they do.
///
/// Mutation check (2026-09-20), run: taking the real storage away from the
/// client with a window -- leaving it the do-nothing store the scenario used
/// to run against on both arms -- turned
/// <see cref="TheWindowShapedSurfacesAreInertAndSafeOnBothClients"/> red on
/// what a plugin saved and read back. Restoring it turned it green.
/// </summary>
public sealed class SurfaceParityTests
{
    [Fact]
    public void SelectingAndClearingBehavesTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            ParityWorld.Stage(arm);
            ISelectionService selection = arm.Host.Selection;
            var seen = new List<SelectionChangedEvent>();
            selection.Changed += change => seen.Add(change);

            transcript.Step("select a creature");
            transcript.Record("accepted", selection.Select(ParityWorld.Monster));
            transcript.Record("selected", selection.SelectedObjectId);
            transcript.Record("previous", selection.PreviousObjectId);
            arm.Advance();

            transcript.Step("select another");
            transcript.Record(
                "accepted", selection.Select(ParityWorld.SecondMonster));
            transcript.Record("selected", selection.SelectedObjectId);
            transcript.Record("previous", selection.PreviousObjectId);
            arm.Advance();

            transcript.Step("select the same again");
            transcript.Record(
                "accepted", selection.Select(ParityWorld.SecondMonster));
            transcript.Record("selected", selection.SelectedObjectId);
            arm.Advance();

            transcript.Step("clear");
            selection.Clear();
            transcript.Record("selected", selection.SelectedObjectId);
            transcript.Record("previous", selection.PreviousObjectId);
            arm.Advance();

            transcript.Step("what the plugin was told");
            transcript.Record("count", seen.Count);
            for (int index = 0; index < seen.Count; index++)
            {
                transcript.Record(
                    $"change[{index}].previous", seen[index].PreviousObjectId);
                transcript.Record(
                    $"change[{index}].selected", seen[index].SelectedObjectId);
            }
        });

    /// <summary>
    /// The plugin UI, hotkeys, the host window, the world-line overlay, the
    /// clipboard and storage. Two of these only a window can really do and
    /// are inert on both clients; the rest are the real registries on both,
    /// so this also says that what a plugin puts in comes back out.
    ///
    /// Inert has to mean the same thing on both clients: never throwing,
    /// never handing back a null a plugin would walk into, and saying the
    /// same "no" -- otherwise a plugin that runs happily under a window
    /// falls over in a bot, or the other way round.
    /// </summary>
    [Fact]
    public void TheWindowShapedSurfacesAreInertAndSafeOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            IPluginHost host = arm.Host;

            transcript.Step("ui");
            transcript.Record(
                "panel",
                Safely(() => host.Ui.AddMarkupPanel("parity", new object())));

            transcript.Step("hotkeys");
            IDisposable? registration = null;
            transcript.Record(
                "registered",
                Safely(() => registration = host.Hotkeys.Register(
                    "parity.test", "Parity", default, static () => { })));
            // A plugin does something with what it is handed back, so a
            // client that hands back nothing is a client a plugin crashes on.
            transcript.Record("registration", registration is not null);
            Assert.NotNull(registration);
            transcript.Record("unregistered", Safely(registration.Dispose));

            transcript.Step("window");
            transcript.Record("minimized", host.Window.IsMinimized);

            transcript.Step("world lines");
            IPluginWorldLineLayer? layer = null;
            transcript.Record(
                "layer", Safely(() => layer = host.WorldLines.CreateLayer()));
            // A client that draws nothing answers with no layer, which the
            // plugin interface says outright, so a plugin holds a
            // may-be-nothing. What has to match is that asking, using and
            // letting go of whatever came back are all quiet on both.
            transcript.Record(
                "layer.used",
                Safely(() => layer?.SetLines(Array.Empty<PluginWorldLine>())));
            transcript.Record("layer.released", Safely(() => layer?.Dispose()));

            transcript.Step("clipboard");
            transcript.Record(
                "set", Safely(() => _ = host.Clipboard.TrySetText("parity")));
            transcript.Record("set.answer", host.Clipboard.TrySetText("parity"));

            transcript.Step("storage");
            transcript.Record(
                "missing", Safely(() => _ = host.Storage.ReadText("parity")));
            transcript.Record("missing.answer", host.Storage.ReadText("parity"));
            transcript.Record(
                "written", Safely(() => host.Storage.WriteText("parity", "kept")));
            // What a plugin saves has to still be there on both clients: a
            // bot that cannot keep its own settings between runs is a
            // different client from one that can.
            transcript.Record("read.back", host.Storage.ReadText("parity"));
            Assert.Equal("kept", host.Storage.ReadText("parity"));
            transcript.Record(
                "vtank", Safely(() => _ = host.VtankProfiles.ReadText("parity")));
        });

    /// <summary>
    /// Runs something a plugin might do to a window-shaped surface and says
    /// whether it came back quietly. Inert means quiet on both clients.
    /// </summary>
    private static string Safely(Action action)
    {
        try
        {
            action();
            return "quiet";
        }
        catch (Exception error)
        {
            return $"threw {error.GetType().Name}";
        }
    }
}
