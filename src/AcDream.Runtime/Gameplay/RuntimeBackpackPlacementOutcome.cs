namespace AcDream.Runtime.Gameplay;

/// <summary>
/// How a request to put a world item away ended. Four of the five endings
/// send nothing to the server, and they are not the same thing: the player
/// watching an inventory panel learns which one it was from the notice beside
/// it, but a caller with no one to read a notice has to be told.
/// </summary>
public enum RuntimeBackpackPlacementOutcome
{
    /// <summary>
    /// Not a request this client could take at all: no item was named, or
    /// nothing is wired up to place one.
    /// </summary>
    NotThisClients,

    /// <summary>The request went out to the server.</summary>
    Sent,

    /// <summary>
    /// Neither the pack that was asked for nor any other the player has open
    /// had room for the item. The "completely full" notice was shown.
    /// </summary>
    NoRoom,

    /// <summary>
    /// A placement for this item is already waiting on the server's answer.
    /// The client sends one at a time.
    /// </summary>
    AlreadyPending,

    /// <summary>
    /// A send was planned -- a stack to merge into -- and then did not go
    /// out.
    /// </summary>
    NotDispatched,
}
