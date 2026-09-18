using System.Diagnostics;
using AcDream.Core.Combat;

namespace AcDream.Runtime.Gameplay;

public enum RuntimeInputActivation
{
    Press,
    Release,
}

public enum RuntimeCombatAttackCommand
{
    LowAttack,
    MediumAttack,
    HighAttack,
    DecreasePower,
    IncreasePower,
    AbortForMovement,
}

public readonly record struct RuntimeCombatAttackInput(
    RuntimeCombatAttackCommand Command,
    RuntimeInputActivation Activation);

public interface IRuntimeCombatAttackOperations
{
    /// <summary>
    /// Whether an attack can start now. With <paramref name="allowAutoTarget"/>
    /// the host may pick the closest hostile when nothing attackable is
    /// selected, as the player option allows; without it the selected
    /// target is the only one an attack may go to. Automation names its own
    /// targets, so it never gets the fallback.
    /// </summary>
    bool CanStartAttack(bool allowAutoTarget);
    void PrepareAttackRequest();
    bool SendAttack(AttackHeight height, float power, bool allowAutoTarget);
    void SendCancelAttack();
    bool IsDualWield { get; }
    bool PlayerReadyForAttack { get; }
    bool AutoRepeatAttack { get; }
}

internal sealed class DelegateRuntimeCombatAttackOperations
    : IRuntimeCombatAttackOperations
{
    private readonly Func<bool, bool> _canStartAttack;
    private readonly Action _prepareAttackRequest;
    private readonly Func<AttackHeight, float, bool, bool> _sendAttack;
    private readonly Action _sendCancelAttack;
    private readonly Func<bool> _isDualWield;
    private readonly Func<bool> _playerReadyForAttack;
    private readonly Func<bool> _autoRepeatAttack;

    public DelegateRuntimeCombatAttackOperations(
        Func<bool, bool> canStartAttack,
        Func<AttackHeight, float, bool, bool> sendAttack,
        Action? prepareAttackRequest,
        Action? sendCancelAttack,
        Func<bool>? isDualWield,
        Func<bool>? playerReadyForAttack,
        Func<bool>? autoRepeatAttack)
    {
        _canStartAttack = canStartAttack
            ?? throw new ArgumentNullException(nameof(canStartAttack));
        _sendAttack = sendAttack ?? throw new ArgumentNullException(nameof(sendAttack));
        _prepareAttackRequest = prepareAttackRequest ?? (() => { });
        _sendCancelAttack = sendCancelAttack ?? (() => { });
        _isDualWield = isDualWield ?? (() => false);
        _playerReadyForAttack = playerReadyForAttack ?? (() => true);
        _autoRepeatAttack = autoRepeatAttack ?? (() => false);
    }

    public bool CanStartAttack(bool allowAutoTarget) => _canStartAttack(allowAutoTarget);
    public void PrepareAttackRequest() => _prepareAttackRequest();
    public bool SendAttack(AttackHeight height, float power, bool allowAutoTarget) =>
        _sendAttack(height, power, allowAutoTarget);
    public void SendCancelAttack() => _sendCancelAttack();
    public bool IsDualWield => _isDualWield();
    public bool PlayerReadyForAttack => _playerReadyForAttack();
    public bool AutoRepeatAttack => _autoRepeatAttack();
}

public sealed class RuntimeCombatAttackState : IDisposable
{
    public const double AttackPowerUpSeconds = CombatInputPlanner.AttackPowerUpSeconds;
    public const double DualWieldPowerUpSeconds = CombatInputPlanner.DualWieldPowerUpSeconds;
    public const float InitialDesiredPower = 0.5f;
    public const float DesiredPowerStep = 1f / 6f;

    private readonly CombatState _combat;
    private readonly IRuntimeCombatAttackOperations _operations;
    private readonly Func<double> _now;

