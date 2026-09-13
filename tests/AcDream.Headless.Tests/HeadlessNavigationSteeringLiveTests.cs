using System.Globalization;
using System.Text.Json;
using AcDream.Headless.Configuration;
using AcDream.Headless.Credentials;
using AcDream.Headless.Diagnostics;
using AcDream.Headless.Hosting;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime;
using AcDream.Runtime.Chat;
using AcDream.Runtime.Entities;
using Xunit.Abstractions;

namespace AcDream.Headless.Tests;

/// <summary>
/// The live pin for route steering in a host that plays no animations: from the
/// arena the bot must turn toward the session proof route's first waypoint and
/// walk to it.
/// </summary>
/// <remarks>
/// Both halves are pinned because the route mover uses both and each failed on
/// its own. Facing is the one that was broken: a dispatched motion was never
/// completed here, which suspends the move-to layer, so a turn was accepted and
/// then never started, the mover never came inside its heading tolerance, and
/// the character stood on the spot for the whole session. Walking is pinned
/// beside it so a regression can be told apart at a glance.
/// </remarks>
[Trait("Lane", "Live")]
public sealed class HeadlessNavigationSteeringLiveTests(ITestOutputHelper output)
{
    /// <summary>The recorded flat standing point the session proof uses.</summary>
    private const string ArenaTeleport =
        "@teleloc A9B40029 133.603592 17.391838 96.330009 1 0 0 0";

    private const uint ArenaCell = 0xA9B40029u;

    /// <summary>
    /// The first waypoint of `Fixtures/vt-proof/navs/vt-proof-route.af`, in the
    /// same game coordinates the route file carries.
    /// </summary>
    private const double FirstWaypointEastWest = 33.7900150d;
    private const double FirstWaypointNorthSouth = 42.1057993d;
    private const double FirstWaypointElevation = 0.4013750d;

    /// <summary>The route's second waypoint: the first leg ends here.</summary>
    private const double SecondWaypointEastWest = 33.8233483d;
    private const double SecondWaypointNorthSouth = 42.1057993d;

    /// <summary>The route mover's arrival radius, `NavigationSettings.MinimumDistanceMeters`.</summary>
    private const double ArrivalMeters = 2d;

    /// <summary>`NavigationController.HeadingToleranceDegrees`.</summary>
    private const float HeadingToleranceDegrees = 4f;

