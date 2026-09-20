using AcDream.App.Input;
using AcDream.App.Net;
using AcDream.Core.Combat;
using AcDream.Core.Net.Messages;
using AcDream.Runtime.Gameplay;

namespace AcDream.App.Combat;

internal interface ICombatAttackTargetSource
{
    /// <summary>
    /// The creature this attack goes to: the selected one when it can be
    /// attacked, else the nearest monster when the caller allows the
    /// substitution and the character's option asks for it.
    /// </summary>
    uint? GetSelectedOrClosestCombatTarget(bool allowAutoTarget);
}

internal interface ICombatGameplaySettingsSource
{
    bool AutoTarget { get; }
    bool AutoRepeatAttack { get; }
    bool ViewCombatTarget { get; }
}

internal sealed class CharacterOptionCombatSettingsSource : ICombatGameplaySettingsSource
{
    private readonly RuntimeCharacterOptionsState _options;

    public CharacterOptionCombatSettingsSource(RuntimeCharacterOptionsState options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public bool AutoTarget =>
        _options.GetOptionBit(CharacterOptionId.AutoTarget);

    public bool AutoRepeatAttack =>
        _options.GetOptionBit(CharacterOptionId.AutoRepeatAttack);

    public bool ViewCombatTarget =>
        _options.GetOptionBit(CharacterOptionId.ViewCombatTarget);
}

internal interface ICombatFeedbackSink
{
    void Show(string message);
}

internal sealed class CombatFeedbackSlot : ICombatFeedbackSink
{
    private Action<string>? _target;

    public void Bind(Action<string> target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (_target is not null && !ReferenceEquals(_target, target))
            throw new InvalidOperationException(
                "Combat feedback is already bound to a presentation target.");
        _target = target;
    }

    public IDisposable BindOwned(Action<string> target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (_target is not null)
            throw new InvalidOperationException(
                "Combat feedback is already bound to a presentation target.");
        _target = target;
        return new Binding(this, target);
    }

    public void Unbind(Action<string> target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (ReferenceEquals(_target, target))
            _target = null;
    }

    public void Show(string message) => _target?.Invoke(message);

