using System;
using System.Numerics;

namespace AcDream.Runtime.Physics;

/// <summary>
/// What a client remembers about the server's last word on where another
/// creature is: where that word put it, when it arrived, and how fast the
/// creature is therefore travelling.
/// </summary>
/// <remarks>
/// This bookkeeping is what lets the next word about the same creature be
/// treated as a continuation rather than a first sighting: a body whose
/// arrival time was never written down looks brand new every time, so every
/// update places it outright instead of letting it walk there. Both clients
/// run these steps, in the same places in the same order, so a body behaves
/// the same whether or not anything is drawing it.
/// </remarks>
internal static class RuntimeRemoteServerPosition
{
    /// <summary>
    /// The shortest gap, in seconds, between two words from the server that
    /// is long enough to divide a distance by. Anything shorter would turn
    /// rounding noise into an enormous speed.
    /// </summary>
    private const double MinimumDerivationInterval = 0.001;

    /// <summary>
    /// Works out how fast the creature is travelling for this update, before
    /// its body is placed or asked to catch up. The server's own figure is
    /// used when it sent one; otherwise the speed is the distance from the
    /// previous word divided by the time since it. A body being put somewhere
    /// outright carries no travel speed at all, so this leaves the previous
    /// answer alone for those.
    /// </summary>
    internal static void DeriveVelocity(
        RemoteMotion remote,
        Vector3? wireVelocity,
        Vector3 worldPosition,
        double nowSeconds,
        bool isTeleportRoute)
    {
        ArgumentNullException.ThrowIfNull(remote);
        if (isTeleportRoute)
            return;

        Vector3? serverVelocity = wireVelocity;
        if (serverVelocity is null && remote.LastServerPosTime > 0.0)
        {
            double elapsed = nowSeconds - remote.LastServerPosTime;
            if (elapsed > MinimumDerivationInterval)
            {
                serverVelocity =
                    (worldPosition - remote.LastServerPos) / (float)elapsed;
            }
        }

        if (serverVelocity is { } authoritativeVelocity)
        {
            remote.ServerVelocity = authoritativeVelocity;
            remote.HasServerVelocity = true;
        }
        else
        {
            remote.ServerVelocity = Vector3.Zero;
            remote.HasServerVelocity = false;
        }
    }

    /// <summary>
    /// Writes down the server's word and when it arrived, once the body has
    /// been placed or queued for this update.
    /// </summary>
    internal static void Stamp(
        RemoteMotion remote,
        Vector3 worldPosition,
        double nowSeconds)
    {
        ArgumentNullException.ThrowIfNull(remote);
        remote.LastServerPos = worldPosition;
        remote.LastServerPosTime = nowSeconds;
    }

    /// <summary>
    /// Writes down the server's word for an update that does nothing to the
    /// body because the creature is off the ground. The cell comes along
    /// because nothing later in the update will adopt it.
    /// </summary>
    internal static void StampAirborneLeftover(
        RemoteMotion remote,
        uint wireCellId,
        Vector3 worldPosition,
        double nowSeconds)
    {
        ArgumentNullException.ThrowIfNull(remote);
        remote.CellId = wireCellId;
        Stamp(remote, worldPosition, nowSeconds);
    }
}
