using AcDream.Content;
using AcDream.Headless.Hosting;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime;
using AcDream.Runtime.Navigation;
using AcDream.Runtime.Plugins;
using AcDream.Runtime.Session;

namespace AcDream.Headless.Plugins;

/// <summary>
/// The session-owned parts the plugin surface is built from. Every one is
/// optional because a windowless session can run without installed content or
/// without item automation; what a missing part costs a plugin is decided in
/// one place, <see cref="HeadlessAutomationCapabilities.Build"/>, so a test can
/// build the host's real record without starting a session.
/// </summary>
internal sealed record HeadlessAutomationParts
{
    /// <summary>The runtime whose owners answer everything host-independent.</summary>
    public required GameRuntime Runtime { get; init; }

    /// <summary>Where a binding problem is reported.</summary>
    public Action<string>? Warn { get; init; }

    public IDatReaderWriter? Content { get; init; }
    public MagicCatalog? MagicCatalog { get; init; }
    public Func<string, bool>? SubmitChatText { get; init; }
    public IGameRuntimeCommands? SessionCommands { get; init; }
    public NavigationWalkController? NavigationWalk { get; init; }
    public HeadlessLogoutAutomation? Logout { get; init; }
    public Func<uint, bool, bool>? AnswerConfirmation { get; init; }

    /// <summary>The tick the plugin surface and its own verbs follow.</summary>
    public IEvents? Events { get; init; }
}

/// <summary>
/// What the windowless host can lend the plugin surface. The declared set is a
/// standing claim the host-parity census reads without starting a session; the
/// binding pass refuses a record that supplies anything missing from it, and
/// the census builds the record <see cref="Build"/> really makes, so the claim
/// cannot drift away from the code.
/// </summary>
internal static partial class HeadlessAutomationCapabilities
{
    internal static IReadOnlySet<string> Declared { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(RuntimeAutomationHostCapabilities.Content),
            nameof(RuntimeAutomationHostCapabilities.MagicCatalog),
            nameof(RuntimeAutomationHostCapabilities.SubmitChatText),
            nameof(RuntimeAutomationHostCapabilities.SessionCommands),
            nameof(RuntimeAutomationHostCapabilities.NavigationWalk),
            nameof(RuntimeAutomationHostCapabilities.Logout),
            nameof(RuntimeAutomationHostCapabilities.AnswerConfirmation),
            nameof(RuntimeAutomationHostCapabilities.RemoteBodiesUnsimulated),
        };

