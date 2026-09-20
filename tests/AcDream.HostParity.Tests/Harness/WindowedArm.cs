using AcDream.App.Combat;
using AcDream.App.Input;
using AcDream.App.Rendering;
using AcDream.App.Net;
using AcDream.App.Plugins;
using AcDream.Core.Net;
using AcDream.Core.Plugins;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Plugins;
using AcDream.Runtime.Session;
using AcDream.UI.Abstractions.Input;
using Silk.NET.Input;

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
/// * the three extras the window adds to the shared item owner -- the
///   walk-to-then-act route, which pack the inventory panel has open, and
///   how much of a stack the split control is asking for -- which need a
///   presentation tree; the item commands themselves come from the runtime
///   and are under this arm;
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
    private InputDispatcher? _dispatcher;

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
            Runtime,
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

    /// <summary>
    /// The windowed client's own frame host, built from the real classes:
    /// its camera, its player-mode state, its chase-camera slot, its input
    /// source over a real detached dispatcher, the runtime's movement owner
    /// and the shared outbound owner. Two things it asks for are the drawn
    /// world's and are stood in for here -- what the world says about the
    /// player's body (which drawable, out of sight, clock running) and the
    /// projection of the result onto that drawable -- because nothing is
    /// drawn. Both are reported, and the first is the difference the entity
    /// projection work has to close.
    /// </summary>
    protected override RuntimeLocalPlayerFrameController
        CreateFrameController()
    {
        var camera = new CameraController(new OrbitCamera(), new FlyCamera());
        var mode = new AcDream.App.Input.LocalPlayerModeState
        {
            IsPlayerMode = true,
        };
        var chase = new AcDream.App.Input.ChaseCameraInputState
        {
            Legacy = new ChaseCamera(),
        };
        var input = new DispatcherMovementInputSource(Runtime.MovementOwner);
        _dispatcher = InputDispatcher.CreateDetached(
            new SilentKeyboard(),
            new SilentMouse(),
            new KeyBindings());
        input.Bind(_dispatcher);
        var frameRuntime = new AcDream.App.Input.LiveLocalPlayerFrameRuntime(
            camera,
            mode,
            Runtime.MovementOwner,
            chase,
            input,
            new RuntimeDirectoryWorldFacts(Runtime),
            new AcDream.App.Input.LocalPlayerIdentityState(
                Runtime.PlayerIdentity),
            new AcDream.App.Input.LocalPlayerPhysicsHostSlot(),
            new AcDream.App.Input.LocalPlayerProjectionController(
                new UndrawnProjectionRuntime()),
            new LocalPlayerOutboundController(
                static (_, _, _, _, _, _) => { }),
            _sessionSource);
        return Runtime.CreateLocalPlayerFrameController(frameRuntime, input);
    }

    /// <summary>
    /// What the world says about the player's body when nothing is drawing
    /// it. The windowed client asks its drawn-entity owner these three
    /// questions and the windowless one asks the entity directory; until that
    /// is one owner, an arm with no window has to read the directory.
    /// </summary>
    private sealed class RuntimeDirectoryWorldFacts(GameRuntime runtime)
        : AcDream.App.Input.ILocalPlayerWorldFacts
    {
        public uint ResolveLocalEntityId(uint serverGuid) =>
            serverGuid != 0u
            && runtime.EntityObjects.Entities.TryGetActive(
                serverGuid,
                out var record)
                ? record.LocalEntityId ?? 0u
                : 0u;

        public bool IsHidden(uint serverGuid) =>
            serverGuid != 0u
            && runtime.EntityObjects.Entities.TryGetActive(
                serverGuid,
                out var record)
            && (record.FinalPhysicsState
                & AcDream.Core.Physics.PhysicsStateFlags.Hidden) != 0;

        public AcDream.Core.Physics.RetailObjectClockDisposition
            GetRootObjectClockDisposition(uint serverGuid)
        {
            if (serverGuid == 0u
                || !runtime.EntityObjects.Entities.TryGetActive(
                    serverGuid,
                    out var record)
                || record.FullCellId == 0u
                || (record.FinalPhysicsState
                    & (AcDream.Core.Physics.PhysicsStateFlags.Frozen
                        | AcDream.Core.Physics.PhysicsStateFlags.Static)) != 0)
            {
                return AcDream.Core.Physics.RetailObjectClockDisposition
                    .Suspend;
            }
            return AcDream.Core.Physics.RetailObjectClockDisposition.Advance;
        }
    }

    /// <summary>There is no drawable to move, so the projection has nothing to do.</summary>
    private sealed class UndrawnProjectionRuntime
        : AcDream.App.Input.ILocalPlayerProjectionRuntime
    {
        public AcDream.Core.World.WorldEntity? ResolveEntity() => null;
        public int LiveCenterX => 0;
        public int LiveCenterY => 0;
        public void SyncShadow(
            AcDream.Core.World.WorldEntity entity, uint cellId)
        {
        }

        public void Rebucket(uint serverGuid, uint landblockId)
        {
        }

        public bool IsCurrentVisibleProjection(
            AcDream.Core.World.WorldEntity entity) => false;

        public void SuspendShadow(AcDream.Core.World.WorldEntity entity)
        {
        }
    }

    /// <summary>A keyboard nobody is typing on.</summary>
    private sealed class SilentKeyboard : IKeyboardSource
    {
#pragma warning disable CS0067
        public event Action<Key, ModifierMask>? KeyDown;
        public event Action<Key, ModifierMask>? KeyUp;
#pragma warning restore CS0067
        public bool IsHeld(Key key) => false;
        public ModifierMask CurrentModifiers => ModifierMask.None;
    }

    /// <summary>A mouse nobody is holding.</summary>
    private sealed class SilentMouse : IMouseSource
    {
#pragma warning disable CS0067
        public event Action<MouseButton, ModifierMask>? MouseDown;
        public event Action<MouseButton, ModifierMask>? MouseUp;
        public event Action<float, float>? MouseMove;
        public event Action<float>? Scroll;
#pragma warning restore CS0067
        public bool WantCaptureKeyboard { get; set; }
        public bool WantCaptureMouse { get; set; }
        public bool IsHeld(MouseButton button) => false;
    }

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
        _dispatcher?.Dispose();
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
