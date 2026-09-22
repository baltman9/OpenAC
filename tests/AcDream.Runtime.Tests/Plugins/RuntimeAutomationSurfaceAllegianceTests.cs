using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Plugins;
using AcDream.Runtime.Tests.Support;

namespace AcDream.Runtime.Tests.Plugins;

/// <summary>
/// Swearing to a patron and breaking a tie. The two commands are refused on
/// different grounds on purpose: swearing is done face to face, so the patron
/// has to be a player standing there, while a tie can be broken with someone
/// a continent away who is not even logged in -- all that is needed is that
/// the server has said they are in this character's allegiance.
///
/// Mutation checks (2026-09-22), each run: dropping the
/// <c>PluginObjectClass.Player</c> arm of the swear check turned
/// <see cref="SwearingToACreatureThatIsNotAPlayerIsRefusedAndNeverSent"/> red;
/// dropping the presence check turned
/// <see cref="SwearingToAKnownPlayerWhoIsNotInTheWorldIsRefusedAndNeverSent"/>
/// red;
/// replacing the break's membership check with the swear's presence check
/// turned <see cref="BreakingWithAPatronWhoIsNotInTheWorldIsStillSent"/> red,
/// along with every row of
/// <see cref="BreakingWithSomeoneInTheAllegianceReachesTheRuntimeCommand"/>; and
/// dropping the membership check turned
/// <see cref="BreakingWithSomeoneOutsideTheAllegianceIsRefusedAndNeverSent"/>
/// red. Restoring each turned them green.
/// </summary>
public sealed class RuntimeAutomationSurfaceAllegianceTests
{
    private const uint MonarchGuid = 0x50000001u;
    private const uint PatronGuid = 0x50000002u;
    private const uint VassalGuid = 0x50000004u;

    /// <summary>Another player, standing in the world, outside the allegiance.</summary>
    private const uint Stranger = 0x50000020u;

    /// <summary>A creature standing in the world that is not a player.</summary>
    private const uint Monster = 0x50000030u;

    /// <summary>A guid nothing in the world answers to.</summary>
    private const uint Nobody = 0x500000FFu;

    /// <summary>
    /// A player the client has a record of but who is standing nowhere.
    /// </summary>
    private const uint AbsentPlayer = 0x50000021u;

    private const uint Cell = 0x0001_0100u;

    [Fact]
    public void SwearingToAPlayerStandingThereReachesTheRuntimeCommand()
    {
        using Fixture fixture = Fixture.InWorld();

        PluginAllegianceCommandResult result = fixture.Allegiance.Swear(Stranger);

        Assert.Equal(PluginAllegianceCommandStatus.Sent, result.Status);
        Assert.True(result.Accepted);
        Assert.Equal(Stranger, Assert.Single(fixture.Commands.Sworn));
        Assert.Empty(fixture.Commands.Broken);
    }

