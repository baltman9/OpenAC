using AcDream.Core.Chat;

namespace AcDream.Core.Tests.Chat;

/// <summary>
/// OpenAC #142: a fellowship message read "[ch 2048] Name says". The numbered
/// channels each have a sentence of their own, for hearing and for sending.
/// </summary>
public sealed class LegacyChannelSentenceTests
{
    [Theory]
    [InlineData(0x0800u, "[Fellowship] Bob says, \"hi\"")]
    [InlineData(0x1000u, "Your patron Bob says to you, \"hi\"")]
    [InlineData(0x2000u, "Your vassal Bob says to you, \"hi\"")]
    [InlineData(0x4000u, "Your follower Bob says to you, \"hi\"")]
    [InlineData(0x0100_0000u, "[Co-Vassals] Bob says, \"hi\"")]
    [InlineData(0x0200_0000u, "[Allegiance Broadcast] Bob says, \"hi\"")]
    [InlineData(0x0400u, "Bob says on the Help channel, \"hi\"")]
    [InlineData(0x0800_0000u, "Bob says on the Celestial Hand channel, \"hi\"")]
    [InlineData(7u, "Bob says on the <unknown> channel, \"hi\"")]
    public void WhatIsHeardIsWordedByTheChannel(uint channel, string expected) =>
        Assert.Equal(expected, LegacyChannelSentence.Heard(channel, "Bob", "hi"));

    [Theory]
    [InlineData(0x0800u, "[Fellowship] You say, \"hi\"")]
    [InlineData(0x1000u, "You say to your Vassals, \"hi\"")]
    [InlineData(0x2000u, "You say to your Patron, \"hi\"")]
    [InlineData(0x4000u, "You say to your Monarch, \"hi\"")]
    [InlineData(0x0100_0000u, "[Co-Vassals] You say, \"hi\"")]
    [InlineData(0x0200_0000u, "[Allegiance Broadcast] You say, \"hi\"")]
    [InlineData(0x0400_0000u, "hi")]
    [InlineData(0x0400u, "You say on the Help channel, \"hi\"")]
    public void WhatIsSentIsWordedByTheChannel(uint channel, string expected) =>
        Assert.Equal(expected, LegacyChannelSentence.Sent(channel, "hi"));
}