    [Fact]
    public void TheBotTurnsTowardTheRouteAndWalksItsFirstLeg()
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
            "acdream-nav-steering-pin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        string statusPath = Path.Combine(temporaryRoot, "status.jsonl");
        var descriptor = new HeadlessSessionDescriptor
        {
            Id = "nav-steering-pin",
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

        using HeadlessProcessContentOwner content = OpenProcessContent();
        using HeadlessProcessContentOwner.HeadlessProcessContentLease lease =
            content.AcquireLease(descriptor.Id);

        using var session = new HeadlessSessionHost(
            descriptor,
            new HeadlessCredentialSecret("nav-steering-pin", pass!),
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
        Assert.True(
            WaitUntil(TimeSpan.FromSeconds(20d), () => HasBody(session)),
            "The local player never received a physics body.");
        Assert.Equal(SubmitOutcome.Sent, session.SubmitConsoleLine(ArenaTeleport));
        Assert.True(
            WaitUntil(
                TimeSpan.FromSeconds(30d),
                () => session.Runtime.Movement.Snapshot.Position.ObjCellId == ArenaCell),
            "The administrative teleport never placed the bot at the arena.");

        INavigationAutomation navigation =
            session.Plugins.Host.Automation.Navigation;
        var waypoint = new PluginNavigationPosition(
            ArenaCell,
            FirstWaypointEastWest,
            FirstWaypointNorthSouth,
            FirstWaypointElevation,
            HeadingDegrees: 0f,
            IsOutdoor: true);

        Assert.True(
            navigation.Snapshot.IsAvailable,
            "the navigation surface is unavailable.");

        // Arrange a heading the bot has to turn out of. The teleport leaves the
        // heading wherever the previous session left it, and a bot that happens
        // to already face the goal would not exercise the turn at all.
        float away = (DesiredHeading(navigation.Snapshot.Position, waypoint)
            + 180f) % 360f;
        _ = navigation.ClearMovementIntent();
        _ = navigation.FaceHeading(away);
        for (int tick = 0; tick < 40; tick++)
        {
            session.Tick(0.1d);
            Thread.Sleep(100);
            if (Math.Abs(SignedHeadingDelta(
                    navigation.Snapshot.Position.HeadingDegrees,
                    away)) <= 1f)
            {
                break;
            }
        }

        PluginNavigationSnapshot start = navigation.Snapshot;
        double startDistance = start.Position.HorizontalDistanceMeters(waypoint);
        float startDelta = SignedHeadingDelta(
            start.Position.HeadingDegrees,
            DesiredHeading(start.Position, waypoint));
        output.WriteLine(
            $"start: {startDistance:0.00} m away, {startDelta:0.0} deg off");
        Assert.True(
            startDistance > ArrivalMeters,
            "the pin needs the bot to start outside the arrival radius.");
        Assert.True(
            Math.Abs(startDelta) > HeadingToleranceDegrees,
            "the bot could not be turned away from the goal, so the turn it is "
                + "supposed to perform was never set up: it is "
                + $"{startDelta:0.0} deg off after being asked to face "
                + $"{away:0.0} deg.");

        // The route mover's own sequence: face the goal while outside the
        // heading tolerance, then hold forward. One pass per tick.
        double arrivedAt = double.NaN;
        bool everFaced = false;
        double elapsed = 0d;
        double facedAt = double.NegativeInfinity;
        for (int tick = 0; tick < 300; tick++, elapsed += 0.1d)
        {
            PluginNavigationSnapshot now = navigation.Snapshot;
            double distance = now.Position.HorizontalDistanceMeters(waypoint);
            if (distance <= ArrivalMeters)
            {
                arrivedAt = distance;
                break;
            }

            float desired = DesiredHeading(now.Position, waypoint);
            float delta = SignedHeadingDelta(now.Position.HeadingDegrees, desired);
            if (Math.Abs(delta) > HeadingToleranceDegrees)
            {
                Assert.Equal(
                    PluginNavigationCommandStatus.Accepted,
                    navigation.ClearMovementIntent());
                // `NavigationController.FaceHeadingReissueSeconds`: one face per
                // 0.7 s, so a turn in flight is left to finish.
                if (elapsed - facedAt >= 0.7d)
                {
                    facedAt = elapsed;
                    Assert.Equal(
                        PluginNavigationCommandStatus.Accepted,
                        navigation.FaceHeading(desired));
                    everFaced = true;
                }
            }
            else
            {
                facedAt = double.NegativeInfinity;
                Assert.Equal(
                    PluginNavigationCommandStatus.Accepted,
                    navigation.SetMovementIntent(
                        new PluginMovementIntent(Forward: true, Run: true)));
            }

            session.Tick(0.1d);
            Thread.Sleep(100);
            if (tick % 10 == 9)
            {
                output.WriteLine(
                    $"  +{(tick + 1) * 0.1:0.0}s {distance:0.00} m, "
                        + $"{delta:0.0} deg off, "
                        + $"heading {now.Position.HeadingDegrees:0.0}");
            }
        }

        _ = navigation.ClearMovementIntent();

        // The route mover's own open-world loop, driven the same way: hold a
        // turn key while walking, with the far relaxation. It has to close the
        // distance rather than oscillate.
        var far = new PluginNavigationPosition(
            ArenaCell,
            SecondWaypointEastWest,
            SecondWaypointNorthSouth,
            FirstWaypointElevation,
            HeadingDegrees: 0f,
            IsOutdoor: true);
        double farStart = navigation.Snapshot.Position
            .HorizontalDistanceMeters(far);
        double closest = farStart;
        output.WriteLine($"curve start: {farStart:0.00} m away");
        for (int tick = 0; tick < 120; tick++)
        {
            PluginNavigationSnapshot now = navigation.Snapshot;
            double distance = now.Position.HorizontalDistanceMeters(far);
            closest = Math.Min(closest, distance);
            if (distance <= ArrivalMeters)
                break;

            float desired = DesiredHeading(now.Position, far);
            float delta = SignedHeadingDelta(now.Position.HeadingDegrees, desired);
            float offset = Math.Abs(delta);
            bool moves = offset <= (distance > 3d ? 45f : 15f);
            _ = navigation.SetMovementIntent(new PluginMovementIntent(
                Forward: moves,
                TurnLeft: offset > HeadingToleranceDegrees && delta < 0f,
                TurnRight: offset > HeadingToleranceDegrees && delta > 0f,
                Run: moves && distance >= 1.5d));

            session.Tick(0.1d);
            Thread.Sleep(100);
            if (tick % 10 == 9)
            {
                output.WriteLine(
                    $"  curve +{(tick + 1) * 0.1:0.0}s {distance:0.00} m, "
                        + $"{delta:0.0} deg off");
            }
        }
        _ = navigation.ClearMovementIntent();
        output.WriteLine($"curve closest: {closest:0.00} m of {farStart:0.00} m");
        Assert.True(
            closest <= ArrivalMeters,
            "The route mover's own loop did not close the route's first leg: it "
                + $"got no closer than {closest:0.00} m of {farStart:0.00} m. "
                + "Holding a turn key while walking only converges if the turn "
                + "rate is slow enough for the tick rate.");

        Assert.True(everFaced, "the bot never had to face the goal.");
        Assert.False(
            double.IsNaN(arrivedAt),
            "The bot never reached the route's first waypoint. It is "
                + $"{navigation.Snapshot.Position.HorizontalDistanceMeters(waypoint):0.00} m "
                + "away and facing "
                + $"{navigation.Snapshot.Position.HeadingDegrees:0.0} deg. A turn "
                + "that is accepted and never starts leaves the mover outside "
                + "its heading tolerance forever, so it never walks.");
        output.WriteLine($"arrived at {arrivedAt:0.00} m");

        _ = session.Stop("pin complete");
    }

    /// <summary>`NavigationController.DesiredHeading`.</summary>
    private static float DesiredHeading(
        in PluginNavigationPosition from,
        in PluginNavigationPosition to)
    {
        double dx = to.EastWest - from.EastWest;
        double dy = to.NorthSouth - from.NorthSouth;
        double heading = Math.Atan2(dx, dy) * 180d / Math.PI;
        if (heading < 0d)
            heading += 360d;
        return (float)heading;
    }

    /// <summary>`NavigationController.SignedHeadingDelta`.</summary>
    private static float SignedHeadingDelta(float current, float desired)
    {
        float delta = (desired - current) % 360f;
        if (delta > 180f)
            delta -= 360f;
        else if (delta < -180f)
            delta += 360f;
        return delta;
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

    private static HeadlessProcessContentOwner OpenProcessContent()
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
            _ => { });
    }

    private static string[] EventNames(string statusPath)
    {
        if (!File.Exists(statusPath))
            return [];
        var names = new List<string>();
        foreach (string line in File.ReadAllLines(statusPath))
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
