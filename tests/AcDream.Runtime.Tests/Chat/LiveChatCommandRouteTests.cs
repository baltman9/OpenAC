using AcDream.Core.Chat;
using AcDream.Runtime.Chat;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Chat;

public sealed class LiveChatCommandRouteTests
{
    [Fact]
    public void FourRegistrationsPreserveOrderedWireRoutesAndCanonicalEcho()
    {
        using var communication = new RuntimeCommunicationState();
        using var character = new RuntimeCharacterState();
        var sent = new List<string>();
        var route = new LiveChatCommandRoute(new LiveChatCommandBindings(
            command => sent.Add($"client:{command.Command}"),
            communication,
            communication.Chat,
            communication.TurbineChat,
            character,
            () => 0x50000001u,
            text => sent.Add($"talk:{text}"),
            (target, text) => sent.Add($"tell:{target}:{text}"),
            (guid, text) => sent.Add($"talkdirect:{guid:X8}:{text}"),
            (channel, text) => sent.Add($"channel:{channel:X8}:{text}"),
            (_, _, _, _, text, _) => sent.Add($"turbine:{text}"),
            _ => null,
            motion => sent.Add($"motion:{motion}"),
            text => sent.Add($"soul:{text}")));

        route.Activate();
        route.Publish(new SendServerCommandCmd("@server"));
        route.Publish(new SendChatCmd(ChatChannelKind.Say, null, "say"));
        route.Publish(new SendChatCmd(ChatChannelKind.Tell, "Bob", "secret"));
        route.Publish(new SendChatCmd(
            ChatChannelKind.Fellowship,
            null,
            "group"));
        route.Publish(new SendRawChannelCmd(0x00000002u, "admin"));
        route.Publish(new ExecuteClientCommandCmd(
            ClientCommandId.QueryAge,
            string.Empty));

        Assert.Equal(
            [
                "talk:@server",
                "talk:say",
                "tell:Bob:secret",
                "channel:00000800:group",
                "channel:00000002:admin",
                "client:QueryAge",
            ],
            sent);
        Assert.Empty(communication.Chat.Snapshot());

        route.Dispose();
        route.Publish(new SendServerCommandCmd("@stale"));
        Assert.Equal(6, sent.Count);
    }

    [Fact]
    public void Say_ConsumesValidDatPoseAndLeavesUnknownTokenAsSpeech()
    {
        using var communication = new RuntimeCommunicationState();
        using var character = new RuntimeCharacterState();
        var sent = new List<string>();
        var route = new LiveChatCommandRoute(new LiveChatCommandBindings(
            _ => { },
            communication,
            communication.Chat,
            communication.TurbineChat,
            character,
            () => 0x50000001u,
            text => sent.Add($"talk:{text}"),
            (_, _) => { },
            (_, _) => { },
            (_, _) => { },
            (_, _, _, _, _, _) => { },
            ResolvePose: command => string.Equals(
                    command,
                    "wave",
                    StringComparison.OrdinalIgnoreCase)
                ? new RetailChatPose(0x13000087u, "wave.", "waves.")
                : null,
            ExecuteMotion: motion => sent.Add($"motion:{motion:X8}"),
            SendSoulEmote: text => sent.Add($"soul:{text}")));
        route.Activate();

        route.Publish(new SendChatCmd(
            ChatChannelKind.Say,
            null,
            "hello *WAVE* there *not-a-pose*"));

        Assert.Equal(
            [
                "motion:13000087",
                "soul:waves.",
                "talk:hello  there *not-a-pose*",
            ],
            sent);
        ChatEntry local = Assert.Single(communication.Chat.Snapshot());
        Assert.Equal(ChatKind.SoulEmote, local.Kind);
        Assert.Equal("You", local.Sender);
        Assert.Equal("wave.", local.Text);
    }

    /// <summary>
    /// A pose in speech is not a window feature. It used to be optional here,
    /// and the windowless host passed none of the three, so "hello *wave*"
    /// sent plain talk and played nothing while the same line in a chat box
    /// waved. A route built without them now refuses to be built at all.
    /// </summary>
    [Fact]
    public void AChatRouteCannotBeBuiltWithoutThePosesSpeechCanCarry()
    {
        using var communication = new RuntimeCommunicationState();
        using var character = new RuntimeCharacterState();

        Assert.Throws<ArgumentNullException>(() =>
            new LiveChatCommandRoute(new LiveChatCommandBindings(
                _ => { }, communication, communication.Chat,
                communication.TurbineChat, character, () => 1u,
                _ => { }, (_, _) => { }, (_, _) => { }, (_, _) => { },
                (_, _, _, _, _, _) => { },
                ResolvePose: null!,
                ExecuteMotion: _ => { },
                SendSoulEmote: _ => { })));
    }

    /// <summary>
    /// The table of poses belongs to the session, not to a window, so a front
    /// end reads it from the communication owner the shared content pass
    /// fills in.
    /// </summary>
    [Fact]
    public void TheSessionCarriesThePoseTableAndIsEmptyUntilItIsRead()
    {
        using var communication = new RuntimeCommunicationState();

        Assert.Same(ChatPoseCatalog.Empty, communication.ChatPoses);
        Assert.Null(communication.ChatPoses.Resolve("wave", male: true));
    }

    [Fact]
    public void Say_ContainingOnlyValidPoseDoesNotSendEmptyTalk()
    {
        using var communication = new RuntimeCommunicationState();
        using var character = new RuntimeCharacterState();
        var sent = new List<string>();
        var route = new LiveChatCommandRoute(new LiveChatCommandBindings(
            _ => { }, communication, communication.Chat,
            communication.TurbineChat, character, () => 1u,
            text => sent.Add($"talk:{text}"), (_, _) => { }, (_, _) => { },
            (_, _) => { },
            (_, _, _, _, _, _) => { },
            ResolvePose: _ => new RetailChatPose(7u, string.Empty, string.Empty),
            ExecuteMotion: motion => sent.Add($"motion:{motion}"),
            SendSoulEmote: text => sent.Add($"soul:{text}")));
        route.Activate();

        route.Publish(new SendChatCmd(ChatChannelKind.Say, null, " *wave* "));

        Assert.Equal(["motion:7"], sent);
    }
}
