namespace AcDream.Core.Chat;

/// <summary>
/// The sentence a message on one of the numbered chat channels is shown as.
/// These channels have no name on the wire: the wording comes from which
/// channel it is, and differs between hearing a message and sending one.
/// </summary>
public static class LegacyChannelSentence
{
    /// <summary>
    /// The channel's name as the generic sentence uses it, or null for a
    /// number that is no channel. The allegiance channels are named for who
    /// is being spoken TO, which is what the sender's sentence needs.
    /// </summary>
    public static string? ChannelName(uint channelId) => channelId switch
    {
        0x0000_0001u => "Abuse",
        0x0000_0002u => "Admin",
        0x0000_0004u => "Audit",
        0x0000_0008u => "Advocate 1",
        0x0000_0010u => "Advocate 2",
        0x0000_0020u => "Advocate 3",
        0x0000_0200u => "Sentinel",
        0x0000_0400u => "Help",
        0x0000_0800u => "Fellowship",
        0x0000_1000u => "Vassals",
        0x0000_2000u => "Patron",
        0x0000_4000u => "Monarch",
        0x0100_0000u => "Co-vassals",
        0x0400_0000u => "FellowBroadcast",
        0x0800_0000u => "Celestial Hand",
        0x1000_0000u => "Eldrytch Web",
        0x2000_0000u => "Radiant Blood",
        0x4000_0000u => "Olthoi",
        _ => null,
    };

    /// <summary>The line for a message somebody else sent.</summary>
    /// <param name="sender">The speaker's name, already decorated for display.</param>
    public static string Heard(uint channelId, string sender, string text) =>
        channelId switch
        {
            0x0000_0800u => $"[Fellowship] {sender} says, \"{text}\"",
            0x0000_1000u => $"Your patron {sender} says to you, \"{text}\"",
            0x0000_2000u => $"Your vassal {sender} says to you, \"{text}\"",
            0x0000_4000u => $"Your follower {sender} says to you, \"{text}\"",
            0x0100_0000u => $"[Co-Vassals] {sender} says, \"{text}\"",
            0x0200_0000u => $"[Allegiance Broadcast] {sender} says, \"{text}\"",
            _ => $"{sender} says on the {NameOrUnknown(channelId)} channel, \"{text}\"",
        };

    /// <summary>The line for a message the player sent.</summary>
    public static string Sent(uint channelId, string text) =>
        channelId switch
        {
            0x0000_0800u => $"[Fellowship] You say, \"{text}\"",
            0x0000_1000u or 0x0000_2000u or 0x0000_4000u =>
                $"You say to your {NameOrUnknown(channelId)}, \"{text}\"",
            0x0100_0000u => $"[Co-Vassals] You say, \"{text}\"",
            0x0200_0000u => $"[Allegiance Broadcast] You say, \"{text}\"",
            // A broadcast to the fellowship is shown as it was written.
            0x0400_0000u => text,
            _ => $"You say on the {NameOrUnknown(channelId)} channel, \"{text}\"",
        };

    private static string NameOrUnknown(uint channelId) =>
        ChannelName(channelId) ?? "<unknown>";
}
