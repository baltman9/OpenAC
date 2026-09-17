using System.Net;
using System.Numerics;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Spells;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Navigation;
using AcDream.Runtime.Session;

namespace AcDream.Runtime.Tests.Navigation;

// One walk at a time, and it belongs to whoever asked: a plugin, or the player. Another
// plugin is held rather than taking the character; the player always wins; a plugin
// that goes away takes its walk and pauses with it.
public sealed class RuntimeNavigationOwnershipTests
{
    private const uint Goal = 0x50000123u;

    [Fact]
    public void ASecondPluginIsHeldWhileAnothersWalkIsUnderWay()
    {
        using var h = new Harness();
        INavigationAutomation first = h.Navigation.ScopeTo("first.plugin");
        INavigationAutomation second = h.Navigation.ScopeTo("second.plugin");

        Assert.Equal(PluginNavigationCommandStatus.Accepted, first.GoTo(Goal, 2f));
        Assert.Equal("first.plugin", h.Navigation.WalkOwner);
        Assert.Equal("first.plugin", second.GoToReport.Owner);

        Assert.Equal(PluginNavigationCommandStatus.Held, second.GoTo(Goal, 2f));
        Assert.Equal(PluginNavigationCommandStatus.Held, second.StandOn(Goal, 2f));
        Assert.Equal(PluginNavigationCommandStatus.Held, second.Follow(Goal, 3f));
        Assert.Equal(PluginNavigationCommandStatus.Held, second.StopGoTo());
        Assert.Equal("first.plugin", h.Navigation.WalkOwner);

        Assert.Equal(PluginNavigationCommandStatus.Accepted, first.StopGoTo());
        Assert.Null(h.Navigation.WalkOwner);
        Assert.Equal(PluginNavigationCommandStatus.Accepted, second.GoTo(Goal, 2f));
        Assert.Equal("second.plugin", h.Navigation.WalkOwner);
    }

    [Fact]
    public void ThePlayerReplacesAndStopsAnyPluginsWalk()
    {
        using var h = new Harness();
        INavigationAutomation plugin = h.Navigation.ScopeTo("some.plugin");

        Assert.Equal(PluginNavigationCommandStatus.Accepted, plugin.GoTo(Goal, 2f));
        Assert.Equal(PluginNavigationCommandStatus.Accepted, h.Navigation.GoTo(Goal, 2f));
        Assert.Equal(RuntimeNavigationAutomation.PlayerOwner, h.Navigation.WalkOwner);
        Assert.Equal(RuntimeNavigationAutomation.PlayerOwner, plugin.GoToReport.Owner);

        Assert.Equal(PluginNavigationCommandStatus.Held, plugin.GoTo(Goal, 2f));
        Assert.Equal(PluginNavigationCommandStatus.Held, plugin.StopGoTo());
        Assert.Equal(PluginNavigationCommandStatus.Accepted, h.Navigation.StopGoTo());
        Assert.Null(h.Navigation.WalkOwner);
    }

    [Fact]
    public void APluginKeepsItsOwnWalkAndMayReplaceIt()
    {
        using var h = new Harness();
        INavigationAutomation plugin = h.Navigation.ScopeTo("some.plugin");

        Assert.Equal(PluginNavigationCommandStatus.Accepted, plugin.GoTo(Goal, 2f));
        Assert.Equal(PluginNavigationCommandStatus.Accepted, plugin.StandOn(Goal, 2f));
        Assert.Equal("some.plugin", h.Navigation.WalkOwner);
        Assert.Equal(PluginNavigationCommandStatus.Accepted, plugin.StopGoTo());
        Assert.Equal(PluginNavigationCommandStatus.Rejected, plugin.StopGoTo());
    }

    [Fact]
    public void ReleasingAPluginStopsItsWalkAndDropsItsPausesButNotAnothers()
    {
        using var h = new Harness();
        INavigationAutomation going = h.Navigation.ScopeTo("going.plugin");
        INavigationAutomation staying = h.Navigation.ScopeTo("staying.plugin");
        Assert.Equal(PluginNavigationCommandStatus.Accepted, going.GoTo(Goal, 2f));
        IDisposable goingPause = going.PauseGoToWhile(() => "the going plugin is fighting");
        IDisposable stayingPause = staying.PauseGoToWhile(() => "the staying plugin is looting");
        Assert.Equal("the going plugin is fighting", h.Navigation.PauseReason());

        h.Navigation.Release("going.plugin");

        Assert.Null(h.Navigation.WalkOwner);
        Assert.Null(going.GoToReport.Owner);
        Assert.Equal("the staying plugin is looting", h.Navigation.PauseReason());
        goingPause.Dispose();
        stayingPause.Dispose();
        Assert.Null(h.Navigation.PauseReason());
        Assert.Equal(PluginNavigationCommandStatus.Accepted, staying.GoTo(Goal, 2f));
    }

    [Fact]
    public void ReleasingAPluginThatOwnsNothingChangesNothing()
    {
        using var h = new Harness();
        INavigationAutomation owner = h.Navigation.ScopeTo("owner.plugin");
        Assert.Equal(PluginNavigationCommandStatus.Accepted, owner.GoTo(Goal, 2f));

        h.Navigation.Release("bystander.plugin");

        Assert.Equal("owner.plugin", h.Navigation.WalkOwner);
    }

