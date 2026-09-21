using System.Buffers.Binary;
using System.Net;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Runtime.Session;

namespace AcDream.HostParity.Tests;

/// <summary>One message a host asked the world session to send.</summary>
internal readonly record struct ParityOutbound(uint Opcode, string Body)
{
    /// <summary>The message that carries one of the client's own actions.</summary>
    private const uint GameActionOpcode = 0xF7B1u;

    /// <summary>
    /// Which action this is, when it is one of the client's actions rather
    /// than something else the client sends. A scenario says "the use has
    /// not gone out yet" with this, which it cannot say by counting
    /// messages: a walking character is telling the server where it is the
    /// whole time.
    /// </summary>
    internal uint? GameAction
    {
        get
        {
            if (Opcode != GameActionOpcode || Body.Length < 24)
                return null;
            byte[] body = Convert.FromHexString(Body);
            return System.Buffers.Binary.BinaryPrimitives
                .ReadUInt32LittleEndian(body.AsSpan(8));
        }
    }

    public override string ToString() =>
        $"0x{Opcode:X4} {Body}";
}

/// <summary>
/// The world connection both arms run against: it goes through every step a
/// real one does up to the point where bytes would leave the machine, and
/// records what each host asked to send instead. Both arms share one of
/// these per run, so a difference in the recording is a difference in the
/// hosts and not in the connection under them.
/// </summary>
internal sealed class ParitySessionOperations : ILiveSessionOperations
{
    private readonly uint _characterId;
    private readonly string _characterName;
    private readonly List<ParityOutbound> _outbound = [];

    internal ParitySessionOperations(
        uint characterId = 0x50000001u,
        string characterName = "Parity")
    {
        _characterId = characterId;
        _characterName = characterName;
    }

    /// <summary>Everything the host asked to send, in order.</summary>
    internal IReadOnlyList<ParityOutbound> Outbound => _outbound;

    /// <summary>Takes what has been sent since the last time it was asked.</summary>
    internal IReadOnlyList<ParityOutbound> TakeOutbound()
    {
        ParityOutbound[] taken = [.. _outbound];
        _outbound.Clear();
        return taken;
    }

    public IPEndPoint ResolveEndpoint(string host, int port) =>
        new(IPAddress.Loopback, port);

    public WorldSession CreateSession(IPEndPoint endpoint)
    {
        var session = new WorldSession(endpoint, new SilentTransport());
        session.GameMessageCapture = Record;
        return session;
    }

    public void Connect(WorldSession session, string user, string password)
    {
    }

    public void BeginConnect(WorldSession session, string user, string password)
    {
    }

    public bool PollConnect(WorldSession session) => true;

    public CharacterList.Parsed GetCharacters(WorldSession session) =>
        new(
            0u,
            [new CharacterList.Character(_characterId, _characterName, 0u)],
            [],
            11,
            "Parity",
            true,
            true);

    public void EnterWorld(WorldSession session, int activeCharacterIndex)
    {
    }

    public void Tick(WorldSession session)
    {
    }

    public void DisposeSession(WorldSession session) => session.Dispose();

    private void Record(byte[] body, GameMessageGroup queue)
    {
        uint opcode = body.Length >= 4
            ? BinaryPrimitives.ReadUInt32LittleEndian(body)
            : 0u;
        _outbound.Add(new ParityOutbound(opcode, Convert.ToHexString(body)));
    }

    /// <summary>A wire that accepts nothing and offers nothing.</summary>
    private sealed class SilentTransport : IWorldSessionTransport
    {
        public void Send(ReadOnlySpan<byte> datagram)
        {
        }

        public void Send(IPEndPoint remote, ReadOnlySpan<byte> datagram)
        {
        }

        public int Receive(
            Span<byte> destination, TimeSpan timeout, out IPEndPoint? from)
        {
            from = null;
            return -1;
        }

        public ValueTask<NetReceiveResult> ReceiveAsync(
            Memory<byte> destination, CancellationToken cancellationToken) =>
            throw new OperationCanceledException(cancellationToken);

        public void Dispose()
        {
        }
    }
}
