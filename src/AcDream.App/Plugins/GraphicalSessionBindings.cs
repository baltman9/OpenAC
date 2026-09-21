using AcDream.App.Audio;
using AcDream.App.Input;
using AcDream.App.Rendering;
using AcDream.App.Settings;
using AcDream.App.Streaming;
using AcDream.App.UI;
using AcDream.Core.Combat;
using AcDream.Core.Net;
using AcDream.Runtime;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Chat;
using AcDream.Runtime.Session;
using AcDream.UI.Abstractions.Panels.Vitals;

namespace AcDream.App.Plugins;

/// <summary>
/// The parts the windowed host's live-session bindings are built from. Each is
/// a leaf the session composition already owns, named here so the record the
/// session host is really given can be built -- and inspected -- without
/// opening a window.
/// </summary>
internal sealed record GraphicalSessionHostParts
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

    public required LocalPlayerIdentityState Identity { get; init; }

    public required RuntimeCommunicationState Communication { get; init; }

    public required CombatState Combat { get; init; }

    public required RuntimeSettingsController Settings { get; init; }

    public required PlayerModeAutoEntry PlayerModeAutoEntry { get; init; }

    public required GpuWorldState WorldState { get; init; }

    public required DeferredLocalPlayerTeleportNetworkSink Teleport { get; init; }

    public required SessionStatusWriter StatusWriter { get; init; }

    public required string SessionId { get; init; }

    public VitalsVM? Vitals { get; init; }

    public RetailUiRuntime? RetainedUi { get; init; }

    public PaperdollFramePresenter? Paperdoll { get; init; }

    public WorldAudioSessionGate? WorldAudio { get; init; }

    public LoginCommandSequence? LoginCommands { get; init; }

    /// <summary>
    /// Where a binding problem is reported. Host-shaped, not a binding: it
    /// lends the shared check somewhere to put a line about a declared
    /// binding that arrived empty.
    /// </summary>
    public Action<string>? Warn { get; init; }
}

/// <summary>
/// The parts the windowed host's character bindings are built from, for the
/// same reason.
/// </summary>
internal sealed record GraphicalCharacterSessionParts
{
    public required RuntimeCharacterState Character { get; init; }

    public required CombatState Combat { get; init; }

    public required RuntimeSettingsController Settings { get; init; }

    /// <summary>
    /// The runtime's own owner of how fast the character runs and jumps.
    /// </summary>
    public required AcDream.Runtime.Gameplay.RuntimeMovementStatsApplier
        MovementStats { get; init; }

    /// <summary>Turns a raw skill level into the level the server credits.</summary>
    public required Func<uint, uint, IReadOnlyDictionary<uint, uint>, uint>
        ResolveSkillFormulaBonus { get; init; }

    /// <summary>The clock a spell or attack window is measured against.</summary>
    public required Func<double> ClientTime { get; init; }

    public RetailUiRuntime? RetainedUi { get; init; }

    /// <summary>
    /// Told when the server has said what the character options are, so the
    /// options a session document declared can be compared against them.
    /// </summary>
    public required Action NoteOptionsSeeded { get; init; }

    /// <summary>Where a binding problem is reported, as above.</summary>
    public Action<string>? Warn { get; init; }
}

