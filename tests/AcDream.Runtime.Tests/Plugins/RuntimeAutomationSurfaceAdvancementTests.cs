using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Plugins;
using AcDream.Runtime.Tests.Support;

namespace AcDream.Runtime.Tests.Plugins;

/// <summary>
/// Reading how a stat was bought, and buying more of it. A tool that spends
/// experience needs both halves: without the ranks it cannot price the next
/// raise, and without a way to ask it cannot buy one.
///
/// Mutation check (2026-09-21): dropping the ranks and banked experience out
/// of the three projections turned the reading test red on every field, and
/// returning <see cref="PluginAdvancementStatus.Sent"/> without checking the
/// stat or the cost turned each refusal test red in turn.
/// </summary>
public sealed class RuntimeAutomationSurfaceAdvancementTests
{
    /// <summary>Melee defence, which the fixture character carries.</summary>
    private const uint KnownSkill = 6u;

    /// <summary>A skill the server has never mentioned.</summary>
    private const uint UnknownSkill = 9999u;

    /// <summary>Endurance, as an advancement request names it.</summary>
    private const uint EnduranceStatId = 2u;

    /// <summary>Stamina at full, as an advancement request names it.</summary>
    private const uint MaxStaminaStatId = 3u;

    [Fact]
    public void RanksAndBankedExperienceReachTheSkillAttributeAndVitalRecords()
    {
        using Fixture fixture = Fixture.InWorld();
        ICharacterInfo character = fixture.Surface;

        Assert.True(character.TryGetSkill(KnownSkill, out PluginSkillInfo skill));
        Assert.Equal(37u, skill.Ranks);
        Assert.Equal(4242UL, skill.ExperienceSpent);

        PluginAttributeInfo endurance = Assert.Single(
            character.Attributes,
            attribute => attribute.StatId == EnduranceStatId);
        Assert.Equal(11u, endurance.Ranks);
        Assert.Equal(1234UL, endurance.ExperienceSpent);
        Assert.Equal("Endurance", endurance.Name);

        Assert.True(character.TryGetVital(1, out PluginVitalInfo stamina));
        Assert.Equal(MaxStaminaStatId, stamina.StatId);
        Assert.Equal("Stamina", stamina.Name);
        Assert.Equal(9u, stamina.Ranks);
        Assert.Equal(777UL, stamina.ExperienceSpent);
        Assert.Equal(42u, stamina.Current);
    }

    /// <summary>
    /// The three pools come back in the order a plugin indexes them by, and
    /// each one carries the number a request names it by.
    /// </summary>
    [Fact]
    public void TheVitalListIsHealthStaminaAndManaWithTheirRequestNumbers()
    {
        using Fixture fixture = Fixture.InWorld();

        IReadOnlyList<PluginVitalInfo> vitals = fixture.Surface.Vitals;

        Assert.Equal(
            ["Health", "Stamina", "Mana"],
            vitals.Select(static vital => vital.Name));
        Assert.Equal([0, 1, 2], vitals.Select(static vital => vital.Kind));
        Assert.Equal([1u, 3u, 5u], vitals.Select(static vital => vital.StatId));
    }

    [Theory]
    [InlineData(PluginAdvancementKind.Attribute, EnduranceStatId, 1500UL)]
    [InlineData(PluginAdvancementKind.Vital, MaxStaminaStatId, 900UL)]
    [InlineData(PluginAdvancementKind.Skill, KnownSkill, 2500UL)]
    [InlineData(PluginAdvancementKind.TrainSkill, KnownSkill, 4UL)]
    public void ASpendReachesTheRuntimeCommandUnchanged(
        PluginAdvancementKind kind,
        uint statId,
        ulong cost)
    {
        using Fixture fixture = Fixture.InWorld();

        PluginAdvancementResult result =
            fixture.Surface.RequestAdvancement(kind, statId, cost);

        Assert.Equal(PluginAdvancementStatus.Sent, result.Status);
        Assert.True(result.Accepted);
        RuntimeAdvancementCommand sent = Assert.Single(fixture.Commands.Sent);
        Assert.Equal((int)kind, (int)sent.Kind);
        Assert.Equal(statId, sent.StatId);
        Assert.Equal(cost, sent.Cost);
    }

