using AcDream.Core.Net.Messages;

namespace AcDream.Runtime.Gameplay;

public readonly record struct RuntimeTradeOwnershipSnapshot(
    bool IsDisposed,
    bool IsOpen,
    int StagedItemCount)
{
    public bool IsConverged =>
        IsDisposed
        && !IsOpen
        && StagedItemCount == 0;
}

/// <summary>One player's staged-item side of the trade window.</summary>
public enum RuntimeTradeSide : uint
{
    Self = 1u,
    Partner = 2u,
}

/// <summary>Immutable poll snapshot of the whole trade.</summary>
public readonly record struct RuntimeTradeSnapshot(
    long Revision,
    bool IsOpen,
    uint PartnerGuid,
    bool SelfAccepted,
    bool PartnerAccepted,
    int SelfItemCount,
    int PartnerItemCount,
    uint LastFailureItemGuid,
    uint LastFailureReason);

public interface IRuntimeTradeView
{
    RuntimeTradeSnapshot Snapshot { get; }

    /// <summary>Materialized staged-item guids for one side, in stage order.</summary>
    IReadOnlyList<uint> GetItems(RuntimeTradeSide side);
}

public sealed class RuntimeTradeState : IDisposable
{
    private readonly object _gate = new();
    private readonly AcDream.Core.Items.ClientObjectTable? _objects;
    private readonly List<uint> _selfItems = [];
    private readonly List<uint> _partnerItems = [];
    private bool _isOpen;
    private uint _partnerGuid;
    private bool _selfAccepted;
    private bool _partnerAccepted;
    private uint _lastFailureItemGuid;
    private uint _lastFailureReason;
    private long _revision;
    private bool _disposed;

    public RuntimeTradeState(AcDream.Core.Items.ClientObjectTable? objects = null)
    {
        _objects = objects;
        View = new TradeView(this);
    }

    public IRuntimeTradeView View { get; }

    /// <summary>Raised after the partner puts an item into the trade.</summary>
    public event Action<uint>? PartnerItemAdded;

    /// <summary>
    /// Raised with the partner's items when they leave the trade window: an
    /// item taken back, or the trade reset or closed.
    /// </summary>
    public event Action<IReadOnlyList<uint>>? PartnerItemsReleased;

    public bool IsDisposed
    {
        get { lock (_gate) return _disposed; }
    }

