using AcDream.App.Combat;
using AcDream.App.Net;
using AcDream.App.Plugins;
using AcDream.Core.Net;
using AcDream.Core.Plugins;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Plugins;
using AcDream.Runtime.Session;

namespace AcDream.HostParity.Tests;

/// <summary>
/// The windowed client with the window taken away. Everything below the
/// presentation layer is the real thing: the same operation classes the
/// window's composition binds, into the same slots the window builds its
/// runtime with, behind the capability record the window hands the shared
/// binding pass.
///
/// What is NOT under this arm, and why:
/// * the local player's movement controller, which composition only creates
///   once a body has been placed in a drawn world, so the windowed attack
///   path's pre-attack movement push has nothing to push;
/// * the retained UI, the selection interaction controller, the entity
///   deletion controller and the input dispatcher, each of which needs a
///   presentation tree, so the seams they feed stay empty here exactly as
///   they do in a run with no window;
/// * the item-interaction owner, which the window builds with roughly forty
///   presentation-shaped callbacks;
/// * the spell-cast operations, which the windowed host only binds once a
///   spell catalog has been read out of the installed data files.
/// Each is reported through <see cref="Warnings"/> rather than faked, and a
/// scenario that would depend on one is not run.
/// </summary>
internal sealed class WindowedArm : ParityArm
{
    private readonly CombatFeedbackSlot _feedback = new();
    private readonly LiveSessionCommandSurface _commands = new();
    private readonly LiveSessionAppSource _sessionSource;
    private readonly WorldGameState _state = new();
    private readonly WorldEvents _events = new();
    private readonly RuntimeAutomationSurface _automation;
    private readonly List<string> _warnings = [];
    private readonly List<IDisposable> _bindings = [];

    internal WindowedArm()
        : base(
            ParityHost.Windowed,
            operations =>
            GraphicalAutomationCapabilities.BuildRuntimeDependencies(
                new CombatAttackOperationsSlot(),
                new RuntimeCombatTargetOperationsSlot(),
                new RuntimeCombatModeOperationsSlot(),
                new AcDream.App.Spells.RuntimeSpellCastOperationsSlot(),
                timeSyncDiagnostic: null,
                sessionOperations: operations))
    {
        var attack = (CombatAttackOperationsSlot)Dependencies.CombatAttackOperations;
        var target = (RuntimeCombatTargetOperationsSlot)
            Dependencies.CombatTargetOperations;

        _sessionSource = new LiveSessionAppSource(Runtime.Session, _commands);
        _bindings.Add(_feedback.BindOwned(text =>
            Runtime.CommunicationOwner.AddText(
                text, AcDream.Core.Chat.RetailLogTextType.ClientLocal)));
        _bindings.Add(attack.BindOwned(new LiveCombatAttackOperations(
            Runtime.ActionOwner.Combat,
            new CombatAttackTargetSource(Runtime),
            new CharacterOptionCombatSettingsSource(Runtime.CharacterOwner.Options),
            Runtime.MovementOwner,
            new LocalPlayerOutboundController(
                static (_, _, _, _, _, _) => { }),
            _sessionSource,
            _sessionSource,
            _feedback)));
        _bindings.Add(target.BindOwned(new LiveCombatTargetOperations(
            autoTarget: () => Runtime.CharacterOwner.Options.GetOptionBit(
                AcDream.Core.Net.Messages.CharacterOptionId.AutoTarget),
            selectClosestTarget: () =>
                RuntimeAttackTargetResolver.SelectClosest(Runtime))));

        var mode = (RuntimeCombatModeOperationsSlot)
            Dependencies.CombatModeOperations;
        _bindings.Add(mode.BindOwned(new LiveCombatModeOperations(
            new LiveSessionCombatModeAuthority(Session),
            new LocalPlayerCombatEquipmentSource(
                Runtime.EntityObjects.Objects,
                new AcDream.App.Input.LocalPlayerIdentityState(
                    Runtime.PlayerIdentity)),
            // The window tells its item owner that a stance change was the
            // player's doing, so an auto-wield in flight stands down. There
            // is no auto-wield on either arm, so neither has anything to
            // tell -- the windowless host passes its own absent controller.
            new UnwatchedCombatModeIntent())));

        _automation = new RuntimeAutomationSurface(_events);
        RuntimeAutomationBindings.Apply(
            _automation,
            Runtime,
            GraphicalAutomationCapabilities.Build(new GraphicalAutomationParts
            {
                Runtime = Runtime,
                Warn = _warnings.Add,
            }));
        Host = new AppPluginHost(
            new RecordingPluginLogger(),
            _state,
            _events,
            Runtime.ActionOwner.Selection,
            NoOpUiRegistry.Instance,
            _automation,
            commands: _automation.PluginCommands);
    }

    internal override IPluginHost Host { get; }

    internal override IReadOnlyList<string> Warnings => _warnings;

    /// <summary>
    /// The window drives the surface's own bookkeeping off the plugin event
    /// tick rather than off the session tick, so the arm does the same.
    /// </summary>
    protected override void OnAdvanced()
    {
        // The window ticks the attack owner from its gameplay input frame and
        // the surface from the plugin event tick; both happen once a frame.
        Runtime.ActionOwner.CombatAttack.Tick();
        _events.FireTick(TickSeconds);
    }

    /// <summary>
    /// The windowed combat and cast paths read the world connection off the
    /// session controller, not off the command route, so the route the window
    /// builds -- a chat and client-command surface -- has nothing to do with
    /// what these scenarios exercise.
    /// </summary>
    protected override ILiveSessionCommandRouting CreateCommandRoute(
        WorldSession session) => new InertCommandRoute();

    private sealed class InertCommandRoute : ILiveSessionCommandRouting
    {
        public void Activate()
        {
        }

        public void Dispose()
        {
        }
    }

    protected override void DisposeHost()
    {
        for (int index = _bindings.Count - 1; index >= 0; index--)
            _bindings[index].Dispose();
        _automation.Dispose();
    }

    /// <summary>
    /// Nothing is listening for "the player asked for this stance": there is
    /// no auto-wield controller on either arm.
    /// </summary>
    private sealed class UnwatchedCombatModeIntent : IExplicitCombatModeIntentSink
    {
        public void NotifyExplicitCombatModeRequest()
        {
        }
    }
}