    [Fact]
    public void ArgumentsAreStillCheckedBeforeOwnership()
    {
        using var h = new Harness();
        INavigationAutomation first = h.Navigation.ScopeTo("first.plugin");
        INavigationAutomation second = h.Navigation.ScopeTo("second.plugin");
        Assert.Equal(PluginNavigationCommandStatus.Accepted, first.GoTo(Goal, 2f));

        Assert.Equal(PluginNavigationCommandStatus.Rejected, second.GoTo(0u, 2f));
        Assert.Equal(PluginNavigationCommandStatus.Rejected, second.GoTo(Goal, 0f));
    }

    private sealed class Harness : IDisposable
    {
        internal readonly RuntimeNavigationAutomation Navigation;
        private readonly GameRuntime _runtime;

        internal Harness()
        {
            var gameplay = new InertOperations();
            _runtime = new GameRuntime(new GameRuntimeDependencies(
                gameplay,
                gameplay,
                gameplay,
                gameplay,
                SessionOperations: new RealSessionOperations()));
            var session = new LiveSessionHost(
                _runtime.Session,
                new LiveSessionHostBindings(
                    new LiveSessionRoutingFactories(
                        _ => new NoOpEventRoute(),
                        _ => new NoOpCommandRoute()),
                    _ => { },
                    new LiveSessionSelectionBindings(
                        id => _runtime.PlayerIdentity.ServerGuid = id,
                        _ => { },
                        _ => { },
                        _ => { },
                        _ => { },
                        () => { }),
                    new LiveSessionEnteredWorldBindings(
                        _ => { },
                        () => { },
                        () => { },
                        _ => { },
                        () => { }),
                    (_, _, _) => { },
                    () => { },
                    _ => { },
                    _ => { }),
                new LiveSessionConnectOptions(
                    true,
                    "127.0.0.1",
                    9000,
                    "user",
                    "password"),
                runtime: _runtime);
            var commands = new DirectGameRuntimeCommandAdapter(_runtime, session);
            _ = commands.Start(_runtime.Generation);
            Assert.True(_runtime.Session.IsInWorld);

            Navigation = new RuntimeNavigationAutomation();
            Navigation.Bind(_runtime);
            Navigation.BindWalk(new NavigationWalkController(new PhysicsEngine(), new NoWalkBody(), new NoWalkGoals()));
        }

        public void Dispose() => _runtime.Dispose();
    }

    private sealed class NoWalkBody : INavigationWalkBody
    {
        public bool TrySample(out NavigationWalkBodySample sample)
        {
            sample = default;
            return false;
        }

        public bool BeginMove(in RuntimeMoveRequest request) => false;
        public bool StopMove(RuntimeMoveChannel channel) => false;
    }

    private sealed class NoWalkGoals : INavigationGoalSource
    {
        public bool TryLocate(uint objectId, out Vector3 position)
        {
            position = default;
            return false;
        }
    }

    private sealed class RealSessionOperations : ILiveSessionOperations
    {
        public IPEndPoint ResolveEndpoint(string host, int port) => new(IPAddress.Loopback, port);
        public WorldSession CreateSession(IPEndPoint endpoint) => new(endpoint, new NoOpTransport());
        public void Connect(WorldSession session, string user, string password) { }
        public CharacterList.Parsed GetCharacters(WorldSession session) =>
            new(0u, [new CharacterList.Character(0x50000001u, "Fixture", 0u)], [], 11, "account", true, true);
        public void EnterWorld(WorldSession session, int activeCharacterIndex) { }
        public void Tick(WorldSession session) { }
        public void DisposeSession(WorldSession session) => session.Dispose();
    }

    private sealed class NoOpTransport : IWorldSessionTransport
    {
        public void Send(ReadOnlySpan<byte> datagram) { }
        public void Send(IPEndPoint remote, ReadOnlySpan<byte> datagram) { }
        public int Receive(Span<byte> destination, TimeSpan timeout, out IPEndPoint? from)
        {
            from = null;
            return -1;
        }
        public ValueTask<NetReceiveResult> ReceiveAsync(Memory<byte> destination, CancellationToken cancellationToken) =>
            throw new OperationCanceledException(cancellationToken);
        public void Dispose() { }
    }

    private sealed class NoOpEventRoute : ILiveSessionEventRouting
    {
        public void Attach() { }
        public void Dispose() { }
    }

    private sealed class NoOpCommandRoute : ILiveSessionCommandRouting
    {
        public void Activate() { }
        public void Dispose() { }
    }

    private sealed class InertOperations
        : IRuntimeCombatAttackOperations,
          IRuntimeCombatTargetOperations,
          IRuntimeCombatModeOperations,
          IRuntimeSpellCastOperations
    {
        public bool CanStartAttack() => false;
        public void PrepareAttackRequest() { }
        public bool SendAttack(AttackHeight height, float power) => false;
        public void SendCancelAttack() { }
        public bool IsDualWield => false;
        public bool PlayerReadyForAttack => false;
        public bool AutoRepeatAttack => false;
        public bool AutoTarget => false;
        public uint? SelectClosestTarget() => null;
        public bool IsInWorld => false;
        public IReadOnlyList<ClientObject> GetOrderedEquipment() => [];
        public void NotifyExplicitCombatModeRequest() { }
        public void SendChangeCombatMode(CombatMode mode) { }
        public uint LocalPlayerId => 0u;
        public bool CanSend => false;
        public bool HasRequiredComponents(uint spellId) => false;
        public bool IsTargetCompatible(uint targetId, SpellMetadata spell, bool showMessage) => false;
        public void StopCompletely() { }
        public void SendUntargeted(uint spellId) { }
        public void SendTargeted(uint targetId, uint spellId) { }
        public void DisplayMessage(string message) { }
        public void IncrementBusy() { }
    }
}
