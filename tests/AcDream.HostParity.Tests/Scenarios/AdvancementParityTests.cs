using AcDream.Plugin.Abstractions;

namespace AcDream.HostParity.Tests;

/// <summary>
/// Spending experience, through the plugin surface, on both clients. A tool
/// that plans a spend reads how many times a stat has already been bought and
/// then asks the client to buy another; both halves have to answer the same on
/// the client with a window and the one without, or a bot that works in one
/// quietly does nothing in the other.
///
/// Mutation check (2026-09-21): binding the windowless arm's session commands
/// to nothing turned every request line red (Sent against Unavailable), and
/// dropping the ranks out of the skill projection turned the reading lines
/// red on both arms at once, which is how a shared-projection break shows up
/// here.
/// </summary>
public sealed class AdvancementParityTests
{
    /// <summary>Melee defence, which both arms are told about below.</summary>
    private const uint Skill = 6u;

    /// <summary>A skill neither arm has ever been told the character has.</summary>
    private const uint UnknownSkill = 9999u;

    /// <summary>Endurance, as a request names it.</summary>
    private const uint Endurance = 2u;

    /// <summary>Stamina at full, as a request names it.</summary>
    private const uint MaxStamina = 3u;

    [Fact]
    public void HowAStatWasBoughtReadsTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            Stage(arm);
            ICharacterInfo character = arm.Host.Automation.Character;

            transcript.Step("skill");
            transcript.Record(
                "found", character.TryGetSkill(Skill, out PluginSkillInfo skill));
            transcript.Record("ranks", skill.Ranks);
            transcript.Record("experience", skill.ExperienceSpent);
            // Two clients that both answer zero agree and prove nothing, so
            // each arm is held to the numbers the server really stated.
            Assert.True(
                skill.Ranks == 37u && skill.ExperienceSpent == 4242UL,
                $"the {arm.Name} client lost how the skill was bought: "
                + $"{skill.Ranks} ranks, {skill.ExperienceSpent} experience");

            transcript.Step("attributes");
            foreach (PluginAttributeInfo attribute in character.Attributes)
            {
                transcript.Record($"{attribute.Kind}.statId", attribute.StatId);
                transcript.Record($"{attribute.Kind}.ranks", attribute.Ranks);
                transcript.Record(
                    $"{attribute.Kind}.experience", attribute.ExperienceSpent);
            }

            transcript.Step("vitals");
            IReadOnlyList<PluginVitalInfo> vitals = character.Vitals;
            transcript.Record("count", vitals.Count);
            foreach (PluginVitalInfo vital in vitals)
            {
                transcript.Record($"{vital.Kind}.name", vital.Name);
                transcript.Record($"{vital.Kind}.statId", vital.StatId);
                transcript.Record($"{vital.Kind}.current", vital.Current);
                transcript.Record($"{vital.Kind}.maximum", vital.Maximum);
                transcript.Record($"{vital.Kind}.base", vital.Base);
                transcript.Record($"{vital.Kind}.ranks", vital.Ranks);
                transcript.Record(
                    $"{vital.Kind}.experience", vital.ExperienceSpent);
            }

            transcript.Step("vitals/one by kind");
            transcript.Record(
                "found", character.TryGetVital(1, out PluginVitalInfo stamina));
            transcript.Record("ranks", stamina.Ranks);
            transcript.Record("experience", stamina.ExperienceSpent);
            Assert.True(
                vitals.Count == 3 && stamina.Ranks == 9u
                    && stamina.ExperienceSpent == 777UL,
                $"the {arm.Name} client lost how a pool was bought: "
                + $"{vitals.Count} pools, {stamina.Ranks} ranks, "
                + $"{stamina.ExperienceSpent} experience");
            PluginAttributeInfo endurance = Assert.Single(
                character.Attributes,
                attribute => attribute.StatId == Endurance);
            Assert.True(
                endurance.Ranks == 11u && endurance.ExperienceSpent == 1234UL,
                $"the {arm.Name} client lost how the attribute was bought: "
                + $"{endurance.Ranks} ranks, "
                + $"{endurance.ExperienceSpent} experience");