    private bool _buildInProgress;
    private double _buildStartTime;
    private bool _attackRequestInProgress;
    private bool _attackServerResponsePending;
    private bool _attackWhenResponseReceived;
    private float _attackWhenResponseReceivedPower;
    private bool _currentBuildIsAutomatic;
    private bool _repeatAttacking;
    private float _requestedAttackPower;
    private float _latestPowerBarLevel;
    private bool _disposed;
    private long _completionRevision;

    public RuntimeCombatAttackState(
        CombatState combat,
        Func<bool> canStartAttack,
        Func<AttackHeight, float, bool> sendAttack,
        Action? prepareAttackRequest = null,
        Action? sendCancelAttack = null,
        Func<bool>? isDualWield = null,
        Func<bool>? playerReadyForAttack = null,
        Func<bool>? autoRepeatAttack = null,
        Func<double>? now = null)
        : this(
            combat,
            new DelegateRuntimeCombatAttackOperations(
                _ => canStartAttack(),
                (height, power, _) => sendAttack(height, power),
                prepareAttackRequest,
                sendCancelAttack,
                isDualWield,
                playerReadyForAttack,
                autoRepeatAttack),
            now)
    {
    }

    public RuntimeCombatAttackState(
        CombatState combat,
        IRuntimeCombatAttackOperations operations,
        Func<double>? now = null)
    {
        _combat = combat ?? throw new ArgumentNullException(nameof(combat));
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _now = now ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);

