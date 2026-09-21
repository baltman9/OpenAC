using AcDream.Core.Items;
using AcDream.Core.Properties;
using AcDream.Plugin.Abstractions;

namespace AcDream.Runtime.Gameplay;

// Projects VendorState (RuntimeInventoryState.Vendor) onto the plugin
// vendor contract. Buy/sell staging lives entirely on this adapter; BuyAll
// and SellAll commit the staged lists through the same WorldSession
// builders as the vendor window. The shared inventory request owner tracks
// the outstanding shop operation until a matching vendor response, while
// this adapter correlates transaction outcomes with the generic use
// completion signal. Both hosts bind it over the same GameRuntime.
public sealed class RuntimeVendorAutomation : IVendorAutomation, IDisposable
{
    private readonly GameRuntime _runtime;
    private readonly object _gate = new();
    private readonly List<(uint TemplateObjectId, int Count)> _buyList = [];
    private readonly List<uint> _sellList = [];
    private PluginVendorTransactionKind? _pendingKind;
    private bool _hasLatchedFailure;
    private uint _latchedFailureError;
    private PluginVendorTransaction? _completedTransaction;
    private bool _disposed;

    private Action<uint>? _opened;
    private Action? _closed;
    private Action<PluginVendorTransaction>? _transactionCompleted;

