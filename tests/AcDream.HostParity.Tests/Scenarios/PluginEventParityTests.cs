using AcDream.Plugin.Abstractions;

namespace AcDream.HostParity.Tests;

/// <summary>
/// The events a plugin subscribes to, on both clients. These are the
/// callbacks a bot is built out of: it learns it is in the world, that an
/// object arrived or went away, that a description came back, that a
/// container opened, that it died, that something is being asked of the
/// player. A client where one of them never arrives runs a bot that waits
/// forever, and nothing else the client does well makes up for it.
///
/// Every event is asserted outright as well as recorded. Two clients that
/// both deliver nothing would write identical transcripts, so agreement
/// alone would say nothing here.
///
/// Mutation check (2026-09-20), run: making the windowless client's
/// object-change delivery a no-op turned the arrival, inventory and
/// description scenarios red on that arm and left the rest green; making its
/// arrival delivery a no-op turned the login scenario red on its own.
/// Restoring both turned them green.
/// </summary>
public sealed class PluginEventParityTests
{
    private const uint Arrival = 0x5000_0060u;

    /// <summary>Long enough for the pacing between two uses to lapse.</summary>
    private const int TicksPastTheUsePacing = 40;

    [Fact]
    public void ArrivingInTheWorldTellsAPluginOnBothClients() =>
        ParityScenario.RunFromLogin(static (arm, transcript) =>
        {
            int arrivals = 0;
            arm.Host.Events.LoginComplete += () => arrivals++;

            transcript.Step("not in yet");
            transcript.Record("arrivals", arrivals);
            Assert.Equal(0, arrivals);

            transcript.Step("the server lets the character in");
            arm.EnterWorld();
            transcript.Record("arrivals", arrivals);
            // Exactly once: a plugin that is told twice starts twice.
            Assert.Equal(1, arrivals);
        });

    [Fact]
    public void AnObjectArrivingAndLeavingTellsAPluginOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            var changes = new List<PluginObjectChange>();
            arm.Host.Events.ObjectChanged += changes.Add;

            transcript.Step("something arrives");
            arm.Server.CreateObject(ParityWorld.Spawn(
                Arrival,
                ParityWorld.PlayerX + 4f,
                ParityWorld.PlayerY,
                ParityPlayerBody.Cell,
                state: 0));
            arm.Advance();
            Record(transcript, "arrived", changes);
            Assert.Contains(
                changes,
                change => change.ObjectId == Arrival
                    && change.Kind == PluginObjectChangeKind.Created);