        _combat.CombatModeChanged += OnCombatModeChanged;
        _combat.AttackCommenced += OnAttackCommenced;
        _combat.AttackDone += OnAttackDone;
    }

    public AttackHeight RequestedHeight { get; private set; } = AttackHeight.Medium;
    /// <summary>
    /// True while a plugin drives combat. Such an owner names every target
    /// itself: no automatic repeat, and no closest-hostile fallback when the
    /// selection is gone, or a kill would end with the character locked onto
    /// whatever stands nearest, in range or not.
    /// </summary>
    internal bool AutomationControlled { get; set; }
    private bool AutoRepeatAllowed => !AutomationControlled && _operations.AutoRepeatAttack;
    public float DesiredPower { get; private set; } = InitialDesiredPower;
    public bool AttackRequestInProgress => _attackRequestInProgress;
    public bool AttackServerResponsePending => _attackServerResponsePending;
    public bool RepeatAttackInProgress => _repeatAttacking;
    public float RequestedAttackPower => _requestedAttackPower;
    public bool BuildInProgress => _buildInProgress;
    public bool IsDisposed => _disposed;
    public long CompletionRevision => _completionRevision;
    public uint CompletionSequence { get; private set; }
    public uint CompletionWeenieError { get; private set; }

    public float PowerBarLevel => _buildInProgress
        ? GetPowerBarLevel()
        : _latestPowerBarLevel;

    public event Action? StateChanged;

    public bool HandleCommand(in RuntimeCombatAttackInput command)
    {
        AttackHeight? height = command.Command switch
        {
            RuntimeCombatAttackCommand.LowAttack => AttackHeight.Low,
            RuntimeCombatAttackCommand.MediumAttack => AttackHeight.Medium,
            RuntimeCombatAttackCommand.HighAttack => AttackHeight.High,
            _ => null,
        };

        if (height is not null)
        {
            if (command.Activation == RuntimeInputActivation.Press)
                PressAttack(height.Value);
            else if (command.Activation == RuntimeInputActivation.Release)
                ReleaseAttack();
            return true;
        }

        if (command.Activation != RuntimeInputActivation.Press)
            return false;

        if (command.Command == RuntimeCombatAttackCommand.DecreasePower)
        {
            StepDesiredPower(-1);
            return true;
        }
        if (command.Command == RuntimeCombatAttackCommand.IncreasePower)
        {
            StepDesiredPower(1);
            return true;
        }
        if (command.Command == RuntimeCombatAttackCommand.AbortForMovement)
        {
            AbortAutomaticAttack();
            return true;
        }
        return false;
    }

    public void SetDesiredPower(float power)
    {
        float value = Math.Clamp(power, 0f, 1f);
        if (Math.Abs(DesiredPower - value) < float.Epsilon)
            return;
        DesiredPower = value;
        StateChanged?.Invoke();
    }

    public void PressAttack(AttackHeight height)
    {
        bool heightChanged = RequestedHeight != height;
        RequestedHeight = height;
        if (heightChanged || !_attackRequestInProgress)
            StartAttackRequest();
        StateChanged?.Invoke();
    }

    public void ReleaseAttack() => EndAttackRequest(committedPower: null);

    private void EndAttackRequest(float? committedPower)
    {
        if (!_attackRequestInProgress)
            return;

        _attackRequestInProgress = false;
        float currentPower = GetPowerBarLevel();
        // Key-up commits the larger of the bar setting and the level reached so
        // far: an early release keeps loading to the setting, a late release
        // fires at the held level. A commit deferred over a busy server keeps
        // the level it was queued with.
        _requestedAttackPower = committedPower
            ?? Math.Max(DesiredPower, currentPower);

        if (_attackServerResponsePending)
        {
            _attackWhenResponseReceived = true;
            _attackWhenResponseReceivedPower = _requestedAttackPower;
        }
        else if (DesiredPower <= currentPower || _repeatAttacking)
        {
            ExecuteAttack(RequestedHeight, setServerPending: true);

            // A charged swing is immediately followed by a second request at
            // the bar setting. The server consumes one requested level per
            // swing, so without this the swing after the charged one repeats
            // the charged level instead of returning to the setting.
            if (_requestedAttackPower > DesiredPower)
            {
                _requestedAttackPower = DesiredPower;
                ExecuteAttack(RequestedHeight, setServerPending: true);
            }
        }

        StateChanged?.Invoke();
    }

    public void AbortAutomaticAttack()
    {
        if (!_attackServerResponsePending
            && !_attackRequestInProgress
            && !_repeatAttacking)
            return;

        _operations.SendCancelAttack();
        _repeatAttacking = false;

        if (_buildInProgress)
            ResetPowerBar();

        StateChanged?.Invoke();
    }

    public void Tick()
    {
        if (_attackRequestInProgress
            && !_buildInProgress
            && !_attackServerResponsePending)
            AttemptStartBuildingAttack();

        if (!_buildInProgress)
            return;

        if (!_operations.PlayerReadyForAttack)
        {
            if (_attackRequestInProgress)
            {
                StopBuild();
                _latestPowerBarLevel = 0f;
            }
            else
            {
                _repeatAttacking = false;
                ResetPowerBar();
            }
            StateChanged?.Invoke();
            return;
        }

        float currentPower = GetPowerBarLevel();
        _latestPowerBarLevel = currentPower;
        if (!_attackRequestInProgress
            && currentPower >= _requestedAttackPower)
        {
            _latestPowerBarLevel = Math.Min(_requestedAttackPower, currentPower);
            if (!_currentBuildIsAutomatic)
                ExecuteAttack(RequestedHeight, setServerPending: true);
            else
                StopBuild();
        }
        StateChanged?.Invoke();
    }

    private void StepDesiredPower(int direction)
    {
        int sixth = (int)MathF.Round(DesiredPower / DesiredPowerStep);
        SetDesiredPower((sixth + Math.Sign(direction)) * DesiredPowerStep);
    }

    private void StartAttackRequest()
    {
        if (!CombatInputPlanner.SupportsTargetedAttack(_combat.CurrentMode)
            || !_operations.CanStartAttack(allowAutoTarget: !AutomationControlled))
            return;

        _attackRequestInProgress = true;
        _requestedAttackPower = 1f;
        _operations.PrepareAttackRequest();
        _currentBuildIsAutomatic = false;
        AttemptStartBuildingAttack();
    }

    private void AttemptStartBuildingAttack()
    {
        if (_buildInProgress
            || _attackServerResponsePending
            || !_operations.PlayerReadyForAttack)
            return;
        StartPowerBarBuild();
    }

    private void StartPowerBarBuild()
    {
        _buildInProgress = true;
        _buildStartTime = _now();
        _latestPowerBarLevel = 0f;
    }

    private float GetPowerBarLevel()
    {
        if (!_buildInProgress)
            return 0f;
        double duration = _operations.IsDualWield
            ? DualWieldPowerUpSeconds
            : AttackPowerUpSeconds;
        return (float)Math.Clamp((_now() - _buildStartTime) / duration, 0d, 1d);
    }

    private void ExecuteAttack(AttackHeight height, bool setServerPending)
    {
        StopBuild();
        if (!_operations.SendAttack(
                height,
                Math.Clamp(_requestedAttackPower, 0f, 1f),
                allowAutoTarget: !AutomationControlled))
        {
            ResetPowerBar();
            return;
        }

        if (AutoRepeatAllowed)
            _repeatAttacking = true;
        _attackServerResponsePending = setServerPending;
    }

    private void OnAttackCommenced()
    {
        _attackServerResponsePending = true;
        if (!_attackRequestInProgress)
        {
            _latestPowerBarLevel = _requestedAttackPower;
            StopBuild(preserveLevel: true);
        }
        StateChanged?.Invoke();
    }

    private void OnAttackDone(uint attackSequence, uint weenieError)
    {
        CompletionSequence = attackSequence;
        CompletionWeenieError = weenieError;
        _completionRevision++;
        _attackServerResponsePending = false;
        if (weenieError != 0)
            _repeatAttacking = false;

        // A repeat only needs a new request when the level in flight no longer
        // matches the bar setting; the server keeps swinging at the level it
        // already holds.
        if (!_attackRequestInProgress
            && AutoRepeatAllowed
            && _repeatAttacking
            && Math.Abs(_requestedAttackPower - DesiredPower) > 0.01f)
        {
            _requestedAttackPower = DesiredPower;
            ExecuteAttack(RequestedHeight, setServerPending: false);
        }

        if (!AutoRepeatAllowed || !_repeatAttacking)
        {
            _repeatAttacking = false;
            ResetPowerBar();
        }
        else if (_attackRequestInProgress)
        {
            AttemptStartBuildingAttack();
        }
        else
        {
            StartPowerBarBuild();
            _currentBuildIsAutomatic = true;
        }

        if (_attackWhenResponseReceived)
        {
            StartAttackRequest();
            EndAttackRequest(_attackWhenResponseReceivedPower);
            _attackWhenResponseReceived = false;
            _attackWhenResponseReceivedPower = 0f;
        }

        StateChanged?.Invoke();
    }

    private void OnCombatModeChanged(CombatMode mode)
    {
        if (!CombatInputPlanner.SupportsTargetedAttack(mode))
            Reset();
        StateChanged?.Invoke();
    }

    private void StopBuild(bool preserveLevel = false)
    {
        if (!preserveLevel && _buildInProgress)
            _latestPowerBarLevel = GetPowerBarLevel();
        _buildInProgress = false;
        _buildStartTime = 0d;
    }

    private void ResetPowerBar()
    {
        StopBuild();
        _latestPowerBarLevel = 0f;
        _currentBuildIsAutomatic = false;
    }

    private void Reset()
    {
        _attackRequestInProgress = false;
        _attackServerResponsePending = false;
        _attackWhenResponseReceived = false;
        _attackWhenResponseReceivedPower = 0f;
        _repeatAttacking = false;
        _requestedAttackPower = 0f;
        _completionRevision = 0;
        CompletionSequence = 0u;
        CompletionWeenieError = 0u;
        ResetPowerBar();
    }

    public void ResetSession()
    {
        Reset();
        RequestedHeight = AttackHeight.Medium;
        DesiredPower = InitialDesiredPower;
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _combat.CombatModeChanged -= OnCombatModeChanged;
        _combat.AttackCommenced -= OnAttackCommenced;
        _combat.AttackDone -= OnAttackDone;
    }
}