    public RuntimeVendorAutomation(GameRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _runtime.InventoryOwner.Vendor.Changed += OnVendorChanged;
        _runtime.ActionOwner.Transactions.UseCompleted += OnUseCompleted;
        _runtime.InventoryOwner.Transactions.RequestFailed += OnShopRequestFailed;
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

    // Transaction outcome reporting is independent of the shared pending
    // shop request; a generic use completion may arrive before vendor refresh.
    public bool IsBusy
    {
        get { lock (_gate) return _pendingKind is not null; }
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
                // The vendor is SELLING this listing to the player, so it
                // prices at its own SellPrice rate -- the same call
                // VendorUiController.ComputeShopItemPrice makes.
                int unitPrice = VendorPricing.SellPrice(
                    perUnit, item.ItemType ?? 0u, vendor.Profile.SellPrice, 1);
                built[index] = new PluginVendorItem(
                    item.ItemGuid,
                    item.WeenieClassId,
                    item.Name ?? string.Empty,
                    ClassifyVendorItem(item),
                    unitPrice,
                    item.StackSize)
                {
                    // A listing that authored no maximum does not stack --
                    // the same reading the vendor window makes of the same
                    // missing field when it decides whether to offer a split.
                    MaxStackSize = item.MaxStackSize ?? 1,
                    ItemType = item.ItemType ?? 0u,
                };
            }
            return built;
        }
    }

    // The shop terms, projected straight off the state the listing carried:
    // what the vendor pays, what it deals in, what it refuses, and what it
    // is paid in. A plugin needs all four to plan a visit before it walks in.
    public PluginVendorProfile Profile
    {
        get
        {
            VendorState vendor = Vendor;
            // Not default: an all-zero profile reads as a vendor that pays
            // nothing and buys nothing worth more than nothing, because zero
            // is a real limit here and "no limit" is the sentinel.
            if (vendor.VendorId == 0u)
                return PluginVendorProfile.Unset;
            VendorShopProfile profile = vendor.Profile;
            // BuyPrice is the rate the vendor pays the player; SellPrice is
            // what it charges, and that already reaches plugins per listing
            // as PluginVendorItem.UnitPrice.
            return new PluginVendorProfile(
                profile.BuyPrice,
                profile.MerchandiseItemTypes,
                profile.MerchandiseMinValue,
                profile.MerchandiseMaxValue,
                profile.DealMagicalItems,
                profile.AlternateCurrencyWcid,
                profile.AlternateCurrencyWcid == 0u
                    ? 0u
                    : profile.AlternateCurrencyAmount,
                profile.AlternateCurrencyWcid == 0u
                    ? null
                    : profile.AlternateCurrencyPluralName);
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
            new Dictionary<uint, uint>(source.InstanceIds))
        {
            WeaponProfile = ClientAppraisalProfileMapper.ToPluginWeaponProfile(
                item.WeaponProfile),
            ArmorProfile = ClientAppraisalProfileMapper.ToPluginArmorProfile(
                item.ArmorProfile,
                source.GetInt((uint)PropertyInt.ArmorLevel)),
        };
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

    // Only a player-owned item that is not itself a vendor listing and not
    // currently equipped is eligible to stage for sale, matching the
    // window's own drag-to-sell gate before it evaluates full retail
    // acceptability (value range, merchandise type mask) at commit time.
    public PluginVendorCommandResult AddToSellList(uint itemObjectId)
    {
        if (!IsAvailable) return new(PluginVendorCommandStatus.Unavailable);
        if (!IsOpen) return new(PluginVendorCommandStatus.NotOpen);
        if (itemObjectId == 0u)
            return new(PluginVendorCommandStatus.InvalidItem);
        if (_runtime.InventoryOwner.Objects.Get(itemObjectId) is not { } item)
            return new(PluginVendorCommandStatus.InvalidItem);
        if (VendorHasTemplate(itemObjectId))
            return new(PluginVendorCommandStatus.InvalidItem);
        if (item.CurrentlyEquippedLocation != EquipMask.None)
            return new(PluginVendorCommandStatus.InvalidItem);
        if (!_runtime.InventoryOwner.Objects.IsOwnedByObject(
                itemObjectId, _runtime.PlayerIdentity.ServerGuid))
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
        if (!IsAvailable) return new(PluginVendorCommandStatus.Unavailable);
        lock (_gate)
            _buyList.RemoveAll(entry => entry.TemplateObjectId == templateObjectId);
        return new(PluginVendorCommandStatus.Sent);
    }

    public PluginVendorCommandResult RemoveFromSellList(uint itemObjectId)
    {
        if (!IsAvailable) return new(PluginVendorCommandStatus.Unavailable);
        lock (_gate)
            _sellList.Remove(itemObjectId);
        return new(PluginVendorCommandStatus.Sent);
    }

    public PluginVendorCommandResult ClearBuyList()
    {
        if (!IsAvailable) return new(PluginVendorCommandStatus.Unavailable);
        lock (_gate)
            _buyList.Clear();
        return new(PluginVendorCommandStatus.Sent);
    }

    public PluginVendorCommandResult ClearSellList()
    {
        if (!IsAvailable) return new(PluginVendorCommandStatus.Unavailable);
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
            if (_pendingKind is not null)
                return new(PluginVendorCommandStatus.Busy);
            if (_buyList.Count == 0)
                return new(PluginVendorCommandStatus.InvalidItem);
            items = _buyList.ToArray();
        }
        if (_runtime.Session.CurrentSession is not { } session)
            return new(PluginVendorCommandStatus.Unavailable);

        var wireItems = new (int Amount, uint ItemGuid)[items.Length];
        for (int index = 0; index < items.Length; index++)
            wireItems[index] = (items[index].Count, items[index].TemplateObjectId);

        uint vendorId = Vendor.VendorId;
        bool sent = _runtime.InventoryOwner.Transactions.TryDispatch(
            InventoryRequestKind.Shop, vendorId, () =>
            {
                lock (_gate)
                {
                    if (_pendingKind is not null)
                        return false;
                    _pendingKind = PluginVendorTransactionKind.Buy;
                }
                try
                {
                    session.SendBuy(vendorId, wireItems, Vendor.Profile.AlternateCurrencyWcid);
                    return true;
                }
                catch
                {
                    lock (_gate)
                        _pendingKind = null;
                    throw;
                }
            });
        if (!sent)
            return new(PluginVendorCommandStatus.Busy);
        lock (_gate)
            _buyList.Clear();
        return new(PluginVendorCommandStatus.Sent);
    }

    public PluginVendorCommandResult SellAll()
    {
        if (!IsAvailable) return new(PluginVendorCommandStatus.Unavailable);
        if (!IsOpen) return new(PluginVendorCommandStatus.NotOpen);
        uint[] items;
        lock (_gate)
        {
            if (_pendingKind is not null)
                return new(PluginVendorCommandStatus.Busy);
            if (_sellList.Count == 0)
                return new(PluginVendorCommandStatus.InvalidItem);
            items = _sellList.ToArray();
        }
        if (_runtime.Session.CurrentSession is not { } session)
            return new(PluginVendorCommandStatus.Unavailable);

        ClientObjectTable objects = _runtime.InventoryOwner.Objects;
        var wireItems = new (int Amount, uint ItemGuid)[items.Length];
        for (int index = 0; index < items.Length; index++)
        {
            int amount = objects.Get(items[index])?.StackSize ?? 1;
            wireItems[index] = (Math.Max(1, amount), items[index]);
        }

        uint vendorId = Vendor.VendorId;
        bool sent = _runtime.InventoryOwner.Transactions.TryDispatch(
            InventoryRequestKind.Shop, vendorId, () =>
            {
                lock (_gate)
                {
                    if (_pendingKind is not null)
                        return false;
                    _pendingKind = PluginVendorTransactionKind.Sell;
                }
                try
                {
                    session.SendSell(vendorId, wireItems);
                    return true;
                }
                catch
                {
                    lock (_gate)
                        _pendingKind = null;
                    throw;
                }
            });
        if (!sent)
            return new(PluginVendorCommandStatus.Busy);
        lock (_gate)
            _sellList.Clear();
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

    // Drains whichever buy/sell outcome the last generic use-completion
    // resolved (see OnUseCompleted) and raises it. Call once per host tick.
    public void Poll()
    {
        PluginVendorTransaction? completed;
        lock (_gate)
        {
            completed = _completedTransaction;
            _completedTransaction = null;
        }
        if (completed is { } transaction)
            Raise(_transactionCompleted, transaction);
    }

    // Every "use" completion arrives here, not just this adapter's own --
    // normal item use, spell casting, and this adapter's buy/sell all
    // funnel through the one generic completion signal. Only react when
    // this adapter itself has a buy/sell outstanding.
    private void OnUseCompleted(uint error)
    {
        lock (_gate)
        {
            if (_pendingKind is not { } pendingKind)
                return;
            _pendingKind = null;
            // A failed buy validation, an empty/invalid sell list, a
            // negative payout, or an over-burden/no-pack-space rejection
            // all come back as InventoryServerSaveFailed (the same signal
            // ClientObjectTable.MoveRequestFailed surfaces) with NO
            // accompanying UseDone error -- error alone is not a reliable
            // success signal for a vendor transaction.
            uint effectiveError = _hasLatchedFailure ? _latchedFailureError : error;
            bool success = effectiveError == 0u && !_hasLatchedFailure;
            _hasLatchedFailure = false;
            _latchedFailureError = 0u;
            string? notice = success
                ? null
                : effectiveError == 0u
                    ? "Vendor transaction failed."
                    : $"Vendor transaction failed (weenie error {effectiveError}).";
            _completedTransaction = new PluginVendorTransaction(pendingKind, success, notice);
        }
    }

    // A failed inventory response belongs to the outstanding request even
    // when the wire carries another object id. Correlate through that
    // request before the generic use completion reports an outcome.
    private void OnShopRequestFailed(PendingInventoryRequest request, uint error)
    {
        lock (_gate)
        {
            if (_pendingKind is null || request.Kind != InventoryRequestKind.Shop)
                return;
            _hasLatchedFailure = true;
            _latchedFailureError = error;
        }
    }

    private void OnVendorChanged(VendorTransition transition)
    {
        switch (transition.Kind)
        {
            case VendorStateTransitionKind.Opened:
                // A vendor switch (a new ApproachVendor without an
                // intervening Close) must not carry over a stale shopping
                // list staged against the previous vendor's stock.
                lock (_gate)
                {
                    _buyList.Clear();
                    _sellList.Clear();
                }
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
                    _hasLatchedFailure = false;
                    _latchedFailureError = 0u;
                    _completedTransaction = null;
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
        _runtime.ActionOwner.Transactions.UseCompleted -= OnUseCompleted;
        _runtime.InventoryOwner.Transactions.RequestFailed -= OnShopRequestFailed;
    }
}
