using System.Net;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Selection;
using AcDream.Core.Spells;
using AcDream.Headless.Configuration;
using AcDream.Headless.Credentials;
using AcDream.Headless.Diagnostics;
using AcDream.Headless.Hosting;
using AcDream.Headless.Plugins;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime;
using AcDream.Runtime.Session;
using AcDream.Tests.Fixtures.AutomationParity;

namespace AcDream.Headless.Tests;

/// <summary>
/// One plugin, two hosts. A plugin loaded into the windowless host must see
/// the same client a plugin loaded into the graphical host sees: the same
/// character, the same spellbook, the same inventory, the same combat state.
/// These are the windowless half of that pin; the graphical half lives in
/// <c>AppAutomationSurfaceTests</c>.
/// </summary>
public sealed class HeadlessAutomationSurfaceParityTests
{
    private const uint SelfBuffSpellId = 42u;

    /// <summary>
    /// The windowless host answers a plugin out of the shared
    /// presentation-free surface and adds nothing of its own to it. A
    /// private answer here is a plugin behaving differently between the two
    /// hosts.
    /// </summary>
    [Fact]
    public void EveryAutomationAnswerComesFromTheSharedRuntimeSurface()
    {
        Type[] contracts = typeof(HeadlessAutomationSurface)
            .GetInterfaces()
            .Where(static contract =>
                contract.Namespace == typeof(IAutomationSurface).Namespace)
            .ToArray();
        Assert.NotEmpty(contracts);

        var declaredByTheHost = new List<string>();
        foreach (Type contract in contracts)
        {
            System.Reflection.InterfaceMapping map =
                typeof(HeadlessAutomationSurface).GetInterfaceMap(contract);
            for (int index = 0; index < map.TargetMethods.Length; index++)
            {
                if (map.TargetMethods[index].DeclaringType
                    != typeof(AcDream.Runtime.Plugins.RuntimeAutomationSurface))
                {
                    declaredByTheHost.Add(
                        $"{contract.Name}.{map.InterfaceMethods[index].Name}");
                }
            }
        }

        Assert.Equal([], declaredByTheHost);
    }

