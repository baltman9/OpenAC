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
/// host supplies and the other does not.
///
/// An entry is one of two things, and the two must not be confused. A DEBT is
/// a difference we mean to close, and it names the stage that will close it.
/// A PERMANENT EXCEPTION is a difference that follows from one client drawing
/// the world and the other not: there is no windowless answer to give, so no
/// stage can close it, and naming one would put work on a list that will
/// never be done. Until the two were told apart every entry read as a debt
/// and the list overstated how much was left to do.
///
/// Neither kind is a permission to differ quietly: the census fails when an
/// entry stops describing a real difference, so closing one forces its entry
/// out, and a difference with no entry at all turns the census red.
/// </summary>
internal readonly record struct ParityAllowance(
    string Member,
    string MissingHost,
    string Reason,
    ParityStage? Stage)
{
    /// <summary>A difference we mean to close, and the stage that closes it.</summary>
    internal static ParityAllowance Debt(
        string member,
        string missingHost,
        string reason,
        ParityStage stage) => new(member, missingHost, reason, stage);

    /// <summary>
    /// A difference inherent to one client drawing the world and the other
    /// not. Nothing closes it, so it names no stage.
    /// </summary>
    internal static ParityAllowance InherentToDrawing(
        string member,
        string missingHost,
        string reason) => new(member, missingHost, reason, Stage: null);

    /// <summary>Whether this entry is work outstanding rather than an exception.</summary>
    internal bool IsDebt => Stage is not null;
}

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
        // The seam itself works: the binding pass fills it the moment a host
        // supplies a physics engine, and until one does the projectile
        // automation answers IsAvailable false and every path request
        // Unavailable, so a plugin is told rather than left guessing. What is
        // silent is the bind pass -- neither host DECLARES the capability, so
        // nothing warns that the seam came out empty. Both facts belong in the
        // reason: the earlier wording, "plugins that ask get nothing", read as
        // though the call itself were dead.
        ParityAllowance.Debt("BindProjectileCollision", ParityHost.Windowed,
            "Neither host supplies a projectile physics engine, so the seam is "
            + "filled on neither and the projectile automation answers every "
            + "request Unavailable. Neither host declares the capability "
            + "either, so nothing warns at bind time and the status is the "
            + "only notice a plugin gets. It needs a runtime source before "
            + "either host can fill it.",
            ParityStage.AnswerItFromOneSource),
        ParityAllowance.Debt("BindProjectileCollision", ParityHost.Windowless,
            "Neither host supplies a projectile physics engine, so the seam is "
            + "filled on neither and the projectile automation answers every "
            + "request Unavailable. Neither host declares the capability "
            + "either, so nothing warns at bind time and the status is the "
            + "only notice a plugin gets. It needs a runtime source before "
            + "either host can fill it.",
            ParityStage.AnswerItFromOneSource),
    ];

    /// <summary>
    /// Anything both hosts declare -- a surface seam, a runtime dependency or
    /// either half of the live-session bindings -- where one of them only
    /// manages it under a condition the other does not have.
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
        new("ResolveSkillFormulaBonus", ParityHost.Windowless,
            "The windowless host reads the skill formulas out of the "
            + "installed data files, so a session with no lease on them "
            + "credits every skill without its attribute-derived term and a "
            + "plugin reads the character's skills far below what the server "
            + "allows it. The windowed host always has them.",
            ParityStage.AnswerItFromOneSource),
    ];

    /// <summary>Runtime dependencies one host fills in and the other does not.</summary>
    internal static IReadOnlyList<ParityAllowance> RuntimeDependencies { get; } =
    [
        ParityAllowance.Debt("TimeProvider", ParityHost.Windowed,
            "The windowed host drives the runtime from its frame clock rather "
            + "than an injected time provider.",
            ParityStage.AnswerItFromOneSource),

        // This was a debt whose reason said neither host has one in a plain
        // run. True of a plain run, but the census compares what the hosts
        // DECLARE, and the windowed host declares it -- so the row described a
        // real difference for the wrong reason. It is an exception: the
        // diagnostic dumps what the client drew in the sky, and a client that
        // draws no sky has no version of it to give.
        ParityAllowance.InherentToDrawing(
            "TimeSyncDiagnostic", ParityHost.Windowless,
            "A launch-option diagnostic that dumps what the client drew in "
            + "the sky. A client that draws nothing has no sky to dump, and "
            + "nothing a plugin can see depends on it."),
    ];

    /// <summary>Live-session host bindings one host fills in and the other does not.</summary>
    internal static IReadOnlyList<ParityAllowance> SessionHostBindings { get; } =
    [
        ParityAllowance.InherentToDrawing(
            "ArmLoginTunnel", ParityHost.Windowless,
            "The login tunnel is a presentation effect of arriving in the "
            + "world; there is nothing to show without a window."),
        ParityAllowance.InherentToDrawing(
            "ResumeWorldAudio", ParityHost.Windowless,
            "There is no mixer to resume without a window."),
        ParityAllowance.InherentToDrawing(
            "SetVitalsIdentity", ParityHost.Windowless,
            "The vitals bar is a drawn panel; whose vitals it shows is a "
            + "question only a host with one can answer."),
        ParityAllowance.InherentToDrawing(
            "MarkPersistent", ParityHost.Windowless,
            "Keeping the character's own drawable from being evicted is "
            + "bookkeeping for a drawn world, and there is none here."),
        ParityAllowance.InherentToDrawing(
            "SetVanishProbeIdentity", ParityHost.Windowless,
            "A diagnostic that watches for the character's drawable going "
            + "missing; nothing draws it here, so there is nothing to watch."),
        ParityAllowance.InherentToDrawing(
            "RestoreLayout", ParityHost.Windowless,
            "Putting the panels back where the player left them needs a "
            + "panel tree."),
        ParityAllowance.InherentToDrawing(
            "SyncToolbar", ParityHost.Windowless,
            "The toolbar buttons are drawn; there are none to match up here."),
        ParityAllowance.InherentToDrawing(
            "ArmPlayerModeAutoEntry", ParityHost.Windowless,
            "Entering player mode on arrival is about where the camera goes "
            + "and what the keyboard steers, and there is neither here."),

        // The one debt on this list: a character's saved preferences are not a
        // drawn-world fact, they are settings a plugin can read, and they have
        // no runtime owner yet.
        ParityAllowance.Debt("LoadCharacterSettings", ParityHost.Windowless,
            "The windowed host reads this character's own saved preferences "
            + "on arrival and the windowless host reads nothing, so the two "
            + "can disagree about settings a plugin can see. The preferences "
            + "have no runtime owner yet.",
            ParityStage.MoveTheOperationIntoTheRuntime),
    ];

    /// <summary>Character-session bindings one host fills in and the other does not.</summary>
    internal static IReadOnlyList<ParityAllowance> CharacterSessionBindings { get; } =
    [
    ];

    /// <summary>
    /// Members of the plugin-facing state view one host answers from its own
    /// store and the other leaves at the interface's empty default.
    /// </summary>
    internal static IReadOnlyList<ParityAllowance> StateMembers { get; } =
    [
        ParityAllowance.InherentToDrawing(
            "SceneryObjects", ParityHost.Windowless,
            "The fixed decoration of the landscape is a drawn-world fact: "
            + "what the client placed, and how far out, follows from what it "
            + "is drawing, so a client that draws nothing has none of it to "
            + "report. It carries no server identity and no command names "
            + "it, which is why it is a list of its own rather than mixed "
            + "into the objects both clients answer alike."),
    ];

    /// <summary>
    /// Inputs the plugin surface is built from that one host does not pass.
    /// </summary>
    internal static IReadOnlyList<ParityAllowance> SurfaceInputs { get; } =
    [
    ];

    /// <summary>Every listed difference, of either kind.</summary>
    internal static IEnumerable<ParityAllowance> All =>
        Seams
            .Concat(RuntimeDependencies)
            .Concat(SessionHostBindings)
            .Concat(CharacterSessionBindings)
            .Concat(StateMembers)
            .Concat(SurfaceInputs);
}
