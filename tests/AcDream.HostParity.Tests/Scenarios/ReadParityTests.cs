using AcDream.Plugin.Abstractions;

namespace AcDream.HostParity.Tests;

/// <summary>
/// Everything a plugin only reads. A read that answers differently is the
/// quietest kind of host difference and the hardest to notice from a bot log,
/// so each one is written down field by field rather than sampled.
///
/// Mutation check (2026-09-20): unbinding the windowed arm's combat-mode
/// operations turned every scenario that enters a stance red, naming the
/// fields that diverged; restoring the binding turned them green.
/// </summary>
public sealed class ReadParityTests
{
    [Fact]
    public void TheCharacterReadsTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            ParityWorld.Stage(arm.Runtime);
            arm.Advance();
            ICharacterInfo character = arm.Host.Automation.Character;

            transcript.Step("character");
            transcript.Record("isInWorld", character.IsInWorld);
            transcript.Record("objectId", character.ObjectId);
            transcript.Record("name", character.Name);
            transcript.Record("worldName", character.WorldName);
            transcript.Record("accountName", character.AccountName);
            transcript.Record("characterIndex", character.CharacterIndex);
            transcript.Record("serverPopulation", character.ServerPopulation);
            transcript.Record("level", character.Level);
            transcript.Record("mainPackFreeSlots", character.MainPackFreeSlots);
            transcript.Record("health", character.CurrentHealth);
            transcript.Record("maxHealth", character.MaxHealth);
            transcript.Record("stamina", character.CurrentStamina);
            transcript.Record("maxStamina", character.MaxStamina);
            transcript.Record("mana", character.CurrentMana);
            transcript.Record("maxMana", character.MaxMana);
        });

    /// <summary>
    /// Skills field by field, including the level in effect against the level
    /// before enchantments -- the pair the windowless host once lost an
    /// attribute term out of.
    /// </summary>
    [Fact]
    public void SkillsAndAttributesReadTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            ParityWorld.Stage(arm.Runtime);
            arm.Advance();
            ICharacterInfo character = arm.Host.Automation.Character;

            transcript.Step("skills");
            IReadOnlyList<PluginSkillInfo> skills = character.Skills;
            transcript.Record("count", skills.Count);
            foreach (PluginSkillInfo skill in skills)
            {
                transcript.Record($"{skill.SkillId}.name", skill.Name);
                transcript.Record($"{skill.SkillId}.training", skill.Training);
                transcript.Record($"{skill.SkillId}.current", skill.Current);
                transcript.Record($"{skill.SkillId}.base", skill.Base);
                transcript.Record($"{skill.SkillId}.icon", skill.IconId);
            }

            transcript.Step("attributes");
            IReadOnlyList<PluginAttributeInfo> attributes = character.Attributes;
            transcript.Record("count", attributes.Count);
            foreach (PluginAttributeInfo attribute in attributes)
            {
                transcript.Record($"{attribute.Kind}.name", attribute.Name);
                transcript.Record($"{attribute.Kind}.current", attribute.Current);
                transcript.Record($"{attribute.Kind}.base", attribute.Base);
            }

            transcript.Step("skills/one by id");
            transcript.Record(
                "found", character.TryGetSkill(6u, out PluginSkillInfo one));
            transcript.Record("current", one.Current);
            transcript.Record("base", one.Base);
        });

    /// <summary>
    /// The world-object reader, field by field, for a creature both clients
    /// are looking at.
    /// </summary>
    [Fact]
    public void AWorldObjectReadsTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            ParityWorld.Stage(arm.Runtime);
            arm.Advance();
            IWorldObjectAutomation objects = arm.Host.Automation.Objects;

            transcript.Step("objects");
            transcript.Record("isAvailable", objects.IsAvailable);
            transcript.Record("openContainer", objects.OpenContainerObjectId);
            transcript.Record(
                "found",
                objects.TryGet(ParityWorld.Monster, out PluginWorldObject found));
            RecordObject(transcript, found);

            transcript.Step("objects/unknown");
            transcript.Record(
                "found",
                objects.TryGet(0x5000BEEFu, out PluginWorldObject missing));
            RecordObject(transcript, missing);

            transcript.Step("objects/all");
            IReadOnlyList<PluginWorldObject> all = objects.CaptureObjects();
            transcript.Record("count", all.Count);
            foreach (PluginWorldObject captured in all
                .OrderBy(static item => item.ObjectId))
            {
                transcript.Record($"{captured.ObjectId:X8}.name", captured.Name);
                transcript.Record(
                    $"{captured.ObjectId:X8}.class", captured.ObjectClass);
            }
        });

    /// <summary>
    /// The navigation reader with no route running, which a bot consults every
    /// step.
    /// </summary>
    [Fact]
    public void TheNavigationSnapshotReadsTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            ParityWorld.Stage(arm.Runtime);
            arm.Advance();
            PluginNavigationSnapshot navigation =
                arm.Host.Automation.Navigation.Snapshot;

            transcript.Step("navigation");
            transcript.Record("isAvailable", navigation.IsAvailable);
            transcript.Record("isPortalSpace", navigation.IsPortalSpace);
            transcript.Record("localObjectId", navigation.LocalObjectId);
            transcript.Record("isMoving", navigation.IsMoving);
            transcript.Record("isAirborne", navigation.IsAirborne);
            transcript.Record(
                "confirmedRevision", navigation.ConfirmedPositionRevision);
        });

    private static void RecordObject(
        ParityTranscript transcript, PluginWorldObject value)
    {
        transcript.Record("id", value.ObjectId);
        transcript.Record("weenieClass", value.WeenieClassId);
        transcript.Record("name", value.Name);
        transcript.Record("class", value.ObjectClass);
        transcript.Record("itemType", value.ItemType);
        transcript.Record("container", value.ContainerObjectId);
        transcript.Record("wielder", value.WielderObjectId);
        transcript.Record("isOwned", value.IsOwned);
        transcript.Record("isLandscape", value.IsLandscape);
        transcript.Record("hasPosition", value.HasPosition);
    }
}
