using AcDream.App.Net;
using AcDream.App.Streaming;
using AcDream.Core.Net;
using Xunit;

namespace AcDream.App.Tests.Streaming;

/// <summary>
/// Telling the server the login is complete is the moment anything that had to
/// wait for a finished login may start asking it for things -- the character
/// options a session document declared, above all. Before this the client sent
/// the message and told nobody, so on this client those options were never
/// seeded and a character started from a document arrived with whatever
/// options it happened to have.
/// </summary>
public sealed class LocalPlayerTeleportSessionTests
{
    [Fact]
    public void SendingLoginCompleteTellsWhoeverWasWaitingForIt()
    {
        int told = 0;
        var session = new LocalPlayerTeleportSession(
            new NoSessionSource(),
            () => told++);

        session.SendLoginComplete();

        Assert.Equal(1, told);
    }

    [Fact]
    public void ASessionWithNobodyWaitingStillSends()
    {
        var session = new LocalPlayerTeleportSession(new NoSessionSource());

        session.SendLoginComplete();
    }

    private sealed class NoSessionSource : ILiveWorldSessionSource
    {
        public WorldSession? CurrentSession => null;
    }
}
