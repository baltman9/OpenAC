using System;
using AcDream.Core.Chat;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;

namespace AcDream.Runtime.Chat;

/// <summary>
/// The handful of client commands that genuinely need something to look at:
/// a frame-rate counter, a lock on the window furniture, saved window
/// layouts, the draw-distance and field-of-view settings, the component
/// buy list a vendor panel fills, and a yes/no dialog. Everything else a
/// slash command does is a message to the server or a change to a runtime
/// owner, and is answered the same way with or without a window.
///
/// A hook left out is not an error: the command answers with a line saying
/// it needs a window, on whichever front end asked. It never throws.
/// </summary>
public sealed record RuntimeClientCommandHostBindings
{
    /// <summary>Shows or hides the frame-rate counter.</summary>
    public Action? ToggleFrameRate { get; init; }

    /// <summary>Locks or unlocks the window furniture.</summary>
    public Action<bool>? SetUiLocked { get; init; }

    /// <summary>
    /// Asks the player a yes/no question and runs the continuation with the
    /// answer. Left out, the question is not asked and the continuation does
    /// not run: a command that needs consent must not act without it.
    /// </summary>
    public Action<string, Action<bool>>? ShowConfirmation { get; init; }

    /// <summary>Saves the window layout under a name.</summary>
    public Action<string>? SaveUi { get; init; }

    /// <summary>Restores the window layout saved under a name.</summary>
    public Action<string>? LoadUi { get; init; }

    /// <summary>Saves the window layout this character comes back to.</summary>
    public Action? SaveAutoUi { get; init; }

    /// <summary>Restores the window layout this character comes back to.</summary>
    public Action? LoadAutoUi { get; init; }

    /// <summary>Fills the open vendor panel's buy list with components.</summary>
    public Action<uint?, uint>? FillComponentBuyList { get; init; }

    /// <summary>How far the landscape is drawn, in landblocks.</summary>
    public Action<int>? SetLandscapeRadius { get; init; }

    /// <summary>How wide the view is, in degrees.</summary>
    public Action<float>? SetFieldOfView { get; init; }
}

/// <summary>
/// Builds the client-command bindings from the things both front ends have:
/// the runtime owners and the world connection. What is left over is the
/// host's, and a host that cannot do one of those says so in a line instead
/// of throwing.
/// </summary>
public static class RuntimeClientCommandBindings
{
    /// <summary>
    /// What a command answers when it needs something to look at and there is
    /// nothing. The same words on either front end, so a script reading the
    /// reply cannot tell which one it is talking to by the failure.
    /// </summary>
    public const string NotAvailableWithoutAWindow =
        "That command needs a window, and this client does not have one.";

    /// <summary>
    /// The version a client reports for itself. Taken from the assembly that
    /// carries this dispatcher, which is the same build on both front ends.
    /// </summary>
    public static string ClientVersion() =>
        typeof(RuntimeClientCommandBindings).Assembly.GetName().Version?
            .ToString(3) ?? "unknown";

