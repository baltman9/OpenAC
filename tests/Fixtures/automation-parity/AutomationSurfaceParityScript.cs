using System.Globalization;
using System.Net;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Selection;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Session;

namespace AcDream.Tests.Fixtures.AutomationParity;

/// <summary>
/// One plugin, two hosts, one answer. This file is compiled into both host
/// test suites: each stands up the same runtime, binds its own host's
/// automation surface to it, runs the same script of plugin requests, and
/// asserts the same recorded answers. A host that quietly answers
/// differently — or cannot answer at all, which is what a bot without a
/// window used to get for every item, equipment and projectile request —
/// shows up as a differing line. Ghost retirement is deliberately absent:
/// each host retires its own projection of an entity, so that one route is
/// installed by the host and pinned where it is installed.
/// </summary>
internal static class AutomationSurfaceParityScript
{
    internal const uint CharacterId = 0x50000001u;
    internal const uint CorpseId = 0x50000900u;
    internal const uint LootId = 0x50000901u;
    internal const uint CarriedId = 0x50000902u;
    internal const uint WieldableId = 0x50000903u;
    internal const uint WornId = 0x50000904u;
    internal const uint TargetId = 0x70000010u;

    /// <summary>
    /// The answers both hosts must give, in order. Kept as text so a
    /// difference reads as a diff rather than as a failed assertion buried
    /// in one of thirteen requests.
    /// </summary>
    internal static readonly string[] Expected =
    [
        "items.available=True",
        "equipment.available=True",
        "loot.available=True",
        "projectiles.available=True",
        "pickup unknown=InvalidItem",
        "pickup carried=InvalidItem",
        "pickup corpse-item=Started",
        "equip plain=InvalidItem",
        "equip worn=AlreadyEquipped",
        "equip wieldable=Started",
        "sell carried=InvalidTarget",
        "move carried-to-nothing=InvalidTarget",
        "select target=0x70000010",
        "accepted requests reached the wire=True",
    ];

    internal static IReadOnlyList<string> Run(
        GameRuntime runtime,
        IAutomationSurface automation,
        Func<int> sentGameActionCount)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(automation);
        ArgumentNullException.ThrowIfNull(sentGameActionCount);
        int before = sentGameActionCount();
        Seed(runtime);

        var answers = new List<string>
        {
            $"items.available={automation.Items.IsAvailable}",
            $"equipment.available={automation.Equipment.IsAvailable}",
            $"loot.available={automation.Loot.IsAvailable}",
            $"projectiles.available={automation.Projectiles.IsAvailable}",
            $"pickup unknown={automation.Loot.Pickup(0x5000FFFFu).Status}",
            $"pickup carried={automation.Loot.Pickup(CarriedId).Status}",
            $"pickup corpse-item={automation.Loot.Pickup(LootId).Status}",
            $"equip plain={automation.Equipment.Equip(CarriedId).Status}",
            $"equip worn={automation.Equipment.Equip(WornId).Status}",
            $"equip wieldable={automation.Equipment.Equip(WieldableId).Status}",
            $"sell carried={automation.Items.Sell(CarriedId).Status}",
            "move carried-to-nothing="
                + automation.Items.MoveToContainer(CarriedId, 0u).Status,
        };

        runtime.ActionOwner.Selection.Select(
            TargetId,
            SelectionChangeSource.Plugin);
        answers.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"select target=0x{automation.Combat.Snapshot.SelectedObjectId:X8}"));
        // An accepted equip is a real request: it must have reached the
        // wire, not just returned a hopeful status.
        answers.Add(
            $"accepted requests reached the wire="
            + (sentGameActionCount() > before));
        return answers;
    }

    private static void Seed(GameRuntime runtime)
    {
        uint player = runtime.PlayerIdentity.ServerGuid;
        ClientObjectTable objects = runtime.InventoryOwner.Objects;
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = player,
            Name = "Parity",
        });
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = CorpseId,
            Name = "Corpse of a Drudge",
            Type = ItemType.Container,
            PublicWeenieBitfield = (uint)PublicWeenieFlags.Corpse,
            ItemsCapacity = 10,
        });
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = LootId,
            Name = "Lead Scarab",
            Type = ItemType.SpellComponents,
            ContainerId = CorpseId,
        });
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = CarriedId,
            Name = "Bundle of Arrowheads",
            Type = ItemType.Misc,
            ContainerId = player,
        });
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = WieldableId,
            Name = "Sword",
            Type = ItemType.MeleeWeapon,
            ContainerId = player,
            ValidLocations = EquipMask.MeleeWeapon,
        });
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = WornId,
            Name = "Chainmail Coat",
            Type = ItemType.Armor,
            WielderId = player,
            ValidLocations = EquipMask.ChestArmor,
            CurrentlyEquippedLocation = EquipMask.ChestArmor,
        });

        // Containment is a placement, not a field the table reads back, so
        // the contents index is filled the way the wire fills it.
        _ = objects.MoveItem(LootId, CorpseId);
        _ = objects.MoveItem(CarriedId, player);
        _ = objects.MoveItem(WieldableId, player);

        // The corpse is the open container: a plugin may only lift what the
        // player is actually looking into.
        _ = runtime.InventoryOwner.ExternalContainers.RequestOpen(
            CorpseId,
            isCorpse: true);
        _ = runtime.InventoryOwner.ExternalContainers.ApplyViewContents(
            CorpseId);
    }
}

