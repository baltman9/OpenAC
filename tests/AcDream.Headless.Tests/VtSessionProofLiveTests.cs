using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using AcDream.Core.Physics;
using AcDream.Core.Player;
using AcDream.Headless.Configuration;
using AcDream.Headless.Credentials;
using AcDream.Headless.Diagnostics;
using AcDream.Headless.Hosting;
using AcDream.Headless.Plugins;
using AcDream.Runtime;
using AcDream.Runtime.Chat;
using Xunit.Abstractions;

namespace AcDream.Headless.Tests;

/// <summary>
/// The rerunnable proof that the MossTank plugin plays a full automation
/// session against a live server: it stages its own scenario through the
/// server's own administrative chat commands (the proof character is
/// privileged), then checks nine ordered milestones and reports every one of
/// them as a single PASS/FAIL line so a script can table the run.
/// Milestones the plugin cannot meet yet fail with their evidence attached;
/// they are the work list, not something to soften.
/// </summary>
[Trait("Lane", "Live")]
public sealed class VtSessionProofLiveTests(ITestOutputHelper output)
{
    /// <summary>
    /// The outdoor arena the run stages itself into: a recorded flat standing
    /// point near the starting town. The route fixture's waypoints are the
    /// same point expressed in game coordinates, so the two always agree.
    /// </summary>
    private const string ArenaTeleport =
        "@teleloc A9B40029 133.603592 17.391838 96.330009 1 0 0 0";

    private const double ArenaEastWest = 33.8066816d;
    private const double ArenaNorthSouth = 42.1224660d;

    /// <summary>How close the character must come to count as having reached a waypoint.</summary>
    private const double WaypointArrivalMeters = 3.0d;

    /// <summary>The weenie the run spawns to make a fight happen.</summary>
    private const string MonsterWeenie = "7";

    private const string SettingsProfileName = "vt-proof-settings";
    private const string LootProfileName = "vt-proof-loot";
    private const string RouteProfileName = "vt-proof-route";

    /// <summary>
    /// The plugin's own log channels. The run turns every one of them on so
    /// that whatever the plugin is willing to say about its work reaches the
    /// chat log and the session diagnostics.
    /// </summary>
    private static readonly string[] LogChannels =
    [
        "ActiveRule", "RuleInfo", "BusyState", "SpellCast",
        "DebuffChoice", "CastInfo", "Misc", "Timers", "Loot",
    ];

    [Fact]
    public void MossTankPlaysAnAutomationSessionAgainstTheLiveServer()
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
        Assert.NotNull(user);
        Assert.NotNull(pass);
        Assert.NotEmpty(user!);
        Assert.NotEmpty(pass!);

        const string character = "+Acdream";
        var ledger = new VtProofLedger();

        using var temporary = new TemporaryDirectory();
        string pluginRoot = InstallRealMossTankPlugin(temporary.Path);
        Assert.True(Directory.Exists(pluginRoot));
        string vtankRoot = StageProfileFixtures(temporary.Path);
        IReadOnlyList<VtProofRoutePoint> route = VtProofRouteFixture.ReadPoints(
            File.ReadAllText(Path.Combine(
                vtankRoot, "navs", RouteProfileName + ".af")));
        Assert.True(route.Count >= 3, "The proof route fixture must hold at least three points.");
        string statusPath = Path.Combine(temporary.Path, "status.jsonl");

        var descriptor = new HeadlessSessionDescriptor
        {
            Id = "vt-proof",
            Endpoint = new HeadlessEndpointDescriptor
            {
                Host = host,
                Port = int.Parse(portText, CultureInfo.InvariantCulture),
            },
            Account = user!,
            Character = new HeadlessCharacterSelector { Name = character },
            Policy = new HeadlessBotPolicyDescriptor { Id = "idle" },
            Credential = new HeadlessCredentialReference
            {
                Provider = HeadlessCredentialProviderKind.Environment,
                Reference = "ACDREAM_TEST_PASS",
            },
            Plugins = ["acdream.mosstank"],
            PluginSettings = new Dictionary<string, Dictionary<string, string>>
            {
                ["acdream.mosstank"] = new()
                {
                    ["settingsProfile"] = SettingsProfileName,
                    ["lootProfile"] = LootProfileName,
                    ["navProfile"] = RouteProfileName,
                    ["enableMeta"] = "false",
                    ["startMacro"] = "true",
                },
            },
            StatusFile = statusPath,
        };
        var credential = new HeadlessCredentialSecret("vt-proof", pass!);
        var diagnosticsOutput = new StringWriter();
        var observed = new SessionObservation();

        // The session needs a body: without prepared content there is no
        // local movement controller and no collision, so nothing that walks,
        // faces or measures a distance can be judged.
        using HeadlessProcessContentOwner content = OpenProcessContent(
            message => diagnosticsOutput.WriteLine("content: " + message));
        using HeadlessProcessContentOwner.HeadlessProcessContentLease contentLease =
            content.AcquireLease(descriptor.Id);