    private sealed class Binding(CombatFeedbackSlot slot, Action<string> target)
        : IDisposable
    {
        public void Dispose() => slot.Unbind(target);
    }
}

internal sealed class CombatAttackOperationsSlot
    : IRuntimeCombatAttackOperations
{
    private IRuntimeCombatAttackOperations? _owner;

    public void Bind(IRuntimeCombatAttackOperations owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (_owner is not null && !ReferenceEquals(_owner, owner))
            throw new InvalidOperationException(
                "Combat attack operations are already bound.");
        _owner = owner;
    }

    public IDisposable BindOwned(IRuntimeCombatAttackOperations owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (_owner is not null)
            throw new InvalidOperationException(
                "Combat attack operations are already bound.");
        _owner = owner;
        return new Binding(this, owner);
    }

    private void Unbind(IRuntimeCombatAttackOperations expected)
    {
        if (ReferenceEquals(_owner, expected))
            _owner = null;
    }

    public bool CanStartAttack(bool allowAutoTarget) =>
        _owner?.CanStartAttack(allowAutoTarget) == true;
    public void PrepareAttackRequest() => _owner?.PrepareAttackRequest();
    public bool SendAttack(AttackHeight height, float power, bool allowAutoTarget) =>
        _owner?.SendAttack(height, power, allowAutoTarget) == true;
    public void SendCancelAttack() => _owner?.SendCancelAttack();
    public bool IsDualWield => _owner?.IsDualWield == true;
    public bool PlayerReadyForAttack => _owner?.PlayerReadyForAttack == true;
    public bool AutoRepeatAttack => _owner?.AutoRepeatAttack == true;

    private sealed class Binding : IDisposable
    {
        private CombatAttackOperationsSlot? _slot;
        private readonly IRuntimeCombatAttackOperations _expected;

        public Binding(
            CombatAttackOperationsSlot slot,
            IRuntimeCombatAttackOperations expected)
        {
            _slot = slot;
            _expected = expected;
        }

        public void Dispose() =>
            Interlocked.Exchange(ref _slot, null)?.Unbind(_expected);
    }
}

internal sealed class LiveCombatAttackOperations
    : IRuntimeCombatAttackOperations
{
    private readonly CombatState _combat;
    private readonly ICombatAttackTargetSource _targets;
    private readonly ICombatGameplaySettingsSource _settings;
    private readonly IRuntimeLocalPlayerControllerSource _player;
    private readonly LocalPlayerOutboundController _outbound;
    private readonly ILiveInWorldSource _inWorld;
    private readonly ILiveWorldSessionSource _session;
    private readonly ICombatFeedbackSink _feedback;

    public LiveCombatAttackOperations(
        CombatState combat,
        ICombatAttackTargetSource targets,
        ICombatGameplaySettingsSource settings,
        IRuntimeLocalPlayerControllerSource player,
        LocalPlayerOutboundController outbound,
        ILiveInWorldSource inWorld,
        ILiveWorldSessionSource session,
        ICombatFeedbackSink feedback)
    {
        _combat = combat ?? throw new ArgumentNullException(nameof(combat));
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _outbound = outbound ?? throw new ArgumentNullException(nameof(outbound));
        _inWorld = inWorld ?? throw new ArgumentNullException(nameof(inWorld));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _feedback = feedback ?? throw new ArgumentNullException(nameof(feedback));
    }

    public bool IsDualWield =>
        _player.Controller?.Motion.InterpretedState.CurrentStyle
        == CombatInputPlanner.DualWieldCombatStyle;

    public bool PlayerReadyForAttack
    {
        get
        {
            if (_player.Controller is not { } controller)
                return false;
            var motion = controller.Motion.InterpretedState;
            return CombatInputPlanner.PlayerInReadyPositionForAttack(
                _combat.CurrentMode,
                motion.CurrentStyle,
                motion.ForwardCommand);
        }
    }

    public bool AutoRepeatAttack => _settings.AutoRepeatAttack;

    public bool CanStartAttack(bool allowAutoTarget) =>
        ResolveAttackTarget(allowAutoTarget) is not null;

    /// <summary>
    /// The creature this attack goes to, with the refusals a player sees.
    /// The swing is sent to what this returns rather than to whatever the
    /// selection holds when the swing goes out: the two are the same thing
    /// for a player, and they are not when something else moves the
    /// selection between the request and the swing.
    /// </summary>
    private uint? ResolveAttackTarget(bool allowAutoTarget)
    {
        if (!_inWorld.IsInWorld)
            return null;

        if (!CombatInputPlanner.SupportsTargetedAttack(_combat.CurrentMode))
        {
            Console.WriteLine(
                "combat: attack ignored; not in melee/missile combat mode");
            return null;
        }

        if (_targets.GetSelectedOrClosestCombatTarget(allowAutoTarget)
            is not { } target)
        {
            _feedback.Show(AcDream.Core.Chat.ClientTextRefusals.MustSelectCombatTarget);
            Console.WriteLine("combat: attack ignored; no creature target found");
            return null;
        }

        return target;
    }

    public bool SendAttack(AttackHeight height, float power, bool allowAutoTarget)
    {
        if (ResolveAttackTarget(allowAutoTarget) is not { } target
            || _session.CurrentSession is not { } session)
        {
            return false;
        }

        power = Math.Clamp(power, 0f, 1f);
        if (_combat.CurrentMode == CombatMode.Missile)
        {
            session.SendMissileAttack(target, height, power);
            Console.WriteLine(
                $"combat: missile attack target=0x{target:X8} height={height} accuracy={power:F2}");
        }
        else
        {
            session.SendMeleeAttack(target, height, power);
            Console.WriteLine(
                $"combat: melee attack target=0x{target:X8} height={height} power={power:F2}");
        }
        return true;
    }

    public void SendCancelAttack() =>
        _session.CurrentSession?.SendCancelAttack();

    public void PrepareAttackRequest()
    {
        if (_player.Controller is not { } controller
            || !controller.PrepareForAttackRequest())
        {
            return;
        }

        _outbound.TrySendMovement(
            _session.CurrentSession,
            controller,
            controller.CaptureMovementResult(mouseLookEvent: false));
    }
}
