using AcDream.Core.Chat;
using AcDream.Core.Net;

namespace AcDream.Runtime.Chat;

/// <summary>
/// What a typed line needs to become speech, a tell, a channel message, a
/// pose or a client command: the runtime's own owners and one world
/// connection.
/// </summary>
/// <remarks>
/// Both clients type into the same chat entry and the same command parser,
/// and both then needed this record filled in. Each had been filling it in
/// separately, which is how one of them came to play a pose in a line of
/// speech and the other to drop it. There is nothing host-specific left in
/// it: the two things a window genuinely lends -- what its client commands
/// may ask of it, and where a chat transcript is written -- are handed
/// straight through, and a client with neither passes nothing and is
/// answered that those commands need a window.
/// </remarks>
public static class RuntimeChatCommandBindings
{
    /// <summary>Which of the two sets of pose text a character is given.</summary>
    private const uint GenderProperty = 0x71u;

    /// <summary>Builds the bindings for one world connection.</summary>
    /// <param name="runtime">The runtime whose owners answer.</param>
    /// <param name="session">The connection everything is sent on.</param>
    /// <param name="host">
    /// What a window can lend the client commands, or null on a client
    /// without one.
    /// </param>
    /// <param name="setChatLogFile">
    /// Where a chat transcript is written, or null where none can be.
    /// </param>
    /// <param name="log">Where the route writes what it did, if anywhere.</param>
    public static LiveChatCommandBindings Create(
        GameRuntime runtime,
        WorldSession session,
        RuntimeClientCommandHostBindings? host = null,
        Func<string, ChatLogResult>? setChatLogFile = null,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(session);
        return new(
            ExecuteClientCommand: new RuntimeClientCommandDispatcher(
                RuntimeClientCommandBindings.Build(
                    runtime,
                    session,
                    host,
                    setChatLogFile)).Execute,
            Communication: runtime.CommunicationOwner,
            Chat: runtime.CommunicationOwner.Chat,
            TurbineChat: runtime.CommunicationOwner.TurbineChat,
            CharacterState: runtime.CharacterOwner,
            PlayerGuid: () => runtime.PlayerIdentity.ServerGuid,
            SendTalk: session.SendTalk,
            SendTell: session.SendTell,
            SendTalkDirect: session.SendTalkDirect,
            SendChannel: session.SendChannel,
            SendTurbineChat: session.SendTurbineChatTo,
            // A pose in a line of speech plays the same motion and prints the
            // same line without a window as with one; the table behind it is
            // read in the shared content pass.
            ResolvePose: command =>
                runtime.CommunicationOwner.ChatPoses.Resolve(
                    command,
                    male: runtime.EntityObjects.Objects
                        .Get(runtime.PlayerIdentity.ServerGuid)?
                        .Properties.GetInt(GenderProperty) == 1),
            ExecuteMotion: motion =>
                _ = runtime.MovementOwner.ExecuteMotion(motion),
            SendSoulEmote: session.SendSoulEmote,
            Log: log);
    }
}