    /// <summary>
    /// A stat id of zero names nothing on any of the four kinds, so none of
    /// them may reach the wire with one.
    /// </summary>
    [Theory]
    [InlineData(PluginAdvancementKind.Attribute)]
    [InlineData(PluginAdvancementKind.Vital)]
    [InlineData(PluginAdvancementKind.Skill)]
    [InlineData(PluginAdvancementKind.TrainSkill)]
    public void ASpendOnStatZeroIsRefusedAndNeverSent(PluginAdvancementKind kind)
    {
        using Fixture fixture = Fixture.InWorld();

        PluginAdvancementResult result =
            fixture.Surface.RequestAdvancement(kind, 0u, 100UL);

        Assert.Equal(PluginAdvancementStatus.UnknownStat, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.Notice));
        Assert.Empty(fixture.Commands.Sent);
    }

    /// <summary>
    /// A number outside the attributes and pools that exist, and a skill the
    /// character was never said to have, are all refused before the wire.
    /// </summary>
    [Theory]
    // There is no seventh attribute.
    [InlineData(PluginAdvancementKind.Attribute, 7u)]
    // 2 is "health as it stands", which names a pool but cannot be bought.
    [InlineData(PluginAdvancementKind.Vital, 2u)]
    [InlineData(PluginAdvancementKind.Skill, UnknownSkill)]
    [InlineData(PluginAdvancementKind.TrainSkill, UnknownSkill)]
    public void ASpendOnAStatTheCharacterHasNotGotIsRefusedAndNeverSent(
        PluginAdvancementKind kind,
        uint statId)
    {
        using Fixture fixture = Fixture.InWorld();

        PluginAdvancementResult result =
            fixture.Surface.RequestAdvancement(kind, statId, 100UL);

        Assert.Equal(PluginAdvancementStatus.UnknownStat, result.Status);
        Assert.Empty(fixture.Commands.Sent);
    }

    [Fact]
    public void ASpendOfNothingIsRefusedAndNeverSent()
    {
        using Fixture fixture = Fixture.InWorld();

        PluginAdvancementResult result = fixture.Surface.RequestAdvancement(
            PluginAdvancementKind.Skill, KnownSkill, 0UL);

        Assert.Equal(PluginAdvancementStatus.InvalidCost, result.Status);
        Assert.Empty(fixture.Commands.Sent);
    }

    /// <summary>
    /// Experience and skill credits have their own ceilings, and a cost above
    /// either is a mistake rather than a spend: the credit one matters most,
    /// because a number that does not fit is otherwise cut down to one that
    /// does on its way to the wire.
    /// </summary>
    [Theory]
    [InlineData(
        PluginAdvancementKind.Skill,
        PluginAdvancement.MaxExperienceCost + 1UL)]
    [InlineData(PluginAdvancementKind.Skill, ulong.MaxValue)]
    [InlineData(
        PluginAdvancementKind.TrainSkill,
        PluginAdvancement.MaxSkillCredits + 1UL)]
    [InlineData(PluginAdvancementKind.TrainSkill, 0x1_0000_0001UL)]
    public void AnAbsurdCostIsRefusedAndNeverSent(
        PluginAdvancementKind kind,
        ulong cost)
    {
        using Fixture fixture = Fixture.InWorld();

        PluginAdvancementResult result =
            fixture.Surface.RequestAdvancement(kind, KnownSkill, cost);

        Assert.Equal(PluginAdvancementStatus.InvalidCost, result.Status);
        Assert.Empty(fixture.Commands.Sent);
    }

    /// <summary>
    /// Before the character is in the world there is nothing to spend on and
    /// no connection to spend over, so the request is refused rather than
    /// queued.
    /// </summary>
    [Fact]
    public void ASpendBeforeTheCharacterIsInTheWorldIsRefusedAndNeverSent()
    {
        using var surface = new RuntimeAutomationSurface();
        var commands = new RecordingCommands();
        using GameRuntime runtime = GameRuntimeTestFactory.Create();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        surface.BindSessionCommands(commands);
        Assert.False(surface.IsAvailable);

        PluginAdvancementResult result = surface.RequestAdvancement(
            PluginAdvancementKind.Attribute, EnduranceStatId, 100UL);

        Assert.Equal(PluginAdvancementStatus.Unavailable, result.Status);
        Assert.Empty(commands.Sent);
    }

    /// <summary>
    /// A character with no session behind it answers the same way, and none
    /// of the reads throw.
    /// </summary>
    [Fact]
    public void AnUnboundSurfaceAnswersEmptyRatherThanThrowing()
    {
        using var surface = new RuntimeAutomationSurface();
        ICharacterInfo character = surface;

        Assert.Empty(character.Vitals);
        Assert.False(character.TryGetVital(0, out PluginVitalInfo vital));
        Assert.Equal(default, vital);
        Assert.Equal(
            PluginAdvancementStatus.Unavailable,
            character.RequestAdvancement(
                PluginAdvancementKind.Skill, KnownSkill, 10UL).Status);
    }

    /// <summary>
    /// A live client with a character in the world, the stats the server has
    /// stated about it, and a command adapter that writes down what it is
    /// asked to send instead of sending it.
    /// </summary>
    private sealed class Fixture : IDisposable
    {
        private readonly NoWindowGameRuntimeHost _host;

        private Fixture(
            NoWindowGameRuntimeHost host,
            RuntimeAutomationSurface surface,
            RecordingCommands commands)
        {
            _host = host;
            Surface = surface;
            Commands = commands;
        }

        internal RuntimeAutomationSurface Surface { get; }

        internal RecordingCommands Commands { get; }

        internal static Fixture InWorld()
        {
            var host = new NoWindowGameRuntimeHost();
            host.Start();
            for (int tick = 0; tick < 4; tick++)
                host.Session.Tick();
            GameRuntime runtime = host.Runtime;
            Assert.True(runtime.Session.IsInWorld);

            AcDream.Core.Player.LocalPlayerState player =
                runtime.CharacterOwner.LocalPlayer;
            player.OnSkillWireUpdate(
                KnownSkill,
                ranks: 37u,
                status: 2u,
                xp: 4242u,
                init: 5u,
                resistance: 0u,
                lastUsed: 0d);
            player.OnAttributeUpdate(
                EnduranceStatId, ranks: 11u, start: 100u, xp: 1234u);
            player.OnVitalUpdate(
                1u, ranks: 3u, start: 60u, xp: 111u, current: 55u);
            player.OnVitalUpdate(
                MaxStaminaStatId,
                ranks: 9u,
                start: 50u,
                xp: 777u,
                current: 42u);
            player.OnVitalUpdate(
                5u, ranks: 1u, start: 40u, xp: 22u, current: 40u);

            var surface = new RuntimeAutomationSurface();
            surface.Bind(
                runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
            var commands = new RecordingCommands();
            surface.BindSessionCommands(commands);
            Assert.True(surface.IsAvailable);
            return new Fixture(host, surface, commands);
        }

        public void Dispose()
        {
            Surface.Dispose();
            _host.Dispose();
        }
    }

    /// <summary>
    /// Stands where the client's own command adapter stands and writes down
    /// every advancement it is handed, so a test can tell a request that was
    /// refused at the surface from one that reached the wire.
    /// </summary>
    private sealed class RecordingCommands
        : IGameRuntimeCommands, IRuntimeCharacterCommands, IRuntimeMovementCommands
    {
        private readonly List<RuntimeAdvancementCommand> _sent = [];

        internal IReadOnlyList<RuntimeAdvancementCommand> Sent => _sent;

        public IRuntimeCharacterCommands Character => this;

        // Read when the surface is bound, so it has to answer.
        public IRuntimeMovementCommands Movement => this;

        public IRuntimeSessionCommands Session => throw Unused();
        public IRuntimeSelectionCommands Selection => throw Unused();
        public IRuntimeCombatCommands Combat => throw Unused();
        public IRuntimeMagicCommands Magic => throw Unused();
        public IRuntimeChatCommands Chat => throw Unused();
        public IRuntimePortalCommands Portal => throw Unused();
        public IRuntimeInventoryStateCommands InventoryState => throw Unused();
        public IRuntimeSpellbookCommands Spellbook => throw Unused();
        public IRuntimeSocialCommands Social => throw Unused();
        public IRuntimeFellowshipCommands Fellowship => throw Unused();
        public IRuntimeAllegianceCommands Allegiance => throw Unused();

        public RuntimeCommandResult Advance(
            RuntimeGenerationToken expectedGeneration,
            in RuntimeAdvancementCommand command)
        {
            _sent.Add(command);
            return new(RuntimeCommandStatus.Accepted, expectedGeneration);
        }

        public RuntimeCommandResult SetSingleOption(
            RuntimeGenerationToken expectedGeneration,
            uint optionId,
            bool value) => throw Unused();

        public RuntimeCommandResult SaveOptions(
            RuntimeGenerationToken expectedGeneration) => throw Unused();

        public RuntimeCommandResult SetTitle(
            RuntimeGenerationToken expectedGeneration,
            uint titleId) => throw Unused();

        public RuntimeCommandResult Execute(
            RuntimeGenerationToken expectedGeneration,
            RuntimeMovementCommand command) => throw Unused();

        public RuntimeCommandResult ExecuteMotion(
            RuntimeGenerationToken expectedGeneration,
            uint motionCommand) => throw Unused();

        public RuntimeCommandResult SetIntent(
            RuntimeGenerationToken expectedGeneration,
            in MovementInput input) => throw Unused();

        public RuntimeCommandResult ClearIntent(
            RuntimeGenerationToken expectedGeneration) => throw Unused();

        public RuntimeCommandResult TurnToHeading(
            RuntimeGenerationToken expectedGeneration,
            float headingDegrees,
            bool applyRunHoldKey = false) => throw Unused();

        public RuntimeCommandResult BeginMove(
            RuntimeGenerationToken expectedGeneration,
            in RuntimeMoveRequest request) => throw Unused();

        public RuntimeCommandResult StopMove(
            RuntimeGenerationToken expectedGeneration) => throw Unused();

        public RuntimeCommandResult StopMove(
            RuntimeGenerationToken expectedGeneration,
            RuntimeMoveChannel channel) => throw Unused();

        public RuntimeCommandResult Jump(
            RuntimeGenerationToken expectedGeneration,
            float power) => throw Unused();

        private static NotSupportedException Unused() =>
            new("This test adapter answers only advancement commands.");
    }
}
