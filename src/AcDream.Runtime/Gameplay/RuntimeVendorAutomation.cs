using AcDream.Core.Items;
using AcDream.Plugin.Abstractions;

namespace AcDream.Runtime.Gameplay;

// Projects VendorState (RuntimeInventoryState.Vendor) onto the plugin
// vendor contract. Buy/sell staging lives entirely on this adapter; BuyAll
// and SellAll commit the staged lists through the same WorldSession
// builders the retail-look vendor window's own Buy All / Sell All buttons
// use, gated by the same interaction-transaction busy state as every other
// item command. Shared by the graphical and headless hosts: both bind one
// instance over the same GameRuntime.
public sealed class RuntimeVendorAutomation : IVendorAutomation, IDisposable
{
    private readonly GameRuntime _runtime;
    private readonly object _gate = new();
    private readonly List<(uint TemplateObjectId, int Count)> _buyList = [];
    private readonly List<uint> _sellList = [];
    private long _lastCompletionRevision;
    private PluginVendorTransactionKind? _pendingKind;
    private bool _disposed;

    private Action<uint>? _opened;
    private Action? _closed;
    private Action<PluginVendorTransaction>? _transactionCompleted;

    public RuntimeVendorAutomation(GameRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _lastCompletionRevision =
            _runtime.ActionOwner.Transactions.LastItemUseCompletion.Revision;
        _runtime.InventoryOwner.Vendor.Changed += OnVendorChanged;
    }

    public bool IsAvailable =>
        _runtime.Lifecycle.State == RuntimeLifecycleState.InWorld;

    private VendorState Vendor => _runtime.InventoryOwner.Vendor;

    public bool IsOpen => Vendor.VendorId != 0u;
    public uint VendorObjectId => Vendor.VendorId;

    public string VendorName
    {
        get
        {
            uint vendorId = Vendor.VendorId;
            return vendorId == 0u
                ? string.Empty
                : _runtime.InventoryOwner.Objects.Get(vendorId)?.Name
                    ?? string.Empty;
        }
    }

    public bool IsBusy
    {
        get
        {
            InventoryTransactionState transactions =
                _runtime.ActionOwner.Transactions.Inventory;
            return transactions.BusyCount != 0 || transactions.HasPendingRequest;
        }
    }

    public IReadOnlyList<PluginVendorItem> Items
    {
        get
        {
            VendorState vendor = Vendor;
            if (vendor.VendorId == 0u)
                return Array.Empty<PluginVendorItem>();
            IReadOnlyList<VendorShopItem> source = vendor.Items;
            var built = new PluginVendorItem[source.Count];
            for (int index = 0; index < source.Count; index++)
            {
                VendorShopItem item = source[index];
                int perUnit = VendorPricing.PerUnitValue(
                    item.Value ?? 0, item.DescStackSize);
                int unitPrice = VendorPricing.BuyPrice(
                    perUnit, item.ItemType ?? 0u, vendor.Profile.BuyPrice, 1);
                built[index] = new PluginVendorItem(
                    item.ItemGuid,
                    item.WeenieClassId,
                    item.Name ?? string.Empty,
                    ClassifyVendorItem(item),
                    unitPrice,
                    item.StackSize);
            }
            return built;
        }
    }

    public bool TryCaptureProperties(
        uint templateObjectId,
        out PluginItemProperties properties)
    {
        ClientObject? item = _runtime.InventoryOwner.Objects.Get(templateObjectId);
        if (item is null)
        {
            properties = default;
            return false;
        }
        PropertyBundle source = item.Properties;
        properties = new PluginItemProperties(
            new Dictionary<uint, int>(source.Ints),
            new Dictionary<uint, long>(source.Int64s),
            new Dictionary<uint, bool>(source.Bools),
            new Dictionary<uint, double>(source.Floats),
            new Dictionary<uint, string>(source.Strings),
            new Dictionary<uint, uint>(source.DataIds),
            new Dictionary<uint, uint>(source.InstanceIds));
        return true;
    }

    public IReadOnlyList<(uint TemplateObjectId, int Count)> BuyList
    {
        get { lock (_gate) return _buyList.ToArray(); }
    }

    public IReadOnlyList<uint> SellList
    {
        get { lock (_gate) return _sellList.ToArray(); }
    }

