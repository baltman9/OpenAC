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
/// </summary>
public sealed class SurfaceParityTests
{
    [Fact]
    public void SelectingAndClearingBehavesTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            ParityWorld.Stage(arm.Runtime);
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
    /// Cycling the selection. The windowless client cannot do it at all, and
    /// under this harness neither can the windowed one: its cycle lives in the
    /// selection interaction controller, which needs a presentation tree. So
    /// this records that both refuse today rather than claiming the listed
    /// difference is covered; driving the windowed cycle for real belongs to
    /// the stage that moves the cycle onto the entity directory.
    /// </summary>
    [Fact]
    public void CyclingTheSelectionIsRefusedTheSameWayToday() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            ParityWorld.Stage(arm.Runtime);
            ISelectionService selection = arm.Host.Selection;
            _ = selection.Select(ParityWorld.Monster);
            arm.Advance();

            transcript.Step("next player");
            transcript.Record(
                "executed",
                arm.Host.Automation.Selection.Execute(
                    PluginSelectionAction.NextPlayer));
            transcript.Record("selected", selection.SelectedObjectId);
        });

    /// <summary>
    /// The plugin UI, hotkeys, the host window, the world-line overlay, the
    /// clipboard and storage: everything a plugin might reach for that only a
    /// window can really do. Each has to come back quietly on both clients.
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
            transcript.Record(
                "registered",
                Safely(() => _ = host.Hotkeys.Register(
                    "parity.test", "Parity", default, static () => { })));

            transcript.Step("window");
            transcript.Record("minimized", host.Window.IsMinimized);

            transcript.Step("world lines");
            transcript.Record(
                "layer", Safely(() => _ = host.WorldLines.CreateLayer()));

            transcript.Step("clipboard");
            transcript.Record(
                "set", Safely(() => _ = host.Clipboard.TrySetText("parity")));

            transcript.Step("storage");
            transcript.Record(
                "read", Safely(() => _ = host.Storage.ReadText("parity")));
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
