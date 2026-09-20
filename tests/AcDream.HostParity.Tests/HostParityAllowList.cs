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
        new("SetVitalsIdentity", ParityHost.Windowless,
            "The vitals bar is a drawn panel; whose vitals it shows is a "
            + "question only a host with one can answer.",
            ParityStage.AnswerItFromOneSource),
        new("MarkPersistent", ParityHost.Windowless,
            "Keeping the character's own drawable from being evicted is "
            + "bookkeeping for a drawn world, and there is none here.",
            ParityStage.AnswerItFromOneSource),
        new("SetVanishProbeIdentity", ParityHost.Windowless,
            "A diagnostic that watches for the character's drawable going "
            + "missing; nothing draws it here, so there is nothing to watch.",
            ParityStage.AnswerItFromOneSource),
        new("RestoreLayout", ParityHost.Windowless,
            "Putting the panels back where the player left them needs a "
            + "panel tree.",
            ParityStage.AnswerItFromOneSource),
        new("SyncToolbar", ParityHost.Windowless,
            "The toolbar buttons are drawn; there are none to match up here.",
            ParityStage.AnswerItFromOneSource),
        new("ArmPlayerModeAutoEntry", ParityHost.Windowless,
            "Entering player mode on arrival is about where the camera goes "
            + "and what the keyboard steers, and there is neither here.",
            ParityStage.AnswerItFromOneSource),
        new("LoadCharacterSettings", ParityHost.Windowless,
            "The windowed host reads this character's own saved preferences "
            + "on arrival and the windowless host reads nothing, so the two "
            + "can disagree about settings a plugin can see. The preferences "
            + "have no runtime owner yet.",
            ParityStage.MoveTheOperationIntoTheRuntime),
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