    [Fact]
    public void SwearingToNobodyIsRefusedAndNeverSent()
    {
        using Fixture fixture = Fixture.InWorld();

        PluginAllegianceCommandResult result = fixture.Allegiance.Swear(0u);

        Assert.Equal(PluginAllegianceCommandStatus.InvalidTarget, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.Notice));
        Assert.Empty(fixture.Commands.Sworn);
    }

    /// <summary>
    /// A guid the client has never been told about. Swearing at it would put
    /// a number on the wire the server can only refuse, so it stops here.
    /// </summary>
    [Fact]
    public void SwearingToSomeoneTheClientHasNeverHeardOfIsRefusedAndNeverSent()
    {
        using Fixture fixture = Fixture.InWorld();

        PluginAllegianceCommandResult result = fixture.Allegiance.Swear(Nobody);

        Assert.Equal(PluginAllegianceCommandStatus.InvalidTarget, result.Status);
        Assert.Empty(fixture.Commands.Sworn);
    }

    /// <summary>
    /// A player the client knows perfectly well -- it has the record, and the
    /// record says player -- who is not standing anywhere: no entity in the
    /// world. The client knows plenty of players it cannot see, off a roster
    /// or a chat line, and none of them can be sworn to. This is the one case
    /// where only the presence half of the check refuses.
    /// </summary>
    [Fact]
    public void SwearingToAKnownPlayerWhoIsNotInTheWorldIsRefusedAndNeverSent()
    {
        using Fixture fixture = Fixture.InWorld();
        fixture.Runtime.InventoryOwner.Objects.AddOrUpdate(PlayerObject(AbsentPlayer));
        Assert.False(
            fixture.Runtime.EntityObjects.Entities.TryGetActive(AbsentPlayer, out _));

        PluginAllegianceCommandResult result =
            fixture.Allegiance.Swear(AbsentPlayer);

        Assert.Equal(PluginAllegianceCommandStatus.InvalidTarget, result.Status);
        Assert.Empty(fixture.Commands.Sworn);
    }

    /// <summary>
    /// Something the client CAN see, standing at the same distance, that is
    /// not a player. Only the kind of object tells these two apart, so this
    /// is what says the check reads it.
    /// </summary>
    [Fact]
    public void SwearingToACreatureThatIsNotAPlayerIsRefusedAndNeverSent()
    {
        using Fixture fixture = Fixture.InWorld();

        PluginAllegianceCommandResult result = fixture.Allegiance.Swear(Monster);

        Assert.Equal(PluginAllegianceCommandStatus.InvalidTarget, result.Status);
        Assert.Empty(fixture.Commands.Sworn);
    }

    [Theory]
    [InlineData(PatronGuid)]
    [InlineData(VassalGuid)]
    [InlineData(MonarchGuid)]
    public void BreakingWithSomeoneInTheAllegianceReachesTheRuntimeCommand(
        uint target)
    {
        using Fixture fixture = Fixture.InWorld();

        PluginAllegianceCommandResult result = fixture.Allegiance.Break(target);

        Assert.Equal(PluginAllegianceCommandStatus.Sent, result.Status);
        Assert.True(result.Accepted);
        Assert.Equal(target, Assert.Single(fixture.Commands.Broken));
        Assert.Empty(fixture.Commands.Sworn);
    }

    /// <summary>
    /// The patron is nowhere in the world -- no entity, no object record --
    /// and the tie is still there to break. Said outright, because the
    /// swear's own check would turn this away.
    /// </summary>
    [Fact]
    public void BreakingWithAPatronWhoIsNotInTheWorldIsStillSent()
    {
        using Fixture fixture = Fixture.InWorld();
        Assert.False(
            fixture.Runtime.EntityObjects.Entities.TryGetActive(PatronGuid, out _));

        PluginAllegianceCommandResult result = fixture.Allegiance.Break(PatronGuid);

        Assert.Equal(PluginAllegianceCommandStatus.Sent, result.Status);
        Assert.Equal(PatronGuid, Assert.Single(fixture.Commands.Broken));
    }

    [Fact]
    public void BreakingWithNobodyIsRefusedAndNeverSent()
    {
        using Fixture fixture = Fixture.InWorld();

        PluginAllegianceCommandResult result = fixture.Allegiance.Break(0u);

        Assert.Equal(PluginAllegianceCommandStatus.InvalidTarget, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.Notice));
        Assert.Empty(fixture.Commands.Broken);
    }

    /// <summary>
    /// Another player, standing right there, who is in no allegiance with
    /// this character. Being visible is not what a break needs.
    /// </summary>
    [Fact]
    public void BreakingWithSomeoneOutsideTheAllegianceIsRefusedAndNeverSent()
    {
        using Fixture fixture = Fixture.InWorld();

        PluginAllegianceCommandResult result = fixture.Allegiance.Break(Stranger);

        Assert.Equal(PluginAllegianceCommandStatus.InvalidTarget, result.Status);
        Assert.Empty(fixture.Commands.Broken);
    }

    /// <summary>
    /// Off-world both commands answer the same inert word, and neither
    /// reaches the wire.
    /// </summary>
    [Fact]
    public void NeitherCommandIsSentBeforeTheCharacterIsInTheWorld()
    {
        using var surface = new RuntimeAutomationSurface();
        var commands = new RecordingCommands();
        using GameRuntime runtime = GameRuntimeTestFactory.Create();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        surface.BindSessionCommands(commands);
        Assert.False(surface.IsAvailable);
        IAllegianceAutomation allegiance = surface;

        Assert.Equal(
            PluginAllegianceCommandStatus.Unavailable,
            allegiance.Swear(Stranger).Status);
        Assert.Equal(
            PluginAllegianceCommandStatus.Unavailable,
            allegiance.Break(PatronGuid).Status);
        Assert.Empty(commands.Sworn);
        Assert.Empty(commands.Broken);
    }

    /// <summary>A surface no host ever bound answers rather than throwing.</summary>
    [Fact]
    public void AnUnboundSurfaceAnswersUnavailableRatherThanThrowing()
    {
        using var surface = new RuntimeAutomationSurface();
        IAllegianceAutomation allegiance = surface;

        Assert.Equal(
            PluginAllegianceCommandStatus.Unavailable,
            allegiance.Swear(Stranger).Status);
        Assert.Equal(
            PluginAllegianceCommandStatus.Unavailable,
            allegiance.Break(PatronGuid).Status);
    }

    /// <summary>
    /// A live client with a character in the world, an allegiance the server
    /// has stated, two things standing next to the character -- one a player
    /// and one not -- and a command adapter that writes down what it is asked
    /// to send instead of sending it.
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

        internal IAllegianceAutomation Allegiance => Surface;

        internal RecordingCommands Commands { get; }

        internal GameRuntime Runtime => _host.Runtime;

        internal static Fixture InWorld()
        {
            var host = new NoWindowGameRuntimeHost();
            host.Start();
            for (int tick = 0; tick < 4; tick++)
                host.Session.Tick();
            GameRuntime runtime = host.Runtime;
            Assert.True(runtime.Session.IsInWorld);

            runtime.AllegianceOwner!.ApplyUpdate(Profile());
            Stage(runtime, Stranger, PlayerObject(Stranger));
            Stage(runtime, Monster, MonsterObject(Monster));

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
    /// The allegiance the server states: a monarch, the patron above this
    /// character, and a vassal below it.
    /// </summary>
    private static ClientCommandResponses.AllegianceUpdate Profile() =>
        new(
            Rank: 3u,
            TotalMembers: 4u,
            TotalVassals: 1u,
            RecordCount: 4,
            AllegianceName: "The Order",
            Monarch: new ClientCommandResponses.AllegianceMemberRecord(
                MonarchGuid, 0u, true, "Monarch"),
            Records:
            [
                new ClientCommandResponses.AllegianceMemberRecord(
                    PatronGuid, MonarchGuid, true, "Patron"),
                new ClientCommandResponses.AllegianceMemberRecord(
                    VassalGuid, PatronGuid, false, "Vassal"),
            ]);

    private static ClientObject PlayerObject(uint objectId) => new()
    {
        ObjectId = objectId,
        Type = ItemType.Creature,
        Name = $"Player {objectId:X8}",
        // The bit that makes an object a player rather than a creature.
        PublicWeenieBitfield = 0x00000008u,
    };

    private static ClientObject MonsterObject(uint objectId) => new()
    {
        ObjectId = objectId,
        Type = ItemType.Creature,
        Name = $"Monster {objectId:X8}",
        PublicWeenieBitfield = 0u,
    };

    /// <summary>Puts one thing in the world, as a create from the server does.</summary>
    private static void Stage(GameRuntime runtime, uint guid, ClientObject item)
    {
        RuntimeEntityRecord record = runtime.EntityObjects
            .RegisterEntity(Spawn(guid))
            .Canonical!;
        runtime.EntityObjects.ApplyAcceptedSpawn(
            record,
            record.CreateIntegrationVersion,
            record.Snapshot,
            replaceGeneration: false);
        runtime.InventoryOwner.Objects.AddOrUpdate(item);
    }

    private static WorldSession.EntitySpawn Spawn(uint guid)
    {
        var position = new CreateObject.ServerPosition(
            Cell, 10f, 20f, 7f, 1f, 0f, 0f, 0f);
        var timestamps = new PhysicsTimestamps(
            Position: 1,
            Movement: 1,
            State: 1,
            Vector: 1,
            Teleport: 0,
            ServerControlledMove: 1,
            ForcePosition: 0,
            ObjDesc: 1,
            Instance: 1);
        var physics = new PhysicsSpawnData(
            RawState: (uint)PhysicsStateFlags.Gravity,
            Position: position,
            Movement: null,
            AnimationFrame: null,
            SetupTableId: null,
            MotionTableId: null,
            SoundTableId: null,
            PhysicsScriptTableId: null,
            Parent: null,
            Children: null,
            Scale: null,
            Friction: null,
            Elasticity: null,
            Translucency: null,
            Velocity: null,
            Acceleration: null,
            AngularVelocity: null,
            DefaultScriptType: null,
            DefaultScriptIntensity: null,
            Timestamps: timestamps);
        return new WorldSession.EntitySpawn(
            guid,
            position,
            SetupTableId: null,
            Array.Empty<CreateObject.AnimPartChange>(),
            Array.Empty<CreateObject.TextureChange>(),
            Array.Empty<CreateObject.SubPaletteSwap>(),
            BasePaletteId: null,
            ObjScale: null,
            Name: guid.ToString("X8"),
            ItemType: null,
            MotionState: null,
            MotionTableId: null,
            PhysicsState: physics.RawState,
            InstanceSequence: 1,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: physics);
    }

    /// <summary>
    /// Stands where the client's own command adapter stands and writes down
    /// every allegiance command it is handed, so a test can tell one that was
    /// refused at the surface from one that reached the wire.
    /// </summary>
    private sealed class RecordingCommands
        : IGameRuntimeCommands, IRuntimeAllegianceCommands, IRuntimeMovementCommands
    {
        private readonly List<uint> _sworn = [];
        private readonly List<uint> _broken = [];

        internal IReadOnlyList<uint> Sworn => _sworn;

        internal IReadOnlyList<uint> Broken => _broken;

        public IRuntimeAllegianceCommands Allegiance => this;

        // Read when the surface is bound, so it has to answer.
        public IRuntimeMovementCommands Movement => this;

        public IRuntimeCharacterCommands Character => throw Unused();
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

        public RuntimeCommandResult Swear(
            RuntimeGenerationToken expectedGeneration,
            uint patronGuid)
        {
            _sworn.Add(patronGuid);
            return new(RuntimeCommandStatus.Accepted, expectedGeneration);
        }

        public RuntimeCommandResult Break(
            RuntimeGenerationToken expectedGeneration,
            uint targetGuid)
        {
            _broken.Add(targetGuid);
            return new(RuntimeCommandStatus.Accepted, expectedGeneration);
        }

        public RuntimeCommandResult Kick(
            RuntimeGenerationToken expectedGeneration,
            uint vassalGuid) => throw Unused();

        public RuntimeCommandResult RequestInfo(
            RuntimeGenerationToken expectedGeneration,
            string playerName) => throw Unused();

        public RuntimeCommandResult SetUpdateSubscription(
            RuntimeGenerationToken expectedGeneration,
            bool on) => throw Unused();

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
            new("This scenario does not reach that command.");
    }
}