    /// <param name="runtime">The owners every client command reads and writes.</param>
    /// <param name="session">The world connection every command sends over.</param>
    /// <param name="host">
    /// What this front end can do beyond that. Null means none of it.
    /// </param>
    /// <param name="setChatLogFile">
    /// Opens or closes the file the transcript is copied to. Null answers
    /// every attempt with a closed log, which is what a client that has no
    /// writable place for one should say.
    /// </param>
    public static RuntimeClientCommandDispatcher.Bindings Build(
        GameRuntime runtime,
        WorldSession session,
        RuntimeClientCommandHostBindings? host = null,
        Func<string, ChatLogResult>? setChatLogFile = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(session);
        RuntimeClientCommandHostBindings hooks = host ?? new();

        void ShowSystemMessage(string text) =>
            runtime.CommunicationOwner.Chat.OnSystemMessage(text, 0x00u);

        void NeedsAWindow() => ShowSystemMessage(NotAvailableWithoutAWindow);

        void SendSingleCharacterOption(uint optionId, bool value) =>
            runtime.CharacterOwner.Options.TrySetOption(
                optionId,
                value,
                sendAutoSave: session.SendSetSingleCharacterOption);

        return new RuntimeClientCommandDispatcher.Bindings(
            TeleportToLifestone: session.SendTeleportToLifestone,
            TeleportToMarketplace: session.SendTeleportToMarketplace,
            TeleportToPkArena: session.SendTeleportToPkArena,
            TeleportToPkLiteArena: session.SendTeleportToPkLiteArena,
            TeleportToHouse: session.SendTeleportToHouse,
            TeleportToMansion: session.SendTeleportToMansion,
            QueryAge: session.SendQueryAge,
            QueryBirth: session.SendQueryBirth,
            ToggleFrameRate: hooks.ToggleFrameRate ?? NeedsAWindow,
            ToggleUiLock: () =>
            {
                // The lock is a character option either way; only showing it
                // needs a window.
                bool locked = !runtime.CharacterOwner.Options.GetOptionBit(
                    CharacterOptionId.LockUI);
                SendSingleCharacterOption((uint)CharacterOptionId.LockUI, locked);
                if (hooks.SetUiLocked is { } setLocked)
                    setLocked(locked);
            },
            ShowSystemMessage: ShowSystemMessage,
            ShowClientLocalMessage: text =>
                runtime.CommunicationOwner.AddText(
                    text, RetailLogTextType.ClientLocal),
            ShowWeenieError: code =>
            {
                (string? text, RetailLogTextType type) =
                    WeenieErrorMessages.Resolve(code, null);
                if (text is not null)
                    runtime.CommunicationOwner.AddText(text, type);
            },
            PlayerPublicWeenieBitfield: () =>
                runtime.EntityObjects.Objects
                    .Get(runtime.PlayerIdentity.ServerGuid)?
                    .PublicWeenieBitfield,
            ClientVersion: ClientVersion,
            CurrentPosition: () =>
                runtime.MovementOwner.Controller is { } controller
                    ? controller.CellPosition
                    : (Position?)null,
            LastOutsideCorpsePosition: () =>
                runtime.CharacterOwner.LocalPlayer.GetPosition(0x0Eu),
            ShowConfirmation: (message, completed) =>
            {
                if (hooks.ShowConfirmation is { } ask)
                    ask(message, completed);
                else
                    NeedsAWindow();
            },
            Suicide: session.SendSuicide,
            ClearChat: _ => runtime.CommunicationOwner.Chat.Clear(),
            SetChatLogFile: setChatLogFile
                ?? (_ => new ChatLogResult(
                    Opened: false,
                    Closed: false,
                    string.Empty,
                    null)),
            SaveUi: name =>
            {
                if (hooks.SaveUi is { } save)
                    save(name);
                else
                    NeedsAWindow();
            },
            LoadUi: name =>
            {
                if (hooks.LoadUi is { } load)
                    load(name);
                else
                    NeedsAWindow();
            },
            SaveAutoUi: hooks.SaveAutoUi ?? NeedsAWindow,
            LoadAutoUi: hooks.LoadAutoUi ?? NeedsAWindow,
            IsAway: () =>
                runtime.EntityObjects.Objects
                    .Get(runtime.PlayerIdentity.ServerGuid)?
                    .Properties.GetBool(0x6Eu) == true,
            SetAway: session.SendSetAfkMode,
            SetAwayMessage: session.SendSetAfkMessage,
            AcceptLootPermits: () =>
                runtime.CharacterOwner.Options.GetOptionBit(
                    CharacterOptionId.AcceptLootPermits),
            SetAcceptLootPermits: value =>
                SendSingleCharacterOption(
                    (uint)CharacterOptionId.AcceptLootPermits, value),
            DisplayConsent: session.SendDisplayConsent,
            ClearConsent: session.SendClearConsent,
            RemoveConsent: session.SendRemoveConsent,
            SendEmote: session.SendEmote,
            runtime.CommunicationOwner.Friends,
            AddFriend: session.SendAddFriend,
            RemoveFriend: session.SendRemoveFriend,
            ClearFriends: session.SendClearFriends,
            RequestLegacyFriends: session.SendLegacyFriendsListRequest,
            runtime.CommunicationOwner.Squelch,
            ModifyCharacterSquelch: session.SendModifyCharacterSquelch,
            ModifyAccountSquelch: session.SendModifyAccountSquelch,
            ModifyGlobalSquelch: session.SendModifyGlobalSquelch,
            LastTeller: () =>
                runtime.CommunicationOwner.CommandTargets.LastIncomingTellSender,
            ClearDesiredComponents: session.SendClearDesiredComponents,
            HasOpenVendor: () => runtime.InventoryOwner.Vendor.VendorId != 0u,
            FillComponentBuyList: (category, maximumPrice) =>
            {
                if (hooks.FillComponentBuyList is { } fill)
                    fill(category, maximumPrice);
                else
                    NeedsAWindow();
            },
            EnterPkLite: session.SendEnterPkLite,
            IsUsingTurbineChat: () =>
                runtime.CommunicationOwner.TurbineChat.Enabled,
            SetChatTitle: _ => { },
            SetSingleCharacterOption: SendSingleCharacterOption,
            AddPlayerPermission: session.SendAddPlayerPermission,
            RemovePlayerPermission: session.SendRemovePlayerPermission,
            RequestAvailableHouses: session.SendListAvailableHouses,
            RequestChannelIndex: session.SendIndexChannels,
            RequestChannelList: session.SendListChannel,
            JoinGmChannel: session.SendOnChannel,
            LeaveGmChannel: session.SendOffChannel,
            RecallAllegianceHometown: session.SendRecallAllegianceHometown,
            RequestAllegianceInfo: session.SendAllegianceInfoRequest,
            AbandonHouse: session.SendAbandonHouse,
            Administration:
                new RuntimeClientCommandDispatcher.AdministrationBindings(
                    BreakAllegianceBoot: session.SendBreakAllegianceBoot,
                    AllegianceChatBoot: session.SendAllegianceChatBoot,
                    AllegianceChatGag: session.SendAllegianceChatGag,
                    AllegianceBroadcast: text =>
                        session.SendChannel(0x02000000u, text),
                    ListAllegianceBans: session.SendListAllegianceBans,
                    AddAllegianceBan: session.SendAddAllegianceBan,
                    RemoveAllegianceBan: session.SendRemoveAllegianceBan,
                    ListAllegianceOfficers: session.SendListAllegianceOfficers,
                    ClearAllegianceOfficers: session.SendClearAllegianceOfficers,
                    SetAllegianceOfficer: session.SendSetAllegianceOfficer,
                    RemoveAllegianceOfficer: session.SendRemoveAllegianceOfficer,
                    ListAllegianceOfficerTitles:
                        session.SendListAllegianceOfficerTitles,
                    ClearAllegianceOfficerTitles:
                        session.SendClearAllegianceOfficerTitles,
                    SetAllegianceOfficerTitle:
                        session.SendSetAllegianceOfficerTitle,
                    QueryAllegianceName: session.SendQueryAllegianceName,
                    SetAllegianceName: session.SendSetAllegianceName,
                    ClearAllegianceName: session.SendClearAllegianceName,
                    AllegianceLockAction: session.SendAllegianceLockAction,
                    SetAllegianceApprovedVassal:
                        session.SendSetAllegianceApprovedVassal,
                    AllegianceHouseAction: session.SendAllegianceHouseAction,
                    QueryMotd: session.SendQueryMotd,
                    SetMotd: session.SendSetMotd,
                    ClearMotd: session.SendClearMotd,
                    SetOpenHouseStatus: session.SendSetOpenHouseStatus,
                    AddPermanentGuest: session.SendAddPermanentGuest,
                    RemovePermanentGuest: session.SendRemovePermanentGuest,
                    RemoveAllPermanentGuests: session.SendRemoveAllPermanentGuests,
                    ChangeStoragePermission: session.SendChangeStoragePermission,
                    AddAllStoragePermission: session.SendAddAllStoragePermission,
                    RemoveAllStoragePermission:
                        session.SendRemoveAllStoragePermission,
                    RequestFullGuestList: session.SendRequestFullGuestList,
                    BootSpecificHouseGuest: session.SendBootSpecificHouseGuest,
                    BootEveryone: session.SendBootEveryone,
                    SetHooksVisibility: session.SendSetHooksVisibility,
                    ModifyAllegianceGuestPermission:
                        session.SendModifyAllegianceGuestPermission,
                    ModifyAllegianceStoragePermission:
                        session.SendModifyAllegianceStoragePermission),
            IsPersistentDaylight: () =>
                runtime.CharacterOwner.Options.GetOptionBit(
                    CharacterOptionId.PersistentAtDay),
            SetPersistentDaylight: enabled =>
                SendSingleCharacterOption(
                    (uint)CharacterOptionId.PersistentAtDay, enabled),
            SetLandscapeRadius: radius =>
            {
                if (hooks.SetLandscapeRadius is { } set)
                    set(radius);
                else
                    NeedsAWindow();
            },
            SetFieldOfView: degrees =>
            {
                if (hooks.SetFieldOfView is { } set)
                    set(degrees);
                else
                    NeedsAWindow();
            });
    }
}
