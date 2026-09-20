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
    /// <summary>A movement that names the thing to walk to.</summary>
    private const byte MoveToObjectMovementType = 6;


    /// <summary>
    /// The flags a plain walk order carries: it may be run, and the walk is
    /// allowed to charge when the thing is far enough off.
    /// </summary>
    private const uint MoveToFlags = 0x2u | 0x10u;

    /// <summary>The stance a walk order is written in when it names none.</summary>
    private const ushort DefaultStance = 0x003D;

    private uint _gameEventSequence;

    /// <summary>
    /// The last step of letting a character in: the connection is in the
    /// world from here on.
    /// </summary>
    /// <remarks>
    /// Without this the connection stays where a connection with no wire
    /// under it stays, and every client path that asks "am I in the world"
    /// before it sends -- which is every use, every pickup and every
    /// description -- answers no on BOTH clients. Two clients that both
    /// refuse agree line for line, so a scenario over them proves nothing.
    /// This is the same state change the real entry makes as its last act,
    /// so what watches for it is told in the ordinary way.
    /// </remarks>
    internal void LetTheCharacterIn() => Invoke(
        "Transition",
        Enum.Parse(typeof(WorldSession.State), nameof(WorldSession.State.InWorld)));

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

    /// <summary>
    /// An order to walk to a named thing and stop a given distance short of
    /// it. This is the answer the server gives to a use issued from out of
    /// reach, so a client that lets it pass waits out the whole of the
    /// server's patience for a "done" that did nothing.
    /// </summary>
    /// <param name="guid">Whose movement this is.</param>
    /// <param name="targetGuid">The thing to walk to.</param>
    /// <param name="distanceToObject">How close to get, in metres.</param>
    /// <param name="originX">Where the order says the thing was.</param>
    /// <param name="originY">Where the order says the thing was.</param>
    /// <param name="runRate">How fast, as a multiple of the walk rate.</param>
    /// <param name="movementSequence">The order's place in the movement series.</param>
    /// <param name="bitfield">The order's own flags, as they arrive on the wire.</param>
    internal void MoveToObject(
        uint guid,
        uint targetGuid,
        float distanceToObject,
        float originX,
        float originY,
        float runRate = 1f,
        ushort movementSequence = 2,
        uint bitfield = MoveToFlags) =>
        Motion(
            guid,
            MoveToObjectMovementType,
            new CreateObject.MoveToPathData(
                TargetGuid: targetGuid,
                OriginCellId: ParityPlayerBody.Cell,
                OriginX: originX,
                OriginY: originY,
                OriginZ: ParityPlayerBody.GroundHeight,
                DistanceToObject: distanceToObject,
                MinDistance: 0f,
                FailDistance: 50f,
                WalkRunThreshold: 1f,
                DesiredHeading: 0f,
                Bitfield: bitfield),
            runRate,
            movementSequence);


    /// <summary>
    /// The movement the two orders above are written in, for a scenario that
    /// needs to say something the two shorthands do not cover.
    /// </summary>
    internal void Motion(
        uint guid,
        byte movementType,
        CreateObject.MoveToPathData path,
        float runRate,
        ushort movementSequence) =>
        Raise(
            nameof(WorldSession.MotionUpdated),
            new WorldSession.EntityMotionUpdate(
                guid,
                new CreateObject.ServerMotionState(
                    Stance: DefaultStance,
                    ForwardCommand: null,
                    MovementType: movementType,
                    MoveToParameters: path.Bitfield,
                    MoveToSpeed: 1f,
                    MoveToRunRate: runRate,
                    MoveToPath: path),
                InstanceSequence: 1,
                MovementSequence: movementSequence,
                ServerControlSequence: movementSequence,
                IsAutonomous: false));

    /// <summary>The server's answer that a use has finished, and how.</summary>
    /// <param name="weenieError">Zero when it worked.</param>
    internal void UseDone(uint weenieError = 0u)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, weenieError);
        GameEvent(GameEventType.UseDone, payload);
    }

    /// <summary>
    /// What a container holds, as the server lists it once the container has
    /// been opened. The objects themselves arrive separately.
    /// </summary>
    internal void ViewContents(uint containerGuid, params uint[] itemGuids)
    {
        ArgumentNullException.ThrowIfNull(itemGuids);
        var payload = new byte[8 + (itemGuids.Length * 8)];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, containerGuid);
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(4), (uint)itemGuids.Length);
        for (int index = 0; index < itemGuids.Length; index++)
        {
            Span<byte> entry = payload.AsSpan(8 + (index * 8));
            BinaryPrimitives.WriteUInt32LittleEndian(entry, itemGuids[index]);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], 1u);
        }
        GameEvent(GameEventType.ViewContents, payload);
    }

    /// <summary>
    /// The description of an object the character asked to be told about,
    /// carrying whichever whole-number properties the scenario names.
    /// </summary>
    /// <param name="guid">The object described.</param>
    /// <param name="properties">Property id to value.</param>
    /// <param name="success">Whether the server had anything to say.</param>
    internal void AppraisalResponse(
        uint guid,
        IReadOnlyList<(uint Property, int Value)> properties,
        bool success = true)
    {
        ArgumentNullException.ThrowIfNull(properties);
        var payload = new byte[16 + (properties.Count * 8)];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, guid);
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(4),
            (uint)AppraiseInfoParser.IdentifyResponseFlags.IntStatsTable);
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(8), success ? 1u : 0u);
        // The whole-number table: how many, how many buckets it was written
        // into, then the pairs.
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(12), (ushort)properties.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(14), (ushort)properties.Count);
        for (int index = 0; index < properties.Count; index++)
        {
            Span<byte> entry = payload.AsSpan(16 + (index * 8));
            BinaryPrimitives.WriteUInt32LittleEndian(
                entry, properties[index].Property);
            BinaryPrimitives.WriteUInt32LittleEndian(
                entry[4..], unchecked((uint)properties[index].Value));
        }
        GameEvent(GameEventType.IdentifyObjectResponse, payload);
    }

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
    /// <summary>Runs one of the connection's own state changes.</summary>
    private void Invoke(string methodName, params object?[] arguments)
    {
        MethodInfo method = typeof(WorldSession).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"The world connection has no {methodName}.");
        _ = method.Invoke(session, arguments);
    }

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