    /// <summary>
    /// Capabilities this host supplies only when something outside the code is
    /// there. Named so the binding pass can tell a configuration apart from a
    /// defect when a seam comes out unfilled.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> Conditional { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(RuntimeAutomationHostCapabilities.Content)] =
                "this session has no lease on the installed data files",
            [nameof(RuntimeAutomationHostCapabilities.MagicCatalog)] =
                "the spell catalog comes from the installed data files",
            [nameof(RuntimeAutomationHostCapabilities.NavigationWalk)] =
                "walking a route needs the collision data the installed data "
                + "files carry",
        };

    /// <summary>
    /// Builds the record this host really hands the binding pass. The session
    /// only names its parts; every decision about what a missing part costs a
    /// plugin is here, where a test can run it.
    /// </summary>
    internal static RuntimeAutomationHostCapabilities Build(
        HeadlessAutomationParts parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        HeadlessLogoutAutomation? logout = parts.Logout;
        return new RuntimeAutomationHostCapabilities
        {
            HostName = "windowless",
            Declared = Declared,
            Conditional = Conditional,
            Warn = parts.Warn,
            PluginEvents = parts.Events,
            // Nothing here is drawn, so the two navigation verbs that draw
            // are left out and answer that in plain words.
            Content = parts.Content,
            MagicCatalog = parts.MagicCatalog,
            SubmitChatText = parts.SubmitChatText,
            SessionCommands = parts.SessionCommands,
            NavigationWalk = parts.NavigationWalk,
            Logout = logout is null
                ? null
                : new RuntimeAutomationLogoutCommands(
                    logout.TryRequestLogout,
                    () => logout.CanRequestLogout),
            AnswerConfirmation = parts.AnswerConfirmation,
            // Nothing here moves a remote entity's body between server
            // updates, so positions are read from the last snapshot.
            RemoteBodiesUnsimulated = true,
        };
    }

    /// <summary>Which runtime dependencies this host fills in.</summary>
    internal static IReadOnlySet<string> DeclaredRuntimeDependencies { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(GameRuntimeDependencies.CombatAttackOperations),
            nameof(GameRuntimeDependencies.CombatTargetOperations),
            nameof(GameRuntimeDependencies.CombatModeOperations),
            nameof(GameRuntimeDependencies.SpellCastOperations),
            nameof(GameRuntimeDependencies.Log),
            nameof(GameRuntimeDependencies.TimeProvider),
            nameof(GameRuntimeDependencies.SessionOperations),
        };

    /// <summary>
    /// Runtime dependencies this host fills in only under a condition, for
    /// the same reason as <see cref="Conditional"/>.
    /// </summary>
    internal static IReadOnlyDictionary<string, string>
        ConditionalRuntimeDependencies { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(GameRuntimeDependencies.SessionOperations)] =
                "a caller handed this session its own way of opening and "
                + "ticking a world connection; a plain run leaves the runtime "
                + "to use the shared one",
        };

    /// <summary>
    /// Builds the dependency record this host really constructs the runtime
    /// with, so the census reads the code rather than a list beside it.
    /// </summary>
    internal static GameRuntimeDependencies BuildRuntimeDependencies(
        HeadlessGameplayOperations gameplay,
        TimeProvider timeProvider,
        Action<string> log,
        ILiveSessionOperations? sessionOperations)
    {
        ArgumentNullException.ThrowIfNull(gameplay);
        return new GameRuntimeDependencies(
            gameplay,
            gameplay,
            gameplay,
            gameplay,
            TimeProvider: timeProvider,
            Log: log,
            SessionOperations: sessionOperations);
    }

    /// <summary>Which live-session host bindings this host fills in.</summary>
    internal static IReadOnlySet<string> DeclaredSessionHostBindings { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(LiveSessionHostBindings.Routing),
            nameof(LiveSessionHostBindings.Reset),
            nameof(LiveSessionHostBindings.Selection),
            nameof(LiveSessionHostBindings.EnteredWorld),
            nameof(LiveSessionHostBindings.Connecting),
            nameof(LiveSessionHostBindings.Connected),
            nameof(LiveSessionHostBindings.Roster),
            nameof(LiveSessionHostBindings.CharacterEntered),
            nameof(LiveSessionHostBindings.LoginCommands),
            nameof(LiveSessionHostBindings.CharacterCreated),
            nameof(LiveSessionHostBindings.CreationFailed),
            nameof(LiveSessionSelectionBindings.SetPlayerIdentity),
            nameof(LiveSessionSelectionBindings.SetChatIdentity),
            nameof(LiveSessionSelectionBindings.ClearCombat),
            nameof(LiveSessionEnteredWorldBindings.SetActiveCharacter),
        };

    /// <summary>Which character-session bindings this host fills in.</summary>
    internal static IReadOnlySet<string> DeclaredCharacterSessionBindings { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(LiveCharacterSessionBindings.Combat),
            nameof(LiveCharacterSessionBindings.Character),
            nameof(LiveCharacterSessionBindings.ResolveSkillFormulaBonus),
            nameof(LiveCharacterSessionBindings.OnConfirmationRequest),
            nameof(LiveCharacterSessionBindings.OnConfirmationDone),
            nameof(LiveCharacterSessionBindings.ClientTime),
            nameof(LiveCharacterSessionBindings.OnCharacterOptionsChanged),
        };
}