        using var session = new HeadlessSessionHost(
            descriptor,
            credential,
            new HeadlessDiagnosticWriter(diagnosticsOutput),
            sessionOperations: null, // real network
            contentLease: contentLease,
            vtankProfiles: new FilePluginStorage(vtankRoot),
            pluginRoots: [temporary.Path]);
        using IDisposable subscription = session.Runtime.Subscribe(observed);

        var staged = new List<string>();

        void Pump(TimeSpan duration)
        {
            DateTime deadline = DateTime.UtcNow + duration;
            while (DateTime.UtcNow < deadline)
            {
                session.Tick(0.1d);
                Thread.Sleep(100);
            }
        }

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

        void Stage(string command)
        {
            SubmitOutcome outcome = session.SubmitConsoleLine(command);
            staged.Add($"{command} -> {outcome}");
            Pump(TimeSpan.FromSeconds(1.5d));
        }

        string Evidence()
        {
            string[] pluginMessages = PluginMessages(diagnosticsOutput.ToString());
            string[] problems = pluginMessages
                .Where(static line =>
                    line.StartsWith("plugin-warn:", StringComparison.Ordinal)
                    || line.StartsWith("plugin-error:", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return string.Join(
                Environment.NewLine,
                "staged server commands:",
                Indent(staged),
                "plugin warnings and errors:",
                Indent(problems),
                "last 30 chat lines:",
                Indent(Tail(observed.SnapshotChat(), 30)),
                "last 30 plugin messages:",
                Indent(Tail(pluginMessages, 30)));
        }

        _ = session.Start();

        // ---- P1: connected, plugin loaded, entered world -------------------
        bool enteredWorld = WaitUntil(
            TimeSpan.FromSeconds(45d),
            () => EventNames(ReadStatuses(statusPath)).Contains("enteredWorld"));
        string[] names = EventNames(ReadStatuses(statusPath));
        string[] requiredEvents =
            ["started", "pluginLoaded", "connected", "characterList", "enteredWorld"];
        string[] missing = requiredEvents.Where(name => !names.Contains(name)).ToArray();
        bool pluginLoaded = ReadStatuses(statusPath).Any(static item =>
            item.GetProperty("e").GetString() == "pluginLoaded"
            && item.GetProperty("plugin").GetString() == "acdream.mosstank");
        if (enteredWorld && missing.Length == 0 && pluginLoaded
            && session.Plugins.LoadedCount >= 1)
        {
            ledger.Pass(
                "P1",
                "connected, plugin loaded, entered world",
                $"status events {string.Join(",", requiredEvents)} present; "
                    + PositionText(session));
        }
        else
        {
            ledger.Fail(
                "P1",
                "connected, plugin loaded, entered world",
                missing.Length > 0
                    ? $"missing status events: {string.Join(",", missing)}"
                    : $"plugin not loaded (loadedCount={session.Plugins.LoadedCount})",
                Evidence());
        }

        if (ledger.Failed("P1"))
        {
            // Nothing downstream can be judged without a live session.
            foreach ((string id, string title) in RemainingMilestones("P1"))
                ledger.NotReached(id, title, "the session never entered the world");
            FinishAndReport(session, statusPath, ledger, Evidence, output);
            return;
        }

        // Give the plugin's autostart edge a tick or two, then open every
        // log channel the plugin knows about so its work becomes observable.
        Pump(TimeSpan.FromSeconds(3d));
        foreach (string channel in LogChannels)
            Stage($"/vt log {channel} on");

        // ---- P2: the three fixture profiles are loaded ---------------------
        {
            string[] pluginMessages = PluginMessages(diagnosticsOutput.ToString());
            string[] autostartLines = pluginMessages
                .Where(static line => line.Contains("Autostart:", StringComparison.Ordinal))
                .ToArray();
            string[] chat = observed.SnapshotChat();
            bool NamesProfile(string profile) =>
                pluginMessages.Concat(chat).Any(line =>
                    line.Contains(profile, StringComparison.OrdinalIgnoreCase)
                    && line.Contains("oaded", StringComparison.Ordinal));
            string[] unreported = new[]
                {
                    SettingsProfileName + ".usd",
                    LootProfileName + ".utl",
                    RouteProfileName + ".af",
                }
                .Where(profile => !NamesProfile(Path.GetFileNameWithoutExtension(profile)))
                .ToArray();
            if (unreported.Length == 0 && autostartLines.Length == 0)
            {
                ledger.Pass(
                    "P2",
                    "the fixture settings, loot and route profiles are loaded",
                    "each profile reported a load line");
            }
            else
            {
                ledger.Fail(
                    "P2",
                    "the fixture settings, loot and route profiles are loaded",
                    unreported.Length > 0
                        ? $"no load line for: {string.Join(", ", unreported)}"
                        : "the plugin reported an autostart problem",
                    (autostartLines.Length > 0
                        ? "autostart lines:" + Environment.NewLine
                            + Indent(autostartLines) + Environment.NewLine
                        : string.Empty)
                        + Evidence());
            }
        }

        // ---- stage the arena ----------------------------------------------
        Stage(ArenaTeleport);
        bool inArena = WaitUntil(
            TimeSpan.FromSeconds(20d),
            () => DistanceMetersFromArena(session) < 40d);
        Pump(TimeSpan.FromSeconds(2d));
        staged.Add($"arena reached -> {inArena} ({PositionText(session)})");

        // ---- P3: a buff pass runs and finishes -----------------------------
        {
            int before = observed.ChatCount;
            bool buffed = WaitUntil(
                TimeSpan.FromSeconds(45d),
                () => MentionsAny(
                    observed.SnapshotChat(),
                    "Buffing:", "SpellCaster: Begin", "Casting:"));
            bool settled = buffed
                && WaitUntil(
                    TimeSpan.FromSeconds(30d),
                    () => Quiet(observed, "Casting:", TimeSpan.FromSeconds(6d)));
            if (buffed && settled)
            {
                ledger.Pass(
                    "P3",
                    "the buff pass runs and then goes quiet",
                    $"buff lines observed after chat entry {before}");
            }
            else
            {
                ledger.Fail(
                    "P3",
                    "the buff pass runs and then goes quiet",
                    buffed
                        ? "the buff pass never went quiet"
                        : "no buff line was ever emitted",
                    Evidence());
            }
        }

        // ---- P4: a fight -- attack rule active, a kill observed ------------
        Stage("@create " + MonsterWeenie);
        Stage("@create " + MonsterWeenie);
        {
            bool attackRule = WaitUntil(
                TimeSpan.FromSeconds(45d),
                () => MentionsAny(
                    observed.SnapshotChat(),
                    "Picked Attack P:", "(Attack) Running"));
            bool killed = WaitUntil(
                TimeSpan.FromSeconds(45d),
                () => observed.SnapshotChat().Any(IsKillLine));
            if (attackRule && killed)
            {
                ledger.Pass(
                    "P4",
                    "the attack rule wins the loop and a kill is observed",
                    "attack rule active and a kill line reached the chat log");
            }
            else
            {
                ledger.Fail(
                    "P4",
                    "the attack rule wins the loop and a kill is observed",
                    attackRule
                        ? "the attack rule ran but nothing died"
                        : "the attack rule never became the active rule",
                    Evidence());
            }
        }

        // ---- P5: loot -- a corpse is opened and a decision is made ---------
        {
            bool decision = WaitUntil(
                TimeSpan.FromSeconds(45d),
                () => MentionsAny(
                    observed.SnapshotChat(),
                    "Opening ", "Looting ", "Looted "));
            bool picked = observed.InventoryAdditions > 0;
            if (decision && picked)
            {
                ledger.Pass(
                    "P5",
                    "a corpse is opened and at least one loot decision is made",
                    $"{observed.InventoryAdditions} item(s) reached the inventory");
            }
            else
            {
                ledger.Fail(
                    "P5",
                    "a corpse is opened and at least one loot decision is made",
                    decision
                        ? "a loot decision was reported but nothing entered the inventory"
                        : "no corpse was opened and no loot decision was reported",
                    Evidence());
            }
        }

        // Clear the arena so the route and the vitals milestones are not
        // decided by a monster the plugin cannot fight.
        Stage("@smite all");
        Stage("@heal");

        // ---- P6: the route is walked -- two waypoints reached in order -----
        {
            var reached = new List<int>();
            _ = WaitUntil(
                TimeSpan.FromSeconds(100d),
                () =>
                {
                    int index = NearestWaypointWithin(
                        session, route, WaypointArrivalMeters);
                    if (index >= 0 && (reached.Count == 0 || reached[^1] != index))
                        reached.Add(index);
                    return reached.Count >= 3;
                });
            int advances = Math.Max(0, reached.Count - 1);
            if (advances >= 2)
            {
                ledger.Pass(
                    "P6",
                    "the route advances by at least two waypoints",
                    $"waypoint order {string.Join("->", reached)}");
            }
            else
            {
                ledger.Fail(
                    "P6",
                    "the route advances by at least two waypoints",
                    $"only {advances} waypoint advance(s); "
                        + $"visited {(reached.Count == 0 ? "none" : string.Join("->", reached))}; "
                        + PositionText(session),
                    Evidence());
            }
        }

        // ---- P7: a vitals recharge fires -----------------------------------
        Stage("@setvital stamina 10");
        {
            bool recharged = WaitUntil(
                TimeSpan.FromSeconds(45d),
                () => MentionsAny(
                    [.. PluginMessages(diagnosticsOutput.ToString())
                        .Concat(observed.SnapshotChat())],
                    "Vitals:", "Recharging "));
            if (recharged)
            {
                ledger.Pass(
                    "P7",
                    "a vitals recharge fires at least once",
                    "a recharge line was emitted after stamina was forced down");
            }
            else
            {
                ledger.Fail(
                    "P7",
                    "a vitals recharge fires at least once",
                    $"no recharge line after forcing stamina down "
                        + $"(stamina now {VitalText(session, LocalPlayerState.VitalKind.Stamina)})",
                    Evidence());
            }
        }

        // ---- P8: death, then recovery --------------------------------------
        Stage("@setvital health 1");
        Stage("@smite " + character);
        {
            bool died = WaitUntil(
                TimeSpan.FromSeconds(45d),
                () => IsDead(session)
                    || MentionsAny(observed.SnapshotChat(), "You were killed by"));
            // The plugin has to notice the death itself, not merely keep
            // talking: its own death handling stops the macro, so the proof
            // of a real recovery is that acknowledgement followed by the
            // scheduler picking rules again.
            bool acknowledged = died
                && WaitUntil(
                    TimeSpan.FromSeconds(30d),
                    () => observed.SnapshotChat().Any(static line =>
                        line.Contains("[MossTank]", StringComparison.Ordinal)
                        && line.Contains("died", StringComparison.Ordinal)));
            bool recovered = acknowledged
                && WaitUntil(
                    TimeSpan.FromSeconds(60d),
                    () => !IsDead(session) && session.Runtime.Lifecycle.State
                        == RuntimeLifecycleState.InWorld);
            int chatBeforeResume = observed.ChatCount;
            bool macroResumed = recovered
                && WaitUntil(
                    TimeSpan.FromSeconds(45d),
                    () => observed.SnapshotChat()
                        .Skip(chatBeforeResume)
                        .Any(static line => line.Contains(
                            "Picked ", StringComparison.Ordinal)));
            if (died && acknowledged && recovered && macroResumed)
            {
                ledger.Pass(
                    "P8",
                    "the character dies, recovers and the macro resumes",
                    "death observed and acknowledged, vitals restored, "
                        + "the scheduler picked a rule again");
            }
            else
            {
                ledger.Fail(
                    "P8",
                    "the character dies, recovers and the macro resumes",
                    !died
                        ? "no death was observable"
                        : !acknowledged
                            ? "the plugin never noticed the death"
                            : !recovered
                                ? "the character never recovered after dying"
                                : "the scheduler picked no rule after the recovery",
                    Evidence());
            }
        }

        FinishAndReport(session, statusPath, ledger, Evidence, output);
    }

    // ======================================================================
    // Milestone bookkeeping.
    // ======================================================================

    private static readonly (string Id, string Title)[] AllMilestones =
    [
        ("P1", "connected, plugin loaded, entered world"),
        ("P2", "the fixture settings, loot and route profiles are loaded"),
        ("P3", "the buff pass runs and then goes quiet"),
        ("P4", "the attack rule wins the loop and a kill is observed"),
        ("P5", "a corpse is opened and at least one loot decision is made"),
        ("P6", "the route advances by at least two waypoints"),
        ("P7", "a vitals recharge fires at least once"),
        ("P8", "the character dies, recovers and the macro resumes"),
        ("P9", "the session exits gracefully with code 0"),
    ];

    private static IEnumerable<(string Id, string Title)> RemainingMilestones(string after)
    {
        bool seen = false;
        foreach ((string id, string title) in AllMilestones)
        {
            if (seen && id != "P9")
                yield return (id, title);
            if (id == after)
                seen = true;
        }
    }

    private static void FinishAndReport(
        HeadlessSessionHost session,
        string statusPath,
        VtProofLedger ledger,
        Func<string> evidence,
        ITestOutputHelper output)
    {
        // Graceful logout (Dispose runs Stop() first), then the terminal
        // status event is the proof of a clean exit.
        session.Dispose();
        JsonElement[] exited = ReadStatuses(statusPath)
            .Where(static item => item.GetProperty("e").GetString() == "exited")
            .ToArray();
        if (exited.Length == 1
            && exited[0].GetProperty("code").GetInt32() == 0
            && exited[0].GetProperty("reason").GetString() == "graceful")
        {
            ledger.Pass("P9", "the session exits gracefully with code 0", "code 0, reason graceful");
        }
        else
        {
            ledger.Fail(
                "P9",
                "the session exits gracefully with code 0",
                exited.Length == 0
                    ? "no terminal status event was written"
                    : $"code {exited[0].GetProperty("code").GetInt32()}, "
                        + $"reason {exited[0].GetProperty("reason").GetString()}",
                evidence());
        }

        string table = ledger.RenderTable();
        output.WriteLine(table);
        Console.Out.WriteLine(table);
        Console.Out.Flush();
        Assert.True(
            ledger.AllPassed,
            table + Environment.NewLine + Environment.NewLine + ledger.RenderEvidence());
    }

    // ======================================================================
    // Live observation helpers.
    // ======================================================================

    private static bool IsKillLine(string line) =>
        line.StartsWith("You killed ", StringComparison.Ordinal)
        || line.StartsWith("You obliterate ", StringComparison.Ordinal)
        || line.StartsWith("You destroy ", StringComparison.Ordinal)
        || line.Contains(" by your attack!", StringComparison.Ordinal);

    private static bool MentionsAny(IReadOnlyList<string> lines, params string[] needles)
    {
        for (int index = 0; index < lines.Count; index++)
        {
            foreach (string needle in needles)
            {
                if (lines[index].Contains(needle, StringComparison.Ordinal))
                    return true;
            }
        }
        return false;
    }

    private static bool Quiet(SessionObservation observed, string needle, TimeSpan window) =>
        observed.SecondsSinceLastMatch(needle) >= window.TotalSeconds;

    private static bool IsDead(HeadlessSessionHost session) =>
        session.Runtime.Character.TryGetVital(
            (int)LocalPlayerState.VitalKind.Health,
            out RuntimeVitalSnapshot health)
        && health.Maximum > 0u
        && health.Current == 0u;

    private static string VitalText(
        HeadlessSessionHost session,
        LocalPlayerState.VitalKind kind) =>
        session.Runtime.Character.TryGetVital((int)kind, out RuntimeVitalSnapshot vital)
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{vital.Current}/{vital.Maximum}")
            : "unknown";

    /// <summary>
    /// The character's position expressed in the game's own map coordinates
    /// (east-west and north-south), which is the frame the route file uses.
    /// </summary>
    private static (double EastWest, double NorthSouth) GameCoordinates(Position position)
    {
        double blockX = (position.ObjCellId >> 24) & 0xFFu;
        double blockY = (position.ObjCellId >> 16) & 0xFFu;
        Vector3 local = position.Frame.Origin;
        return (
            ((blockX - 127d) * 192d + local.X - 84d) / 240d,
            ((blockY - 127d) * 192d + local.Y - 84d) / 240d);
    }

    /// <summary>
    /// A one-line report of where the session believes the character is.
    /// The controller flag matters: without one there is no position to
    /// judge a route against, whatever the character is really doing.
    /// </summary>
    private static string PositionText(HeadlessSessionHost session)
    {
        RuntimeMovementSnapshot movement = session.Runtime.Movement.Snapshot;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"movementController={movement.HasController} "
                + $"cell=0x{movement.Position.ObjCellId:X8} "
                + $"local=({movement.Position.Frame.Origin.X:0.0},"
                + $"{movement.Position.Frame.Origin.Y:0.0},"
                + $"{movement.Position.Frame.Origin.Z:0.0}) "
                + $"{DistanceMetersFromArena(session):0.0} m from the arena centre");
    }

