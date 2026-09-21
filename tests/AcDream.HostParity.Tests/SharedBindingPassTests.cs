using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using AcDream.Content;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Physics;
using AcDream.Core.Spells;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Plugins;
using AcDream.Runtime.Session;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;
using DatIDBObj = DatReaderWriter.Lib.IO.IDBObj;

namespace AcDream.HostParity.Tests;

/// <summary>
/// The census is only as honest as the seam map behind it, so the map is
/// checked against what the binding pass really does: given every capability
/// a host could lend, the pass fills exactly the seams the map claims.
/// </summary>
public sealed class SharedBindingPassTests
{
    /// <summary>
    /// The two seams whose capability cannot be fabricated without a loaded
    /// world. They are covered by the census through the hosts' declarations
    /// instead.
    /// </summary>
    private static readonly string[] NeedsAWorld = ["BindNavigationWalk"];

    [Fact]
    public void TheBindingPassFillsExactlyTheSeamsItsMapClaims()
    {
        using GameRuntime runtime = NewRuntime();
        using var surface = new RuntimeAutomationSurface();
        using var content = new SkillTableContent();
        var physics = new PhysicsEngine();

        IReadOnlySet<string> bound = RuntimeAutomationBindings.Apply(
            surface, runtime, Everything(content, physics, runtime));

        string[] expected = RuntimeAutomationBindings.SeamCapabilities.Keys
            .Where(static seam => !NeedsAWorld.Contains(seam))
            .OrderBy(static seam => seam, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            expected,
            bound.OrderBy(static seam => seam, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void TheBindingPassRefusesACapabilityTheHostNeverDeclared()
    {
        using GameRuntime runtime = NewRuntime();
        using var surface = new RuntimeAutomationSurface();

        InvalidOperationException refused =
            Assert.Throws<InvalidOperationException>(() =>
                RuntimeAutomationBindings.Apply(
                    surface,
                    runtime,
                    new RuntimeAutomationHostCapabilities
                    {
                        HostName = "a host under test",
                        Declared = new HashSet<string>(StringComparer.Ordinal),
                        AnswerConfirmation = static (_, _) => true,
                    }));

        Assert.Contains(
            nameof(RuntimeAutomationHostCapabilities.AnswerConfirmation),
            refused.Message,
            StringComparison.Ordinal);
    }

    private static RuntimeAutomationHostCapabilities Everything(
        IDatReaderWriter content,
        PhysicsEngine physics,
        GameRuntime runtime) => new()
    {
        HostName = "a host under test",
        Declared = RuntimeAutomationHostCapabilities.AllCapabilityNames,
        Content = content,
        MagicCatalog = MagicCatalog.Empty,
        SubmitChatText = static _ => true,
        SessionCommands = new DirectGameRuntimeCommandAdapter(
            runtime, new UnusedSessionCommands()),
        Logout = new RuntimeAutomationLogoutCommands(
            static () => true, static () => true),
        AnswerConfirmation = static (_, _) => true,
        ProjectileCollision = physics,
    };

    private static GameRuntime NewRuntime()
    {
        var operations = new UnusedOperations();
        return new GameRuntime(new GameRuntimeDependencies(
            operations, operations, operations, operations));
    }

    private sealed class UnusedSessionCommands : IRuntimeSessionCommands
    {
        public RuntimeSessionStartResult Start(
            RuntimeGenerationToken expectedGeneration) =>
            throw new NotSupportedException();

        public RuntimeSessionStartResult Reconnect(
            RuntimeGenerationToken expectedGeneration) =>
            throw new NotSupportedException();

        public RuntimeTeardownAcknowledgement Stop(
            RuntimeGenerationToken expectedGeneration) =>
            throw new NotSupportedException();
    }

    private sealed class UnusedOperations :
        IRuntimeCombatAttackOperations,
        IRuntimeCombatTargetOperations,
        IRuntimeCombatModeOperations,
        IRuntimeSpellCastOperations
    {
        public bool CanStartAttack(bool allowAutoTarget) => false;
        public void PrepareAttackRequest() { }
        public bool SendAttack(
            AttackHeight height, float power, bool allowAutoTarget) => false;
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
        public bool IsTargetCompatible(
            uint targetId, SpellMetadata spell, bool showMessage) => false;
        public void StopCompletely() { }
        public void SendUntargeted(uint spellId) { }
        public void SendTargeted(uint targetId, uint spellId) { }
        public void DisplayMessage(string message) { }
        public void IncrementBusy() { }
    }

    /// <summary>
    /// Just enough installed content for the shared content binding: the
    /// skill table it names skills from, and nothing else.
    /// </summary>
    private sealed class SkillTableContent : IDatReaderWriter
    {
        private const uint SkillTableId = 0x0E000004u;
        private readonly SkillTable _skills = BuildSkillTable();

        public string SourceDirectory => string.Empty;
        public IDatDatabase Portal => throw new NotSupportedException();
        public IDatDatabase Cell => throw new NotSupportedException();
        public ReadOnlyDictionary<uint, IDatDatabase> CellRegions { get; } =
            new(new Dictionary<uint, IDatDatabase>());
        public IDatDatabase HighRes => throw new NotSupportedException();
        public IDatDatabase Language => throw new NotSupportedException();
        public IDatDatabase Local => throw new NotSupportedException();
        public ReadOnlyDictionary<uint, uint> RegionFileMap { get; } =
            new(new Dictionary<uint, uint>());
        public int PortalIteration => 0;
        public int CellIteration => 0;
        public int HighResIteration => 0;
        public int LanguageIteration => 0;

        public bool TryGetFileBytes(
            uint regionId, uint fileId, ref byte[] bytes, out int bytesRead)
        {
            bytesRead = 0;
            return false;
        }

        public IEnumerable<uint> GetAllIdsOfType<T>() where T : DatIDBObj =>
            Array.Empty<uint>();

        public IEnumerable<IDatReaderWriter.IdResolution> ResolveId(uint id) =>
            Array.Empty<IDatReaderWriter.IdResolution>();

        public bool TrySave<T>(T obj, int iteration = 0) where T : DatIDBObj =>
            throw new NotSupportedException();

        public bool TrySave<T>(uint regionId, T obj, int iteration = 0)
            where T : DatIDBObj => throw new NotSupportedException();

        [return: MaybeNull]
        public T Get<T>(uint fileId) where T : DatIDBObj =>
            fileId == SkillTableId && typeof(T) == typeof(SkillTable)
                ? (T)(object)_skills
                : default;

        public bool TryGet<T>(uint fileId, [MaybeNullWhen(false)] out T value)
            where T : DatIDBObj
        {
            value = Get<T>(fileId);
            return value is not null;
        }

        public void Dispose()
        {
        }

        private static SkillTable BuildSkillTable()
        {
            var table = new SkillTable();
            table.Skills.Add(DatReaderWriter.Enums.SkillId.Run, new SkillBase
            {
                Name = "Run",
                IconId = 1u,
                MinLevel = 1u,
            });
            return table;
        }
    }
}