    public void ApplyRegister(GameEvents.RegisterTrade update, uint selfGuid)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _isOpen = true;
            _partnerGuid = update.Initiator != selfGuid && update.Initiator != 0u
                ? update.Initiator
                : update.Partner;
            _selfItems.Clear();
            _partnerItems.Clear();
            _selfAccepted = false;
            _partnerAccepted = false;
            _lastFailureItemGuid = 0u;
            _lastFailureReason = 0u;
            Bump();
        }
    }

    public void ApplyClose()
    {
        uint[] released;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            released = _partnerItems.ToArray();
            ClearLocked();
        }
        Release(released);
    }

    public void ApplyAdd(GameEvents.AddToTrade update)
    {
        bool partnerAdded = false;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_isOpen) return;
            List<uint> items = update.Side == (uint)RuntimeTradeSide.Partner
                ? _partnerItems
                : _selfItems;
            if (!items.Contains(update.ItemGuid))
                items.Add(update.ItemGuid);
            if (update.Side != (uint)RuntimeTradeSide.Partner)
                SetTradeState(update.ItemGuid, 1);
            else
                partnerAdded = true;
            _selfAccepted = false;
            _partnerAccepted = false;
            Bump();
        }
        if (partnerAdded)
            PartnerItemAdded?.Invoke(update.ItemGuid);
    }

    public void ApplyRemove(GameEvents.RemoveFromTrade update)
    {
        bool partnerRemoved;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_isOpen) return;
            bool removed = _selfItems.Remove(update.ItemGuid);
            partnerRemoved = _partnerItems.Remove(update.ItemGuid);
            removed |= partnerRemoved;
            if (removed)
            {
                SetTradeState(update.ItemGuid, 0);
                Bump();
            }
        }
        if (partnerRemoved)
            Release([update.ItemGuid]);
    }

    public void ApplyAccept(uint whoAccepted, uint selfGuid)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_isOpen) return;
            if (whoAccepted == selfGuid) _selfAccepted = true;
            else _partnerAccepted = true;
            Bump();
        }
    }

    /// <summary>0x0203 DeclineTrade — withdraws that side's acceptance.</summary>
    public void ApplyDecline(uint whoDeclined, uint selfGuid)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_isOpen) return;
            if (whoDeclined == selfGuid) _selfAccepted = false;
            else _partnerAccepted = false;
            Bump();
        }
    }

    public void ApplyReset()
    {
        uint[] released;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_isOpen) return;
            released = _partnerItems.ToArray();
            ClearSelfTradeStates();
            _selfItems.Clear();
            _partnerItems.Clear();
            _selfAccepted = false;
            _partnerAccepted = false;
            Bump();
        }
        Release(released);
    }

    public void ApplyFailure(GameEvents.TradeFailure failure)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_isOpen) return;
            _selfItems.Remove(failure.ItemGuid);
            _partnerItems.Remove(failure.ItemGuid);
            SetTradeState(failure.ItemGuid, 0);
            _lastFailureItemGuid = failure.ItemGuid;
            _lastFailureReason = failure.Reason;
            Bump();
        }
    }

    public void ApplyClearAcceptance()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_isOpen) return;
            _selfAccepted = false;
            _partnerAccepted = false;
            Bump();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (_disposed) return;
            ClearLocked();
        }
    }

    public RuntimeTradeOwnershipSnapshot CaptureOwnership()
    {
        lock (_gate)
            return new RuntimeTradeOwnershipSnapshot(
                _disposed,
                _isOpen,
                _selfItems.Count + _partnerItems.Count);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            ClearLocked();
            _disposed = true;
        }
    }

    private void ClearSelfTradeStates()
    {
        foreach (uint guid in _selfItems)
            SetTradeState(guid, 0);
    }

    private void SetTradeState(uint itemGuid, int state)
    {
        if (_objects?.Get(itemGuid) is not { } item || item.TradeState == state)
            return;
        item.TradeState = state;
        _objects.NotifyObjectUpdated(itemGuid);
    }

    private void ClearLocked()
    {
        bool changed = _isOpen
            || _selfItems.Count != 0
            || _partnerItems.Count != 0
            || _selfAccepted
            || _partnerAccepted;
        ClearSelfTradeStates();
        _isOpen = false;
        _partnerGuid = 0u;
        _selfItems.Clear();
        _partnerItems.Clear();
        _selfAccepted = false;
        _partnerAccepted = false;
        _lastFailureItemGuid = 0u;
        _lastFailureReason = 0u;
        if (changed) Bump();
    }

    private void Bump() => _revision++;

    private void Release(uint[] released)
    {
        if (released.Length != 0)
            PartnerItemsReleased?.Invoke(released);
    }

    private sealed class TradeView(RuntimeTradeState owner) : IRuntimeTradeView
    {
        public RuntimeTradeSnapshot Snapshot
        {
            get
            {
                lock (owner._gate)
                    return new RuntimeTradeSnapshot(
                        owner._revision,
                        owner._isOpen,
                        owner._partnerGuid,
                        owner._selfAccepted,
                        owner._partnerAccepted,
                        owner._selfItems.Count,
                        owner._partnerItems.Count,
                        owner._lastFailureItemGuid,
                        owner._lastFailureReason);
            }
        }

        public IReadOnlyList<uint> GetItems(RuntimeTradeSide side)
        {
            lock (owner._gate)
                return side == RuntimeTradeSide.Partner
                    ? [.. owner._partnerItems]
                    : [.. owner._selfItems];
        }
    }
}
