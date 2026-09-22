using AcDream.Core.Items;
using AcDream.Core.Properties;
using AcDream.Plugin.Abstractions;

namespace AcDream.HostParity.Tests;

/// <summary>
/// Everything a plugin only reads. A read that answers differently is the
/// quietest kind of host difference and the hardest to notice from a bot log,
/// so each one is written down field by field rather than sampled.
///
/// Every value is recorded AND asserted, and the character these scenarios
/// read is one the server has really said something about. Recording alone
/// compares the two clients and nothing else, and this file was the worst
/// case of that: with nothing staged, every skill, attribute and pool read
/// back zero, an empty world name and a level of nought, and the two clients
/// agreed on all of it line for line. A host that lost the whole character
/// projection would have passed.
///
/// Mutation check (2026-09-20): unbinding the windowed arm's combat-mode
/// operations turned every scenario that enters a stance red, naming the
/// fields that diverged; restoring the binding turned them green.
/// Mutation check (2026-09-21), run: dropping the ranks bought into a pool
/// out of the maximum it adds up to turned
/// <see cref="TheCharacterReadsTheSameOnBothClients"/> red on both arms at
/// once, 159 against 156 -- which is exactly the shape of break the
/// comparison alone could never see, since both clients were wrong together
/// and their transcripts still matched line for line.
/// </summary>
public sealed class ReadParityTests
{
    /// <summary>Melee defence, the skill the server states below.</summary>
    private const uint MeleeDefence = 6u;

    /// <summary>Endurance, as the server names it.</summary>
    private const uint Endurance = 2u;

    /// <summary>The three pools, as the server names them on the wire.</summary>
    private const uint MaxHealth = 1u;
    private const uint MaxStamina = 3u;
    private const uint MaxMana = 5u;

    /// <summary>What the server said each pool is at.</summary>
    private const uint HealthLeft = 55u;
    private const uint StaminaLeft = 42u;
    private const uint ManaLeft = 40u;

    /// <summary>How high the server says this character is.</summary>
    private const int Level = 27;

    /// <summary>
    /// Everything the staged pools add up to: the hundred the server states
    /// as each pool's base, plus the ranks bought into it, plus what the
    /// character's endurance or self contributes. Asserted rather than left
    /// to the comparison so that two clients both answering zero -- which is
    /// what they did before this scenario staged anything -- cannot pass.
    /// </summary>
    private const uint MaxHealthTotal = 159u;
    private const uint MaxStaminaTotal = 220u;
    private const uint MaxManaTotal = 101u;

    /// <summary>Everything the staged world holds, in id order.</summary>
    private static readonly uint[] EverythingStaged =
    [
        ParityWorld.Player,
        ParityWorld.HiddenMonster,
        ParityWorld.DeadMonster,
        ParityWorld.Monster,
        ParityWorld.SecondMonster,
        ParityWorld.Bystander,
    ];

    [Fact]
    public void TheCharacterReadsTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            Stage(arm);
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

            // Who the client thinks it is playing, which every command a
            // plugin sends is built on.
            Assert.True(character.IsInWorld, $"{arm.Name} is not in the world.");
            Assert.Equal(ParityWorld.Player, character.ObjectId);
            Assert.Equal("Parity", character.Name);
            Assert.Equal(ParitySessionOperations.WorldName, character.WorldName);
            Assert.Equal("Parity", character.AccountName);
            Assert.Equal(0, character.CharacterIndex);
            Assert.Equal(
                ParitySessionOperations.ServerPopulation,
                character.ServerPopulation);
            Assert.Equal(Level, character.Level);
            // Nothing is carried in this scenario, so the main pack is empty.
            Assert.Equal(102, character.MainPackFreeSlots);

            // The pools, as the server stated them. A client that lost the
            // character projection answers zero for all six, and so does the
            // other one, which is how this used to pass.
            Assert.Equal(HealthLeft, character.CurrentHealth);
            Assert.Equal(StaminaLeft, character.CurrentStamina);
            Assert.Equal(ManaLeft, character.CurrentMana);
            Assert.Equal(MaxHealthTotal, character.MaxHealth);
            Assert.Equal(MaxStaminaTotal, character.MaxStamina);
            Assert.Equal(MaxManaTotal, character.MaxMana);
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
            Stage(arm);
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
            // The LIST is empty on both clients and is not assertable here:
            // it is built over the client's skill-name table, which is read
            // out of the data files, and neither arm has any. What the
            // server stated is read back one skill at a time below, which
            // needs no table.

