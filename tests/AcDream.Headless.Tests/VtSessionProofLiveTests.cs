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
using AcDream.Plugin.Abstractions;
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

    /// <summary>
    /// The caster the run gives itself. The proof character owns no weapon
    /// of any kind, and every rule that casts — buffs, recharges, war spells
    /// — goes through one preparation gate that stops the whole macro when
    /// the profile's Items page names no wand it can find. So the run stages
    /// a plain wand before it starts the macro, and the fixture's companion
    /// document names it.
    /// </summary>
    private const string CasterWeenie = "2472";

    /// <summary>
    /// The staged caster's display name, which is also the single entry on
    /// the fixture profile's Items page. If the server ever renames the
    /// weenie the run says so instead of silently going quiet.
    /// </summary>
    private const string CasterItemName = "Wand";

    /// <summary>
    /// How long the first buff pass may take. A character that knows the
    /// whole self-buff book and has every skill trained casts dozens of
    /// them one after another, and each one is a real cast on a real
    /// server; the run waits that out rather than judging combat and
    /// navigation through a buff pass that is still working.
    /// </summary>
    private static readonly TimeSpan BuffPassBudget = TimeSpan.FromMinutes(6d);

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
        (string vtankRoot, string pluginStorageRoot) =
            StageProfileFixtures(temporary.Path);
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
                    // Before the macro, not after it: several of the
                    // plugin's most useful lines are emitted once per run,
                    // so a channel this run opened from outside would have
                    // missed them.
                    ["logChannels"] = string.Join(',', LogChannels),
                    // Deliberately not autostarted. Autostart fires on the
                    // tick the character's name lands, which is long before
                    // anything outside can stage a scenario — and the
                    // preparation gate every casting rule shares stops the
                    // macro on its first pass if the profile's wand is not
                    // in the character's hands yet. So the run stages the
                    // wand first and then starts the macro the way the Run
                    // Macro checkbox does; autostart still owns the
                    // profiles and the log channels.
                    ["startMacro"] = "false",
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

        var session = new HeadlessSessionHost(
            descriptor,
            credential,
            new HeadlessDiagnosticWriter(diagnosticsOutput),
            sessionOperations: null, // real network
            contentLease: contentLease,
            vtankProfiles: new FilePluginStorage(vtankRoot),
            pluginRoots: [temporary.Path],
            // The plugin's own persisted state, which is where MossTank
            // keeps everything the .usd format has no table for — the
            // Items page among it.
            pluginStorage: new FilePluginStorage(pluginStorageRoot));
        using IDisposable subscription = session.Runtime.Subscribe(observed);
        // A proof that cannot report its own failure is worth nothing, and a
        // disposal that throws on the way out of a failed run replaces the
        // reason with itself. So the run's own error is printed first and the
        // teardown's, if any, printed after it — neither hides the other.
        try
        {
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

            /// <summary>
            /// What one of the plugin's own settings currently reads, asked
            /// and answered the way a player would: through its option verb.
            /// Null when the answer never came back.
            /// </summary>
            string? ReadOption(string name)
            {
                string marker = $"Option {name} = ";
                int before = observed.ChatCount;
                Stage("/vt opt get " + name);
                foreach (string line in observed.SnapshotChat().Skip(before))
                {
                    int at = line.IndexOf(marker, StringComparison.Ordinal);
                    if (at >= 0)
                        return line[(at + marker.Length)..].Trim();
                }
                return null;
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
                // The scheduler prints a header and a winner line every
                // 0.293 s. At roughly 1,200 lines per run that crowds every
                // other channel out of any fixed tail, which is exactly what
                // hid the rules' own reasons last time — so the pass spam
                // gets its own small window and everything else keeps a full
                // one.
                bool IsPassSpam(string line) =>
                    line.Contains("Primary logic loop started", StringComparison.Ordinal)
                    || line.Contains("All rules inactive.", StringComparison.Ordinal)
                    || line.Contains(") Running", StringComparison.Ordinal)
                    || line.Contains("Picked ", StringComparison.Ordinal);
                string[] chat = observed.SnapshotChat();
                return string.Join(
                    Environment.NewLine,
                    "the character the plugin can see:",
                    Indent([CharacterText(session)]),
                    "staged server commands:",
                    Indent(staged),
                    "plugin warnings and errors:",
                    Indent(problems),
                    "last 6 scheduler pass lines:",
                    Indent(Tail([.. chat.Where(IsPassSpam)], 6)),
                    "last 30 other chat lines:",
                    Indent(Tail([.. chat.Where(line => !IsPassSpam(line))], 30)),
                    "last 30 other plugin messages:",
                    Indent(Tail(
                        [.. pluginMessages.Where(line => !IsPassSpam(line))],
                        30)));
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

            // The channels came up with autostart, before the first pass.
            // Re-asserting them from here would be harmless and would also
            // be too late, so this only gives the autostart edge a tick or
            // two and then reads the state back into the record.
            Pump(TimeSpan.FromSeconds(3d));
            Stage("/vt log");

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

            // ---- stage the caster, then start the macro ------------------------
            // In that order: the gate every casting rule shares stops the
            // macro outright the first time it looks for the profile's wand
            // and the character has none.
            Stage("@ci " + CasterWeenie);
            bool casterOwned = WaitUntil(
                TimeSpan.FromSeconds(20d),
                () => OwnedEquipmentNames(session).Contains(CasterItemName));
            staged.Add(
                $"caster '{CasterItemName}' owned -> {casterOwned}; "
                    + $"equipment now: {string.Join(", ", OwnedEquipmentNames(session))}");
            Stage("/vt start");
            bool macroStarted = WaitUntil(
                TimeSpan.FromSeconds(20d),
                () => MentionsAny(observed.SnapshotChat(), "Macro started."));
            staged.Add($"macro started -> {macroStarted}");

            // ---- P3: buff spells are cast until nothing is due -----------------
            // The pass is as long as the character's spellbook makes it — a
            // fully skilled character really does cast dozens of buffs — and
            // it ends by saying so, which is a far better signal than a
            // silence anyone could mistake for a stalled macro. Everything
            // after this waits for it, because the buff rule outranks combat
            // and navigation and would otherwise eat their windows.
            {
                int before = observed.ChatCount;
                _ = WaitUntil(
                    TimeSpan.FromSeconds(BuffPassBudget.TotalSeconds),
                    () => VtProofCastEvidence.Read(observed.SnapshotChat())
                        .IsSettledPass);
                VtProofCastEvidence evidence =
                    VtProofCastEvidence.Read(observed.SnapshotChat());
                if (evidence.IsSettledPass)
                {
                    ledger.Pass(
                        "P3",
                        "buff spells are cast until nothing is due",
                        $"{evidence.CastLine}; {evidence.NothingDueLine} "
                            + $"(after chat entry {before})");
                }
                else
                {
                    ledger.Fail(
                        "P3",
                        "buff spells are cast until nothing is due",
                        evidence.Explain(),
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
                    () => VtProofKillEvidence.FirstKillLine(
                        observed.SnapshotChat()) is not null);
                if (attackRule && killed)
                {
                    ledger.Pass(
                        "P4",
                        "the attack rule wins the loop and a kill is observed",
                        "attack rule active; "
                            + VtProofKillEvidence.FirstKillLine(
                                observed.SnapshotChat()));
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

            // ---- P8: death, the stop, and the round picked up again ------------
            // Dying stops the macro and changes nothing else. The five checks
            // are: the death was noticed and said so, nothing was silently
            // reconfigured, the pass really did stop, starting again is all it
            // takes to get the rules running, and the route carries on from
            // the waypoint the death interrupted instead of the first one.
            {
                Stage("@setvital health 1");
                int chatBeforeDeath = observed.ChatCount;
                Stage("@smite " + character);

                bool died = WaitUntil(
                    TimeSpan.FromSeconds(45d),
                    () => IsDead(session)
                        || MentionsAny(observed.SnapshotChat(), "You were killed by"));

                // Read last, not first: the route can still advance a waypoint
                // in the seconds before the death, and nothing advances it
                // after — so the last one the rule named is the one the start
                // has to come back to.
                int? waypointBefore = LastRouteWaypointIndex(
                    observed.SnapshotChat(), 0, route);

                // (a) the plugin noticed the death itself and said so. That
                // line is the only thing that tells a player why the macro
                // went quiet.
                bool stopAnnounced = died
                    && WaitUntil(
                        TimeSpan.FromSeconds(30d),
                        () => observed.SnapshotChat().Any(static line =>
                            line.Contains(
                                "Macro stopped because the character died.",
                                StringComparison.Ordinal)));

                bool recovered = stopAnnounced
                    && WaitUntil(
                        TimeSpan.FromSeconds(60d),
                        () => !IsDead(session) && session.Runtime.Lifecycle.State
                            == RuntimeLifecycleState.InWorld);

                // (b) the pass really stopped. A macro that says it stopped
                // and keeps picking rules is worse than one that never said
                // anything, so this looks for the absence deliberately rather
                // than assuming it.
                int chatAfterDeath = observed.ChatCount;
                Pump(TimeSpan.FromSeconds(6d));
                string[] passesAfterDeath = observed.SnapshotChat()
                    .Skip(chatAfterDeath)
                    .Where(static line => line.Contains(
                        "Picked ", StringComparison.Ordinal))
                    .ToArray();
                bool passStopped = recovered && passesAfterDeath.Length == 0;

                // (c) nothing was reconfigured behind the player's back. The
                // four settings a death must not touch, read back through the
                // plugin's own option command.
                string[] deathSettings =
                    ["EnableBuffing", "EnableCombat", "EnableNav", "EnableLooting"];
                var afterDeath = new Dictionary<string, string?>(StringComparer.Ordinal);
                if (recovered)
                {
                    foreach (string name in deathSettings)
                        afterDeath[name] = ReadOption(name);
                }
                string[] changed = deathSettings
                    .Where(name => !string.Equals(
                        afterDeath.GetValueOrDefault(name), "True", StringComparison.Ordinal))
                    .ToArray();
                bool settingsUntouched = recovered && changed.Length == 0;

                // (d) starting again. Two things a player would do first: the
                // death left the caster on the corpse and every rule that
                // casts stops the macro outright when the profile's wand is
                // nowhere to be found; and the character respawned at its
                // lifestone, where the route is out of range. Both are also
                // how the run leaves the character somewhere sane for the next
                // one.
                Stage("@ci " + CasterWeenie);
                bool casterRestaged = WaitUntil(
                    TimeSpan.FromSeconds(20d),
                    () => OwnedEquipmentNames(session).Contains(CasterItemName));
                staged.Add($"caster '{CasterItemName}' owned again -> {casterRestaged}");
                // A character revives with its vitals near the floor, and the
                // recharge rule then wins every single pass trying to fix
                // that — it sits far above navigation, so nothing below it is
                // ever asked. Getting back on your feet is what a player does
                // too.
                Stage("@heal");
                Stage(ArenaTeleport);
                _ = WaitUntil(
                    TimeSpan.FromSeconds(20d),
                    () => DistanceMetersFromArena(session) < 40d);

                int chatBeforeStart = observed.ChatCount;
                Stage("/vt start");
                bool restarted = settingsUntouched
                    && WaitUntil(
                        TimeSpan.FromSeconds(45d),
                        () => observed.SnapshotChat()
                            .Skip(chatBeforeStart)
                            .Any(static line => line.Contains(
                                "Picked ", StringComparison.Ordinal)));

                // (e) the round carries on where it was. Dying strips the
                // character's enchantments, so the first thing a restarted
                // macro does is re-buff all of them, and a burst holds every
                // other rule's gate shut while it casts — on this character
                // for longer than the whole rest of the run takes. The route's
                // position has nothing to do with buffing, so the run puts
                // buffing down for as long as it takes the navigation rule to
                // say which waypoint it is on, then hands it straight back.
                int? waypointAfter = null;
                bool sameWaypoint = false;
                string routeSilence = string.Empty;
                if (restarted)
                {
                    Stage("/vt opt set EnableBuffing false");
                    int chatBeforeRoute = observed.ChatCount;
                    _ = WaitUntil(
                        TimeSpan.FromSeconds(60d),
                        () => FirstRouteWaypointIndex(
                            observed.SnapshotChat(), chatBeforeRoute, route) is not null);
                    waypointAfter = FirstRouteWaypointIndex(
                        observed.SnapshotChat(), chatBeforeRoute, route);
                    sameWaypoint = waypointBefore is not null
                        && waypointAfter == waypointBefore;
                    if (waypointAfter is null)
                    {
                        // The navigation rule can only name its waypoint on a
                        // pass it is asked about, and it is sixty-first in the
                        // list. Whoever won those passes instead is the whole
                        // explanation, so say who rather than leaving the
                        // reader with a silence.
                        routeSilence = "; the route rule was never asked — "
                            + (Tail(
                                    [.. observed.SnapshotChat()
                                        .Skip(chatBeforeRoute)
                                        .Where(static line => line.Contains(
                                            "Picked ", StringComparison.Ordinal))],
                                    1)
                                .FirstOrDefault()
                                ?? "no rule won a pass at all");
                    }
                    Stage("/vt opt set EnableBuffing true");
                }

                string verdict = string.Create(
                    CultureInfo.InvariantCulture,
                    $"stop-announced={Yes(stopAnnounced)}, "
                        + $"pass-stopped={Yes(passStopped)}, "
                        + $"settings-untouched={Yes(settingsUntouched)}, "
                        + $"restarted={Yes(restarted)}, "
                        + $"resumed-on-waypoint={Yes(sameWaypoint)} "
                        + $"(before={Waypoint(waypointBefore)} "
                        + $"after={Waypoint(waypointAfter)}){routeSilence}");

                const string title =
                    "death stops the macro, changes no setting, and starting "
                    + "again resumes the same waypoint";
                if (died && stopAnnounced && recovered && passStopped
                    && settingsUntouched && restarted && sameWaypoint)
                {
                    ledger.Pass("P8", title, verdict);
                }
                else
                {
                    string reason =
                        !died ? "no death was observable"
                        : !stopAnnounced
                            ? "the plugin never said the death stopped the macro"
                        : !recovered ? "the character never recovered after dying"
                        : !passStopped
                            ? "the scheduler kept picking rules after the stop: "
                                + string.Join(" | ", Tail(passesAfterDeath, 2))
                        : !settingsUntouched
                            ? "the death changed settings it must not: "
                                + string.Join(
                                    ", ",
                                    changed.Select(name =>
                                        $"{name}={afterDeath.GetValueOrDefault(name) ?? "unread"}"))
                        : !restarted
                            ? "no rule was picked after the macro was started again"
                        : "the route did not resume on the waypoint it had";
                    ledger.Fail("P8", title, reason + "; " + verdict, Evidence());
                }

                // Leave the character alive and unencumbered by this run's
                // scenery: the next run starts from whatever this one left.
                Stage("@smite all");
                Stage("@heal");
            }


            FinishAndReport(session, statusPath, ledger, Evidence, output);
        }
        catch (Exception error) when (error is not Xunit.Sdk.XunitException)
        {
            Console.Out.WriteLine(
                "vt-proof: the run threw before it could report:");
            Console.Out.WriteLine(error.ToString());
            Console.Out.WriteLine("player: " + PositionText(session));
            Console.Out.WriteLine("player record: " + LocalPlayerRecordText(session));
            Console.Out.WriteLine("last 60 host diagnostics (plugin chatter removed):");
            Console.Out.WriteLine(Indent(Tail(
                SplitLines(diagnosticsOutput.ToString())
                    .Where(static line => !line.Contains(
                        "\"eventName\":\"plugin-", StringComparison.Ordinal))
                    .ToArray(),
                60)));
            Console.Out.WriteLine("last 20 plugin messages:");
            Console.Out.WriteLine(Indent(
                Tail(PluginMessages(diagnosticsOutput.ToString()), 20)));
            Console.Out.WriteLine("last 20 chat lines:");
            Console.Out.WriteLine(Indent(Tail(observed.SnapshotChat(), 20)));

            // The table is the deliverable. A run that dies mid-way still
            // owes one, with every milestone it never got to marked as such.
            string reason = error.GetBaseException().Message;
            foreach ((string id, string title) in AllMilestones)
            {
                if (!ledger.Reported(id))
                    ledger.NotReached(id, title, "the run threw: " + reason);
            }
            Console.Out.WriteLine(ledger.RenderTable());
            Console.Out.Flush();
            throw;
        }
        finally
        {
            try
            {
                session.Dispose();
            }
            catch (Exception teardown)
            {
                Console.Out.WriteLine("vt-proof: teardown threw: " + teardown);
                Console.Out.Flush();
            }
        }
    }

    // ======================================================================
    // Milestone bookkeeping.
    // ======================================================================

    private static readonly (string Id, string Title)[] AllMilestones =
    [
        ("P1", "connected, plugin loaded, entered world"),
        ("P2", "the fixture settings, loot and route profiles are loaded"),
        ("P3", "buff spells are cast until nothing is due"),
        ("P4", "the attack rule wins the loop and a kill is observed"),
        ("P5", "a corpse is opened and at least one loot decision is made"),
        ("P6", "the route advances by at least two waypoints"),
        ("P7", "a vitals recharge fires at least once"),
        ("P8", "death stops the macro, changes no setting, and starting "
            + "again resumes the same waypoint"),
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

    /// <summary>
    /// What the plugin's equipment surface says the character owns and could
    /// wield. The profile's Items page is matched against these names, so a
    /// staged item that never shows up here is invisible to every rule.
    /// </summary>
    private static string[] OwnedEquipmentNames(HeadlessSessionHost session)
    {
        IEquipmentAutomation equipment = session.Plugins.Host.Automation.Equipment;
        return equipment.IsAvailable
            ? [.. equipment.CaptureOwnedEquipment()
                .Select(static item => item.Name)
                .Order(StringComparer.Ordinal)]
            : [];
    }

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
    /// <summary>
    /// What the runtime actually holds for the local player. A placement that
    /// refuses because there is "no canonical body" is unreadable without
    /// knowing whether the record exists, whether it has a body, and whether
    /// its arrival placement is still unresolved.
    /// </summary>
    private static string LocalPlayerRecordText(HeadlessSessionHost session)
    {
        uint guid = session.Runtime.PlayerIdentity.ServerGuid;
        if (!session.Runtime.EntityObjects.Entities.TryGetActive(
                guid,
                out AcDream.Runtime.Entities.RuntimeEntityRecord record))
        {
            return $"guid=0x{guid:X8} no active record "
                + $"(entities={session.Runtime.Entities.Count})";
        }
        return string.Create(
            CultureInfo.InvariantCulture,
            $"guid=0x{guid:X8} body={record.PhysicsBody is not null} "
                + $"cell=0x{record.Snapshot.Position?.LandblockId ?? 0u:X8} "
                + $"initialCreateResidence="
                + $"{session.Runtime.EntityObjects.TryGetInitialCreateResidence(record, out _)} "
                + $"entities={session.Runtime.Entities.Count}");
    }

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

    /// <summary>
    /// What the plugin's own surface says the character is and carries.
    /// A profile that names no weapon, or an inventory the surface cannot
    /// see, stops the whole macro on its first pass — and the run's evidence
    /// used to show neither.
    /// </summary>
    private static string CharacterText(HeadlessSessionHost session)
    {
        IAutomationSurface automation = session.Plugins.Host.Automation;
        if (!automation.IsAvailable)
            return "the automation surface is not available";
        string carried = automation.Items.IsAvailable
            ? string.Join(
                ", ",
                automation.Items.CaptureOwnedItems()
                    .Select(static item => item.Name)
                    .Order(StringComparer.Ordinal))
            : "(the item surface is unavailable)";
        string equipped = automation.Equipment.IsAvailable
            ? string.Join(
                ", ",
                automation.Equipment.CaptureOwnedEquipment()
                    .Select(static item => item.Name)
                    .Order(StringComparer.Ordinal))
            : "(the equipment surface is unavailable)";
        string counts = string.Create(
            CultureInfo.InvariantCulture,
            $"name='{automation.Character.Name}' "
                + $"skills={automation.Character.Skills.Count} "
                + $"selfBuffs={automation.Spells.KnownSelfBuffs.Count} "
                + $"combatMode={automation.Combat.Snapshot.Mode}");
        return counts
            + Environment.NewLine
            + "carried: " + carried
            + Environment.NewLine
            + "equipment: " + equipped;
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

    private static string Yes(bool value) => value ? "yes" : "no";

    private static string Waypoint(int? index) =>
        index is { } value
            ? value.ToString(CultureInfo.InvariantCulture)
            : "unread";

    private static int? LastRouteWaypointIndex(
        IReadOnlyList<string> lines,
        int from,
        IReadOnlyList<VtProofRoutePoint> route) =>
        VtProofRouteProgress.Last(lines, from, route);

    private static int? FirstRouteWaypointIndex(
        IReadOnlyList<string> lines,
        int from,
        IReadOnlyList<VtProofRoutePoint> route) =>
        VtProofRouteProgress.First(lines, from, route);

    /// <summary>One map coordinate unit is 240 metres of world distance.</summary>
    internal static double CoordinateDistanceMeters(
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
                _chat.Add(text);
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

    /// <summary>
    /// The fixture folder holds two different things and they belong in two
    /// different places: the three real VTank files (.usd, .utl, .af) go to
    /// the shared profile directory the plugin reads profiles from, and
    /// everything under <c>plugin-storage/</c> goes to the plugin's own
    /// state directory at exactly the key it is filed under. That is where
    /// MossTank's companion document to the .usd lives — the Items page
    /// naming the run's wand among it — because the .usd format has no
    /// table that could carry it.
    /// </summary>
    private static (string VtankRoot, string PluginStorageRoot)
        StageProfileFixtures(string root)
    {
        const string pluginStateFolder = "plugin-storage";
        string source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "vt-proof");
        Assert.True(
            Directory.Exists(source),
            $"The proof profile fixtures are missing: {source}");
        string vtankRoot = Path.Combine(root, "vtank");
        string pluginStorageRoot = Path.Combine(root, pluginStateFolder);
        Directory.CreateDirectory(Path.Combine(vtankRoot, "navs"));
        Directory.CreateDirectory(Path.Combine(vtankRoot, "metas"));
        Directory.CreateDirectory(pluginStorageRoot);
        string pluginStatePrefix = pluginStateFolder + Path.DirectorySeparatorChar;
        foreach (string file in Directory.EnumerateFiles(
            source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            bool pluginState = relative.StartsWith(
                pluginStatePrefix, StringComparison.Ordinal);
            string destination = pluginState
                ? Path.Combine(
                    pluginStorageRoot, relative[pluginStatePrefix.Length..])
                : Path.Combine(vtankRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
        Assert.True(
            File.Exists(Path.Combine(
                pluginStorageRoot,
                "acdream.mosstank",
                "profiles",
                "macro",
                "sidecar",
                SettingsProfileName + ".usd.json")),
            "The fixture's companion document to the settings profile is "
                + "missing; without it the profile's Items page is empty and "
                + "the macro stops on its first pass.");
        return (vtankRoot, pluginStorageRoot);
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
            // The plugin's load context unloads asynchronously, so the
            // copied plugin assembly can still be mapped for a moment after
            // the session is gone. Cleaning up scratch files is housekeeping:
            // it must never throw over the run's own verdict, which is the
            // only thing this test exists to report.
            for (int attempt = 0; attempt < 50 && Directory.Exists(Path); attempt++)
            {
                try
                {
                    Directory.Delete(Path, recursive: true);
                    return;
                }
                catch (Exception error)
                    when (error is IOException or UnauthorizedAccessException)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    Thread.Sleep(20);
                }
            }

            if (Directory.Exists(Path))
                Console.Out.WriteLine($"vt-proof: scratch directory left behind: {Path}");
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

    /// <summary>Has this milestone been judged at all yet?</summary>
    internal bool Reported(string id) =>
        _entries.Exists(entry => entry.Id == id);

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

/// <summary>
/// Whether a run killed anything, taken from the plugin's own verdict
/// rather than from a list of death sentences kept here.
/// <para>
/// The server announces a death in one of dozens of ways — "You run it
/// through!", "its death is preceded by a sharp, stabbing pain!" — and the
/// plugin already owns the table that recognises all of them, because
/// recognising them is part of what it does: a kill resets the target's
/// attempt count and retires it. A second, shorter list here watched for
/// four sentences the server never sent, so the milestone reported nothing
/// died in a run where both monsters did. So the milestone reads the
/// verdict, and the verdict quotes the sentence it was reached from.
/// </para>
/// </summary>
internal static class VtProofKillEvidence
{
    /// <summary>The caster's verdict and the attack executor's, in that order.</summary>
    private static readonly string[] Verdicts =
    [
        "SpellCaster: Spell kill reset (",
        "AttackExecutor: Kill blow (",
    ];

    internal static string? FirstKillLine(IReadOnlyList<string> chat)
    {
        ArgumentNullException.ThrowIfNull(chat);
        foreach (string line in chat)
        {
            foreach (string verdict in Verdicts)
            {
                if (line.Contains(verdict, StringComparison.Ordinal))
                    return line;
            }
        }
        return null;
    }
}

/// <summary>
/// Whether a run's chat shows a buff pass that really ran and really
/// finished, read out of three of the plugin's own lines.
/// <para>
/// The planner is chatty — it says which buff it would like, on whom, and
/// how long it is covered for — and none of that means anything left the
/// character: the milestone used to accept exactly those lines, so it
/// stayed green through a run whose macro had already stopped. The cast
/// line is written only after the caster surface accepted the request, and
/// the tracking line only once the caster opened its record of that same
/// cast, so the pair cannot be produced by planning alone.
/// </para>
/// <para>
/// The pass ends when the buff rule declines because nothing is due. That
/// is the milestone's own wording, and it is a much better end signal than
/// silence: a stalled macro is silent too.
/// </para>
/// </summary>
internal readonly record struct VtProofCastEvidence(
    string? CastLine,
    bool CasterBeganTracking,
    string? NothingDueLine)
{
    internal bool IsCast => CastLine is not null && CasterBeganTracking;

    internal bool IsSettledPass => IsCast && NothingDueLine is not null;

    internal static VtProofCastEvidence Read(IReadOnlyList<string> chat)
    {
        ArgumentNullException.ThrowIfNull(chat);
        string? castLine = null;
        string? nothingDue = null;
        bool tracking = false;
        foreach (string line in chat)
        {
            castLine ??= line.Contains("Casting: ", StringComparison.Ordinal)
                ? line
                : null;
            tracking |= line.Contains("SpellCaster: Begin", StringComparison.Ordinal);
            // Only the buff rule's own decline counts: the same wording from
            // another rule would say nothing about buffs.
            nothingDue ??= line.Contains(
                "(BuffSelf) declined: nothing is due", StringComparison.Ordinal)
                ? line
                : null;
        }
        return new VtProofCastEvidence(castLine, tracking, nothingDue);
    }

    internal string Explain() => CastLine is null
        ? "no spell was ever cast (no cast line)"
        : !CasterBeganTracking
            ? "a cast line appeared but the caster never began tracking it: "
                + CastLine
            : "the buff pass never reported that nothing was due; last it "
                + "said: " + CastLine;
}

internal readonly record struct VtProofRoutePoint(
    double EastWest,
    double NorthSouth,
    double Elevation);

/// <summary>
/// Which waypoint of the route the plugin says it is working on, read out of
/// the lines its navigation rule prints. The rule names the waypoint two ways
/// depending on whether it won the pass — the goal it is steering at while it
/// runs, the numbered waypoint while it declines — and both name the same
/// position in the route file. That is what "the macro came back on the
/// waypoint it had" is measured against: a death must not send the route back
/// to its first point.
/// </summary>
internal static class VtProofRouteProgress
{
    /// <summary>
    /// A waypoint's coordinates are printed to six decimals of a unit worth
    /// 240 metres, so a few centimetres is all the tolerance matching one
    /// needs — far below the spacing of any real route.
    /// </summary>
    private const double MatchMeters = 0.05d;

    internal static int? IndexFromLine(
        string line,
        IReadOnlyList<VtProofRoutePoint> route)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(route);

        const string numbered = "Waypoint ";
        int at = line.IndexOf(numbered, StringComparison.Ordinal);
        if (at >= 0)
        {
            int slash = line.IndexOf('/', at);
            if (slash > 0
                && int.TryParse(
                    line.AsSpan(at + numbered.Length, slash - at - numbered.Length),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int ordinal)
                && ordinal >= 1
                && ordinal <= route.Count)
            {
                return ordinal - 1;
            }
        }

        const string goal = "targ loc ";
        at = line.IndexOf(goal, StringComparison.Ordinal);
        if (at < 0)
            return null;
        int end = line.IndexOf(']', at);
        string[] parts =
            (end < 0 ? line[(at + goal.Length)..] : line[(at + goal.Length)..end])
            .Split(',');
        if (parts.Length < 2
            || !double.TryParse(
                parts[0].Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double eastWest)
            || !double.TryParse(
                parts[1].Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double northSouth))
        {
            return null;
        }
        for (int index = 0; index < route.Count; index++)
        {
            if (VtSessionProofLiveTests.CoordinateDistanceMeters(
                    eastWest,
                    northSouth,
                    route[index].EastWest,
                    route[index].NorthSouth)
                < MatchMeters)
            {
                return index;
            }
        }
        return null;
    }

    internal static int? Last(
        IReadOnlyList<string> lines,
        int from,
        IReadOnlyList<VtProofRoutePoint> route)
    {
        ArgumentNullException.ThrowIfNull(lines);
        int? found = null;
        for (int index = Math.Max(0, from); index < lines.Count; index++)
        {
            if (IndexFromLine(lines[index], route) is { } waypoint)
                found = waypoint;
        }
        return found;
    }

    internal static int? First(
        IReadOnlyList<string> lines,
        int from,
        IReadOnlyList<VtProofRoutePoint> route)
    {
        ArgumentNullException.ThrowIfNull(lines);
        for (int index = Math.Max(0, from); index < lines.Count; index++)
        {
            if (IndexFromLine(lines[index], route) is { } waypoint)
                return waypoint;
        }
        return null;
    }
}

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
    public void ABuffPassThatOnlyPlannedIsNotACast()
    {
        string[] chat =
        [
            "[MossTank] Buffing: Strength Self VII → Strength Self VII, covered for 0s of 60s",
            "[MossTank] Buffing: attribute +Acdream (1342177290) → Strength Self VII [family 12], covered for 0s of 60s",
            "[MossTank] Picked BuffSelf P: 60",
        ];

        VtProofCastEvidence evidence = VtProofCastEvidence.Read(chat);

        Assert.False(evidence.IsCast);
        Assert.Null(evidence.CastLine);
        Assert.Equal("no spell was ever cast (no cast line)", evidence.Explain());
    }

    [Fact]
    public void ACastLineWithTheCastersOwnRecordOfItCountsAsACast()
    {
        string[] chat =
        [
            "[MossTank] Buffing: Strength Self VII → Strength Self VII, covered for 0s of 60s",
            "[MossTank] Casting: Strength Self VII on 1342177290 (+Acdream)",
            "[MossTank] SpellCaster: Begin",
        ];

        VtProofCastEvidence evidence = VtProofCastEvidence.Read(chat);

        Assert.True(evidence.IsCast);
        Assert.Equal(
            "[MossTank] Casting: Strength Self VII on 1342177290 (+Acdream)",
            evidence.CastLine);
    }

    /// <summary>
    /// A pass that is still casting has not finished, however many spells it
    /// has got through; the buff rule saying nothing is due is what ends it.
    /// </summary>
    [Fact]
    public void ABuffPassStillCastingHasNotSettled()
    {
        string[] chat =
        [
            "[MossTank] Casting: Strength Self VII on 1342177290 (+Acdream)",
            "[MossTank] SpellCaster: Begin",
            "[MossTank] SpellCaster: Spell success reset (You cast Strength Self VII on yourself)",
        ];

        VtProofCastEvidence evidence = VtProofCastEvidence.Read(chat);

        Assert.True(evidence.IsCast);
        Assert.False(evidence.IsSettledPass);
        Assert.Contains(
            "never reported that nothing was due",
            evidence.Explain(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ABuffPassThatCastAndThenFoundNothingDueHasSettled()
    {
        string[] chat =
        [
            "[MossTank] Casting: Strength Self VII on 1342177290 (+Acdream)",
            "[MossTank] SpellCaster: Begin",
            "[MossTank] (BuffSelf) declined: nothing is due within 330s that this "
                + "character can cast (2054 self buffs known, 34 items carried)",
        ];

        VtProofCastEvidence evidence = VtProofCastEvidence.Read(chat);

        Assert.True(evidence.IsSettledPass);
        Assert.Contains(
            "nothing is due within 330s",
            evidence.NothingDueLine!,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Another rule declining for its own reasons is not the buff pass
    /// ending, and a pass that never cast anything has not run at all.
    /// </summary>
    [Fact]
    public void AnotherRulesDeclineDoesNotEndTheBuffPass()
    {
        string[] chat =
        [
            "[MossTank] Casting: Strength Self VII on 1342177290 (+Acdream)",
            "[MossTank] SpellCaster: Begin",
            "[MossTank] (RechargeSelfNormal) declined: nothing is due",
        ];

        VtProofCastEvidence evidence = VtProofCastEvidence.Read(chat);

        Assert.False(evidence.IsSettledPass);
    }

    [Fact]
    public void APassThatOnlyDeclinedNeverRan()
    {
        string[] chat =
        [
            "[MossTank] (BuffSelf) declined: nothing is due within 330s that this "
                + "character can cast (0 self buffs known, 0 items carried)",
        ];

        VtProofCastEvidence evidence = VtProofCastEvidence.Read(chat);

        Assert.False(evidence.IsSettledPass);
        Assert.Equal("no spell was ever cast (no cast line)", evidence.Explain());
    }

    [Fact]
    public void ACastLineWithoutTheCastersRecordOfItIsReportedAsSuch()
    {
        string[] chat =
            ["[MossTank] Casting: Strength Self VII on 1342177290 (+Acdream)"];

        VtProofCastEvidence evidence = VtProofCastEvidence.Read(chat);

        Assert.False(evidence.IsCast);
        Assert.Contains(
            "never began tracking it",
            evidence.Explain(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The two sentences a live run really produced, and the verdict the
    /// plugin reached from each. Neither sentence is in any list the harness
    /// keeps, which is the point: the harness reads the verdict.
    /// </summary>
    [Theory]
    [InlineData(
        "[MossTank] SpellCaster: Spell kill reset (Drudge Skulker's death is "
            + "preceded by a sharp, stabbing pain!)")]
    [InlineData(
        "[MossTank] AttackExecutor: Kill blow (You run Drudge Skulker through!)")]
    public void ThePluginsOwnKillVerdictIsWhatCountsAsAKill(string line)
    {
        string[] chat =
        [
            "[MossTank] Casting: Incantation of Force Bolt on 2147504356 (Drudge Skulker)",
            "Drudge Skulker's death is preceded by a sharp, stabbing pain!",
            line,
        ];

        Assert.Equal(line, VtProofKillEvidence.FirstKillLine(chat));
    }

    [Fact]
    public void TheServersOwnDeathSentenceAloneIsNotAKillVerdict()
    {
        string[] chat =
        [
            "[MossTank] Casting: Incantation of Force Bolt on 2147504356 (Drudge Skulker)",
            "You run Drudge Skulker through!",
            "[MossTank] SpellCaster: Cast result timeout",
        ];

        Assert.Null(VtProofKillEvidence.FirstKillLine(chat));
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

    private static readonly VtProofRoutePoint[] ProgressRoute =
    [
        new(33.7900150d, 42.1057993d, 0.4013750d),
        new(33.8233483d, 42.1057993d, 0.4013750d),
        new(33.8233483d, 42.1391327d, 0.4013750d),
        new(33.7900150d, 42.1391327d, 0.4013750d),
    ];

    [Fact]
    public void TheWinningNavigationLineNamesItsWaypointByPosition()
    {
        const string line =
            "[MossTank] (NavigateRouteIdle) Running [targ range 6.087, "
            + "targ loc 33.823348, 42.139133, 0.401375 ]";

        Assert.Equal(2, VtProofRouteProgress.IndexFromLine(line, ProgressRoute));
    }

    [Fact]
    public void TheDecliningNavigationLineNamesItsWaypointByNumber()
    {
        const string line =
            "[MossTank] (NavigateRouteIdle) declined: Waypoint 3/4: 5.9m";

        Assert.Equal(2, VtProofRouteProgress.IndexFromLine(line, ProgressRoute));
    }

    [Fact]
    public void APositionOffTheRouteIsNotAWaypoint()
    {
        const string line =
            "[MossTank] (NavigateRouteIdle) Running [targ range 6.087, "
            + "targ loc 33.900000, 42.139133, 0.401375 ]";

        Assert.Null(VtProofRouteProgress.IndexFromLine(line, ProgressRoute));
        Assert.Null(VtProofRouteProgress.IndexFromLine("Picked Attack P: 34", ProgressRoute));
    }

    [Fact]
    public void RouteProgressReadsTheLastAndFirstWaypointFromAnOffset()
    {
        string[] lines =
        [
            "[MossTank] (NavigateRouteIdle) declined: Waypoint 1/4: 9.0m",
            "[MossTank] Picked Attack P: 34",
            "[MossTank] (NavigateRouteIdle) declined: Waypoint 2/4: 5.0m",
            "[MossTank] (NavigateRouteIdle) declined: Waypoint 4/4: 2.0m",
        ];

        Assert.Equal(3, VtProofRouteProgress.Last(lines, 0, ProgressRoute));
        Assert.Equal(0, VtProofRouteProgress.First(lines, 0, ProgressRoute));
        Assert.Equal(1, VtProofRouteProgress.First(lines, 1, ProgressRoute));
        Assert.Null(VtProofRouteProgress.First(["nothing here"], 0, ProgressRoute));
    }
}
