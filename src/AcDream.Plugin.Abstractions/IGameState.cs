// src/AcDream.Plugin.Abstractions/IGameState.cs
namespace AcDream.Plugin.Abstractions;

/// <summary>
/// A read-only view of what the client currently has in the world. Every
/// list is a snapshot the host rebuilds; it is never mutated underneath a
/// reader.
/// </summary>
public interface IGameState
{
    /// <summary>
    /// Everything the client is currently drawing, with its position and
    /// facing. Empty before the local player is in the world.
    /// </summary>
    IReadOnlyList<WorldEntitySnapshot> Entities { get; }

    /// <summary>
    /// The character's tracked quest contracts. Empty on a host that does
    /// not track them and before the server has sent any.
    /// </summary>
    IReadOnlyList<ContractSnapshot> Contracts => [];

    /// <summary>
    /// The fixed decoration of the landscape around the character -- trees,
    /// rocks, buildings and the other pieces that come with the map rather
    /// than from the world server -- with the position and facing the client
    /// placed each one at.
    /// <para>
    /// These are not objects. Nothing here can be selected, used, attacked
    /// or picked up, and none of it has a server identity, so an id in this
    /// list never appears in <see cref="Entities"/> and never belongs in a
    /// command. It is useful for reading the shape of the surroundings and
    /// for nothing else.
    /// </para>
    /// <para>
    /// Empty on a host that draws nothing, because what is placed, and how
    /// far out, is a fact about a drawn world. A plugin that wants the
    /// surroundings has to cope with an empty list.
    /// </para>
    /// </summary>
    IReadOnlyList<WorldEntitySnapshot> SceneryObjects => [];
}