            transcript.Step("and goes away again");
            changes.Clear();
            arm.Server.DeleteObject(Arrival, instanceSequence: 1);
            arm.Advance();
            Record(transcript, "left", changes);
            Assert.Contains(
                changes,
                change => change.ObjectId == Arrival
                    && change.Kind == PluginObjectChangeKind.Released);
        });

    /// <summary>
    /// The server re-describing something already in the world. It arrives as
    /// a fresh incarnation under the same id: the previous one is retired
    /// before the new one is registered, so a plugin hears the object
    /// released and then created again under the one id, and the id is still
    /// in the world when the batch is over. That is what the client with a
    /// window has told plugins since before either client's producers were
    /// shared, and it is what both tell them now; the guide says so, because
    /// a plugin that reads a release as "gone for good" would drop a creature
    /// still standing in front of it.
    ///
    /// The object here is one the server alone knows about, so the whole
    /// sequence comes from the one source and can be asserted outright --
    /// two clients that both reported nothing would write identical
    /// transcripts.
    ///
    /// Mutation check (2026-09-20), run: mapping the retirement of a replaced
    /// incarnation to <c>Updated</c> in the runtime's plugin event mapping
    /// turned this red and left the rest of the file green; restoring it
    /// turned it green.
    /// </summary>
    [Fact]
    public void ReSendingAnObjectReleasesAndCreatesItOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            transcript.Step("something arrives");
            arm.Server.CreateObject(ParityWorld.Spawn(
                Arrival,
                ParityWorld.PlayerX + 4f,
                ParityWorld.PlayerY,
                ParityPlayerBody.Cell,
                state: 0));
            arm.Advance();

            var changes = new List<PluginObjectChange>();
            arm.Host.Events.ObjectChanged += changes.Add;

            transcript.Step("the server sends it again");
            arm.Server.CreateObject(ParityWorld.Spawn(
                Arrival,
                ParityWorld.PlayerX + 4f,
                ParityWorld.PlayerY,
                ParityPlayerBody.Cell,
                state: 0,
                instance: 2));
            arm.Advance();
            Record(transcript, "resent", changes);

            // Released then created, twice over: the object side and the
            // carried-items side each report the handover, as they do for
            // any one underlying change to an object the client holds a row
            // for. Pinned in full because the order and the count are what a
            // plugin actually receives, and both clients owe the same one.
            Assert.Equal(
                [
                    PluginObjectChangeKind.Released,
                    PluginObjectChangeKind.Created,
                    PluginObjectChangeKind.Released,
                    PluginObjectChangeKind.Created,
                ],
                changes
                    .Where(change => change.ObjectId == Arrival)
                    .Select(change => change.Kind)
                    .ToList());
            // The release was a handover, not a departure: the id is still
            // one the client answers for.
            transcript.Record(
                "known",
                arm.Runtime.EntityObjects.Entities.TryGetActive(
                    Arrival, out _));
            Assert.True(arm.Runtime.EntityObjects.Entities.TryGetActive(
                Arrival, out _));
        });


    [Fact]
    public void SomethingEnteringThePacksTellsAPluginOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            var changes = new List<PluginObjectChange>();
            arm.Host.Events.ObjectChanged += changes.Add;

            transcript.Step("the packs are filled");
            ParityWorld.StageCarriedItems(arm.Runtime);
            arm.Advance();
            Record(transcript, "carried", changes);
            Assert.Contains(
                changes,
                change => change.ObjectId == ParityWorld.Kit
                    && change.Kind == PluginObjectChangeKind.Created);
        });

    [Fact]
    public void ADescriptionComingBackTellsAPluginOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            _ = ParityWorld.Stage(arm);
            ParityWorld.StageCorpse(arm.Runtime, ParityWorld.WithinArmsReach);
            ParityWorld.StageCorpseContents(arm.Runtime);
            var changes = new List<PluginObjectChange>();
            arm.Host.Events.ObjectChanged += changes.Add;

            transcript.Step("ask about the gem");
            Assert.Equal(
                PluginItemCommandStatus.Started,
                arm.Host.Automation.Objects.Identify(
                    ParityWorld.CorpseGem).Status);

            transcript.Step("the server answers");
            changes.Clear();
            arm.Server.AppraisalResponse(ParityWorld.CorpseGem, []);
            arm.Advance();
            Record(transcript, "identified", changes);
            Assert.Contains(
                changes,
                change => change.ObjectId == ParityWorld.CorpseGem
                    && change.Kind == PluginObjectChangeKind.IdentReceived);
        });

    [Fact]
    public void ACorpseOpeningAndClosingTellsAPluginOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            _ = ParityWorld.Stage(arm);
            ParityWorld.StageCorpse(arm.Runtime, ParityWorld.WithinArmsReach);
            ParityWorld.StageCorpseContents(arm.Runtime);
            var opened = new List<uint>();
            var closed = new List<uint>();
            arm.Host.Events.ContainerOpened += opened.Add;
            arm.Host.Events.ContainerClosed += closed.Add;
            ILootAutomation loot = arm.Host.Automation.Loot;

            transcript.Step("open it");
            _ = loot.Open(ParityWorld.Corpse);
            arm.Server.ViewContents(
                ParityWorld.Corpse,
                ParityWorld.CorpseCoin,
                ParityWorld.CorpseGem);
            arm.Server.UseDone();
            arm.Advance();
            transcript.Record("opened", string.Join(",", opened));
            Assert.Contains(ParityWorld.Corpse, opened);

            transcript.Step("close it");
            // The pacing between two uses has to lapse first, or the close is
            // refused as early and nothing is closed on either client.
            for (int step = 0; step < TicksPastTheUsePacing; step++)
                arm.Advance();
            transcript.Record(
                "close.status",
                loot.Close(ParityWorld.Corpse).Status.ToString());
            arm.Server.ClosedTheContainer(ParityWorld.Corpse);
            arm.Advance();
            transcript.Record("closed", string.Join(",", closed));
            Assert.Contains(ParityWorld.Corpse, closed);
        });

    [Fact]
    public void DyingTellsAPluginOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            var deaths = new List<string>();
            arm.Host.Events.LocalPlayerDied += deaths.Add;

            transcript.Step("the server says the character died");
            arm.Server.KilledTheCharacter("You were slain by a Drudge Slinker!");
            arm.Advance();
            transcript.Record("deaths", string.Join("|", deaths));
            Assert.Equal(
                ["You were slain by a Drudge Slinker!"],
                deaths);
        });

    [Fact]
    public void BeingAskedSomethingTellsAPluginOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            var asked = new List<PluginConfirmation>();
            arm.Host.Events.ConfirmationRequested += asked.Add;

            transcript.Step("the client asks the player");
            arm.ShowConfirmation(new PluginConfirmation(
                ContextId: 77u,
                Type: 5,
                Text: "Allow the swap?"));
            transcript.Record("asked.count", asked.Count);
            transcript.Record(
                "asked.context",
                asked.Count == 0 ? 0u : asked[0].ContextId);
            transcript.Record(
                "asked.text",
                asked.Count == 0 ? string.Empty : asked[0].Text);
            // Exactly once: a client that raises it twice has two producers,
            // which is a plugin answering the same question twice.
            Assert.Single(asked);
            Assert.Equal(77u, asked[0].ContextId);
        });

    private static void Record(
        ParityTranscript transcript,
        string key,
        IReadOnlyList<PluginObjectChange> changes)
    {
        transcript.Record($"{key}.count", changes.Count);
        for (int index = 0; index < changes.Count; index++)
        {
            transcript.Record($"{key}[{index}].id", changes[index].ObjectId);
            transcript.Record(
                $"{key}[{index}].kind", changes[index].Kind.ToString());
        }
    }
}
