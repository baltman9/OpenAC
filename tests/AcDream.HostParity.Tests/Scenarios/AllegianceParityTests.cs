using AcDream.Plugin.Abstractions;

namespace AcDream.HostParity.Tests;

/// <summary>
/// Swearing to a patron and breaking a tie, run against both clients.
///
/// Regression guard, in the sense the suite README gives the word: the owner
/// of both commands is the shared runtime surface, so the two arms run the
/// same code and agreement is true by construction. What earns its keep here
/// is that each step is ASSERTED per arm -- on the answer the plugin was
/// given AND on the id that really left the client for the server -- so a
/// client that composed nothing, or composed the wrong id, is caught rather
/// than agreeing with the other client about doing nothing.
///
/// Mutation checks (2026-09-22), each run: sending the character's own id in
/// place of the patron's turned
/// <see cref="SwearingToAPlayerLeavesBothClientsWithThatPlayersId"/> red on
/// both arms at once, on the id read back off the wire; dropping the
/// player-kind half of the swear check turned
/// <see cref="SwearingToACreatureIsRefusedTheSameOnBothClients"/> red on a
/// swear that should never have been composed; and dropping the membership
/// check turned
/// <see cref="BreakingWithAStrangerIsRefusedTheSameOnBothClients"/> red the
/// same way. Restoring each turned them green.
/// </summary>
public sealed class AllegianceParityTests
{
    /// <summary>The client action that says "swear to that one".</summary>
    private const uint SwearAction = 0x001Du;

    /// <summary>The client action that says "break with that one".</summary>
    private const uint BreakAction = 0x001Eu;

    [Fact]
    public void SwearingToAPlayerLeavesBothClientsWithThatPlayersId() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            IAllegianceAutomation allegiance = StageWorld(arm);

            transcript.Step("swear to the other player");
            PluginAllegianceCommandResult result =
                allegiance.Swear(ParityWorld.OtherPlayer);
            Record(transcript, "swear", result);
            arm.Advance();
            // One swear went out, and it names the player that was asked
            // for. A client that composed nothing would agree with the
            // other client about nothing, so the id is read back off the
            // wire rather than counted.
            Assert.Equal(PluginAllegianceCommandStatus.Sent, result.Status);
            Assert.Equal(ParityWorld.OtherPlayer, WhoWasNamed(arm, SwearAction));
            Assert.Empty(Sent(arm, BreakAction));
            transcript.RecordOutbound(arm);
        });

    [Fact]
    public void BreakingWithThePatronLeavesBothClientsWithThePatronsId() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            IAllegianceAutomation allegiance = StageWorld(arm);

            transcript.Step("break with the patron");
            PluginAllegianceCommandResult result =
                allegiance.Break(ParityWorld.Patron);
            Record(transcript, "break", result);
            arm.Advance();
            // The patron is not in the world at all, and the tie is still
            // broken: a break asks the server about the allegiance list, not
            // about who is standing where.
            Assert.Equal(PluginAllegianceCommandStatus.Sent, result.Status);
            Assert.Equal(ParityWorld.Patron, WhoWasNamed(arm, BreakAction));
            Assert.Empty(Sent(arm, SwearAction));
            transcript.RecordOutbound(arm);
        });

    /// <summary>
    /// Something standing right there that is not a player. Both clients
    /// refuse it for the target rather than sending a number the server can
    /// only throw away.
    /// </summary>
    [Fact]
    public void SwearingToACreatureIsRefusedTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            IAllegianceAutomation allegiance = StageWorld(arm);

            transcript.Step("swear to a creature");
            PluginAllegianceCommandResult result =
                allegiance.Swear(ParityWorld.Monster);
            Record(transcript, "swear", result);
            arm.Advance();
            Assert.Equal(PluginAllegianceCommandStatus.InvalidTarget, result.Status);
            Assert.Empty(Sent(arm, SwearAction));
            transcript.RecordOutbound(arm);
        });

    /// <summary>
    /// Another player, visible, who is in no allegiance with this character.
    /// Being visible is not what a break needs, and both clients say so.
    /// </summary>
    [Fact]
    public void BreakingWithAStrangerIsRefusedTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            IAllegianceAutomation allegiance = StageWorld(arm);

            transcript.Step("break with a player outside the allegiance");
            PluginAllegianceCommandResult result =
                allegiance.Break(ParityWorld.OtherPlayer);
            Record(transcript, "break", result);
            arm.Advance();
            Assert.Equal(PluginAllegianceCommandStatus.InvalidTarget, result.Status);
            Assert.Empty(Sent(arm, BreakAction));
            transcript.RecordOutbound(arm);
        });

    /// <summary>
    /// The character in the world with an allegiance behind it, another
    /// player standing next to it, and the outbound record wiped so what a
    /// scenario reads off the wire is its own.
    /// </summary>
    private static IAllegianceAutomation StageWorld(ParityArm arm)
    {
        _ = ParityWorld.Stage(arm);
        ParityWorld.StageAnotherPlayer(arm.Runtime);
        ParityWorld.StageAllegiance(arm.Runtime);
        _ = arm.Operations.TakeOutbound();
        return arm.Host.Automation.Allegiance;
    }

    /// <summary>Every command of this kind this arm has asked to send.</summary>
    private static IReadOnlyList<ParityOutbound> Sent(ParityArm arm, uint action)
        => [.. arm.Operations.Outbound.Where(
            message => message.GameAction == action)];

    /// <summary>Who the one command of this kind this arm sent named.</summary>
    private static uint WhoWasNamed(ParityArm arm, uint action)
    {
        ParityOutbound message = Assert.Single(Sent(arm, action));
        byte[] body = Convert.FromHexString(message.Body);
        return System.Buffers.Binary.BinaryPrimitives
            .ReadUInt32LittleEndian(body.AsSpan(12));
    }

    private static void Record(
        ParityTranscript transcript,
        string key,
        PluginAllegianceCommandResult result)
    {
        transcript.Record($"{key}.status", result.Status.ToString());
        transcript.Record($"{key}.notice", result.Notice);
    }
}
