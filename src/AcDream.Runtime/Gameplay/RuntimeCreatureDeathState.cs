using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;

namespace AcDream.Runtime.Gameplay;

/// <summary>
/// What the client knows about which creatures have died. The server says so
/// by playing the death animation, and that statement arrives on every host
/// the same way, so it is read here once rather than in each host's own
/// presentation path: a host without a window has to learn of a death exactly
/// as a host with one does.
/// </summary>
public sealed class RuntimeCreatureDeathState
{
    // The incarnation the creature carried when it died, so a later creature
    // handed the same object id is not reported dead on its predecessor's
    // account.
    private readonly Dictionary<uint, ushort> _dead = [];

    /// <summary>
    /// Raised once for each creature the client learns has died, carrying the
    /// object id of the creature.
    /// </summary>
    public event Action<uint>? Died;

    /// <summary>The number of creatures currently known to be dead.</summary>
    public int Count => _dead.Count;

    /// <summary>
    /// Whether the creature holding <paramref name="objectId"/> at
    /// <paramref name="incarnation"/> is known to have died. An object id
    /// whose incarnation has moved on is a different creature and reads as
    /// alive.
    /// </summary>
    public bool IsDead(uint objectId, ushort incarnation) =>
        _dead.TryGetValue(objectId, out ushort died) && died == incarnation;

    /// <summary>
    /// Whether <paramref name="objectId"/> is known to have died, whatever
    /// incarnation it carried. Callers holding no incarnation of their own ask
    /// this.
    /// </summary>
    public bool IsDead(uint objectId) => _dead.ContainsKey(objectId);

    /// <summary>
    /// Reads a motion the server sent for an object and records a death when
    /// that motion is the death animation.
    /// </summary>
    public void ObserveMotion(in WorldSession.EntityMotionUpdate update) =>
        Observe(update.Guid, update.InstanceSequence, update.MotionState);

    /// <summary>
    /// Reads the motion an object enters the world holding. A corpse the
    /// client is only now seeing arrives already playing its death animation;
    /// anything else means this object id now belongs to something alive.
    /// </summary>
    public void ObserveSpawn(in WorldSession.EntitySpawn spawn)
    {
        _dead.Remove(spawn.Guid);
        Observe(spawn.Guid, spawn.InstanceSequence, spawn.MotionState);
    }

    /// <summary>Drops whatever was known about <paramref name="objectId"/>.</summary>
    public void Forget(uint objectId) => _dead.Remove(objectId);

    /// <summary>Drops everything known, for a session that is starting over.</summary>
    public void Clear() => _dead.Clear();

    private void Observe(
        uint objectId,
        ushort incarnation,
        CreateObject.ServerMotionState? motion)
    {
        if (objectId == 0u || motion is not { } state || !IsDeathMotion(state))
            return;
        if (_dead.TryGetValue(objectId, out ushort known)
            && known == incarnation)
        {
            return;
        }
        _dead[objectId] = incarnation;
        Died?.Invoke(objectId);
    }

    private static bool IsDeathMotion(in CreateObject.ServerMotionState state)
    {
        if (state.ForwardCommand is { } forward && IsDeath(forward))
            return true;
        if (state.Commands is not { } commands)
            return false;
        foreach (CreateObject.MotionItem item in commands)
        {
            if (IsDeath(item.Command))
                return true;
        }
        return false;
    }

    private static bool IsDeath(ushort wireCommand) =>
        MotionCommandResolver.ReconstructFullCommand(wireCommand)
            == MotionCommand.Dead;
}
