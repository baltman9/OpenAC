namespace AcDream.Plugin.Abstractions;

public enum PluginVendorCommandStatus
{
    Unavailable = 0,
    NotOpen,
    InvalidItem,
    Busy,
    Sent,
    Refused,
}

public readonly record struct PluginVendorCommandResult(
    PluginVendorCommandStatus Status,
    string? Notice = null)
{
    public bool Accepted => Status == PluginVendorCommandStatus.Sent;
}

/// <summary>One item a vendor currently has listed for sale.</summary>
public readonly record struct PluginVendorItem(
    uint TemplateObjectId,
    uint WeenieClassId,
    string Name,
    PluginObjectClass ObjectClass,
    int UnitPrice,
    int StackSize);

public enum PluginVendorTransactionKind
{
    Buy = 0,
    Sell,
}

public readonly record struct PluginVendorTransaction(
    PluginVendorTransactionKind Kind,
    bool Success,
    string? Notice);

public interface IVendorAutomation
{
    bool IsAvailable => false;
    bool IsOpen => false;
    uint VendorObjectId => 0u;
    string VendorName => string.Empty;
    IReadOnlyList<PluginVendorItem> Items => Array.Empty<PluginVendorItem>();
    bool IsBusy => false;

    bool TryCaptureProperties(
        uint templateObjectId,
        out PluginItemProperties properties)
    {
        properties = default;
        return false;
    }

    /// <summary>Every item currently staged to buy, with its requested count.</summary>
    IReadOnlyList<(uint TemplateObjectId, int Count)> BuyList =>
        Array.Empty<(uint, int)>();

    /// <summary>Every owned item currently staged to sell.</summary>
    IReadOnlyList<uint> SellList => Array.Empty<uint>();

    PluginVendorCommandResult AddToBuyList(uint templateObjectId, int count) =>
        new(PluginVendorCommandStatus.Unavailable);

    PluginVendorCommandResult AddToSellList(uint itemObjectId) =>
        new(PluginVendorCommandStatus.Unavailable);

    PluginVendorCommandResult RemoveFromBuyList(uint templateObjectId) =>
        new(PluginVendorCommandStatus.Unavailable);

    PluginVendorCommandResult RemoveFromSellList(uint itemObjectId) =>
        new(PluginVendorCommandStatus.Unavailable);

    PluginVendorCommandResult ClearBuyList() =>
        new(PluginVendorCommandStatus.Unavailable);

    PluginVendorCommandResult ClearSellList() =>
        new(PluginVendorCommandStatus.Unavailable);

    PluginVendorCommandResult BuyAll() =>
        new(PluginVendorCommandStatus.Unavailable);

    PluginVendorCommandResult SellAll() =>
        new(PluginVendorCommandStatus.Unavailable);

    /// <summary>Raised when a vendor's shop pane becomes the open one.</summary>
    event Action<uint> Opened
    {
        add { }
        remove { }
    }

    /// <summary>Raised when the open vendor's shop pane closes.</summary>
    event Action Closed
    {
        add { }
        remove { }
    }

    /// <summary>Raised when a buy-all or sell-all transaction completes.</summary>
    event Action<PluginVendorTransaction> TransactionCompleted
    {
        add { }
        remove { }
    }
}
