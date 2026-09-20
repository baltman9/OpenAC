namespace AcDream.HostParity.Tests;

/// <summary>The two hosts the census compares.</summary>
internal static class ParityHost
{
    internal const string Windowed = "windowed";
    internal const string Windowless = "windowless";

    internal static readonly string[] Both = [Windowed, Windowless];
}

/// <summary>
/// What closing a listed difference will take. Each stage is a batch of the
/// host-parity work: an operation still living in one host has to move into
/// the runtime, while a detail both hosts answer from different places has
/// to be answered from one source.
/// </summary>
internal enum ParityStage
{
    /// <summary>The operation itself lives in one host and must move.</summary>
    MoveTheOperationIntoTheRuntime,

    /// <summary>Both hosts answer it, from different sources.</summary>
    AnswerItFromOneSource,
}

/// <summary>
/// One known difference between the hosts: a seam or a dependency member one
/// host supplies and the other does not. Every entry is a debt with a named
/// owner stage, not a permission; the census fails when an entry stops
/// describing a real difference, so closing the difference forces the entry
/// out.
/// </summary>
internal readonly record struct ParityAllowance(
    string Member,
    string MissingHost,
    string Reason,
    ParityStage Stage);

/// <summary>
/// A member both hosts can supply, where one of them can only do it when a
/// condition holds. A plugin sees the same surface on paper and a different
/// one in a particular session, so this is a difference too -- a quieter one.
/// </summary>
internal readonly record struct ParityConditionalAllowance(
    string Member,
    string ConditionalHost,
    string Reason,
    ParityStage Stage);

internal static class HostParityAllowList
{
    /// <summary>
    /// Seams on the shared plugin surface that one host cannot fill yet.
    /// </summary>
    internal static IReadOnlyList<ParityAllowance> Seams { get; } =
    [
        new("BindItems.salvageItems", ParityHost.Windowless,
            "Salvaging runs through the windowed item-interaction owner; the "
            + "windowless host has its own item automation and no salvage path.",
            ParityStage.MoveTheOperationIntoTheRuntime),
        new("BindItems.sellItem", ParityHost.Windowless,
            "Selling runs through the windowed item-interaction owner; the "
            + "windowless host has no vendor sell path.",
            ParityStage.MoveTheOperationIntoTheRuntime),
        new("BindWorldObjectUse", ParityHost.Windowless,
            "Using an object the player does not own walks to it first, and "
            + "that walk-then-use route is still windowed-host code.",
            ParityStage.MoveTheOperationIntoTheRuntime),
        new("BindGhostDeletion", ParityHost.Windowless,
            "Letting go of a target the client still believes in is done by "
            + "the windowed host's entity deletion owner.",
            ParityStage.MoveTheOperationIntoTheRuntime),
        new("BindSelectionActions", ParityHost.Windowless,
            "Cycling the selection is an input action of the windowed host; "
            + "the cycle itself belongs over the entity directory.",
            ParityStage.MoveTheOperationIntoTheRuntime),
        new("BindChatInputActive", ParityHost.Windowless,
            "Whether the keyboard is going into the chat entry needs a chat "
            + "entry; a console draft owner has to exist first.",
            ParityStage.MoveTheOperationIntoTheRuntime),
        new("BindChatComposer", ParityHost.Windowless,
            "Staging chat text without sending it needs a draft; the "
            + "windowless host has no draft owner yet.",
            ParityStage.MoveTheOperationIntoTheRuntime),
        new("BindSpeciesNameResolver", ParityHost.Windowless,
            "The creature display-name table is loaded by windowed layout "
            + "code and has not been moved to the shared content load.",
            ParityStage.AnswerItFromOneSource),
        new("BindRemoteBodiesUnsimulated", ParityHost.Windowed,
            "The windowed host moves a remote body between server updates, so "
            + "it must not read positions off the last snapshot; one host "
            + "advancing remote bodies and the other not is the difference to "
            + "remove, and this flag goes with it.",
            ParityStage.MoveTheOperationIntoTheRuntime),
        new("BindProjectileCollision", ParityHost.Windowed,
            "Neither host fills the projectile seam; plugins that ask get "
            + "nothing. It needs a runtime source before either can.",
            ParityStage.AnswerItFromOneSource),
        new("BindProjectileCollision", ParityHost.Windowless,
            "Neither host fills the projectile seam; plugins that ask get "
            + "nothing. It needs a runtime source before either can.",
            ParityStage.AnswerItFromOneSource),
    ];

    /// <summary>
    /// Seams both hosts declare, where one of them only manages it under a
    /// condition the other does not have.
    /// </summary>
    internal static IReadOnlyList<ParityConditionalAllowance>
        ConditionalSeams { get; } =
    [
        new("NavigationWalk", ParityHost.Windowless,
            "The windowless host builds its walk controller only when the "
            + "session holds a lease on the installed data files, so a "
            + "content-less bot answers navigation calls where a window "
            + "would act on them.",
            ParityStage.AnswerItFromOneSource),
    ];

    /// <summary>Runtime dependencies one host fills in and the other does not.</summary>
    internal static IReadOnlyList<ParityAllowance> RuntimeDependencies { get; } =
    [
        new("TimeProvider", ParityHost.Windowed,
            "The windowed host drives the runtime from its frame clock rather "
            + "than an injected time provider.",
            ParityStage.AnswerItFromOneSource),
        new("SessionOperations", ParityHost.Windowed,
            "Neither host installs its own way of opening and ticking a world "
            + "connection in a plain run: the windowed host never offers one "
            + "and the windowless host only when a caller hands it in, so "
            + "both normally fall back to the shared one. The declaration "
            + "stays until the windowless host stops taking the override.",
            ParityStage.AnswerItFromOneSource),
        new("CombatTime", ParityHost.Windowed,
            "The attack power-up is timed off a wall clock with a window and "
            + "off the simulation clock without one; the same clock has to "
            + "time both.",
            ParityStage.AnswerItFromOneSource),
        new("TimeSyncDiagnostic", ParityHost.Windowless,
            "A launch-option diagnostic the windowed host fills in only when "
            + "the sky dump is on, so in a plain run neither host has one. It "
            + "observes nothing a plugin can see.",
            ParityStage.AnswerItFromOneSource),
    ];

    /// <summary>Live-session host bindings one host fills in and the other does not.</summary>
    internal static IReadOnlyList<ParityAllowance> SessionHostBindings { get; } =
    [
        new("ArmLoginTunnel", ParityHost.Windowless,
            "The login tunnel is a presentation effect of arriving in the "
            + "world; there is nothing to show without a window.",
            ParityStage.AnswerItFromOneSource),
        new("ResumeWorldAudio", ParityHost.Windowless,
            "There is no mixer to resume without a window.",
            ParityStage.AnswerItFromOneSource),
    ];

    /// <summary>Character-session bindings one host fills in and the other does not.</summary>
    internal static IReadOnlyList<ParityAllowance> CharacterSessionBindings { get; } =
    [
        new("OnSkillsUpdated", ParityHost.Windowless,
            "New skill levels re-derive run and jump speed on the windowed "
            + "host only, so the two hosts can disagree about how fast the "
            + "character moves after a raise.",
            ParityStage.AnswerItFromOneSource),
        new("OnMovementStatsUpdated", ParityHost.Windowless,
            "The same re-derivation from a movement-stat update; it belongs to "
            + "whoever owns movement, not to whoever has a window.",
            ParityStage.AnswerItFromOneSource),
    ];
}