    public PluginVendorCommandResult AddToBuyList(uint templateObjectId, int count)
    {
        if (!IsAvailable) return new(PluginVendorCommandStatus.Unavailable);
        if (!IsOpen) return new(PluginVendorCommandStatus.NotOpen);
        if (templateObjectId == 0u || count <= 0)
            return new(PluginVendorCommandStatus.InvalidItem);
        if (!VendorHasTemplate(templateObjectId))
            return new(PluginVendorCommandStatus.InvalidItem);
        lock (_gate)
        {
            for (int index = 0; index < _buyList.Count; index++)
            {
                if (_buyList[index].TemplateObjectId == templateObjectId)
                {
                    _buyList[index] = (templateObjectId, _buyList[index].Count + count);
                    return new(PluginVendorCommandStatus.Sent);
                }
            }
            _buyList.Add((templateObjectId, count));
        }
        return new(PluginVendorCommandStatus.Sent);
    }

    public PluginVendorCommandResult AddToSellList(uint itemObjectId)
    {
        if (!IsAvailable) return new(PluginVendorCommandStatus.Unavailable);
        if (!IsOpen) return new(PluginVendorCommandStatus.NotOpen);
        if (itemObjectId == 0u
            || _runtime.InventoryOwner.Objects.Get(itemObjectId) is null)
        {
            return new(PluginVendorCommandStatus.InvalidItem);
        }
        lock (_gate)
        {
            if (!_sellList.Contains(itemObjectId))
                _sellList.Add(itemObjectId);
        }
        return new(PluginVendorCommandStatus.Sent);
    }

    public PluginVendorCommandResult RemoveFromBuyList(uint templateObjectId)
    {
        lock (_gate)
            _buyList.RemoveAll(entry => entry.TemplateObjectId == templateObjectId);
        return new(PluginVendorCommandStatus.Sent);
    }

    public PluginVendorCommandResult RemoveFromSellList(uint itemObjectId)
    {
        lock (_gate)
            _sellList.Remove(itemObjectId);
        return new(PluginVendorCommandStatus.Sent);
    }

    public PluginVendorCommandResult ClearBuyList()
    {
        lock (_gate)
            _buyList.Clear();
        return new(PluginVendorCommandStatus.Sent);
    }

    public PluginVendorCommandResult ClearSellList()
    {
        lock (_gate)
            _sellList.Clear();
        return new(PluginVendorCommandStatus.Sent);
    }

    public PluginVendorCommandResult BuyAll()
    {
        if (!IsAvailable) return new(PluginVendorCommandStatus.Unavailable);
        if (!IsOpen) return new(PluginVendorCommandStatus.NotOpen);
        (uint TemplateObjectId, int Count)[] items;
        lock (_gate)
        {
            if (_buyList.Count == 0)
                return new(PluginVendorCommandStatus.InvalidItem);
            items = _buyList.ToArray();
        }
        InventoryTransactionState transactions =
            _runtime.ActionOwner.Transactions.Inventory;
        if (transactions.BusyCount != 0 || transactions.HasPendingRequest)
            return new(PluginVendorCommandStatus.Busy);
        if (_runtime.Session.CurrentSession is not { } session)
            return new(PluginVendorCommandStatus.Unavailable);

        var wireItems = new (int Amount, uint ItemGuid)[items.Length];
        for (int index = 0; index < items.Length; index++)
            wireItems[index] = (items[index].Count, items[index].TemplateObjectId);

        ItemUseRequestReservation reservation =
            _runtime.ActionOwner.Transactions.BeginUseRequestReservation();
        try
        {
            session.SendBuy(Vendor.VendorId, wireItems, Vendor.Profile.AlternateCurrencyWcid);
        }
        catch
        {
            reservation.CancelBeforeDispatch();
            throw;
        }
        reservation.MarkDispatched();
        lock (_gate)
        {
            _pendingKind = PluginVendorTransactionKind.Buy;
            _buyList.Clear();
        }
        return new(PluginVendorCommandStatus.Sent);
    }