internal static partial class GraphicalAutomationCapabilities
{
    /// <summary>
    /// Builds the live-session bindings this host really hands the session
    /// host, so the census reads the code rather than a list beside it.
    /// </summary>
    internal static LiveSessionHostBindings BuildSessionHostBindings(
        GraphicalSessionHostParts parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        RetailUiRuntime? retainedUi = parts.RetainedUi;
        PaperdollFramePresenter? paperdoll = parts.Paperdoll;
        VitalsVM? vitals = parts.Vitals;
        WorldAudioSessionGate? worldAudio = parts.WorldAudio;
        RuntimeSettingsController settings = parts.Settings;
        SessionStatusWriter status = parts.StatusWriter;
        string sessionId = parts.SessionId;
        LocalPlayerIdentityState identity = parts.Identity;
        RuntimeCommunicationState communication = parts.Communication;
        var bindings = new LiveSessionHostBindings(
            Routing: new(parts.CreateEvents, parts.CreateCommands),
            Reset: parts.Reset,
            Selection: new(
                SetPlayerIdentity: id => identity.ServerGuid = id,
                SetVitalsIdentity: id => vitals?.SetLocalPlayerGuid(id),
                SetChatIdentity: id =>
                    communication.Chat.SetLocalPlayerGuid(id),
                MarkPersistent: id => parts.WorldState.MarkPersistent(id),
                SetVanishProbeIdentity: id => EntityVanishProbe.PlayerGuid = id,
                ClearCombat: () => parts.Combat.Clear(),
                ArmLoginTunnel: () => parts.Teleport.ArmLoginTunnel()),
            EnteredWorld: new(
                SetActiveCharacter: name => settings.SetActiveCharacter(name),
                RestoreLayout: () =>
                {
                    settings.SetGameplayDisplay(true);
                    retainedUi?.RestoreLayout();
                    retainedUi?.InventoryPanelController?.Populate();
                    paperdoll?.MarkDirty();
                    retainedUi?.RedeclareSocialPanelAfterWorldEntry();
                },
                SyncToolbar: () => retainedUi?.SyncToolbarWindowButtons(),
                LoadCharacterSettings: name =>
                {
                    settings.LoadCharacterContext(name);
                    retainedUi?.LoadJournal(name);
                },
                ArmPlayerModeAutoEntry: () => parts.PlayerModeAutoEntry.Arm(),
                ResumeWorldAudio: () => worldAudio?.ResumeForWorldEntry()),
            Connecting: (host, port, user) =>
                communication.Chat.OnSystemMessage(
                    $"connecting to {host}:{port} as {user}",
                    chatType: 1),
            Connected: () =>
            {
                communication.Chat.OnSystemMessage(
                    "connected — character list received",
                    chatType: 1);
                status.Connected(sessionId);
            },
            Roster: roster => status.CharacterList(sessionId, roster),
            CharacterEntered: selection => status.EnteredWorld(
                sessionId,
                selection.CharacterId,
                selection.CharacterName),
            LoginCommands: parts.LoginCommands,
            CharacterCreated: identityCreated => status.CharacterCreated(
                sessionId,
                identityCreated.Guid,
                identityCreated.Name),
            CreationFailed: rejection => status.CreationFailed(
                sessionId,
                rejection.RawCode,
                rejection.Reason,
                rejection.AttemptedName));
        LiveSessionBindingDeclarations.ReportDeclaredButUnfilled(
            "windowed",
            "live-session host binding",
            bindings,
            DeclaredSessionHostBindings,
            ConditionalSessionHostBindings,
            parts.Warn);
        return bindings;
    }

    /// <summary>
    /// Builds the character bindings this host really hands the event router.
    /// </summary>
    internal static LiveCharacterSessionBindings BuildCharacterSessionBindings(
        GraphicalCharacterSessionParts parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        RetailUiRuntime? retainedUi = parts.RetainedUi;
        RuntimeSettingsController settings = parts.Settings;
        RuntimeCharacterState character = parts.Character;
        AcDream.Runtime.Gameplay.RuntimeMovementStatsApplier movementStats =
            parts.MovementStats;
        var bindings = new LiveCharacterSessionBindings(
            parts.Combat,
            character,
            ResolveSkillFormulaBonus: (skill, raw, credits) =>
                parts.ResolveSkillFormulaBonus(skill, raw, credits),
            OnSkillsUpdated: (runSkill, jumpSkill) =>
                movementStats.Apply("skills"),
            OnConfirmationRequest: request =>
                retainedUi?.HandleConfirmationRequest(request),
            OnConfirmationDone: done =>
                retainedUi?.HandleConfirmationDone(done),
            ClientTime: () => parts.ClientTime(),
            OnMovementStatsUpdated: () => movementStats.Apply("stats"),
            OnCharacterOptionsChanged: (_, options2) =>
            {
                settings.SyncChatFromServerOptions(options2);
                settings.SetUiLocked(character.Options.GetOptionBit(
                    AcDream.Core.Net.Messages.CharacterOptionId.LockUI));
                // Open option-bearing panels re-read the live bits at every
                // seed, which happens on login and again on a reconnect.
                settings.NotifyServerOptionsSeeded();
                parts.NoteOptionsSeeded();
            });
        LiveSessionBindingDeclarations.ReportDeclaredButUnfilled(
            "windowed",
            "character-session binding",
            bindings,
            DeclaredCharacterSessionBindings,
            ConditionalCharacterSessionBindings,
            parts.Warn);
        return bindings;
    }
}