    private static double DistanceMetersFromArena(HeadlessSessionHost session)
    {
        (double eastWest, double northSouth) =
            GameCoordinates(session.Runtime.Movement.Snapshot.Position);
        return CoordinateDistanceMeters(
            eastWest, northSouth, ArenaEastWest, ArenaNorthSouth);
    }

    private static int NearestWaypointWithin(
        HeadlessSessionHost session,
        IReadOnlyList<VtProofRoutePoint> route,
        double radiusMeters)
    {
        (double eastWest, double northSouth) =
            GameCoordinates(session.Runtime.Movement.Snapshot.Position);
        int best = -1;
        double bestDistance = radiusMeters;
        for (int index = 0; index < route.Count; index++)
        {
            double distance = CoordinateDistanceMeters(
                eastWest, northSouth, route[index].EastWest, route[index].NorthSouth);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = index;
            }
        }
        return best;
    }

    /// <summary>One map coordinate unit is 240 metres of world distance.</summary>
    private static double CoordinateDistanceMeters(
        double eastWest,
        double northSouth,
        double otherEastWest,
        double otherNorthSouth)
    {
        double deltaEastWest = eastWest - otherEastWest;
        double deltaNorthSouth = northSouth - otherNorthSouth;
        return Math.Sqrt(
            (deltaEastWest * deltaEastWest) + (deltaNorthSouth * deltaNorthSouth)) * 240d;
    }

    private sealed class SessionObservation : IRuntimeEventObserver
    {
        private readonly object _gate = new();
        private readonly List<string> _chat = [];
        private readonly Dictionary<string, DateTime> _lastMatch = new(StringComparer.Ordinal);

        internal int InventoryAdditions { get; private set; }

        internal int ChatCount
        {
            get
            {
                lock (_gate)
                    return _chat.Count;
            }
        }

        internal string[] SnapshotChat()
        {
            lock (_gate)
                return [.. _chat];
        }

        internal double SecondsSinceLastMatch(string needle)
        {
            lock (_gate)
            {
                return _lastMatch.TryGetValue(needle, out DateTime stamp)
                    ? (DateTime.UtcNow - stamp).TotalSeconds
                    : double.MaxValue;
            }
        }

        public void OnLifecycle(in RuntimeLifecycleDelta delta) { }
        public void OnCommand(in RuntimeCommandDelta delta) { }
        public void OnEntity(in RuntimeEntityDelta delta) { }

        public void OnInventory(in RuntimeInventoryDelta delta)
        {
            if (delta.Change == RuntimeInventoryChange.Added)
                InventoryAdditions++;
        }

        public void OnChat(in RuntimeChatDelta delta)
        {
            string text = delta.Entry.Text;
            lock (_gate)
            {
                _chat.Add(text);
                if (text.Contains("Casting:", StringComparison.Ordinal))
                    _lastMatch["Casting:"] = DateTime.UtcNow;
            }
        }

        public void OnMovement(in RuntimeMovementDelta delta) { }
        public void OnPortal(in RuntimePortalDelta delta) { }
        public void OnCombat(in RuntimeCombatDelta delta) { }
    }

    // ======================================================================
    // Fixture staging.
    // ======================================================================

    /// <summary>
    /// The installed retail data plus the machine-local prepared package the
    /// production hosts read. The proof refuses to run without them rather
    /// than reporting a bodiless session's milestones as if they meant
    /// something.
    /// </summary>
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
            $"The proof needs the installed data directory: {datDirectory} "
                + "(set ACDREAM_DAT_DIR).");
        Assert.True(
            File.Exists(preparedAssetPath),
            $"The proof needs the prepared package: {preparedAssetPath} "
                + "(set ACDREAM_PAK_PATH).");

        return new HeadlessProcessContentOwner(
            new HeadlessContentDescriptor
            {
                DatDirectory = datDirectory,
                PreparedAssetPath = preparedAssetPath,
            },
            diagnostic);
    }

    private static string StageProfileFixtures(string root)
    {
        string source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "vt-proof");
        Assert.True(
            Directory.Exists(source),
            $"The proof profile fixtures are missing: {source}");
        string vtankRoot = Path.Combine(root, "vtank");
        Directory.CreateDirectory(Path.Combine(vtankRoot, "navs"));
        Directory.CreateDirectory(Path.Combine(vtankRoot, "metas"));
        foreach (string file in Directory.EnumerateFiles(
            source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            string destination = Path.Combine(vtankRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
        return vtankRoot;
    }

    private static string InstallRealMossTankPlugin(string root)
    {
        const string fileName = "AcDream.Plugins.MossTank.dll";
        string configuration = new DirectoryInfo(AppContext.BaseDirectory)
            .Parent!.Name;
        string repoRoot = FindRepoRoot(AppContext.BaseDirectory);
        string source = Path.Combine(
            repoRoot,
            "src",
            "AcDream.Plugins.MossTank",
            "bin",
            configuration,
            "net10.0",
            fileName);
        Assert.True(File.Exists(source), $"MossTank plugin DLL not found: {source}");

        string pluginDirectory = Path.Combine(root, "mosstank");
        Directory.CreateDirectory(pluginDirectory);
        File.Copy(source, Path.Combine(pluginDirectory, fileName));
        File.WriteAllText(
            Path.Combine(pluginDirectory, "plugin.json"),
            JsonSerializer.Serialize(new
            {
                id = "acdream.mosstank",
                displayName = "MossTank",
                version = "0.1.0",
                entryDll = fileName,
                apiVersion = 1,
            }));
        return pluginDirectory;
    }

    private static string FindRepoRoot(string start)
    {
        DirectoryInfo? directory = new(start);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "AcDream.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
            ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static JsonElement[] ReadStatuses(string path) =>
        File.Exists(path)
            ? File.ReadAllLines(path)
                .Where(static line => line.Length > 0)
                .Select(static line => JsonDocument.Parse(line).RootElement.Clone())
                .ToArray()
            : [];

    private static string[] EventNames(IEnumerable<JsonElement> events) =>
        events.Select(static item => item.GetProperty("e").GetString()!).ToArray();

    /// <summary>
    /// The plugin's own messages, lifted out of the session diagnostics.
    /// Matching against the raw diagnostic lines would also match the
    /// session name and other envelope fields, so the envelope is dropped.
    /// </summary>
    private static string[] PluginMessages(string diagnostics)
    {
        var messages = new List<string>();
        foreach (string line in SplitLines(diagnostics))
        {
            string? name;
            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                name = document.RootElement.TryGetProperty(
                    "eventName", out JsonElement value)
                    ? value.GetString()
                    : null;
            }
            catch (JsonException)
            {
                continue;
            }
            if (name is not null
                && name.StartsWith("plugin-", StringComparison.Ordinal))
            {
                messages.Add(name);
            }
        }
        return [.. messages];
    }

    private static string[] SplitLines(string text) => text
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Split('\n')
        .Where(static line => line.Length > 0)
        .ToArray();

    private static string[] Tail(IReadOnlyList<string> lines, int count) =>
        lines.Count <= count
            ? [.. lines]
            : [.. lines.Skip(lines.Count - count)];

    private static string Indent(IReadOnlyList<string> lines) =>
        lines.Count == 0
            ? "  (none)"
            : string.Join(
                Environment.NewLine,
                lines.Select(static line => "  " + line));

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"acdream-headless-vt-proof-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            for (int attempt = 0; Directory.Exists(Path); attempt++)
            {
                try
                {
                    Directory.Delete(Path, recursive: true);
                    return;
                }
                catch (Exception error)
                    when (error is IOException or UnauthorizedAccessException
                        && attempt < 9)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    Thread.Sleep(10);
                }
            }
        }
    }
}