            transcript.Step("attributes");
            IReadOnlyList<PluginAttributeInfo> attributes = character.Attributes;
            transcript.Record("count", attributes.Count);
            foreach (PluginAttributeInfo attribute in attributes)
            {
                transcript.Record($"{attribute.Kind}.name", attribute.Name);
                transcript.Record($"{attribute.Kind}.current", attribute.Current);
                transcript.Record($"{attribute.Kind}.base", attribute.Base);
            }
            // The one attribute the server stated: a hundred to start with
            // and eleven raises bought into it.
            PluginAttributeInfo endurance = Assert.Single(attributes);
            Assert.Equal(Endurance, endurance.StatId);
            Assert.Equal(AttributeStart + AttributeRanks, endurance.Base);
            Assert.Equal(AttributeStart + AttributeRanks, endurance.Current);

            transcript.Step("skills/one by id");
            transcript.Record(
                "found",
                character.TryGetSkill(MeleeDefence, out PluginSkillInfo one));
            transcript.Record("current", one.Current);
            transcript.Record("base", one.Base);
            transcript.Record("ranks", one.Ranks);
            transcript.Record("experience", one.ExperienceSpent);
            transcript.Record("training", one.Training);
            // What the server really said about this skill: how far it has
            // been trained and what was spent getting there. Both clients
            // answering "no such skill" would agree line for line.
            Assert.True(
                character.TryGetSkill(MeleeDefence, out _),
                $"{arm.Name} cannot find the skill the server stated.");
            Assert.Equal(MeleeDefence, one.SkillId);
            Assert.Equal(SkillRanks, one.Ranks);
            Assert.Equal(SkillExperience, one.ExperienceSpent);
            Assert.Equal(PluginSkillTraining.Trained, one.Training);

