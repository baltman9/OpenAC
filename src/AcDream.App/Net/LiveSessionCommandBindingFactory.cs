using AcDream.Core.Chat;
using AcDream.Core.Net;
using AcDream.Runtime;
using AcDream.Runtime.Chat;
using AcDream.Runtime.Gameplay;

namespace AcDream.App.Net;

/// <summary>
/// Everything the windowed client's outbound command route is built from,
/// assembled out of the runtime's own owners and the world connection.
/// </summary>
/// <remarks>
/// This used to be a method on the session factory, reachable only with a
/// whole presentation tree beside it, which is why the windowed client's chat
/// and command route had never been built under a test while the windowless
/// one's had. Nothing here needs a window: every line is either a send on the
/// connection or an owner the runtime holds. The two things a window really
/// does supply -- what its client commands may ask of it, and where a chat
/// transcript is written -- are handed in, so the caller with a window passes
/// its own and the caller without one passes what it has.
/// </remarks>
internal static class LiveSessionCommandBindingFactory
{
    /// <summary>
    /// Builds the bindings for one world connection.
    /// </summary>
    /// <param name="runtime">The runtime whose owners answer.</param>
    /// <param name="session">The connection everything is sent on.</param>
    /// <param name="clientCommands">
    /// The client-side slash commands, already built with whatever the caller
    /// can lend them.
    /// </param>
    /// <param name="log">Where the route writes what it did, if anywhere.</param>
    internal static LiveSessionCommandBindings Create(
        GameRuntime runtime,
        WorldSession session,
        RuntimeClientCommandDispatcher.Bindings clientCommands,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(clientCommands);

        RuntimeCharacterState character = runtime.CharacterOwner;
        RuntimeCommunicationState communication = runtime.CommunicationOwner;

        void SendSingleCharacterOption(uint optionId, bool value) =>
            character.Options.TrySetOption(
                optionId,
                value,
                sendAutoSave: session.SendSetSingleCharacterOption);

        void SaveCharacterOptionsIfDirty() =>
            character.Options.TryFlush(() =>
            {
                CharacterOptionsBlobEcho echo = CharacterOptionsBlobSource.Capture(
                    character,
                    runtime.InventoryOwner.Shortcuts);
                session.SendSetCharacterOptions(
                    echo.Options1,
                    echo.Options2,
                    echo.Shortcuts,
                    echo.FavoriteSpells,
                    echo.DesiredComponents,
                    echo.SpellbookFilters);
            });

        return new(
            ClientCommands: clientCommands,
            communication.Chat,
            communication.TurbineChat,
            PlayerGuid: () => runtime.PlayerIdentity.ServerGuid,
            SendTalk: session.SendTalk,
            SendTell: session.SendTell,
            SendTalkDirect: session.SendTalkDirect,
            SendChannel: session.SendChannel,
            SendTurbineChat: (
                roomId,
                chatType,
                dispatchType,
                senderGuid,
                text,
                cookie) => session.SendTurbineChatTo(
                    roomId,
                    chatType,
                    dispatchType,
                    senderGuid,
                    text,
                    cookie),
            AddShortcut: session.SendAddShortcut,
            RemoveShortcut: session.SendRemoveShortcut,
            AddFavorite: session.SendAddSpellFavorite,
            RemoveFavorite: session.SendRemoveSpellFavorite,
            SetSpellbookFilter: session.SendSpellbookFilter,
            ForgetSpell: session.SendRemoveSpell,
            SetDesiredComponent: session.SendSetDesiredComponentLevel,
            ClearDesiredComponents: session.SendClearDesiredComponents,
            RaiseAttribute: session.SendRaiseAttribute,
            RaiseVital: session.SendRaiseVital,
            RaiseSkill: session.SendRaiseSkill,
            TrainSkill: session.SendTrainSkill,
            AddFriend: session.SendAddFriend,
            RemoveFriend: session.SendRemoveFriend,
            ClearFriends: session.SendClearFriends,
            RequestLegacyFriends: session.SendLegacyFriendsListRequest,
            OpenTradeNegotiations: session.SendOpenTradeNegotiations,
            CloseTradeNegotiations: session.SendCloseTradeNegotiations,
            AddToTrade: item => session.SendAddToTrade(item),
            AcceptTrade: (partner, selfAccepted, partnerAccepted) =>
                session.SendAcceptTrade(
                    partner, 0d, 0u, partner, selfAccepted, partnerAccepted),
            DeclineTrade: session.SendDeclineTrade,
            ResetTrade: session.SendResetTrade,
            ModifyCharacterSquelch: session.SendModifyCharacterSquelch,
            ModifyAccountSquelch: session.SendModifyAccountSquelch,
            ModifyGlobalSquelch: session.SendModifyGlobalSquelch,
            Communication: communication,
            CharacterState: character,
            SendSingleCharacterOption: SendSingleCharacterOption,
            SaveCharacterOptions: SaveCharacterOptionsIfDirty,
            SendSetTitle: session.SendSetTitle,
            SendFellowshipCreate: session.SendFellowshipCreate,
            SendFellowshipRecruit: session.SendFellowshipRecruit,
            SendFellowshipDismiss: session.SendFellowshipDismiss,
            SendFellowshipQuit: session.SendFellowshipQuit,
            SendFellowshipAssignNewLeader: session.SendFellowshipAssignNewLeader,
            SendFellowshipChangeOpenness: session.SendFellowshipChangeOpenness,
            SendFellowshipUpdateRequest: session.SendFellowshipUpdateRequest,
            SendAllegianceSwear: session.SendAllegianceSwear,
            SendAllegianceBreak: session.SendAllegianceBreak,
            SendAllegianceKick: session.SendAllegianceKick,
            SendAllegianceInfoRequest: session.SendAllegianceInfoRequest,
            SendAllegianceUpdateRequest: session.SendAllegianceUpdateRequest,
            Log: log,
            ResolvePose: command =>
                communication.ChatPoses.Resolve(
                    command,
                    male: runtime.EntityObjects.Objects
                        .Get(runtime.PlayerIdentity.ServerGuid)?
                        .Properties.GetInt(GenderProperty) == 1),
            ExecuteMotion: motion =>
                _ = runtime.MovementOwner.ExecuteMotion(motion),
            SendSoulEmote: session.SendSoulEmote);
    }

    /// <summary>Which of the two sets of pose text a character is given.</summary>
    private const uint GenderProperty = 0x71u;
}
