using AcDream.Content;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatMotionCommand = DatReaderWriter.Enums.MotionCommand;

namespace AcDream.Runtime.Chat;

/// <summary>
/// The poses a player can put in a line of speech, such as
/// <c>hello *wave*</c>: which animation each word plays and what the client
/// says about it, read from the installed data files. Both front ends read
/// the same table, so the same line does the same thing at a console as in a
/// chat box.
/// </summary>
public sealed class ChatPoseCatalog
{
    private const uint ChatPoseTableId = 0x0E000007u;
    private readonly IReadOnlyDictionary<string, RetailChatPose> _poses;

    /// <summary>A catalog with no poses in it, for a client with no data files.</summary>
    public static ChatPoseCatalog Empty { get; } =
        new(new Dictionary<string, RetailChatPose>(
            StringComparer.OrdinalIgnoreCase));

    private ChatPoseCatalog(
        IReadOnlyDictionary<string, RetailChatPose> poses) =>
        _poses = poses;

    /// <param name="datLock">
    /// Held while the table is read: the data files are not safe to read from
    /// two threads at once.
    /// </param>
    public static ChatPoseCatalog Load(IDatReaderWriter dats, object datLock)
    {
        ArgumentNullException.ThrowIfNull(dats);
        ArgumentNullException.ThrowIfNull(datLock);
        lock (datLock)
        {
            ChatPoseTable? table = dats.Get<ChatPoseTable>(ChatPoseTableId);
            if (table is null)
                return new ChatPoseCatalog(
                    new Dictionary<string, RetailChatPose>(
                        StringComparer.OrdinalIgnoreCase));

            var emotes = new Dictionary<string, (string Self, string Others)>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var pair in table.ChatEmotes)
            {
                emotes[pair.Key.Value] = (
                    pair.Value.MyEmote.Value,
                    pair.Value.OtherEmote.Value);
            }

            var poses = new Dictionary<string, RetailChatPose>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var pair in table.ChatPoses)
            {
                string command = pair.Key.Value;
                string motionName = pair.Value.Value;
                if (string.IsNullOrEmpty(command)
                    || !Enum.TryParse(
                        motionName,
                        ignoreCase: true,
                        out DatMotionCommand motion))
                {
                    continue;
                }
                emotes.TryGetValue(motionName, out var text);
                poses[command] = new RetailChatPose(
                    (uint)motion,
                    text.Self ?? string.Empty,
                    text.Others ?? string.Empty);
            }
            return new ChatPoseCatalog(poses);
        }
    }

    /// <summary>
    /// The pose a word in speech names, or null when it names none.
    /// </summary>
    /// <param name="male">
    /// Which possessive the line about it reads with.
    /// </param>
    public RetailChatPose? Resolve(string command, bool male)
    {
        if (!_poses.TryGetValue(command, out RetailChatPose pose))
            return null;
        string possessive = male ? "his" : "her";
        return pose with
        {
            OthersText = pose.OthersText.Replace(
                "%p",
                possessive,
                StringComparison.Ordinal),
        };
    }
}