            transcript.Step("vitals/unknown kind");
            transcript.Record(
                "found", character.TryGetVital(7, out PluginVitalInfo missing));
            transcript.Record("name", missing.Name ?? string.Empty);
        });

    /// <summary>
    /// The four kinds of spend, asked for through the plugin surface. Each
    /// client carries it to its own command adapter, and what the plugin is
    /// told back has to be the same word on both.
    /// </summary>
    [Fact]
    public void AskingToSpendAnswersTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            Stage(arm);
            ICharacterInfo character = arm.Host.Automation.Character;

            Ask(transcript, arm, "attribute",
                PluginAdvancementKind.Attribute, Endurance, 1500UL,
                PluginAdvancementStatus.Sent);
            Ask(transcript, arm, "vital",
                PluginAdvancementKind.Vital, MaxStamina, 900UL,
                PluginAdvancementStatus.Sent);
            Ask(transcript, arm, "skill",
                PluginAdvancementKind.Skill, Skill, 2500UL,
                PluginAdvancementStatus.Sent);
            Ask(transcript, arm, "train",
                PluginAdvancementKind.TrainSkill, Skill, 4UL,
                PluginAdvancementStatus.Sent);
        });

    /// <summary>
    /// Every refusal, checked at the same boundary on both clients: a request
    /// one refuses and the other sends is the difference this closes.
    /// </summary>
    [Fact]
    public void ARefusedSpendIsRefusedForTheSameReasonOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            Stage(arm);
            ICharacterInfo character = arm.Host.Automation.Character;

            Ask(transcript, arm, "no stat",
                PluginAdvancementKind.Skill, 0u, 100UL,
                PluginAdvancementStatus.UnknownStat);
            Ask(transcript, arm, "no such attribute",
                PluginAdvancementKind.Attribute, 7u, 100UL,
                PluginAdvancementStatus.UnknownStat);
            Ask(transcript, arm, "pool that cannot be bought",
                PluginAdvancementKind.Vital, 2u, 100UL,
                PluginAdvancementStatus.UnknownStat);
            Ask(transcript, arm, "no such skill",
                PluginAdvancementKind.Skill, UnknownSkill, 100UL,
                PluginAdvancementStatus.UnknownStat);
            Ask(transcript, arm, "no cost",
                PluginAdvancementKind.Skill, Skill, 0UL,
                PluginAdvancementStatus.InvalidCost);
            Ask(transcript, arm, "absurd experience",
                PluginAdvancementKind.Skill,
                Skill,
                PluginAdvancement.MaxExperienceCost + 1UL,
                PluginAdvancementStatus.InvalidCost);
            Ask(transcript, arm, "absurd credits",
                PluginAdvancementKind.TrainSkill,
                Skill,
                PluginAdvancement.MaxSkillCredits + 1UL,
                PluginAdvancementStatus.InvalidCost);
        });

    /// <summary>
    /// Before the character is in the world neither client may send, so both
    /// answer that there is nothing to send over.
    /// </summary>
    [Fact]
    public void ASpendBeforeTheWorldIsRefusedOnBothClients() =>
        ParityScenario.RunFromLogin(static (arm, transcript) =>
        {
            ICharacterInfo character = arm.Host.Automation.Character;
            transcript.Step("before the world");
            transcript.Record("isInWorld", character.IsInWorld);
            Ask(transcript, arm, "skill",
                PluginAdvancementKind.Skill, Skill, 2500UL,
                PluginAdvancementStatus.Unavailable);
        });

    private static void Ask(
        ParityTranscript transcript,
        ParityArm arm,
        string what,
        PluginAdvancementKind kind,
        uint statId,
        ulong cost,
        PluginAdvancementStatus expected)
    {
        transcript.Step($"spend/{what}");
        PluginAdvancementResult result =
            arm.Host.Automation.Character.RequestAdvancement(kind, statId, cost);
        // Two clients that both answer "no" agree line for line and prove
        // nothing, so each arm is held to the answer this request should get.
        Assert.True(
            result.Status == expected,
            $"the {arm.Name} client answered {result.Status} to {what}, "
            + $"not {expected}");
        transcript.Record("status", result.Status);
        transcript.Record("accepted", result.Accepted);
        // The words differ between refusals and that is the point of them, so
        // only whether there is one is compared across the two clients.
        transcript.Record(
            "hasNotice", !string.IsNullOrWhiteSpace(result.Notice));
    }

    /// <summary>
    /// The character both clients are asked about: one skill, one attribute
    /// and all three pools, each with ranks already bought and experience
    /// banked into them.
    /// </summary>
    private static void Stage(ParityArm arm)
    {
        ParityWorld.Stage(arm);
        arm.Server.SkillUpdate(Skill, ranks: 37u, xp: 4242u);
        arm.Server.AttributeUpdate(Endurance, ranks: 11u, xp: 1234u);
        arm.Server.VitalUpdate(1u, current: 55u, ranks: 3u, xp: 111u);
        arm.Server.VitalUpdate(MaxStamina, current: 42u, ranks: 9u, xp: 777u);
        arm.Server.VitalUpdate(5u, current: 40u, ranks: 1u, xp: 22u);
        arm.Advance();
    }
}
