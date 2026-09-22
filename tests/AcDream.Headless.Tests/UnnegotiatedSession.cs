using AcDream.Core.Net;

namespace AcDream.Headless.Tests;

/// <summary>
/// A world connection a fixture hands the client without ever negotiating
/// it: nothing seeds the cipher a reliable send goes out under, so the send
/// would throw rather than reach the fixture's wire.
///
/// A client that has arrived in the world does send on its own -- it asks
/// the server to state the allegiance, which nothing else will make it say
/// -- so a fixture that claims a character is in the world has to be able to
/// take that. The send is taken and dropped here; a test that wants to read
/// what the client sent takes the game-action capture, which sits above
/// this one.
/// </summary>
internal static class UnnegotiatedSession
{
    /// <summary>Takes this connection's sends instead of letting them throw.</summary>
    internal static WorldSession TakingItsSends(this WorldSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.GameMessageCapture = (_, _) => { };
        return session;
    }
}
