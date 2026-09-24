using System.Buffers.Binary;
using System.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Net.Packets;

namespace AcDream.Core.Net.Tests.Transport;

/// <summary>
/// A server that goes away says nothing: its datagrams simply stop. The
/// session gives the server up after 140 seconds of silence and not a moment
/// before.
/// </summary>
public sealed class ServerSilenceTests
{
    private static readonly TimeSpan HarnessPatience = TimeSpan.FromSeconds(60);

    [Fact]
    public void SilentServer_IsLostOnlyPast140Seconds()
    {
        var fake = new FakeAceTransport();
        WorldSession session = CreateSession(fake);
        try
        {
            // At character selection nothing reads the socket between turns,
            // so every datagram the model sends from here on is unheard: the
            // server is silent from the client's point of view.
            session.Connect(
                FakeAceTransport.DefaultAccountName,
                "testpassword",
                TimeSpan.FromSeconds(10));
            Assert.Equal(WorldSession.State.InCharacterSelect, session.CurrentState);

            session.Tick();
            for (int second = 1; second <= 140; second++)
            {
                fake.Clock.Advance(TimeSpan.FromSeconds(1));
                session.Tick();
                Assert.False(session.IsConnectionLost, $"lost after {second} s");
            }
            Assert.Equal(WorldSession.State.InCharacterSelect, session.CurrentState);

            fake.Clock.Advance(TimeSpan.FromMilliseconds(1));
            session.Tick();

            Assert.True(session.IsConnectionLost);
            Assert.Equal(WorldSession.State.Failed, session.CurrentState);
        }
        finally
        {
            session.Dispose();
        }
    }

    [Fact]
    public void ClientThatStoodStill_ReadsItsSocketBeforeJudgingTheServer()
    {
        var fake = new FakeAceTransport();
        WorldSession session = CreateSession(fake);
        try
        {
            session.Connect(
                FakeAceTransport.DefaultAccountName,
                "testpassword",
                TimeSpan.FromSeconds(10));
            session.Tick();

            // The client itself was frozen for 200 s: the first turn after it
            // does not blame the server.
            fake.Clock.Advance(TimeSpan.FromSeconds(200));
            session.Tick();
            Assert.False(session.IsConnectionLost);

            // The next turn has read the socket and still heard nothing.
            fake.Clock.Advance(TimeSpan.FromMilliseconds(15));
            session.Tick();
            Assert.True(session.IsConnectionLost);
        }
        finally
        {
            session.Dispose();
        }
    }

    [Fact]
    public void ServerThatGoesSilentInWorld_IsLost_AndTheSessionEndsWithoutAwaitingLogOff()
    {
        var fake = new FakeAceTransport();
        bool serverGone = false;
        fake.Link.Drop(LinkDirection.ServerToClient, (_, _) => serverGone);
        WorldSession session = CreateSession(fake);
        try
        {
            session.Connect(
                FakeAceTransport.DefaultAccountName,
                "testpassword",
                TimeSpan.FromSeconds(10));
            session.EnterWorld(0, TimeSpan.FromSeconds(10));
            Assert.Equal(WorldSession.State.InWorld, session.CurrentState);

            // A server that still talks keeps the session alive.
            var messages = new List<string>();
            session.ServerMessageReceived += message => messages.Add(message.Message);
            fake.Clock.Advance(TimeSpan.FromSeconds(100));
            session.Tick();
            fake.EnqueueServerGameMessage(
                BuildServerMessage("still here"),
                GameMessageGroup.UIQueue);
            Assert.True(SpinWait.SpinUntil(
                () =>
                {
                    session.Tick();
                    return messages.Count == 1;
                },
                HarnessPatience));

            // Then it goes away. (A datagram already on its way when it went
            // can only push the loss later, never earlier; the exact edge is
            // pinned at character selection above.)
            serverGone = true;
            for (int second = 1; second <= 139; second++)
            {
                fake.Clock.Advance(TimeSpan.FromSeconds(1));
                session.Tick();
                Assert.False(session.IsConnectionLost, $"lost after {second} s");
            }
            for (int second = 0; second < 10 && !session.IsConnectionLost; second++)
            {
                fake.Clock.Advance(TimeSpan.FromSeconds(1));
                session.Tick();
            }

            Assert.True(session.IsConnectionLost);
            Assert.Equal(WorldSession.State.Failed, session.CurrentState);
        }
        finally
        {
            session.Dispose();
        }

        // Ending a lost session sends the transport's goodbye but does not
        // ask a server that is gone to log the character off.
        Assert.Equal(WorldSession.State.Disconnected, session.CurrentState);
        Assert.True(fake.Model.IsTerminated);
        Assert.Equal(
            AceTerminationReason.PacketHeaderDisconnect,
            fake.Model.TerminationReason);
        Assert.DoesNotContain(
            fake.Model.DispatchedMessages,
            body => BinaryPrimitives.ReadUInt32LittleEndian(body) == CharacterLogOff.Opcode);
    }

    [Theory]
    [InlineData(140.0, false)]
    [InlineData(140.001, true)]
    public void SilenceThreshold_IsStrictlyPast140Seconds(double silentSeconds, bool expected)
    {
        const long Frequency = TimeSpan.TicksPerSecond;
        long now = 1_000 * Frequency;
        long lastInbound = now - (long)(silentSeconds * Frequency);

        Assert.Equal(
            expected,
            WorldSession.IsServerSilent(now - Frequency / 60, lastInbound, now, Frequency));
    }

    private static WorldSession CreateSession(FakeAceTransport fake) =>
        new(new IPEndPoint(IPAddress.Loopback, 9000), fake)
        {
            TransportClockSource = (fake.Clock.GetTimestamp, fake.Clock.Frequency),
        };

    private static byte[] BuildServerMessage(string text)
    {
        var writer = new PacketWriter(64);
        writer.WriteUInt32(ServerMessage.Opcode);
        writer.WriteString16L(text);
        writer.WriteUInt32(1);
        return writer.ToArray();
    }
}
