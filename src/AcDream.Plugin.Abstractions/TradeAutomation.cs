namespace AcDream.Plugin.Abstractions;

public enum PluginTradeCommandStatus
{
    Unavailable = 0,
    NotOpen,
    InvalidItem,
    Busy,
    Sent,
    Refused,
}

public readonly record struct PluginTradeCommandResult(
    PluginTradeCommandStatus Status,
    string? Notice = null)
{
    public bool Accepted => Status == PluginTradeCommandStatus.Sent;
}

/// <summary>
/// The pair of participants in a trade that just opened. There is no
/// authoritative "who asked first" bit in the tracked trade state, so
/// <paramref name="InitiatorObjectId"/> is always the local player and
/// <paramref name="PartnerObjectId"/> is always the other side, regardless
/// of who actually sent the open request.
/// </summary>
public readonly record struct PluginTradeOpened(
    uint InitiatorObjectId,
    uint PartnerObjectId);

/// <summary>
/// One item newly staged into the trade. <paramref name="Mine"/>
/// distinguishes which side's grid it landed in, since the underlying
/// state tracks the two sides separately.
/// </summary>
public readonly record struct PluginTradeItemAdded(
    uint ItemObjectId,
    bool Mine);

public interface ITradeAutomation
{
    bool IsAvailable => false;
    bool IsOpen => false;
    uint PartnerObjectId => 0u;
    string PartnerName => string.Empty;
    IReadOnlyList<uint> MyItems => Array.Empty<uint>();
    IReadOnlyList<uint> PartnerItems => Array.Empty<uint>();
    bool MyAccepted => false;
    bool PartnerAccepted => false;

    PluginTradeCommandResult Add(uint itemObjectId) =>
        new(PluginTradeCommandStatus.Unavailable);

    PluginTradeCommandResult Accept() =>
        new(PluginTradeCommandStatus.Unavailable);

    PluginTradeCommandResult Decline() =>
        new(PluginTradeCommandStatus.Unavailable);

    PluginTradeCommandResult Reset() =>
        new(PluginTradeCommandStatus.Unavailable);

    PluginTradeCommandResult End() =>
        new(PluginTradeCommandStatus.Unavailable);

    /// <summary>Raised when a trade window opens with a partner.</summary>
    event Action<PluginTradeOpened> Opened
    {
        add { }
        remove { }
    }

    /// <summary>Raised when the trade window closes, for any reason.</summary>
    event Action Closed
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Raised when the partner accepts the trade; the argument is the
    /// partner's object id. Named <c>PartnerTradeAccepted</c> rather than
    /// <c>PartnerAccepted</c> because C# forbids a property and an event
    /// with the same name on one interface, and <see cref="PartnerAccepted"/>
    /// is already the live acceptance flag.
    /// </summary>
    event Action<uint> PartnerTradeAccepted
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Raised once per item newly staged into either side of the trade.
    /// </summary>
    event Action<PluginTradeItemAdded> ItemAdded
    {
        add { }
        remove { }
    }
}