            transcript.Step("skills/one nobody stated");
            transcript.Record(
                "found", character.TryGetSkill(9999u, out PluginSkillInfo none));
            Assert.False(
                character.TryGetSkill(9999u, out _),
                $"{arm.Name} found a skill nobody ever stated.");
            Assert.Equal(0u, none.Current);
        });

    /// <summary>
    /// The world-object reader, field by field, for a creature both clients
    /// are looking at.
    /// </summary>
    [Fact]
    public void AWorldObjectReadsTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            Stage(arm);
            IWorldObjectAutomation objects = arm.Host.Automation.Objects;

            transcript.Step("objects");
            transcript.Record("isAvailable", objects.IsAvailable);
            transcript.Record("openContainer", objects.OpenContainerObjectId);
            transcript.Record(
                "found",
                objects.TryGet(ParityWorld.Monster, out PluginWorldObject found));
            RecordObject(transcript, found);
            // The creature the scenario staged, field by field. Both clients
            // answering "no such object" would agree line for line.
            Assert.True(
                objects.TryGet(ParityWorld.Monster, out _),
                $"{arm.Name} cannot see the staged creature.");
            Assert.Equal(ParityWorld.Monster, found.ObjectId);
            Assert.Equal("Monster 50000012", found.Name);
            Assert.Equal(PluginObjectClass.Monster, found.ObjectClass);
            Assert.Equal(0u, found.ContainerObjectId);
            Assert.Equal(0u, found.WielderObjectId);
            Assert.False(found.IsOwned);
            Assert.True(
                found.HasPosition,
                $"{arm.Name} knows the creature but not where it is.");

            transcript.Step("objects/unknown");
            transcript.Record(
                "found",
                objects.TryGet(0x5000BEEFu, out PluginWorldObject missing));
            RecordObject(transcript, missing);
            // A guid nothing answers to hands back an empty record rather
            // than something a plugin would act on.
            Assert.False(
                objects.TryGet(0x5000BEEFu, out _),
                $"{arm.Name} found an object nobody ever sent.");
            Assert.Equal(0u, missing.ObjectId);
            Assert.Equal(PluginObjectClass.Unknown, missing.ObjectClass);
            Assert.False(missing.HasPosition);

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
            // Everything the scenario staged, and nothing else: a client that
            // captured an empty world agrees with another that did the same.
            Assert.Equal(
                EverythingStaged,
                all.Select(static item => item.ObjectId).OrderBy(id => id));
            Assert.Equal(
                PluginObjectClass.Player,
                all.Single(static item => item.ObjectId == ParityWorld.Player)
                    .ObjectClass);
            Assert.Equal(
                PluginObjectClass.Npc,
                all.Single(static item => item.ObjectId == ParityWorld.Bystander)
                    .ObjectClass);
        });

    /// <summary>
    /// The navigation reader with no route running, which a bot consults every
    /// step.
    /// </summary>
    [Fact]
    public void TheNavigationSnapshotReadsTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            Stage(arm);
            INavigationAutomation navigation = arm.Host.Automation.Navigation;
            PluginNavigationSnapshot snapshot = navigation.Snapshot;

            transcript.Step("navigation");
            transcript.Record("isAvailable", snapshot.IsAvailable);
            transcript.Record("isPortalSpace", snapshot.IsPortalSpace);
            transcript.Record("localObjectId", snapshot.LocalObjectId);
            transcript.Record("isMoving", snapshot.IsMoving);
            transcript.Record("isAirborne", snapshot.IsAirborne);
            transcript.Record(
                "confirmedRevision", snapshot.ConfirmedPositionRevision);
            transcript.Record("cell", snapshot.Position.CellId);

            // Where the character really is, not merely that both clients
            // say the same thing about it: a bot reads this every step to
            // decide whether it has arrived.
            Assert.True(
                snapshot.IsAvailable,
                $"{arm.Name} has no navigation to read.");
            Assert.Equal(ParityWorld.Player, snapshot.LocalObjectId);
            Assert.False(snapshot.IsMoving);
            Assert.False(snapshot.IsAirborne);
            Assert.False(snapshot.IsPortalSpace);
            // The ground the harness lays down. The cell inside it is the
            // one the body settled into, which is the terrain square the
            // character is standing on rather than the one it was spawned
            // with, so what is pinned is the landblock.
            Assert.Equal(
                ParityPlayerBody.Landblock,
                snapshot.Position.CellId & 0xFFFF0000u);

            transcript.Step("navigation/how far off the creatures are");
            RecordDistance(transcript, navigation, snapshot, "monster",
                ParityWorld.Monster);
            RecordDistance(transcript, navigation, snapshot, "second",
                ParityWorld.SecondMonster);
            // The staged distances: the first creature stands three metres
            // out and the second seven. A client that read the character's
            // position off something stale answers neither.
            Assert.Equal(3d, Distance(navigation, snapshot, ParityWorld.Monster), 3);
            Assert.Equal(
                7d, Distance(navigation, snapshot, ParityWorld.SecondMonster), 3);
        });

    /// <summary>How many ranks the server states into the skill.</summary>
    private const uint SkillRanks = 37u;

    /// <summary>And what it says was spent getting them.</summary>
    private const ulong SkillExperience = 4242UL;

    /// <summary>Where the attribute started, and how far it was raised.</summary>
    private const uint AttributeStart = 100u;
    private const uint AttributeRanks = 11u;

    /// <summary>
    /// The staged world, plus everything the server has said about this
    /// character: one skill, one attribute, all three pools and the level.
    /// Without these every number the scenarios read is zero on both clients,
    /// and two clients reading zero agree.
    /// </summary>
    private static void Stage(ParityArm arm)
    {
        ParityWorld.Stage(arm);
        arm.Server.SkillUpdate(
            MeleeDefence, ranks: SkillRanks, xp: (uint)SkillExperience);
        arm.Server.AttributeUpdate(
            Endurance, ranks: AttributeRanks, start: AttributeStart, xp: 1234u);
        arm.Server.VitalUpdate(MaxHealth, current: HealthLeft, ranks: 3u, xp: 111u);
        arm.Server.VitalUpdate(
            MaxStamina, current: StaminaLeft, ranks: 9u, xp: 777u);
        arm.Server.VitalUpdate(MaxMana, current: ManaLeft, ranks: 1u, xp: 22u);
        // How high the character is, which rides on its own object rather
        // than on one of the statements above.
        if (arm.Runtime.InventoryOwner.Objects.Get(ParityWorld.Player)
            is { } player)
        {
            player.Properties.Ints[(uint)PropertyInt.Level] = Level;
            arm.Runtime.InventoryOwner.Objects.AddOrUpdate(player);
        }
        arm.Advance();
    }

    private static double Distance(
        INavigationAutomation navigation,
        PluginNavigationSnapshot snapshot,
        uint objectId) =>
        navigation.TryGetObject(objectId, out PluginNavigationObject value)
            ? snapshot.Position.HorizontalDistanceMeters(value.Position)
            : double.NaN;

    private static void RecordDistance(
        ParityTranscript transcript,
        INavigationAutomation navigation,
        PluginNavigationSnapshot snapshot,
        string name,
        uint objectId)
    {
        transcript.Record(
            $"{name}.known", navigation.TryGetObject(objectId, out _));
        transcript.Record(
            $"{name}.distance", Distance(navigation, snapshot, objectId));
    }

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
