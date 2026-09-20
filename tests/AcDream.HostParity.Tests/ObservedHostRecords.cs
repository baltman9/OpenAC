using System.Reflection;
using System.Runtime.CompilerServices;
using AcDream.App.Plugins;
using AcDream.Core.Net;
using AcDream.Headless.Plugins;
using AcDream.Runtime;
using AcDream.Runtime.Plugins;
using AcDream.Runtime.Session;

namespace AcDream.HostParity.Tests;

/// <summary>
/// The records each host really builds, built here so the census reads the
/// hosts' own code instead of a list written beside it.
///
/// Neither host can be started without, respectively, a window and a world
/// connection, so the parts are handed in as bare instances that are never
/// called: a capability builder only turns a part into a delegate or passes
/// the reference on, and what this file needs to know is which capabilities
/// came out of that, not what they do. Anything that would read a part while
/// building -- a data table, for instance -- has to be deferred in the
/// builder, which is where it belongs anyway.
///
/// Every delegate this file stands in for does something, because the census
/// now reads a do-nothing delegate as a binding that is not there. A stand-in
/// that did nothing would be indistinguishable from the difference the census
/// exists to find, so each one notes what it was asked.
/// </summary>
internal static class ObservedHostRecords
{
    /// <summary>A part that exists but is never called.</summary>
    private static T Part<T>() where T : class =>
        (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    private static readonly List<string> Noted = [];

    /// <summary>A stand-in for a part that takes a line and acts on it.</summary>
    private static void Note(string text) => Noted.Add(text);

    /// <summary>The same, for a part that also answers.</summary>
    private static bool NoteAndAccept(string text)
    {
        Noted.Add(text);
        return true;
    }

    private static bool NoteAndAnswer(uint context, bool answer)
    {
        Noted.Add($"{context}:{answer}");
        return true;
    }

    private static void NoteNothingHappened() => Noted.Add("done");

    private static void NoteGeneration(RuntimeGenerationToken generation) =>
        Noted.Add($"generation {generation.Value}");

    private static double NoteAndAnswerTime()
    {
        Noted.Add("time");
        return 0d;
    }

    private static uint NoteAndAnswerSkill(
        uint skill,
        uint raw,
        IReadOnlyDictionary<uint, uint> credits)
    {
        Noted.Add($"skill {skill}");
        return raw;
    }

    private static void NoteConfirmationRequest(
        AcDream.Core.Net.Messages.GameEvents.CharacterConfirmationRequest
            request) => Noted.Add($"asked {request.ContextId}");

    private static void NoteConfirmationDone(
        AcDream.Core.Net.Messages.GameEvents.CharacterConfirmationDone done) =>
        Noted.Add("answered");

    /// <summary>
    /// A stand-in for taking hold of a world connection. Never called: the
    /// census only reads which bindings came out of the builder.
    /// </summary>
    private static ILiveSessionEventRouting NoEventRoute(WorldSession session) =>
        throw new NotSupportedException(
            "The census never opens a world connection.");

    private static ILiveSessionCommandRouting NoCommandRoute(
        WorldSession session) =>
        throw new NotSupportedException(
            "The census never opens a world connection.");

    /// <summary>
    /// The windowed host's capability record with every part present, which
    /// is the most it can ever supply.
    /// </summary>
    internal static RuntimeAutomationHostCapabilities WindowedCapabilities() =>
        GraphicalAutomationCapabilities.Build(new GraphicalAutomationParts
        {
            Runtime = Part<GameRuntime>(),
            Warn = Note,
            Content = Part<AcDream.Content.RuntimeDatCollection>(),
            MagicCatalog = Part<AcDream.Content.MagicCatalog>(),
            SessionCommands = Part<AcDream.App.Runtime.CurrentGameRuntimeAdapter>(),
            NavigationWalk = Part<AcDream.Runtime.Navigation.NavigationWalkController>(),
            Teleport = Part<AcDream.App.Streaming.LocalPlayerTeleportController>(),
            RetainedUi = Part<AcDream.App.UI.RetailUiRuntime>(),
            Selection = Part<AcDream.App.Interaction.SelectionInteractionController>(),
            Input = Part<AcDream.UI.Abstractions.Input.InputDispatcher>(),
        });

    /// <summary>
    /// The windowless host's capability record with every part present, which
    /// is the most it can ever supply.
    /// </summary>
    internal static RuntimeAutomationHostCapabilities WindowlessCapabilities() =>
        HeadlessAutomationCapabilities.Build(new HeadlessAutomationParts
        {
            Runtime = Part<GameRuntime>(),
            Warn = Note,
            Content = Part<AcDream.Content.RuntimeDatCollection>(),
            MagicCatalog = Part<AcDream.Content.MagicCatalog>(),
            SubmitChatText = NoteAndAccept,
            SessionCommands = Part<AcDream.App.Runtime.CurrentGameRuntimeAdapter>(),
            NavigationWalk = Part<AcDream.Runtime.Navigation.NavigationWalkController>(),
            Logout = Part<AcDream.Headless.Hosting.HeadlessLogoutAutomation>(),
            AnswerConfirmation = NoteAndAnswer,
        });

    internal static RuntimeAutomationHostCapabilities CapabilitiesFor(string host) =>
        host == ParityHost.Windowed
            ? WindowedCapabilities()
            : WindowlessCapabilities();

    /// <summary>
    /// The windowed host's dependency record with every optional dependency
    /// present, which is the most it can ever supply.
    /// </summary>
    internal static GameRuntimeDependencies WindowedRuntimeDependencies() =>
        GraphicalAutomationCapabilities.BuildRuntimeDependencies(
            Part<AcDream.App.Combat.CombatAttackOperationsSlot>(),
            Part<AcDream.App.Combat.RuntimeCombatTargetOperationsSlot>(),
            Part<AcDream.App.Combat.RuntimeCombatModeOperationsSlot>(),
            Part<AcDream.App.Spells.RuntimeSpellCastOperationsSlot>(),
            timeSyncDiagnostic: Note,
            sessionOperations:
                Part<AcDream.Runtime.Session.ProductionLiveSessionOperations>());

    /// <summary>
    /// The windowless host's dependency record with every optional dependency
    /// present, which is the most it can ever supply.
    /// </summary>
    internal static GameRuntimeDependencies WindowlessRuntimeDependencies() =>
        HeadlessAutomationCapabilities.BuildRuntimeDependencies(
            Part<AcDream.Headless.Hosting.HeadlessGameplayOperations>(),
            TimeProvider.System,
            Note,
            Part<AcDream.Runtime.Session.ProductionLiveSessionOperations>());

    internal static GameRuntimeDependencies RuntimeDependenciesFor(string host) =>
        host == ParityHost.Windowed
            ? WindowedRuntimeDependencies()
            : WindowlessRuntimeDependencies();

    /// <summary>
    /// The live-session bindings the windowed host really hands the session
    /// host, with every part present.
    /// </summary>
    internal static LiveSessionHostBindings WindowedSessionHostBindings() =>
        GraphicalAutomationCapabilities.BuildSessionHostBindings(
            new GraphicalSessionHostParts
            {
                CreateEvents = NoEventRoute,
                CreateCommands = NoCommandRoute,
                Reset = NoteGeneration,
                Identity = Part<AcDream.App.Input.LocalPlayerIdentityState>(),
                Communication = Part<AcDream.Runtime.Gameplay
                    .RuntimeCommunicationState>(),
                Combat = Part<AcDream.Core.Combat.CombatState>(),
                Settings = Part<AcDream.App.Settings.RuntimeSettingsController>(),
                PlayerModeAutoEntry = Part<AcDream.App.Input.PlayerModeAutoEntry>(),
                WorldState = Part<AcDream.App.Streaming.GpuWorldState>(),
                Teleport = Part<AcDream.App.Streaming
                    .DeferredLocalPlayerTeleportNetworkSink>(),
                StatusWriter = Part<SessionStatusWriter>(),
                SessionId = "census",
                Vitals = Part<AcDream.UI.Abstractions.Panels.Vitals.VitalsVM>(),
                RetainedUi = Part<AcDream.App.UI.RetailUiRuntime>(),
                Paperdoll = Part<AcDream.App.Rendering.PaperdollFramePresenter>(),
                WorldAudio = Part<AcDream.App.Audio.WorldAudioSessionGate>(),
                LoginCommands = Part<AcDream.Runtime.Chat.LoginCommandSequence>(),
            });

    /// <summary>The same, for the windowless host.</summary>
    internal static LiveSessionHostBindings WindowlessSessionHostBindings() =>
        HeadlessAutomationCapabilities.BuildSessionHostBindings(
            new HeadlessSessionHostParts
            {
                CreateEvents = NoEventRoute,
                CreateCommands = NoCommandRoute,
                Reset = NoteGeneration,
                Identity = Part<AcDream.Runtime.Gameplay
                    .RuntimeLocalPlayerIdentityState>(),
                Communication = Part<AcDream.Runtime.Gameplay
                    .RuntimeCommunicationState>(),
                Combat = Part<AcDream.Core.Combat.CombatState>(),
                NoteActiveCharacter = Note,
                Diagnostic = Note,
                StatusWriter = Part<SessionStatusWriter>(),
                SessionId = "census",
                NoteConnected = NoteNothingHappened,
                LoginCommands = Part<AcDream.Runtime.Chat.LoginCommandSequence>(),
            });

    internal static LiveSessionHostBindings SessionHostBindingsFor(string host) =>
        host == ParityHost.Windowed
            ? WindowedSessionHostBindings()
            : WindowlessSessionHostBindings();

    /// <summary>
    /// The character bindings the windowed host really hands the event
    /// router, with every part present.
    /// </summary>
    internal static LiveCharacterSessionBindings
        WindowedCharacterSessionBindings() =>
        GraphicalAutomationCapabilities.BuildCharacterSessionBindings(
            new GraphicalCharacterSessionParts
            {
                Character = Part<AcDream.Runtime.Gameplay
                    .RuntimeCharacterState>(),
                Combat = Part<AcDream.Core.Combat.CombatState>(),
                Settings = Part<AcDream.App.Settings.RuntimeSettingsController>(),
                ApplyMovementStats = Note,
                ResolveSkillFormulaBonus = NoteAndAnswerSkill,
                ClientTime = NoteAndAnswerTime,
                RetainedUi = Part<AcDream.App.UI.RetailUiRuntime>(),
            });

    /// <summary>The same, for the windowless host.</summary>
    internal static LiveCharacterSessionBindings
        WindowlessCharacterSessionBindings() =>
        HeadlessAutomationCapabilities.BuildCharacterSessionBindings(
            new HeadlessCharacterSessionParts
            {
                Character = Part<AcDream.Runtime.Gameplay
                    .RuntimeCharacterState>(),
                Combat = Part<AcDream.Core.Combat.CombatState>(),
                ResolveSkillFormulaBonus = NoteAndAnswerSkill,
                ClientTime = NoteAndAnswerTime,
                OnConfirmationRequest = NoteConfirmationRequest,
                OnConfirmationDone = NoteConfirmationDone,
                NoteOptionsSeeded = NoteNothingHappened,
            });

    internal static LiveCharacterSessionBindings
        CharacterSessionBindingsFor(string host) =>
        host == ParityHost.Windowed
            ? WindowedCharacterSessionBindings()
            : WindowlessCharacterSessionBindings();

    internal static IReadOnlyDictionary<string, string> ConditionalCapabilities(
        string host) =>
        host == ParityHost.Windowed
            ? GraphicalAutomationCapabilities.Conditional
            : HeadlessAutomationCapabilities.Conditional;

    internal static IReadOnlyDictionary<string, string>
        ConditionalRuntimeDependencies(string host) =>
        host == ParityHost.Windowed
            ? GraphicalAutomationCapabilities.ConditionalRuntimeDependencies
            : HeadlessAutomationCapabilities.ConditionalRuntimeDependencies;

    /// <summary>
    /// What a capability record really carries, with a capability that is
    /// there and does nothing counted as absent.
    /// </summary>
    internal static IReadOnlySet<string> SuppliedCapabilities(
        RuntimeAutomationHostCapabilities record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var supplied = new HashSet<string>(
            record.Supplied(),
            StringComparer.Ordinal);
        foreach (PropertyInfo property in RuntimeAutomationHostCapabilities
            .CapabilityProperties)
        {
            if (InertBindings.DoesNothing(property.GetValue(record)))
                supplied.Remove(property.Name);
        }
        return supplied;
    }

    /// <summary>
    /// Which members of a dependency record this run really filled in. Only
    /// the ones that can be left out are interesting: a member with no
    /// meaningful empty value is the same on every host by construction. A
    /// member holding a delegate that does nothing counts as left out.
    /// </summary>
    internal static IReadOnlySet<string> SuppliedMembers(object record)
    {
        var supplied = new HashSet<string>(StringComparer.Ordinal);
        foreach (PropertyInfo property in record.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.Name == "EqualityContract")
                continue;
            if (property.PropertyType.IsValueType)
                continue;
            object? value = property.GetValue(record);
            if (value is null || InertBindings.DoesNothing(value))
                continue;
            supplied.Add(property.Name);
        }
        return supplied;
    }

    /// <summary>
    /// The live-session bindings are three records deep -- the session's own,
    /// the ones about taking hold of a character, and the ones about arriving
    /// in the world -- and the census names members of all three, so the
    /// observed set is flattened the same way.
    /// </summary>
    internal static IReadOnlySet<string> SuppliedSessionHostMembers(
        LiveSessionHostBindings bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        var supplied = new HashSet<string>(
            SuppliedMembers(bindings),
            StringComparer.Ordinal);
        if (bindings.Selection is { } selection)
            supplied.UnionWith(SuppliedMembers(selection));
        if (bindings.EnteredWorld is { } enteredWorld)
            supplied.UnionWith(SuppliedMembers(enteredWorld));
        return supplied;
    }

    internal static IReadOnlySet<string> SuppliedSessionHostMembersFor(
        string host) =>
        SuppliedSessionHostMembers(SessionHostBindingsFor(host));
}
