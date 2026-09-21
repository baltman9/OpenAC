using AcDream.Plugin.Abstractions;

namespace AcDream.HostParity.Tests;

/// <summary>
/// Using an object the character does not own -- a corpse, a chest, a vendor,
/// a townsfolk, a door -- run against both clients. Until now only the client
/// with a window had this at all: it walked to the object and used it on
/// arrival, while the client without one refused the call outright, so a bot
/// that opened corpses could not be proved on the client anyone plays. Both
/// now take the one runtime route, and these scenarios are what says so.
///
/// Mutation check (2026-09-20), run: taking the walk-then-use binding out of
/// the shared binding pass for one arm turned three of these four red on the
/// status lines, exactly as the client without a window used to answer;
/// restoring it turned them green. The fourth stays green under that
/// mutation on purpose -- a guid nothing answers to is refused before the
/// route is reached at all, so it is here to pin that the two clients agree
/// on the refusal that comes first, not on the route.
/// </summary>
public sealed class WorldObjectUseParityTests
{
    [Fact]
    public void UsingACorpseSeveralMetresOffLooksTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            IItemAutomation items = StageWorld(arm);

            transcript.Step("use the corpse");
            Record(transcript, "use", items.Use(ParityWorld.Corpse));
            transcript.Record("busy", items.IsBusy);
            transcript.RecordOutbound(arm);

            transcript.Step("a few frames later");
            for (int tick = 0; tick < 10; tick++)
                arm.Advance();
            transcript.Record("busy", items.IsBusy);
            transcript.RecordOutbound(arm);
        });

    [Fact]
    public void UsingSomethingThatIsNotThereIsRefusedTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            IItemAutomation items = StageWorld(arm);

            transcript.Step("use a guid nothing answers to");
            Record(transcript, "use", items.Use(0x5000_00FFu));
            transcript.Record("busy", items.IsBusy);
            transcript.RecordOutbound(arm);
        });

    [Fact]
    public void UsingTheCharacterItselfIsRefusedTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            IItemAutomation items = StageWorld(arm);

            transcript.Step("use the character");
            Record(
                transcript,
                "use",
                items.Use(arm.Runtime.PlayerIdentity.ServerGuid));
            transcript.Record("busy", items.IsBusy);
            transcript.RecordOutbound(arm);
        });

    [Fact]
    public void TwoUsesInARowAreRefusedTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            IItemAutomation items = StageWorld(arm);

            transcript.Step("use the corpse");
            Record(transcript, "first", items.Use(ParityWorld.Corpse));

            transcript.Step("ask again straight away");
            Record(transcript, "second", items.Use(ParityWorld.Corpse));
            transcript.Record("busy", items.IsBusy);
            transcript.RecordOutbound(arm);
        });

    private static IItemAutomation StageWorld(ParityArm arm)
    {
        _ = ParityWorld.Stage(arm);
        ParityWorld.StageCorpse(arm.Runtime);
        _ = arm.Operations.TakeOutbound();
        return arm.Host.Automation.Items;
    }

    private static void Record(
        ParityTranscript transcript, string key, PluginItemCommandResult result)
    {
        transcript.Record($"{key}.status", result.Status.ToString());
        transcript.Record($"{key}.notice", result.Notice);
    }
}
