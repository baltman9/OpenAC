using System.Net;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Headless.Configuration;
using AcDream.Headless.Credentials;
using AcDream.Headless.Diagnostics;
using AcDream.Headless.Hosting;
using AcDream.Runtime;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Session;

namespace AcDream.Headless.Tests;

/// <summary>
/// A host without presentation still destroys objects 25 seconds after they
/// leave visibility. The server forgets them on the same schedule without
/// a message and re-sends them on return, so a bot that never expired
/// anything would keep every corpse, item and creature it ever saw.
/// </summary>
public sealed class HeadlessEntityLivenessTests
{
    private const uint Player = 0x5000_000Au;

    [Fact]
    public void AnObjectOutsideTheNeighbourhoodIsGoneAfterTwentyFiveSecondsOfTicks()
    {
        var operations = new FixtureSessionOperations();
        using var credential = new HeadlessCredentialSecret("fixture", "password");
        using var host = new HeadlessSessionHost(
            Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(TextWriter.Null),
            operations);
        Assert.Equal(RuntimeSessionStartStatus.Connected, host.Start().Status);

        host.Runtime.PlayerIdentity.ServerGuid = Player;
        RuntimeEntityObjectLifetime entities = host.Runtime.EntityObjects;
        Register(entities, Player, 0x3032_0001u);
        Register(entities, 0x7000_0001u, 0xA9B4_0001u);
        Register(entities, 0x7000_0002u, 0x3133_0020u);
        Assert.Equal(3, entities.Entities.Count);

        for (int i = 0; i < 24; i++)
            host.Tick(1.0d);
        Assert.Equal(3, entities.Entities.Count);

        host.Tick(1.0d);
        host.Tick(1.0d);

        Assert.False(entities.Entities.TryGetActive(0x7000_0001u, out _));
        Assert.True(entities.Entities.TryGetActive(0x7000_0002u, out _));
        Assert.True(entities.Entities.TryGetActive(Player, out _));
        Assert.Equal(0, entities.Entities.PendingTeardownCount);
    }

    private static void Register(RuntimeEntityObjectLifetime entities, uint guid, uint cell)
    {
        RuntimeEntityRegistrationResult result = entities.RegisterEntity(Spawn(guid, cell));
        Assert.NotNull(result.Canonical);
    }

    private static WorldSession.EntitySpawn Spawn(uint guid, uint cell)
    {
        const ushort instance = 3;
        var position = new CreateObject.ServerPosition(cell, 51f, 10f, 5f, 1f, 0f, 0f, 0f);
        var timestamps = new PhysicsTimestamps(1, 1, 1, 1, 0, 1, 0, 1, instance);
        var physics = new PhysicsSpawnData(
            RawState: 0,
            Position: position,
            Movement: null,
            AnimationFrame: null,
            SetupTableId: 0x02000001u,
            MotionTableId: 0x09000001u,
            SoundTableId: null,
            PhysicsScriptTableId: null,
            Parent: null,
            Children: null,
            Scale: null,
            Friction: null,
            Elasticity: null,
            Translucency: null,
            Velocity: null,
            Acceleration: null,
            AngularVelocity: null,
            DefaultScriptType: null,
            DefaultScriptIntensity: null,
            Timestamps: timestamps);
        return new WorldSession.EntitySpawn(
            guid,
            position,
            0x02000001u,
            Array.Empty<CreateObject.AnimPartChange>(),
            Array.Empty<CreateObject.TextureChange>(),
            Array.Empty<CreateObject.SubPaletteSwap>(),
            null,
            null,
            "fixture",
            null,
            null,
            0x09000001u,
            PhysicsState: 0,
            InstanceSequence: instance,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: physics);
    }

    private static HeadlessSessionDescriptor Descriptor() => new()
    {
        Id = "bot",
        Endpoint = new HeadlessEndpointDescriptor
        {
            Host = "127.0.0.1",
            Port = 9000,
        },
        Account = "account",
        Character = new HeadlessCharacterSelector
        {
            Name = "headless",
        },
        Policy = new HeadlessBotPolicyDescriptor
        {
            Id = "idle",
        },
        Credential = new HeadlessCredentialReference
        {
            Provider = HeadlessCredentialProviderKind.StandardInput,
            Reference = "fixture-password",
        },
    };

    private sealed class FixtureSessionOperations : ILiveSessionOperations
    {
        public IPEndPoint ResolveEndpoint(string host, int port) =>
            new(IPAddress.Loopback, port);

        public WorldSession CreateSession(IPEndPoint endpoint) => new(endpoint);

        public void Connect(WorldSession session, string user, string password)
        {
        }

        public CharacterList.Parsed GetCharacters(WorldSession session) =>
            new(
                0u,
                [new CharacterList.Character(0x50000001u, "Headless", 0u)],
                [],
                11,
                "account",
                true,
                true);

        public void EnterWorld(WorldSession session, int activeCharacterIndex)
        {
        }

        public void Tick(WorldSession session)
        {
        }

        public void DisposeSession(WorldSession session) => session.Dispose();
    }
}
