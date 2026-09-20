using AcDream.Core.Chat;

namespace AcDream.Runtime.Chat;

/// <summary>
/// A short word for each class of chat text. A front end that can colour its
/// transcript tells the classes apart by colour; one that cannot — a plain
/// console — puts this word in front of the line instead, so the same
/// distinction is still readable.
/// </summary>
public static class RuntimeChatLineTags
{
    /// <summary>
    /// The marker for client-local text: notices the client produced for
    /// itself, which in a window go to the status overlay rather than the
    /// transcript.
    /// </summary>
    public const string ClientLocal = "[client] ";

    private const string Fallback = "[msg] ";

    /// <summary>The tag to put in front of a line of this text class,
    /// brackets and trailing space included.</summary>
    public static string For(uint logTextType) => logTextType switch
    {
        (uint)RetailLogTextType.All => "[all] ",
        (uint)RetailLogTextType.Speech => "[say] ",
        (uint)RetailLogTextType.SpeechDirectSend => "[say] ",
        (uint)RetailLogTextType.Tell => "[tell] ",
        (uint)RetailLogTextType.AdminTell => "[admin] ",
        (uint)RetailLogTextType.System => "[system] ",
        (uint)RetailLogTextType.Combat => "[combat] ",
        (uint)RetailLogTextType.CombatEnemy => "[combat] ",
        (uint)RetailLogTextType.CombatSelf => "[combat] ",
        (uint)RetailLogTextType.Magic => "[magic] ",
        (uint)RetailLogTextType.Spellcasting => "[spell] ",
        (uint)RetailLogTextType.Channel => "[channel] ",
        (uint)RetailLogTextType.ChannelSend => "[channel] ",
        (uint)RetailLogTextType.Social => "[social] ",
        (uint)RetailLogTextType.SocialSend => "[social] ",
        (uint)RetailLogTextType.Emote => "[emote] ",
        (uint)RetailLogTextType.Advancement => "[advance] ",
        (uint)RetailLogTextType.Abuse => "[abuse] ",
        (uint)RetailLogTextType.Help => "[help] ",
        (uint)RetailLogTextType.Appraisal => "[appraise] ",
        (uint)RetailLogTextType.Allegiance => "[allegiance] ",
        (uint)RetailLogTextType.Fellowship => "[fellowship] ",
        (uint)RetailLogTextType.WorldBroadcast => "[broadcast] ",
        (uint)RetailLogTextType.Recall => "[recall] ",
        (uint)RetailLogTextType.Craft => "[craft] ",
        (uint)RetailLogTextType.Salvaging => "[salvage] ",
        (uint)RetailLogTextType.ClientLocal => ClientLocal,
        (uint)RetailLogTextType.TurbineGeneral => "[general] ",
        (uint)RetailLogTextType.TurbineTrade => "[trade] ",
        (uint)RetailLogTextType.TurbineLFG => "[lfg] ",
        (uint)RetailLogTextType.TurbineRoleplay => "[roleplay] ",
        (uint)RetailLogTextType.TurbineSociety => "[society] ",
        _ => Fallback,
    };
}