    public PluginVendorCommandResult SellAll()
    {
        if (!IsAvailable) return new(PluginVendorCommandStatus.Unavailable);
        if (!IsOpen) return new(PluginVendorCommandStatus.NotOpen);
        uint[] items;
        lock (_gate)
        {
            if (_sellList.Count == 0)
                return new(PluginVendorCommandStatus.InvalidItem);
            items = _sellList.ToArray();
        }
        InventoryTransactionState transactions =
            _runtime.ActionOwner.Transactions.Inventory;
        if (transactions.BusyCount != 0 || transactions.HasPendingRequest)
            return new(PluginVendorCommandStatus.Busy);
        if (_runtime.Session.CurrentSession is not { } session)
            return new(PluginVendorCommandStatus.Unavailable);

        ClientObjectTable objects = _runtime.InventoryOwner.Objects;
        var wireItems = new (int Amount, uint ItemGuid)[items.Length];
        for (int index = 0; index < items.Length; index++)
        {
            int amount = objects.Get(items[index])?.StackSize ?? 1;
            wireItems[index] = (Math.Max(1, amount), items[index]);
        }

        ItemUseRequestReservation reservation =
            _runtime.ActionOwner.Transactions.BeginUseRequestReservation();
        try
        {
            session.SendSell(Vendor.VendorId, wireItems);
        }
        catch
        {
            reservation.CancelBeforeDispatch();
            throw;
        }
        reservation.MarkDispatched();
        lock (_gate)
        {
            _pendingKind = PluginVendorTransactionKind.Sell;
            _sellList.Clear();
        }
        return new(PluginVendorCommandStatus.Sent);
    }

    public event Action<uint> Opened
    {
        add { lock (_gate) _opened += value; }
        remove { lock (_gate) _opened -= value; }
    }

    public event Action Closed
    {
        add { lock (_gate) _closed += value; }
        remove { lock (_gate) _closed -= value; }
    }

    public event Action<PluginVendorTransaction> TransactionCompleted
    {
        add { lock (_gate) _transactionCompleted += value; }
        remove { lock (_gate) _transactionCompleted -= value; }
    }

    // Diffs the last-seen item-use completion revision against the current
    // one, and reports the outcome of whichever buy/sell this adapter last
    // dispatched. Call once per host tick.
    public void Poll()
    {
        RuntimeItemUseCompletion completion =
            _runtime.ActionOwner.Transactions.LastItemUseCompletion;
        if (completion.Revision == _lastCompletionRevision)
            return;
        _lastCompletionRevision = completion.Revision;

        PluginVendorTransactionKind? kind;
        lock (_gate)
        {
            kind = _pendingKind;
            _pendingKind = null;
        }
        if (kind is not { } pendingKind)
            return;

        bool success = completion.WeenieError == 0u;
        string? notice = success
            ? null
            : $"Vendor transaction failed (weenie error {completion.WeenieError}).";
        Raise(_transactionCompleted, new PluginVendorTransaction(pendingKind, success, notice));
    }

    private void OnVendorChanged(VendorTransition transition)
    {
        switch (transition.Kind)
        {
            case VendorStateTransitionKind.Opened:
                Raise(_opened, transition.VendorId);
                break;
            case VendorStateTransitionKind.Closed:
            case VendorStateTransitionKind.Reset:
                Raise(_closed);
                lock (_gate)
                {
                    _buyList.Clear();
                    _sellList.Clear();
                    _pendingKind = null;
                }
                break;
        }
    }

    private bool VendorHasTemplate(uint templateObjectId)
    {
        IReadOnlyList<VendorShopItem> items = Vendor.Items;
        for (int index = 0; index < items.Count; index++)
        {
            if (items[index].ItemGuid == templateObjectId)
                return true;
        }
        return false;
    }

    private static PluginObjectClass ClassifyVendorItem(VendorShopItem item) =>
        PluginObjectClassifier.Classify(item.ItemType ?? 0u, item.PublicWeenieBitfield ?? 0u);

    private static void Raise(Action<uint>? handlers, uint value)
    {
        if (handlers is null) return;
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try { ((Action<uint>)handler)(value); }
            catch { }
        }
    }

    private static void Raise(Action? handlers)
    {
        if (handlers is null) return;
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try { ((Action)handler)(); }
            catch { }
        }
    }

    private static void Raise(
        Action<PluginVendorTransaction>? handlers, PluginVendorTransaction value)
    {
        if (handlers is null) return;
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try { ((Action<PluginVendorTransaction>)handler)(value); }
            catch { }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _runtime.InventoryOwner.Vendor.Changed -= OnVendorChanged;
    }
}