/// <summary>
/// The smallest runtime that is really in the world: a session over a
/// transport that drops what it is handed, so a request that reaches the
/// wire is exercised end to end without one.
/// </summary>
internal sealed class AutomationParityRuntimeFixture : IDisposable
{
    private readonly FixtureOperations _operations = new();

    private readonly GameplayOperations _gameplay = new();
    private readonly ResetHost _resetHost = new();
    private readonly IDisposable _hostLease;
    private bool _disposed;

    internal AutomationParityRuntimeFixture()
    {
        Runtime = new GameRuntime(new GameRuntimeDependencies(
            _gameplay,
            _gameplay,
            _gameplay,
            _gameplay,
            SessionOperations: _operations));
        _hostLease = Runtime.AcquireHostLease("automation parity fixture");
        Session = new LiveSessionHost(
            Runtime.Session,
            new LiveSessionHostBindings(
                new LiveSessionRoutingFactories(
                    _ => new Route(),
                    _ => new Route()),
                generation => Runtime.ResetGeneration(generation, _resetHost),
                new LiveSessionSelectionBindings(
                    id => Runtime.PlayerIdentity.ServerGuid = id,
                    _ => { },
                    Runtime.CommunicationOwner.Chat.SetLocalPlayerGuid,
                    _ => { },
                    _ => { },
                    Runtime.ActionOwner.Combat.Clear),
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
                "parity",
                "parity"));
        _ = Session.Start(Runtime.Generation);
    }

    internal GameRuntime Runtime { get; }

    internal LiveSessionHost Session { get; }

    /// <summary>Every accepted request that reached the wire.</summary>
    internal IReadOnlyList<byte[]> GameActions => _operations.GameActions;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _ = Session.Stop(Runtime.Generation);
        _hostLease.Dispose();
        Runtime.Dispose();
    }

    private sealed class Route
        : ILiveSessionEventRouting, ILiveSessionCommandRouting
    {
        public void Attach()
        {
        }

        public void Activate()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class ResetHost : IRuntimeGenerationResetHost
    {
        public void RetireEntityProjection(RuntimeEntityRecord entity)
        {
        }

        public void DrainEntityProjectionBoundary()
        {
        }

        public void CompleteEntityProjectionRetirement()
        {
        }
    }

    private sealed class FixtureOperations : ILiveSessionOperations
    {
        public IPEndPoint ResolveEndpoint(string host, int port) =>
            new(IPAddress.Loopback, port);

        internal List<byte[]> GameActions { get; } = [];

        public WorldSession CreateSession(IPEndPoint endpoint)
        {
            var session = new WorldSession(endpoint, new DroppingTransport());
            // The fixture never negotiates a reliable channel, so an
            // accepted request is captured here instead of framed.
            session.GameActionCapture = GameActions.Add;
            return session;
        }

        public void Connect(WorldSession session, string user, string password)
        {
        }

        public CharacterList.Parsed GetCharacters(WorldSession session) => new(
            0u,
            [new CharacterList.Character(
                AutomationSurfaceParityScript.CharacterId,
                "Parity",
                0u)],
            [],
            11,
            "Parity",
            true,
            true);

        public void EnterWorld(WorldSession session, int activeCharacterIndex)
        {
        }

        public void Tick(WorldSession session)
        {
        }

        public void DisposeSession(WorldSession session) => session.Dispose();
    }

    private sealed class DroppingTransport : IWorldSessionTransport
    {
        public void Send(ReadOnlySpan<byte> datagram)
        {
        }

        public void Send(IPEndPoint remote, ReadOnlySpan<byte> datagram)
        {
        }

        public int Receive(
            Span<byte> destination,
            TimeSpan timeout,
            out IPEndPoint? from)
        {
            from = null;
            return -1;
        }

        public ValueTask<NetReceiveResult> ReceiveAsync(
            Memory<byte> destination,
            CancellationToken cancellationToken) =>
            throw new OperationCanceledException(cancellationToken);

        public void Dispose()
        {
        }
    }

    private sealed class GameplayOperations :
        IRuntimeCombatAttackOperations,
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
        public bool IsInWorld => true;
        public IReadOnlyList<ClientObject> GetOrderedEquipment() => [];
        public void NotifyExplicitCombatModeRequest() { }
        public void SendChangeCombatMode(CombatMode mode) { }
        public uint LocalPlayerId => AutomationSurfaceParityScript.CharacterId;
        public bool CanSend => true;
        public bool HasRequiredComponents(uint spellId) => false;
        public bool IsTargetCompatible(
            uint targetId,
            AcDream.Core.Spells.SpellMetadata spell,
            bool showMessage) => false;
        public void StopCompletely() { }
        public void SendUntargeted(uint spellId) { }
        public void SendTargeted(uint targetId, uint spellId) { }
        public void DisplayMessage(string message) { }
        public void IncrementBusy() { }
    }
}