/// <summary>
/// One milestone's verdict. The ledger renders exactly one line per
/// milestone so a script can table a run, and keeps the long evidence
/// separate so the assertion message stays readable.
/// </summary>
internal sealed class VtProofLedger
{
    internal const string LinePrefix = "VT-PROOF";

    private readonly List<Entry> _entries = [];

    private readonly record struct Entry(
        string Id,
        string Title,
        bool Passed,
        string Summary,
        string Evidence);

    internal bool AllPassed => _entries.TrueForAll(static entry => entry.Passed);

    internal void Pass(string id, string title, string summary) =>
        _entries.Add(new Entry(id, title, true, summary, string.Empty));

    internal void Fail(string id, string title, string summary, string evidence) =>
        _entries.Add(new Entry(id, title, false, summary, evidence));

    internal void NotReached(string id, string title, string because) =>
        _entries.Add(new Entry(id, title, false, "not reached: " + because, string.Empty));

    internal bool Failed(string id) =>
        _entries.Exists(entry => entry.Id == id && !entry.Passed);

    /// <summary>The ids of the milestones that passed, in order, for a failure report.</summary>
    internal string PassedSoFar()
    {
        string[] passed = _entries
            .Where(static entry => entry.Passed)
            .Select(static entry => entry.Id)
            .ToArray();
        return passed.Length == 0 ? "(none)" : string.Join(", ", passed);
    }

