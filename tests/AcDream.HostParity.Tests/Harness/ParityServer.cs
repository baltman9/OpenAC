using System.Buffers.Binary;
using System.Reflection;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;

namespace AcDream.HostParity.Tests;

/// <summary>
/// The server, as far as an arm is concerned. A scenario says what the server
/// said and this hands it to that arm's world connection at the point the
/// connection's own decoder hands a message on, so from there the whole of
/// each client's real inbound path runs: its live-session event router, the
/// binding records that router was built with, the runtime owners underneath
/// and, for a game event, that event's own payload parser.
///
/// Both arms are given the same script at the same step, so anything the two
/// transcripts disagree about afterwards is a difference between the clients.
///
/// What this does NOT reach into, and why:
/// * the reliable transport and the packet codec, which sit above the decoder
///   and are the same code for both clients, so a difference cannot hide there;
/// * the windowed client's drawn-entity sink, which cannot be built without a
///   drawn world -- see <see cref="ParityInboundRoute"/>.
/// </summary>
internal sealed class ParityServer(WorldSession session, Func<uint> playerGuid)
{
    private uint _gameEventSequence;

    /// <summary>An object arrives.</summary>
    internal void CreateObject(WorldSession.EntitySpawn spawn) =>
        Raise(nameof(WorldSession.EntitySpawned), spawn);

    /// <summary>An object is taken out of the world.</summary>
    internal void DeleteObject(uint guid, ushort instanceSequence) =>
        Raise(
            nameof(WorldSession.EntityDeleted),
            new DeleteObject.Parsed(guid, instanceSequence));

    /// <summary>An object's physics state changes -- hidden, frozen, and so on.</summary>
    internal void SetState(
        uint guid,
        PhysicsStateFlags state,
        ushort instanceSequence = 1,
        ushort stateSequence = 2) =>
        Raise(
            nameof(WorldSession.StateUpdated),
            new SetState.Parsed(
                guid, (uint)state, instanceSequence, stateSequence));

    /// <summary>An object is somewhere else now.</summary>
    internal void UpdatePosition(
        uint guid,
        float x,
        float y,
        float z,
        uint cell,
        ushort positionSequence,
        ushort instanceSequence = 1) =>
        Raise(
            nameof(WorldSession.PositionUpdated),
            new WorldSession.EntityPositionUpdate(
                guid,
                new CreateObject.ServerPosition(cell, x, y, z, 1f, 0f, 0f, 0f),
                Velocity: null,
                PlacementId: null,
                IsGrounded: true,
                InstanceSequence: instanceSequence,
                PositionSequence: positionSequence,
                TeleportSequence: 0,
                ForcePositionSequence: 0));

    /// <summary>How much of a creature's health is left, as the server tells it.</summary>
    internal void UpdateHealth(uint targetGuid, float fraction)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, targetGuid);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(4), fraction);
        GameEvent(GameEventType.UpdateHealth, payload);
    }

    /// <summary>A line of text from the server itself.</summary>
    internal void SystemMessage(string text, uint chatType) =>
        Raise(
            nameof(WorldSession.ServerMessageReceived),
            new ServerMessage.Parsed(text, chatType));

    /// <summary>Hands a game event to the connection's own event dispatcher.</summary>
    internal void GameEvent(GameEventType type, byte[] payload) =>
        session.GameEvents.Dispatch(new GameEventEnvelope(
            playerGuid(),
            ++_gameEventSequence,
            type,
            payload));

    /// <summary>
    /// Hands one decoded message to the connection's own subscribers. The
    /// decoder raises these events and nothing else does, so a client's route
    /// cannot tell this apart from a packet off the wire.
    /// </summary>
    private void Raise<T>(string eventName, T message)
    {
        FieldInfo field = typeof(WorldSession).GetField(
            eventName,
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"The world connection has no {eventName} to raise.");
        ((Action<T>?)field.GetValue(session))?.Invoke(message);
    }
}