    /// <summary>
    /// The whole automation contract is reachable from the windowless host —
    /// not just chat. Each of these was a no-op before the surface became
    /// shared, which is exactly the hole that left a bot with no character,
    /// no spells, no targets and no inventory.
    /// </summary>
    [Fact]
    public void TheWindowlessHostAnswersCharacterSpellsItemsAndCombatFromItsRuntime()
    {
        using var temporary = new TemporaryDirectory();
        string statusPath = Path.Combine(temporary.Path, "status.jsonl");
        var diagnostics = new HeadlessDiagnosticWriter(new StringWriter());
        using var session = new HeadlessSessionHost(
            Descriptor(statusPath),
            new HeadlessCredentialSecret("fixture", "password"),
            diagnostics,
            new FixtureSessionOperations());
        _ = session.Start();

        GameRuntime runtime = session.Runtime;
        Assert.Equal(RuntimeLifecycleState.InWorld, runtime.Lifecycle.State);
        IAutomationSurface automation = session.Plugins.Host.Automation;
        Assert.True(automation.IsAvailable);

        // Character: the runtime's own local player, not an empty shell.
        Assert.Equal(runtime.PlayerIdentity.ServerGuid, automation.Character.ObjectId);
        Assert.NotEqual(0u, automation.Character.ObjectId);
        runtime.InventoryOwner.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = runtime.PlayerIdentity.ServerGuid,
            Name = "Fixture",
        });
        Assert.Equal("Fixture", automation.Character.Name);

        // Spells: the runtime's spellbook, projected through the catalog.
        runtime.CharacterOwner.InstallSpellMetadata(
            SpellTable.Create([SelfBuff()]));
        runtime.CharacterOwner.Spellbook.OnSpellLearned(SelfBuffSpellId);
        Assert.True(automation.Spells.IsKnown(SelfBuffSpellId));
        PluginSpellInfo known = Assert.Single(automation.Spells.KnownSelfBuffs);
        Assert.Equal(SelfBuffSpellId, known.SpellId);

        // Items: the runtime's own object table, filtered to what the player
        // carries.
        const uint itemId = 0x50000123u;
        runtime.InventoryOwner.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = itemId,
            Name = "Lead Scarab",
            ContainerId = runtime.PlayerIdentity.ServerGuid,
        });
        PluginInventoryItem carried = Assert.Single(
            automation.Items.CaptureOwnedItems());
        Assert.Equal(itemId, carried.ObjectId);

        // Combat: the runtime's canonical selection.
        const uint targetId = 0x50000456u;
        runtime.ActionOwner.Selection.Select(targetId, SelectionChangeSource.Plugin);
        Assert.Equal(targetId, automation.Combat.Snapshot.SelectedObjectId);

        // Fellowship: the runtime's fellowship owner (empty here, but
        // answered rather than refused).
        Assert.False(automation.Fellowship.IsInFellowship);
        Assert.Empty(automation.Fellowship.CaptureMembers());

        // Navigation: the surface reports a live position rather than
        // pretending it has none.
        Assert.True(automation.Objects.IsAvailable);

        // Ghost retirement: this host installs its own route, so a plugin
        // gets a verdict on the object rather than "no such command here".
        Assert.Equal(
            PluginCombatCommandStatus.InvalidTarget,
            automation.Combat.DismissGhostTarget(0x7000FFFFu).Status);
    }

    /// <summary>
    /// The windowless half of the shared parity script. Every item,
    /// equipment, loot and projectile answer here used to be
    /// <c>Unavailable</c>, because those requests reached the client only
    /// through callbacks the graphical host installed. The graphical half
    /// asserts the same expected list from
    /// <c>AppAutomationSurfaceParityTests</c>.
    /// </summary>
    [Fact]
    public void TheWindowlessHostGivesTheSharedParityAnswers()
    {
        using var fixture = new AutomationParityRuntimeFixture();
        using var surface = new HeadlessAutomationSurface(fixture.Runtime);

        IReadOnlyList<string> answers = AutomationSurfaceParityScript.Run(
            fixture.Runtime,
            surface,
            () => fixture.GameActions.Count);

        Assert.Equal(AutomationSurfaceParityScript.Expected, answers);
    }

    private static SpellMetadata SelfBuff() => new(
        SpellId: SelfBuffSpellId,
        Name: "Strength Self VII",
        School: "Life Magic",
        Family: 7u,
        IconId: 0u,
        SpellWords: string.Empty,
        Duration: 1800f,
        ManaCost: 10,
        IsDebuff: false,
        IsFellowship: false,
        Description: string.Empty,
        SortKey: 0,
        Difficulty: 350,
        Flags: (uint)(SpellFlags.Beneficial | SpellFlags.SelfTargeted),
        Generation: 7,
        IsFastWindup: false,
        IsOffensive: false,
        IsUntargeted: false,
        Speed: 0f,
        CasterEffect: 0u,
        TargetEffect: 0u,
        TargetMask: 1u,
        SpellType: 0);

    private static HeadlessSessionDescriptor Descriptor(string statusPath) => new()
    {
        Id = "headless-automation-parity",
        Endpoint = new HeadlessEndpointDescriptor
        {
            Host = "127.0.0.1",
            Port = 9000,
        },
        Account = "account",
        Character = new HeadlessCharacterSelector { Name = "Fixture" },
        Policy = new HeadlessBotPolicyDescriptor { Id = "idle" },
        Credential = new HeadlessCredentialReference
        {
            Provider = HeadlessCredentialProviderKind.Environment,
            Reference = "FIXTURE_PASSWORD",
        },
        Plugins = [],
        StatusFile = statusPath,
        LoginCommandDelayMs = 0,
    };

    private sealed class FixtureSessionOperations : ILiveSessionOperations
    {
        private static readonly CharacterList.Parsed Characters = new(
            0u,
            [new CharacterList.Character(0x50000001u, "Fixture", 0u)],
            [],
            1,
            "account",
            true,
            true);

        public IPEndPoint ResolveEndpoint(string host, int port) =>
            new(IPAddress.Loopback, port);

        public WorldSession CreateSession(IPEndPoint endpoint) => new(endpoint);

        public void Connect(WorldSession session, string user, string password)
        {
        }

        public CharacterList.Parsed? GetCharacters(WorldSession session) =>
            Characters;

        public void EnterWorld(WorldSession session, int activeCharacterIndex)
        {
        }

        public void Tick(WorldSession session)
        {
        }

        public void DisposeSession(WorldSession session) => session.Dispose();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"acdream-headless-automation-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