    internal string RenderTable()
    {
        var builder = new StringBuilder();
        foreach (Entry entry in _entries)
        {
            builder.Append(LinePrefix)
                .Append(" | ")
                .Append(OneLine(entry.Id))
                .Append(" | ")
                .Append(entry.Passed ? "PASS" : "FAIL")
                .Append(" | ")
                .Append(OneLine(entry.Title))
                .Append(" | ")
                .Append(OneLine(entry.Summary))
                .AppendLine();
        }
        builder.Append(LinePrefix)
            .Append(" | SUMMARY | ")
            .Append(AllPassed ? "PASS" : "FAIL")
            .Append(" | passed: ")
            .Append(PassedSoFar());
        return builder.ToString();
    }

    internal string RenderEvidence()
    {
        var builder = new StringBuilder();
        foreach (Entry entry in _entries)
        {
            if (entry.Passed || entry.Evidence.Length == 0)
                continue;
            builder.Append("=== ")
                .Append(entry.Id)
                .Append(" FAILED: ")
                .Append(entry.Summary)
                .AppendLine(" ===")
                .AppendLine("milestones that passed before it: " + PassedSoFar())
                .AppendLine(entry.Evidence)
                .AppendLine();
        }
        return builder.Length == 0 ? "(no evidence captured)" : builder.ToString();
    }

