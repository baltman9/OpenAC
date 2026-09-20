using AcDream.Core.Combat;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Headless.Hosting;
using AcDream.Runtime;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Chat;
using AcDream.Runtime.Session;

namespace AcDream.Headless.Plugins;

/// <summary>
/// The parts the windowless host's live-session bindings are built from, so
/// the record the session host is really given can be built -- and inspected
/// -- without opening a world connection.
/// </summary>
internal sealed record HeadlessSessionHostParts
{
    /// <summary>How this host takes hold of an opened world connection.</summary>
    public required Func<WorldSession, ILiveSessionEventRouting> CreateEvents
    {
        get; init;
    }

    /// <summary>The outbound half of the same.</summary>
    public required Func<WorldSession, ILiveSessionCommandRouting> CreateCommands
    {
        get; init;
    }

    /// <summary>Puts every owner back to its starting state for a new generation.</summary>
    public required Action<RuntimeGenerationToken> Reset { get; init; }

    public required RuntimeLocalPlayerIdentityState Identity { get; init; }

    public required RuntimeCommunicationState Communication { get; init; }

    public required CombatState Combat { get; init; }

    /// <summary>Takes note of which character this session is playing.</summary>
    public required Action<string> NoteActiveCharacter { get; init; }

    /// <summary>Where a session-level line is written.</summary>
    public required Action<string> Diagnostic { get; init; }

    public required SessionStatusWriter StatusWriter { get; init; }

    public required string SessionId { get; init; }

    /// <summary>Records that the connection came up at least once.</summary>
    public required Action NoteConnected { get; init; }

    public LoginCommandSequence? LoginCommands { get; init; }
}

/// <summary>
/// The parts the windowless host's character bindings are built from, for the
/// same reason.
/// </summary>
internal sealed record HeadlessCharacterSessionParts
{
    public required RuntimeCharacterState Character { get; init; }

    public required CombatState Combat { get; init; }

    /// <summary>
    /// Turns a raw skill level into the level the server credits. Absent when
    /// the session holds no lease on the installed data files, which is where
    /// the formulas are read from.
    /// </summary>
    public Func<uint, uint, IReadOnlyDictionary<uint, uint>, uint>?
        ResolveSkillFormulaBonus { get; init; }

    /// <summary>The clock a spell or attack window is measured against.</summary>
    public required Func<double> ClientTime { get; init; }

    /// <summary>What to do when the server asks the character a question.</summary>
    public required Action<GameEvents.CharacterConfirmationRequest>
        OnConfirmationRequest { get; init; }

    /// <summary>And when it says the question is settled.</summary>
    public required Action<GameEvents.CharacterConfirmationDone>
        OnConfirmationDone { get; init; }

    /// <summary>Takes note that the server has seeded the character options.</summary>
    public required Action NoteOptionsSeeded { get; init; }
}

internal static partial class HeadlessAutomationCapabilities
{
    /// <summary>
    /// Builds the live-session bindings this host really hands the session
    /// host, so the census reads the code rather than a list beside it.
    /// </summary>
    internal static LiveSessionHostBindings BuildSessionHostBindings(
        HeadlessSessionHostParts parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        SessionStatusWriter status = parts.StatusWriter;
        string sessionId = parts.SessionId;
        return new LiveSessionHostBindings(
            Routing: new(parts.CreateEvents, parts.CreateCommands),
            Reset: parts.Reset,
            Selection: new(
                SetPlayerIdentity: id => parts.Identity.ServerGuid = id,
                // Nothing here draws a vitals bar, keeps a drawn world's
                // persistent set or watches for a drawable going missing.
                SetVitalsIdentity: _ => { },
                SetChatIdentity: id =>
                    parts.Communication.Chat.SetLocalPlayerGuid(id),
                MarkPersistent: _ => { },
                SetVanishProbeIdentity: _ => { },
                ClearCombat: () => parts.Combat.Clear()),
            EnteredWorld: new(
                SetActiveCharacter: name => parts.NoteActiveCharacter(name),
                // There is no panel layout to restore, no toolbar to sync and
                // no camera to put behind the character.
                RestoreLayout: () => { },
                SyncToolbar: () => { },
                LoadCharacterSettings: _ => { },
                ArmPlayerModeAutoEntry: () => { },
                ResumeWorldAudio: () => { }),
            Connecting: (host, port, user) =>
                parts.Diagnostic($"connecting:{host}:{port}:{user}"),
            Connected: () =>
            {
                parts.Diagnostic("connected");
                status.Connected(sessionId);
                parts.NoteConnected();
            },
            Roster: roster => status.CharacterList(sessionId, roster),
            CharacterEntered: selection => status.EnteredWorld(
                sessionId,
                selection.CharacterId,
                selection.CharacterName),
            LoginCommands: parts.LoginCommands,
            CharacterCreated: identity => status.CharacterCreated(
                sessionId,
                identity.Guid,
                identity.Name),
            CreationFailed: rejection => status.CreationFailed(
                sessionId,
                rejection.RawCode,
                rejection.Reason,
                rejection.AttemptedName));
    }

    /// <summary>
    /// Builds the character bindings this host really hands the event router.
    /// The skill-formula resolver is the load-bearing part: without it every
    /// skill the server sends loses its attribute-derived term, so a bot
    /// reads its own skills far below what the server credits it with.
    /// </summary>
    internal static LiveCharacterSessionBindings BuildCharacterSessionBindings(
        HeadlessCharacterSessionParts parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        return new LiveCharacterSessionBindings(
            parts.Combat,
            parts.Character,
            ResolveSkillFormulaBonus: parts.ResolveSkillFormulaBonus is
                { } resolveSkillFormulaBonus
                ? (skill, raw, credits) =>
                    resolveSkillFormulaBonus(skill, raw, credits)
                : null,
            OnSkillsUpdated: null,
            OnConfirmationRequest: request =>
                parts.OnConfirmationRequest(request),
            OnConfirmationDone: done => parts.OnConfirmationDone(done),
            ClientTime: () => parts.ClientTime(),
            OnMovementStatsUpdated: null,
            OnCharacterOptionsChanged: (_, _) => parts.NoteOptionsSeeded());
    }
}
