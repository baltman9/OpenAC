using System.Globalization;
using System.Text.Json;
using AcDream.Headless.Configuration;
using AcDream.Headless.Credentials;
using AcDream.Headless.Diagnostics;
using AcDream.Headless.Hosting;
using AcDream.Runtime;
using AcDream.Runtime.Chat;
using AcDream.Runtime.Entities;
using Xunit.Abstractions;

namespace AcDream.Headless.Tests;

/// <summary>
/// The narrow live pin for the headless local player's first entry: a session
/// that holds a prepared-content lease must give the local player a physics
/// body and a movement controller, and must then accept an administrative
/// teleport. Without the body the session is a spectator - nothing that walks,
/// faces, measures a distance or arrives anywhere can be judged - and the
/// portal placement that follows a teleport refuses forever.
/// This is the whole of the world-entry contract the bot surface stands on, so
/// it is pinned on its own rather than only inside the long session proof.
/// </summary>
[Trait("Lane", "Live")]
public sealed class HeadlessLocalPlayerFirstEntryLiveTests(ITestOutputHelper output)
{
    /// <summary>The same recorded flat standing point the session proof uses.</summary>
    private const string ArenaTeleport =
        "@teleloc A9B40029 133.603592 17.391838 94.005005 1 0 0 0";

    private const uint ArenaCell = 0xA9B40029u;

    [Fact]
    public void ALeasedHeadlessSessionGivesTheLocalPlayerABodyAndAcceptsATeleport()
    {
        if (Environment.GetEnvironmentVariable("ACDREAM_LIVE") != "1")
        {
            Assert.Fail(
                "Lane=Live requires ACDREAM_LIVE=1 and a reachable configured server.");
        }

        string host = Environment.GetEnvironmentVariable("ACDREAM_TEST_HOST") ?? "127.0.0.1";
        string portText = Environment.GetEnvironmentVariable("ACDREAM_TEST_PORT") ?? "9000";
        string? user = Environment.GetEnvironmentVariable("ACDREAM_TEST_USER");
        string? pass = Environment.GetEnvironmentVariable("ACDREAM_TEST_PASS");
        Assert.False(string.IsNullOrEmpty(user));
        Assert.False(string.IsNullOrEmpty(pass));

        string temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "acdream-first-entry-pin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        string statusPath = Path.Combine(temporaryRoot, "status.jsonl");
        var descriptor = new HeadlessSessionDescriptor
        {
            Id = "first-entry-pin",
            Endpoint = new HeadlessEndpointDescriptor
            {
                Host = host,
                Port = int.Parse(portText, CultureInfo.InvariantCulture),
            },
            Account = user!,
            Character = new HeadlessCharacterSelector { Name = "+Acdream" },
            Policy = new HeadlessBotPolicyDescriptor { Id = "idle" },
            Credential = new HeadlessCredentialReference
            {
                Provider = HeadlessCredentialProviderKind.Environment,
                Reference = "ACDREAM_TEST_PASS",
            },
            StatusFile = statusPath,
        };
        var diagnosticsOutput = new StringWriter();

        using HeadlessProcessContentOwner content = OpenProcessContent(
            message => diagnosticsOutput.WriteLine("content: " + message));
        using HeadlessProcessContentOwner.HeadlessProcessContentLease lease =
            content.AcquireLease(descriptor.Id);

        using var session = new HeadlessSessionHost(
            descriptor,
            new HeadlessCredentialSecret("first-entry-pin", pass!),
            new HeadlessDiagnosticWriter(diagnosticsOutput),
            sessionOperations: null, // real network
            contentLease: lease);

        bool WaitUntil(TimeSpan timeout, Func<bool> condition)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                    return true;
                session.Tick(0.1d);
                Thread.Sleep(100);
            }
            return condition();
        }

        _ = session.Start();

        Assert.True(
            WaitUntil(
                TimeSpan.FromSeconds(45d),
                () => EventNames(statusPath).Contains("enteredWorld")),
            "The session never entered the world. Diagnostics: " + diagnosticsOutput);

        bool bodied = WaitUntil(TimeSpan.FromSeconds(20d), () => HasBody(session));
        output.WriteLine("after entry: " + Describe(session));
        Assert.True(
            bodied,
            "The local player never received a physics body after entering the "
                + "world with a prepared-content lease: " + Describe(session));
        Assert.True(
            session.Runtime.Movement.Snapshot.HasController,
            "The local player has a body but no movement controller: "
                + Describe(session));

        SubmitOutcome outcome = session.SubmitConsoleLine(ArenaTeleport);
        Assert.Equal(SubmitOutcome.Sent, outcome);

        bool arrived = WaitUntil(
            TimeSpan.FromSeconds(30d),
            () => session.Runtime.Movement.Snapshot.Position.ObjCellId == ArenaCell
                && Math.Abs(
                    session.Runtime.Movement.Snapshot.Position.Frame.Origin.X
                    - 133.603592f) < 2f);
        output.WriteLine("after teleport: " + Describe(session));
        Assert.True(
            arrived,
            "The administrative teleport never placed the local player at the "
                + "arena: " + Describe(session));
    }

    private static bool HasBody(HeadlessSessionHost session)
    {
        uint guid = session.Runtime.PlayerIdentity.ServerGuid;
        return guid != 0u
            && session.Runtime.EntityObjects.Entities.TryGetActive(
                guid,
                out RuntimeEntityRecord record)
            && record.PhysicsBody is not null;
    }

    private static string Describe(HeadlessSessionHost session)
    {
        uint guid = session.Runtime.PlayerIdentity.ServerGuid;
        RuntimeMovementSnapshot movement = session.Runtime.Movement.Snapshot;
        bool active = guid != 0u
            && session.Runtime.EntityObjects.Entities.TryGetActive(
                guid,
                out RuntimeEntityRecord record);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"guid=0x{guid:X8} activeRecord={active} body={HasBody(session)} "
                + $"movementController={movement.HasController} "
                + $"cell=0x{movement.Position.ObjCellId:X8} "
                + $"local=({movement.Position.Frame.Origin.X:0.00},"
                + $"{movement.Position.Frame.Origin.Y:0.00},"
                + $"{movement.Position.Frame.Origin.Z:0.00}) "
                + $"entities={session.Runtime.Entities.Count}");
    }

    private static HeadlessProcessContentOwner OpenProcessContent(
        Action<string> diagnostic)
    {
        string datDirectory =
            Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Documents",
                "Asheron's Call");
        string preparedAssetPath =
            Environment.GetEnvironmentVariable("ACDREAM_PAK_PATH")
            ?? Path.Combine(datDirectory, "acdream.pak");
        Assert.True(
            Directory.Exists(datDirectory),
            $"The pin needs the installed data directory: {datDirectory} "
                + "(set ACDREAM_DAT_DIR).");
        Assert.True(
            File.Exists(preparedAssetPath),
            $"The pin needs the prepared package: {preparedAssetPath} "
                + "(set ACDREAM_PAK_PATH).");
        return new HeadlessProcessContentOwner(
            new HeadlessContentDescriptor
            {
                DatDirectory = datDirectory,
                PreparedAssetPath = preparedAssetPath,
            },
            diagnostic);
    }

    private static string[] EventNames(string statusPath)
    {
        if (!File.Exists(statusPath))
            return [];
        var names = new List<string>();
        foreach (string line in LiveStatusFile.ReadAllLines(statusPath))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            using JsonDocument document = JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("e", out JsonElement name)
                && name.GetString() is { } text)
            {
                names.Add(text);
            }
        }
        return [.. names];
    }
}