    private static string OneLine(string value) => value
        .Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal)
        .Replace("|", "/", StringComparison.Ordinal)
        .Trim();
}

internal readonly record struct VtProofRoutePoint(
    double EastWest,
    double NorthSouth,
    double Elevation);

/// <summary>
/// Reads the point nodes out of the proof route file. Only plain points are
/// read: the proof route is deliberately a bare loop so that "did the
/// character walk it?" has one unambiguous answer.
/// </summary>
internal static class VtProofRouteFixture
{
    internal static IReadOnlyList<VtProofRoutePoint> ReadPoints(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var points = new List<VtProofRoutePoint>();
        foreach (string raw in text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n'))
        {
            string line = raw.Trim();
            if (!line.StartsWith("pnt ", StringComparison.Ordinal))
                continue;
            string[] fields = line[4..].Split(
                ' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 3)
            {
                throw new FormatException(
                    $"A route point needs three coordinates: '{line}'.");
            }
            points.Add(new VtProofRoutePoint(
                ParseCoordinate(fields[0], line),
                ParseCoordinate(fields[1], line),
                ParseCoordinate(fields[2], line)));
        }
        return points;
    }

    private static double ParseCoordinate(string field, string line) =>
        double.TryParse(
            field,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double value)
            ? value
            : throw new FormatException(
                $"'{field}' is not a coordinate in route line '{line}'.");
}

/// <summary>Offline checks for the proof harness's own reusable parts.</summary>
public sealed class VtSessionProofHarnessTests
{
    [Fact]
    public void TheLedgerRendersExactlyOneLinePerMilestonePlusASummary()
    {
        var ledger = new VtProofLedger();
        ledger.Pass("P1", "entered world", "all status events present");
        ledger.Fail("P2", "profiles loaded", "no load line", "chat:\nnothing");

        string[] lines = ledger.RenderTable()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.TrimEnd('\r'))
            .ToArray();

        Assert.Equal(3, lines.Length);
        Assert.Equal(
            "VT-PROOF | P1 | PASS | entered world | all status events present",
            lines[0]);
        Assert.Equal(
            "VT-PROOF | P2 | FAIL | profiles loaded | no load line",
            lines[1]);
        Assert.Equal("VT-PROOF | SUMMARY | FAIL | passed: P1", lines[2]);
    }

    [Fact]
    public void TheLedgerKeepsEveryTableRowOnASingleLine()
    {
        var ledger = new VtProofLedger();
        ledger.Fail(
            "P4",
            "a fight",
            "two lines\nand a | pipe",
            "long evidence");

        string[] lines = ledger.RenderTable()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, lines.Length);
        Assert.Contains("two lines and a / pipe", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ALedgerWithOnlyPassesReportsSuccess()
    {
        var ledger = new VtProofLedger();
        ledger.Pass("P1", "one", "ok");
        ledger.Pass("P2", "two", "ok");

        Assert.True(ledger.AllPassed);
        Assert.Equal("P1, P2", ledger.PassedSoFar());
        Assert.Equal("(no evidence captured)", ledger.RenderEvidence());
    }

    [Fact]
    public void ANotReachedMilestoneFailsAndSaysWhy()
    {
        var ledger = new VtProofLedger();
        ledger.Fail("P1", "entered world", "timed out", "evidence");
        ledger.NotReached("P2", "profiles loaded", "the session never entered the world");

        Assert.False(ledger.AllPassed);
        Assert.True(ledger.Failed("P1"));
        Assert.Contains(
            "not reached: the session never entered the world",
            ledger.RenderTable(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheEvidenceBlockNamesTheMilestonesThatPassedBeforeTheFailure()
    {
        var ledger = new VtProofLedger();
        ledger.Pass("P1", "entered world", "ok");
        ledger.Fail("P2", "profiles loaded", "no load line", "the chat tail");

        string evidence = ledger.RenderEvidence();

        Assert.Contains("=== P2 FAILED: no load line ===", evidence, StringComparison.Ordinal);
        Assert.Contains(
            "milestones that passed before it: P1",
            evidence,
            StringComparison.Ordinal);
        Assert.Contains("the chat tail", evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void TheShippedRouteFixtureIsALoopOfPlainPoints()
    {
        string path = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "vt-proof",
            "navs",
            "vt-proof-route.af");
        Assert.True(File.Exists(path), $"missing route fixture: {path}");

        IReadOnlyList<VtProofRoutePoint> points =
            VtProofRouteFixture.ReadPoints(File.ReadAllText(path));

        Assert.Equal(4, points.Count);
        Assert.All(points, static point =>
        {
            Assert.InRange(point.EastWest, 33.5d, 34.1d);
            Assert.InRange(point.NorthSouth, 41.9d, 42.4d);
        });
        Assert.Equal(4, points.Distinct().Count());
    }

    [Fact]
    public void TheRouteReaderIgnoresCommentsAndTheNavHeader()
    {
        const string text = """
            ~~ { a comment
            ~~ }

            NAV: vtproof circular ~~ {
            	pnt 1.5 -2.25 0.5
            ~~ }
            """;

        IReadOnlyList<VtProofRoutePoint> points = VtProofRouteFixture.ReadPoints(text);

        Assert.Single(points);
        Assert.Equal(new VtProofRoutePoint(1.5d, -2.25d, 0.5d), points[0]);
    }

    [Fact]
    public void TheRouteReaderRefusesAMalformedPoint()
    {
        FormatException error = Assert.Throws<FormatException>(
            static () => VtProofRouteFixture.ReadPoints("\tpnt 1.0 2.0\n"));

        Assert.Contains("three coordinates", error.Message, StringComparison.Ordinal);
    }
}
