using System.Buffers.Binary;
using AcDream.Core.Net.Messages;
using AcDream.Runtime.Tests.Support;

namespace AcDream.Runtime.Tests.Gameplay;

/// <summary>
/// The server says nothing about a character's allegiance until it is asked,
/// so the client asks on arriving in the world. Without it every allegiance
/// question answers "nobody" for the whole session on any client that never
/// opens the allegiance panel -- which is every client with no window, and
/// every windowed one whose player never opened it -- and a plugin breaking
/// a tie is told for ever that the patron is not in the allegiance.
///
/// The ask lives in the runtime, not in either host's composition, so both
/// clients make it by construction rather than by each remembering to.
///
/// Mutation check (2026-09-22): deleting the InWorld arm of the lifecycle
/// gate turned <see cref="TheClientAsksForTheAllegianceOnArrivingInTheWorld"/>
/// and <see cref="TheClientAsksOnceAndNotAgainOnLaterLifecycleEdges"/> red on
/// an empty outbound record; restoring it turned them green. The "once a
/// session" half of the rule is owned by the allegiance state and is proved
/// there, in RuntimeAllegianceStateTests.
/// </summary>
public sealed class RuntimeAllegianceLoginRequestTests
{
    [Fact]
    public void TheClientAsksForTheAllegianceOnArrivingInTheWorld()
    {
        using var host = new NoWindowGameRuntimeHost();
        host.Start();
        for (int tick = 0; tick < 4; tick++)
            host.Session.Tick();
        Assert.True(host.Runtime.Session.IsInWorld);

        byte[] request = Assert.Single(
            UpdateRequests(host.Outbound));
        // The subscribe form, which is what the client sends: it asks for
        // the allegiance now and to be told when it changes.
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(
            request.AsSpan(12)));
    }

    /// <summary>
    /// The lifecycle gate runs on every session tick and on every command
    /// boundary. One arrival is one request; the client does not ask again
    /// each time something else about the session changes.
    /// </summary>
    [Fact]
    public void TheClientAsksOnceAndNotAgainOnLaterLifecycleEdges()
    {
        using var host = new NoWindowGameRuntimeHost();
        host.Start();
        for (int tick = 0; tick < 20; tick++)
            host.Session.Tick();

        Assert.Single(UpdateRequests(host.Outbound));
    }

    /// <summary>
    /// Nothing is asked before the character is in: a request sent into a
    /// session that has not let the character in is one the server has
    /// nothing to answer it with.
    /// </summary>
    [Fact]
    public void NothingIsAskedBeforeTheCharacterIsInTheWorld()
    {
        using var host = new NoWindowGameRuntimeHost(
            deferredConnectTickCount: 4);
        host.Start();
        Assert.False(host.Runtime.Session.IsInWorld);

        Assert.Empty(UpdateRequests(host.Outbound));
    }

    private static IReadOnlyList<byte[]> UpdateRequests(
        IReadOnlyList<byte[]> outbound) =>
        [.. outbound.Where(static body =>
            body.Length >= 16
            && BinaryPrimitives.ReadUInt32LittleEndian(body)
                == AllegianceRequests.GameActionEnvelope
            && BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8))
                == AllegianceRequests.AllegianceUpdateRequestOpcode)];
}
