using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using AcDream.Core.Items;
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
    /// <para>
    /// The elevation is the one the character actually stands at there, and
    /// it has to be: the server places a teleported character exactly where
    /// it was asked to and never lets it fall, while the client's own physics
    /// does let it fall. Name a point in the air and the two views are that
    /// far apart from the first instant, and they only come back together
    /// when the character next moves -- the client reports where it stands
    /// once, when it lands, and a server still finishing the teleport throws
    /// that one report away without a word. Everything the server then places
    /// relative to the character -- a staged monster among it -- hangs in the
    /// air with it. The point used to name an elevation 2.33 m above this
    /// ground, which is why the staged fight sometimes began with two
    /// monsters out of reach overhead.
    /// </para>
    /// </summary>
    private const string ArenaTeleport =
        "@teleloc A9B40029 133.603592 17.391838 94.005005 1 0 0 0";

    /// <summary>
    /// How many of the character's own corpses one sweep will remove. High
    /// enough to clear a backlog in one run, bounded so a corpse that refuses
    /// to go cannot spin the run.
    /// </summary>
    private const int MaximumSweptCorpses = 30;

    /// <summary>
    /// Every character the proof account family plays. The start-of-run sweep
    /// removes a corpse belonging to NONE of them — the previous run's
    /// monsters — so this list is the guard that keeps a player's corpse out
    /// of the sweep's reach, and every character a run can log in as has to
    /// be in it.
    /// </summary>
    private static readonly string[] SweptCharacterNames =
        ["Acdream", "Horan"];

    /// <summary>
    /// How far back the loot milestone stands the character before it judges.
    /// Far enough that the corpse is outside the server's own reach for it —
    /// which is a little under three metres between these two bodies — and
    /// close enough to stay inside the looter's five-metre open step, so what
    /// the milestone proves is that the character walks the difference.
    /// </summary>
    private const double CorpseStepBackMeters = 4.5d;

    /// <summary>
    /// How far the corpse has to have lain from the character when the loot
    /// milestone began for the open to have needed a walk at all. The server
    /// allows an open from a little under three metres between these two
    /// bodies, so below this the pass would be proving a turn.
    /// </summary>
    private const double CorpseMinimumOpenDistanceMeters = 3.0d;

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
    private const string RynthifyLootProfileName = "rynthify-rynth-emitted-loot";
    private const string RynthifyMetaProfileName = "rynthify-controlled-meta";

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
        // The server spells a privileged character's name with a marker
        // and looks one up with or without it, but once the character is
        // made to appear as an ordinary player the marked spelling stops
        // resolving. The plain spelling resolves either way, so that is
        // the one the run names in a command.
        const string plainCharacter = "Acdream";
        var ledger = new VtProofLedger();
        RynthifyProofInput rynthify = RynthifyProofInput.FromEnvironment();
        var rynthifyProgress = new RynthifyProofProgress();

        using var temporary = new TemporaryDirectory();
        string pluginRoot = InstallRealMossTankPlugin(temporary.Path);
        Assert.True(Directory.Exists(pluginRoot));
        (string vtankRoot, string pluginStorageRoot) =
            StageProfileFixtures(temporary.Path, rynthify);
        string routeFixture = File.ReadAllText(Path.Combine(
            vtankRoot, "navs", RouteProfileName + ".af"));
        IReadOnlyList<VtProofRoutePoint> route =
            VtProofRouteFixture.ReadPoints(routeFixture);
        Assert.True(route.Count >= 3, "The proof route fixture must hold at least three points.");
        Assert.True(
            VtProofRouteFixture.IsCircular(routeFixture),
            "The proof's ordered route acceptance requires a circular fixture.");
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
                    ["lootProfile"] = rynthify.LootProfileName,
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

            void Pump(TimeSpan duration, Action? observe = null)
            {
                DateTime deadline = DateTime.UtcNow + duration;
                while (DateTime.UtcNow < deadline)
                {
                    session.Tick(0.1d);
                    observe?.Invoke();
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

            void Stage(string command, Action? observe = null)
            {
                SubmitOutcome outcome = session.SubmitConsoleLine(command);
                staged.Add($"{command} -> {outcome}");
                Pump(TimeSpan.FromSeconds(1.5d), observe);
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

            /// <summary>
            /// Ask the running expression host for the meta engine's current
            /// state. This observes the executed engine rather than inferring
            /// a transition from the file that was loaded.
            /// </summary>
            string? ReadExpressionResult(string expression)
            {
                const string marker = "Result: ";
                int before = observed.ChatCount;
                Stage("/vt mexec " + expression);
                return observed.SnapshotChat()
                    .Skip(before)
                    .Select(line =>
                    {
                        int at = line.IndexOf(marker, StringComparison.Ordinal);
                        return at < 0 ? null : line[(at + marker.Length)..].Trim();
                    })
                    .FirstOrDefault(static value => value is not null);
            }

            string? ReadMetaState() => ReadExpressionResult("vtgetmetastate[]");
            string? ReadMetaProfile() => ReadExpressionResult("vtgetmeta[]");

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
                    "last 40 loot and corpse lines:",
                    Indent(Tail(
                        [.. chat.Where(line =>
                            !IsPassSpam(line)
                            && (line.Contains("Loot", StringComparison.Ordinal)
                                || line.Contains("Corpse", StringComparison.Ordinal)
                                || line.Contains("full", StringComparison.Ordinal)))],
                        40)),
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
                if (rynthify.Enabled)
                {
                    ledger.NotReached(
                        "RYNTHIFY",
                        RynthifyProofProgress.Title,
                        "the session never entered the world");
                }
                FinishAndReport(session, statusPath, ledger, Evidence, output, null);
                return;
            }

            // The channels came up with autostart, before the first pass.
            // Re-asserting them from here would be harmless and would also
            // be too late, so this only gives the autostart edge a tick or
            // two and then reads the state back into the record.
            Pump(TimeSpan.FromSeconds(3d));
            Stage("/vt log");

            string? rynthBeforeBoost = null;
            string? rynthApproach = null;
            bool rynthArenaReach = false;
            string? rynthSelectedBeforeStart = null;
            VtProofProfileBinding rynthBindingBeforeStart = default;

            // ---- stage the arena ----------------------------------------------
            // The proof character is a privileged one, and the server spells
            // a privileged name with a marker in front of it -- except in the
            // "killed by" line it writes into a corpse, which it deliberately
            // strips. The two never match, so such a character can never be
            // recorded as the killer of its own kill and can never loot it.
            // The server's own answer to that is to make the character appear
            // as an ordinary player, which drops the marker from the name
            // everywhere; it is remembered across logins, so the run asks for
            // it every time and it costs nothing once it is set.
            IAutomationSurface profileAutomation =
                session.Plugins.Host.Automation;
            string identityBefore = VtProofProfileIdentity.Describe(profileAutomation);
            // The automation surface can become available before its complete
            // character identity arrives, and the cloak command may also
            // change the displayed name. RYNTHIFY waits for that final identity
            // before making its one deliberate set of profile selections.
            int chatBeforeStableProfiles = observed.ChatCount;
            bool identityStable = true;
            if (rynthify.Enabled)
            {
                Stage("@cloak player");
                identityStable = WaitUntil(
                    TimeSpan.FromSeconds(10d),
                    () => VtProofProfileIdentity.IsReady(
                        profileAutomation,
                        plainCharacter));
                staged.Add(
                    $"profile identity {identityBefore} -> "
                        + VtProofProfileIdentity.Describe(profileAutomation)
                        + $"; stable={identityStable}");
                chatBeforeStableProfiles = observed.ChatCount;
                if (identityStable)
                {
                    // RYNTHIFY makes one deliberate selection after the final
                    // character binding. The ordinary proof continues to
                    // judge the selections made by autostart.
                    Stage("/vt settings load " + SettingsProfileName + ".usd");
                    Stage("/vt loot load " + rynthify.LootProfileName + ".utl");
                    Stage("/vt nav load " + RouteProfileName + ".af");
                    // The controlled graph deliberately starts life as a native
                    // .met in plugin imports. Loading it here exercises the same
                    // importer a player uses, after character rebinding is done.
                    Stage("/vt meta load " + RynthifyMetaProfileName + ".met");
                    rynthSelectedBeforeStart = ReadMetaProfile();
                    rynthBindingBeforeStart = VtProofProfileBinding.Read(
                        vtankRoot,
                        profileAutomation.Character.WorldName,
                        profileAutomation.Character.Name);
                    rynthBeforeBoost = ReadOption("LootPriorityBoost");
                    // This is a controlled-arena reach, not a compatibility
                    // claim about the owner's ordinary approach profile.
                    Stage("/vt opt set CorpseApproachRange-Max 0.08");
                    rynthApproach = ReadOption("CorpseApproachRange-Max");
                    rynthArenaReach = double.TryParse(
                        rynthApproach,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out double approachLandblocks)
                        && (approachLandblocks * 240d) > 15d;
                }
            }

            // ---- P2: the selected fixture profiles are active -----------------
            {
                string[] pluginMessages = PluginMessages(diagnosticsOutput.ToString());
                string[] autostartLines = pluginMessages
                    .Where(static line => line.Contains("Autostart:", StringComparison.Ordinal))
                    .ToArray();
                string[] profileChat = rynthify.Enabled
                    ? observed.SnapshotChat().Skip(chatBeforeStableProfiles).ToArray()
                    : observed.SnapshotChat();
                bool NamesProfile(string profile) =>
                    pluginMessages.Concat(profileChat).Any(line =>
                        line.Contains(profile, StringComparison.OrdinalIgnoreCase)
                        && (line.Contains("oaded", StringComparison.Ordinal)
                            || line.Contains("Imported", StringComparison.Ordinal)));
                var selectedProfiles = new List<string>
                {
                    SettingsProfileName + ".usd",
                    rynthify.LootProfileName + ".utl",
                    RouteProfileName + ".af",
                };
                string[] unreported = selectedProfiles
                    .Where(profile => !NamesProfile(
                        Path.GetFileNameWithoutExtension(profile)))
                    .ToArray();
                bool selectedMeta = !rynthify.Enabled
                    || string.Equals(
                        rynthSelectedBeforeStart,
                        RynthifyMetaProfileName,
                        StringComparison.Ordinal);
                bool selectedBinding = !rynthify.Enabled
                    || rynthBindingBeforeStart.Matches(
                        SettingsProfileName + ".usd",
                        rynthify.LootProfileName + ".utl",
                        "navs/" + RouteProfileName + ".af",
                        "metas/" + RynthifyMetaProfileName + ".af");
                if (identityStable
                    && unreported.Length == 0
                    && selectedMeta
                    && selectedBinding
                    && autostartLines.Length == 0)
                {
                    ledger.Pass(
                        "P2",
                        "the fixture settings, loot and route profiles are loaded",
                        rynthify.Enabled
                            ? "stable character identity; selected binding="
                                + $"{rynthBindingBeforeStart}; "
                                + $"meta query={rynthSelectedBeforeStart}"
                            : "each autostart profile reported a load line");
                }
                else
                {
                    string problem = !identityStable
                        ? "plain-player identity was not observed"
                        : unreported.Length > 0
                            ? $"no post-identity load line for: {string.Join(", ", unreported)}"
                            : !selectedMeta
                                ? $"selected meta was {rynthSelectedBeforeStart ?? "unread"}"
                                : !selectedBinding
                                    ? $"selected binding was {rynthBindingBeforeStart}"
                                : "the plugin reported an autostart problem";
                    ledger.Fail(
                        "P2",
                        "the fixture settings, loot and route profiles are loaded",
                        problem,
                        (autostartLines.Length > 0
                            ? "autostart lines:" + Environment.NewLine
                                + Indent(autostartLines) + Environment.NewLine
                            : string.Empty)
                            + Evidence());
                }
            }

            if (rynthify.Enabled && ledger.Failed("P2"))
            {
                rynthifyProgress.MetaEvidence =
                    $"profile readiness failed; identity {identityBefore} -> "
                    + VtProofProfileIdentity.Describe(profileAutomation)
                    + $"; selected before start="
                    + (rynthSelectedBeforeStart ?? "unread");
                foreach ((string id, string title) in RemainingMilestones("P2"))
                {
                    ledger.NotReached(
                        id,
                        title,
                        "the opt-in profile identity or selection was not ready");
                }
                FinishAndReport(
                    session,
                    statusPath,
                    ledger,
                    Evidence,
                    output,
                    rynthifyProgress);
                return;
            }

            if (!rynthify.Enabled)
                Stage("@cloak player");

            // The proof runs against a live server and has to leave it the way
            // it found it. Every death used to leave a corpse standing in the
            // arena for as long as the server keeps one, and the server stops
            // accepting new ones past its per-player ceiling -- so the run
            // sweeps its own leavings at both ends and, from here on, dies
            // without leaving a corpse at all.
            int SweepArena(string when, bool alsoForeign = false)
            {
                // Monsters first: a live one would fight the sweep, and their
                // own corpses rot on their own.
                Stage("@smite all");
                var removed = new List<string>();
                ILootAutomation corpses = session.Plugins.Host.Automation.Loot;
                for (int round = 0; round < MaximumSweptCorpses; round++)
                {
                    IReadOnlyList<PluginLootContainer> reported =
                        corpses.CaptureCorpses(float.MaxValue);
                    // A monster corpse rots on its own, but not before the
                    // next run has begun and its looter has found it standing
                    // where this run left it. At the START of a run they go
                    // too; at the end they are left to rot, which is what the
                    // server does with them anyway.
                    PluginLootContainer? target =
                        VtProofArena.NextOwnCorpse(reported, character)
                        ?? (alsoForeign
                            ? VtProofArena.NextForeignCorpse(
                                reported,
                                SweptCharacterNames)
                            : null);
                    if (target is not { } own)
                        break;
                    uint corpseId = own.ObjectId;
                    // The server deletes what the character last assessed, so
                    // the assessment IS the selection, and the run will not
                    // ask for a deletion it has not aimed.
                    _ = corpses.Identify(corpseId);
                    if (!WaitUntil(
                            TimeSpan.FromSeconds(5d),
                            () => corpses.Appraisal.CurrentObjectId == corpseId))
                    {
                        removed.Add($"0x{corpseId:X8} could not be selected");
                        break;
                    }
                    Stage("@delete");
                    bool gone = WaitUntil(
                        TimeSpan.FromSeconds(5d),
                        () => corpses.CaptureCorpses(float.MaxValue)
                            .All(corpse => corpse.ObjectId != corpseId));
                    removed.Add($"0x{corpseId:X8} {(gone ? "deleted" : "stayed")}");
                    if (!gone)
                        break;
                }
                staged.Add(
                    $"arena swept ({when}) -> "
                        + (removed.Count == 0
                            ? "nothing to remove"
                            : string.Join(", ", removed)));
                return removed.Count;
            }

            Stage(ArenaTeleport);
            bool inArena = WaitUntil(
                TimeSpan.FromSeconds(20d),
                () => DistanceMetersFromArena(session) < 40d);
            Pump(TimeSpan.FromSeconds(2d));
            staged.Add($"arena reached -> {inArena} ({PositionText(session)})");
            _ = SweepArena("before the run", alsoForeign: true);

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

            // ---- dress the character -------------------------------------------
            // A Bane cast at the character is not worn by the character: the
            // server puts it on the armour and clothing the character has on,
            // and a character wearing none is answered with nothing at all —
            // no enchantment, no result line, not even a refusal. The buff
            // rule can then never record coverage for that row, re-casts it
            // on the next pass, and the buff pass has no way to settle. So
            // the run dresses the character for the same reason it stages the
            // wand, and says so when it could not.
            foreach (PluginEquipmentItem piece in
                VtProofVestments.NotYetWorn(OwnedEquipment(session)))
            {
                PluginEquipmentCommandResult worn =
                    session.Plugins.Host.Automation.Equipment.Equip(piece.ObjectId);
                uint pieceId = piece.ObjectId;
                _ = WaitUntil(
                    TimeSpan.FromSeconds(10d),
                    () => OwnedEquipment(session)
                        .Any(item => item.ObjectId == pieceId && item.IsEquipped));
                staged.Add($"wear '{piece.Name}' -> {worn.Status}");
            }
            staged.Add(
                "vestments worn -> "
                    + VtProofVestments.IsDressed(OwnedEquipment(session))
                    + "; "
                    + string.Join(
                        ", ",
                        OwnedEquipment(session)
                            .Where(VtProofVestments.IsWorn)
                            .Select(static item => item.Name)
                            .DefaultIfEmpty("nothing")));

            Stage("/vt start");
            bool macroStarted = WaitUntil(
                TimeSpan.FromSeconds(20d),
                () => MentionsAny(observed.SnapshotChat(), "Macro started."));
            staged.Add($"macro started -> {macroStarted}");

            // ---- RYNTHIFY meta: run only inside a real macro pass --------------
            // Meta evaluation requires a running macro. Enabling and
            // observing it before /vt start
            // can only prove that an option was stored, never that a rule ran.
            if (RynthifyProofProgress.CanEnableMeta(
                    rynthify.Enabled,
                    macroStarted))
            {
                Stage("/vt opt set EnableMeta True");
                bool changed = WaitUntil(
                    TimeSpan.FromSeconds(10d),
                    () => string.Equals(
                        ReadOption("LootPriorityBoost"),
                        "True",
                        StringComparison.Ordinal));
                string? state = ReadMetaState();
                string? selected = ReadMetaProfile();
                VtProofProfileBinding bindingAfterStart = VtProofProfileBinding.Read(
                    vtankRoot,
                    profileAutomation.Character.WorldName,
                    profileAutomation.Character.Name);
                rynthifyProgress.MetaExecuted = changed
                    && string.Equals(state, "RynthReady", StringComparison.Ordinal)
                    && string.Equals(
                        rynthSelectedBeforeStart,
                        RynthifyMetaProfileName,
                        StringComparison.Ordinal)
                    && string.Equals(
                        selected,
                        RynthifyMetaProfileName,
                        StringComparison.Ordinal)
                    && string.Equals(
                        rynthBeforeBoost,
                        "False",
                        StringComparison.Ordinal)
                    && bindingAfterStart.Equals(rynthBindingBeforeStart)
                    && rynthArenaReach;
                rynthifyProgress.MetaEvidence =
                    $"native {RynthifyMetaProfileName}.met; selected="
                    + $"{rynthSelectedBeforeStart ?? "unread"}->"
                    + $"{selected ?? "unread"}; state={state ?? "unread"}; "
                    + $"binding={rynthBindingBeforeStart}->{bindingAfterStart}; "
                    + $"LootPriorityBoost={rynthBeforeBoost ?? "unread"}->"
                    + (changed ? "True" : ReadOption("LootPriorityBoost") ?? "unread")
                    + $"; controlled arena corpse approach={rynthApproach ?? "unread"}";
                staged.Add("rynthify meta -> " + rynthifyProgress.MetaEvidence);
            }
            else if (rynthify.Enabled)
            {
                rynthifyProgress.MetaEvidence =
                    $"native {RynthifyMetaProfileName}.met was imported, but the "
                    + "macro start was not observed; meta was not enabled";
            }

            if (rynthify.Enabled && !rynthifyProgress.MetaExecuted)
            {
                Stage("/vt stop");
                foreach ((string id, string title) in RemainingMilestones("P2"))
                {
                    ledger.NotReached(
                        id,
                        title,
                        "the opt-in native meta did not reach its required state and option");
                }
                FinishAndReport(
                    session,
                    statusPath,
                    ledger,
                    Evidence,
                    output,
                    rynthifyProgress);
                return;
            }

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

            uint[] rynthFightCorpseIds = [];
            // ---- P4: a fight -- attack rule active, a kill observed ------------
            // The server spawns a staged monster where IT believes the
            // character stands, so the pair of positions below is the only
            // place the run can see the two views apart. A monster hanging in
            // the air above the character is the server holding a stale
            // position, not a combat fault, and reading that off the ledger
            // beats reconstructing it afterwards.
            {
                int chatBeforeFight = observed.ChatCount;
                uint[] corpseIdsBeforeFight = session.Plugins.Host.Automation.Loot
                    .CaptureCorpses(float.MaxValue)
                    .Select(static corpse => corpse.ObjectId)
                    .ToArray();
                uint[] before = SpawnedGuids(session);
                // The looter stays down through the fight: the loot milestone
                // wants the corpse still lying there, unopened, when it starts,
                // so it can stand the character away from it first.
                Stage("/vt opt set EnableLooting False");
                Stage("@create " + MonsterWeenie);
                Stage("@create " + MonsterWeenie);
                Pump(TimeSpan.FromSeconds(2d));
                string report =
                    "monsters staged -> character at "
                        + PositionText(session)
                        + "; spawned: "
                        + string.Join("; ", SpawnedSince(session, before));
                staged.Add(report);
                // Printed whether or not the milestone passes: a pass that
                // staged the fight in the air is luck, not a pass, and the
                // only way to tell them apart is to see the two positions.
                output.WriteLine("VT-PROOF | P4-staging | " + report);
                Console.Out.WriteLine("VT-PROOF | P4-staging | " + report);
                Console.Out.Flush();
                bool attackRule = WaitUntil(
                    TimeSpan.FromSeconds(45d),
                    () => MentionsAny(
                        observed.SnapshotChat(),
                        "Picked Attack P:", "(Attack) Running"));
                int requiredKills = rynthify.Enabled ? 2 : 1;
                bool killed = WaitUntil(
                    rynthify.Enabled
                        ? TimeSpan.FromSeconds(90d)
                        : TimeSpan.FromSeconds(45d),
                    () =>
                    {
                        if (!rynthify.Enabled)
                        {
                            return VtProofKillEvidence.FirstKillLine(
                                observed.SnapshotChat()) is not null;
                        }
                        bool enoughVerdicts = VtProofKillEvidence.Lines(
                                observed.SnapshotChat().Skip(chatBeforeFight).ToArray())
                            .Length >= requiredKills;
                        return enoughVerdicts
                            && session.Plugins.Host.Automation.Loot
                                .CaptureCorpses(float.MaxValue)
                                .Count(corpse => !corpseIdsBeforeFight.Contains(
                                    corpse.ObjectId)) >= 2;
                    });
                string[] killLines = VtProofKillEvidence.Lines(
                    observed.SnapshotChat().Skip(chatBeforeFight).ToArray());
                uint[] newCorpseIds =
                    [.. session.Plugins.Host.Automation.Loot
                        .CaptureCorpses(float.MaxValue)
                        .Where(corpse => !corpseIdsBeforeFight.Contains(corpse.ObjectId))
                        .Select(static corpse => corpse.ObjectId)
                        .Distinct()
                        .Order()];
                if (rynthify.Enabled)
                    rynthFightCorpseIds = newCorpseIds;
                if (rynthify.Enabled)
                {
                    rynthifyProgress.TwoKills =
                        RynthifyProofProgress.HasTwoRealKills(
                            killLines,
                            newCorpseIds);
                    rynthifyProgress.KillEvidence =
                        $"plugin kill verdicts={killLines.Length}: "
                        + string.Join(" / ", killLines.Take(2))
                        + "; new corpse ids="
                        + VtProofLootEvidence.Hex(newCorpseIds);
                }
                if (attackRule && killed)
                {
                    ledger.Pass(
                        "P4",
                        "the attack rule wins the loop and a kill is observed",
                        $"attack rule active; {killLines.Length} kill verdict(s): "
                            + string.Join(" / ", killLines.Take(requiredKills)));
                }
                else
                {
                    ledger.Fail(
                        "P4",
                        "the attack rule wins the loop and a kill is observed",
                        attackRule
                            ? $"the attack rule ran but only {killLines.Length} of "
                                + $"{requiredKills} required kill(s) were observed"
                            : "the attack rule never became the active rule",
                        Evidence());
                }
            }

            // The fight is over before the loot milestone begins. A second
            // drudge still alive would close on the character and die at its
            // feet DURING the milestone, and that corpse -- the character's
            // own kill, a quarter of a metre away -- is the one the looter
            // would open, so nothing here would need a walk. A survivor put
            // down by the server is not the character's kill and the plain
            // rule leaves its corpse alone.
            Stage("@smite all");
            Pump(TimeSpan.FromSeconds(2d));

            // ---- P5: loot -- a corpse is opened and a decision is made ---------
            {
                // The corpse the fight left is the character's own kill, so
                // the profile's plain rule covers it: no switch is opened and
                // nothing is waited out. That only holds because the run
                // makes the character an ordinary one first -- see the
                // plain-player staging above; with the privileged marker on
                // the name the server's own corpse description can never
                // match it and this milestone is unreachable.
                int additionsBefore = observed.InventoryAdditions;
                // Everything this milestone judges has to happen inside it.
                // Watermark the transcript the same way the inventory is
                // watermarked above: a corpse the looter opened during the
                // buff phase used to satisfy all three sub-tests before the
                // window even started.
                int chatBefore = observed.SnapshotChat().Length;
                string[] Slice() => observed.SnapshotChat()
                    .Skip(chatBefore)
                    .ToArray();
                // The point of this milestone is the walk, so the character is
                // stood away from the corpse the fight left -- along the line
                // that already separates the two, not along a fixed compass
                // direction, because a drudge that died on the wrong side of
                // the caster would otherwise be stepped TOWARDS. A corpse
                // that happens to lie underfoot proves nothing about a corpse
                // that does not.
                ILootAutomation lootSurface =
                    session.Plugins.Host.Automation.Loot;
                PluginLootContainer[] RelevantCorpses() => lootSurface
                    .CaptureCorpses(float.MaxValue)
                    .Where(corpse => !rynthify.Enabled
                        || rynthFightCorpseIds.Contains(corpse.ObjectId))
                    .ToArray();
                // The corpse has to be PLACED before there is anything to step
                // away from: it falls a moment after the kill line, and the
                // client learns where it lies a moment after that.
                bool corpsePlaced = WaitUntil(
                    TimeSpan.FromSeconds(20d),
                    () => rynthify.Enabled
                        ? RelevantCorpses()
                            .Where(static corpse => corpse.HasPosition)
                            .Select(static corpse => corpse.ObjectId)
                            .Distinct()
                            .Count() >= 2
                        : RelevantCorpses().Any(static corpse => corpse.HasPosition));
                staged.Add($"corpse placed before the step -> {corpsePlaced}");
                RuntimeMovementSnapshot stood =
                    session.Runtime.Movement.Snapshot;
                PluginNavigationPosition standingAt = session.Plugins.Host
                    .Automation.Navigation.Snapshot.Position;
                if (VtProofArena.StepAwayFromCorpse(
                        standingAt,
                        RelevantCorpses(),
                        CorpseStepBackMeters,
                        out double awayX,
                        out double awayY))
                {
                    Stage(string.Create(
                        CultureInfo.InvariantCulture,
                        $"@teleloc A9B40029 {stood.Position.Frame.Origin.X + awayX:0.000} "
                            + $"{stood.Position.Frame.Origin.Y + awayY:0.000} "
                            + $"{stood.Position.Frame.Origin.Z:0.000} 1 0 0 0"));
                    // And the step has to have LANDED before the looter is let
                    // loose, or it opens the corpse from where the character
                    // still stands.
                    double wantedApart = CorpseStepBackMeters - 0.5d;
                    bool stepLanded = WaitUntil(
                        TimeSpan.FromSeconds(20d),
                        () =>
                        {
                            PluginLootContainer[] placed = RelevantCorpses()
                                .Where(static corpse => corpse.HasPosition)
                                .ToArray();
                            return (!rynthify.Enabled || placed.Length >= 2)
                                && placed.All(corpse => corpse.Distance >= wantedApart);
                        });
                    staged.Add(string.Create(
                        CultureInfo.InvariantCulture,
                        $"stepped {Math.Sqrt((awayX * awayX) + (awayY * awayY)):0.00} m away from the nearest corpse; "
                            + $"landed -> {stepLanded}; nearest corpse now "
                            + $"{RelevantCorpses().Where(static c => c.HasPosition).Select(static c => (double)c.Distance).DefaultIfEmpty(-1d).Min():0.00} m"));
                }
                else
                {
                    staged.Add("no step: no placed corpse to step away from, or already far enough");
                }
                // Where every corpse lay when the milestone opened. The
                // evidence quotes this for the corpse the looter chose, and
                // the pass turns on it: a corpse already within the server's
                // reach is opened where it stands, and an open that needed no
                // walk is not what this milestone is for.
                var layAt = new Dictionary<uint, double>();
                var pickupReceipts = new VtProofPickupReceipts();
                int requiredCorpses = rynthify.Enabled ? 2 : 1;
                void SampleLootState()
                {
                    foreach (PluginLootContainer corpse in
                             lootSurface.CaptureCorpses(float.MaxValue))
                    {
                        _ = layAt.TryAdd(corpse.ObjectId, corpse.Distance);
                    }
                    pickupReceipts.Observe(
                        lootSurface.CurrentContainerId,
                        lootSurface.CaptureCurrentContents(),
                        lootSurface.LastInventoryCompletion);
                }

                // Now the looter may work. Sampling during the command's pump
                // matters: a fast first pickup must retain the corpse that
                // held its exact source item.
                SampleLootState();
                string? activeApproach = rynthify.Enabled
                    ? ReadOption("CorpseApproachRange-Max")
                    : null;
                bool hasActiveRange = double.TryParse(
                    activeApproach,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double activeApproachLandblocks);
                double activeApproachMeters = hasActiveRange
                    ? activeApproachLandblocks * 240d
                    : -1d;
                bool controlledRangeActive = hasActiveRange
                    && Math.Abs(activeApproachMeters - 19.2d) < 0.01d;
                PluginLootContainer[] beforeLoot = lootSurface
                    .CaptureCorpses(float.MaxValue)
                    .Where(corpse => rynthFightCorpseIds.Contains(corpse.ObjectId))
                    .ToArray();
                bool lootSetupReady = !rynthify.Enabled
                    || (controlledRangeActive
                        && rynthFightCorpseIds.Distinct().Count() >= requiredCorpses
                        && beforeLoot.Select(static corpse => corpse.ObjectId)
                            .Distinct().Count() >= requiredCorpses
                        && beforeLoot.All(corpse => corpse.HasPosition
                            && corpse.Distance <= activeApproachMeters));
                string beforeLootEvidence = rynthify.Enabled
                    ? $"active approach={activeApproach ?? "unread"} "
                        + $"({activeApproachMeters:0.00}m); "
                        + VtProofLootEvidence.DescribeCorpses(
                            beforeLoot,
                            rynthFightCorpseIds,
                            lootSurface.CurrentContainerId,
                            lootSurface.Appraisal)
                    : string.Empty;
                staged.Add("loot setup before enable -> "
                    + (lootSetupReady ? "ready" : "not ready")
                    + (rynthify.Enabled ? "; " + beforeLootEvidence : string.Empty));

                bool opened = false;
                bool decided = false;
                bool taken = false;
                if (lootSetupReady)
                {
                    Stage("/vt opt set EnableLooting True", SampleLootState);
                    opened = WaitUntil(
                        rynthify.Enabled
                            ? TimeSpan.FromSeconds(180d)
                            : TimeSpan.FromSeconds(120d),
                        () =>
                        {
                            SampleLootState();
                            string[] chat = Slice();
                            return rynthify.Enabled
                                ? VtProofLootEvidence.OpenedCorpseIds(chat).Count
                                    >= requiredCorpses
                                : MentionsAny(chat, "LootCorpse: opening ");
                        });
                    decided = opened
                        && WaitUntil(
                            TimeSpan.FromSeconds(45d),
                            () =>
                            {
                                SampleLootState();
                                return CountMentioning(Slice(), "LootDecision: ")
                                    >= requiredCorpses;
                            });
                    taken = decided
                        && WaitUntil(
                            rynthify.Enabled
                                ? TimeSpan.FromSeconds(90d)
                                : TimeSpan.FromSeconds(45d),
                            () =>
                            {
                                SampleLootState();
                                return rynthify.Enabled
                                    ? pickupReceipts.CompletedCorpseIds.Count
                                            >= requiredCorpses
                                        && CountMentioning(Slice(), "LootPickup: took ")
                                            >= requiredCorpses
                                    : MentionsAny(Slice(), "LootPickup: took ");
                            });
                }
                // The looter is put down for the rest of the run, the same
                // reason the arena is cleared of monsters below: nothing after
                // this milestone loots, and a looter still working a corpse
                // would spend the route's window on it.
                Stage("/vt opt set EnableLooting False", SampleLootState);
                PluginLootContainer[] afterLoot = lootSurface
                    .CaptureCorpses(float.MaxValue)
                    .Where(corpse => rynthFightCorpseIds.Contains(corpse.ObjectId))
                    .ToArray();
                string[] lootChat = Slice();
                uint[] openedCorpseIds =
                    [.. VtProofLootEvidence.OpenedCorpseIds(lootChat)];
                uint[] completedCorpseIds =
                    [.. pickupReceipts.CompletedCorpseIds
                        .Where(openedCorpseIds.Contains)
                        .Where(id => !rynthify.Enabled || rynthFightCorpseIds.Contains(id))
                        .Order()];
                if (rynthify.Enabled)
                {
                    rynthifyProgress.TwoCorpsePickups =
                        RynthifyProofProgress.HasTwoCorpsePickups(
                            openedCorpseIds,
                            completedCorpseIds,
                            pickupReceipts.SuccessfulPickupCount);
                    rynthifyProgress.LootEvidence =
                        $"setup ready={lootSetupReady}; {beforeLootEvidence}; after: "
                        + VtProofLootEvidence.DescribeCorpses(
                            afterLoot,
                            rynthFightCorpseIds,
                            lootSurface.CurrentContainerId,
                            lootSurface.Appraisal)
                        + $"; opened corpse ids={VtProofLootEvidence.Hex(openedCorpseIds)}; "
                        + $"server-success pickup corpse ids="
                        + VtProofLootEvidence.Hex(completedCorpseIds)
                        + $"; acknowledgements={pickupReceipts.SuccessfulPickupCount}";
                }
                string openedLine =
                    FirstMentioning(lootChat, "LootCorpse: opening ");
                double lay = VtProofArena.CorpseDistanceWhenOpened(
                    openedLine,
                    layAt);
                bool walked = lay > CorpseMinimumOpenDistanceMeters;
                string layText = lay < 0d
                    ? "unknown"
                    : string.Create(CultureInfo.InvariantCulture, $"{lay:0.00} m");
                if (opened && decided && taken && walked)
                {
                    ledger.Pass(
                        "P5",
                        "a corpse is opened and at least one loot decision is made",
                        $"{CountMentioning(lootChat, "LootCorpse: opening ")} "
                            + "corpse open(s); first opened: "
                            + openedLine
                            + $"; it lay {layText} away when the milestone "
                            + "opened, so the character walked to it"
                            + "; first judged: "
                            + FirstMentioning(lootChat, "LootDecision: ")
                            + "; first taken: "
                            + FirstMentioning(lootChat, "LootPickup: took ")
                            + $"; {observed.InventoryAdditions - additionsBefore} "
                            + "inventory addition(s) during the milestone");
                }
                else
                {
                    ledger.Fail(
                        "P5",
                        "a corpse is opened and at least one loot decision is made",
                        !opened
                            ? VtProofLootEvidence.OpenedCountFailure(
                                openedCorpseIds.Length,
                                requiredCorpses)
                            : !decided
                                ? "the corpse was opened but no item was judged"
                                : !taken
                                    ? "the corpse was opened and judged but "
                                        + "nothing was taken out of it"
                                    : "the corpse was looted, but it lay "
                                        + $"{layText} away when the milestone "
                                        + "opened -- near enough for the server "
                                        + "to allow the open where the character "
                                        + "stood, so nothing here needed a walk",
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
                int chatBeforeRoute = observed.ChatCount;
                IReadOnlyList<int> targets = [];
                bool progressed = WaitUntil(
                    TimeSpan.FromSeconds(100d),
                    () =>
                    {
                        int index = NearestWaypointWithin(
                            session, route, WaypointArrivalMeters);
                        if (index >= 0 && (reached.Count == 0 || reached[^1] != index))
                            reached.Add(index);
                        if (!rynthify.Enabled)
                            return reached.Count >= 3;

                        targets = VtProofRouteProgress.TargetSequence(
                            observed.SnapshotChat(),
                            chatBeforeRoute,
                            route);
                        return reached.Count >= 3
                            && VtProofRouteProgress.HasCircularForwardAdvances(
                                targets,
                                route.Count,
                                requiredAdvances: 2);
                    });
                int advances = rynthify.Enabled
                    ? Math.Max(0, targets.Count - 1)
                    : Math.Max(0, reached.Count - 1);
                if (rynthify.Enabled)
                {
                    rynthifyProgress.RouteAdvanced = progressed;
                    rynthifyProgress.RouteEvidence = $"route target advances={advances}; "
                        + "target order="
                        + (targets.Count == 0 ? "none" : string.Join("->", targets))
                        + "; proximity order="
                        + (reached.Count == 0 ? "none" : string.Join("->", reached));
                }
                if (progressed)
                {
                    ledger.Pass(
                        "P6",
                        "the route advances by at least two waypoints",
                        rynthify.Enabled
                            ? $"circular target order {string.Join("->", targets)}; "
                                + $"proximity order {string.Join("->", reached)}"
                            : $"waypoint order {string.Join("->", reached)}");
                }
                else
                {
                    ledger.Fail(
                        "P6",
                        "the route advances by at least two waypoints",
                        rynthify.Enabled
                            ? $"no two-edge forward circular target sequence; targets "
                                + (targets.Count == 0 ? "none" : string.Join("->", targets))
                                + "; proximity "
                                + (reached.Count == 0 ? "none" : string.Join("->", reached))
                                + "; " + PositionText(session)
                            : $"only {advances} waypoint advance(s); "
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
            // The death is real; the corpse it would leave is not wanted. The
            // server's own switch for that also stops the death from dropping
            // what the character is carrying, so the staged caster stays put.
            Stage("@sticky on");
            {
                // What the four settings read BEFORE the death, so "untouched"
                // can mean untouched rather than "all four happen to be on".
                string[] deathSettings =
                    ["EnableBuffing", "EnableCombat", "EnableNav", "EnableLooting"];
                var beforeDeath = new Dictionary<string, string?>(StringComparer.Ordinal);
                foreach (string name in deathSettings)
                    beforeDeath[name] = ReadOption(name);

                Stage("@setvital health 1");
                // Everything below reads the chat from here on. The run has
                // already fought a monster by this point, and a kill line from
                // that fight would otherwise satisfy the death wait before the
                // smite has even landed.
                int chatBeforeDeath = observed.ChatCount;
                Stage("@smite " + plainCharacter);

                bool died = WaitUntil(
                    TimeSpan.FromSeconds(45d),
                    () => IsDead(session)
                        || MentionsAny(
                            [.. observed.SnapshotChat().Skip(chatBeforeDeath)],
                            "You were killed by"));

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
                        () => observed.SnapshotChat()
                            .Skip(chatBeforeDeath)
                            .Any(static line => line.Contains(
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

                // (c) nothing was reconfigured behind the player's back — the
                // same four settings read the same either side of the death,
                // through the plugin's own option command. Compared, not
                // assumed: an earlier milestone leaving one of them off must
                // not read as the death having turned it off.
                var afterDeath = new Dictionary<string, string?>(StringComparer.Ordinal);
                if (recovered)
                {
                    foreach (string name in deathSettings)
                        afterDeath[name] = ReadOption(name);
                }
                string[] changed = deathSettings
                    .Where(name => !string.Equals(
                        afterDeath.GetValueOrDefault(name),
                        beforeDeath.GetValueOrDefault(name),
                        StringComparison.Ordinal))
                    .ToArray();
                bool settingsUntouched = recovered && changed.Length == 0
                    && deathSettings.All(name => beforeDeath.GetValueOrDefault(name) is not null);

                // (d) starting again. The character respawned at its lifestone,
                // where the route is out of range, so it is put back first —
                // which is also how the run leaves the character somewhere sane
                // for the next one. The caster is still carried: the death ran
                // under the server's no-drop switch.
                staged.Add($"caster '{CasterItemName}' still owned after death -> "
                    + OwnedEquipmentNames(session).Contains(CasterItemName));
                // A character revives with its vitals near the floor, and the
                // recharge rule then wins every single pass trying to fix
                // that — it sits far above navigation, so nothing below it is
                // ever asked. Getting back on your feet is what a player does
                // too.
                Stage("@heal");
                Stage(ArenaTeleport);
                // The teleport has to have LANDED before the macro is started:
                // the anchor is taken from where the character stands at the
                // start, and "within forty metres" is already true up the
                // corridor the route walked. The arena teleport puts the
                // character within a metre of the arena point.
                bool backAtArena = WaitUntil(
                    TimeSpan.FromSeconds(20d),
                    () => DistanceMetersFromArena(session) < 3d);
                staged.Add($"back at the arena before the start -> {backAtArena} "
                    + $"({DistanceMetersFromArena(session):0.0} m)");

                // Starting again anchors the round to the nearest point the
                // character could walk to from where it stands — not to where
                // it left off, and not to the first point. So the expected
                // waypoint is measured here, from the character's own position,
                // and the death's own waypoint is only context.
                int expectedAnchor = NearestWaypointWithin(session, route, double.MaxValue);
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
                    // The start says where it put the round before the mover
                    // takes a step, so the reading begins at the start, not
                    // after the buff pause: a character that runs faster than
                    // the pass can log would otherwise be read at the point
                    // AFTER the one it anchored to.
                    int chatBeforeRoute = chatBeforeStart;
                    _ = WaitUntil(
                        TimeSpan.FromSeconds(60d),
                        () => FirstRouteWaypointIndex(
                            observed.SnapshotChat(), chatBeforeRoute, route) is not null);
                    waypointAfter = FirstRouteWaypointIndex(
                        observed.SnapshotChat(), chatBeforeRoute, route);
                    // The check is against the anchor the rule must pick, not
                    // against the waypoint before the death: the character has
                    // been put back at the arena since, and a rewind to the
                    // first point can only pass this when the first point really
                    // is the nearest one.
                    sameWaypoint = expectedAnchor >= 0
                        && waypointAfter == expectedAnchor;
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
                        + $"anchored-to-nearest={Yes(sameWaypoint)} "
                        + $"(interrupted-at={Waypoint(waypointBefore)} "
                        + $"nearest={expectedAnchor} "
                        + $"after={Waypoint(waypointAfter)}){routeSilence}");

                const string title =
                    "death stops the macro, changes no setting, and starting "
                    + "again anchors the round to the nearest point";
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
                                        $"{name} {beforeDeath.GetValueOrDefault(name) ?? "unread"}"
                                            + $" -> {afterDeath.GetValueOrDefault(name) ?? "unread"}"))
                        : !restarted
                            ? "no rule was picked after the macro was started again"
                        : "the route did not anchor to the point nearest the character";
                    ledger.Fail("P8", title, reason + "; " + verdict, Evidence());
                }

                // Leave the character alive and unencumbered by this run's
                // scenery: the next run starts from whatever this one left.
                Stage("@smite all");
                Stage("@heal");
            }

            _ = SweepArena("after the run");
            Stage("@sticky off");
            FinishAndReport(
                session,
                statusPath,
                ledger,
                Evidence,
                output,
                rynthify.Enabled ? rynthifyProgress : null);
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
            if (rynthify.Enabled && !ledger.Reported("RYNTHIFY"))
            {
                ledger.NotReached(
                    "RYNTHIFY",
                    RynthifyProofProgress.Title,
                    "the run threw: " + reason);
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
        ITestOutputHelper output,
        RynthifyProofProgress? rynthify)
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

        rynthify?.Report(ledger, evidence);

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
    private static IReadOnlyList<PluginEquipmentItem> OwnedEquipment(
        HeadlessSessionHost session)
    {
        IEquipmentAutomation equipment = session.Plugins.Host.Automation.Equipment;
        return equipment.IsAvailable ? equipment.CaptureOwnedEquipment() : [];
    }

    private static string[] OwnedEquipmentNames(HeadlessSessionHost session)
    {
        IEquipmentAutomation equipment = session.Plugins.Host.Automation.Equipment;
        return equipment.IsAvailable
            ? [.. equipment.CaptureOwnedEquipment()
                .Select(static item => item.Name)
                .Order(StringComparer.Ordinal)]
            : [];
    }

    /// <summary>How many lines carry the needle, for the record.</summary>
    private static int CountMentioning(IReadOnlyList<string> lines, string needle)
    {
        int count = 0;
        for (int index = 0; index < lines.Count; index++)
        {
            if (lines[index].Contains(needle, StringComparison.Ordinal))
                count++;
        }
        return count;
    }

    /// <summary>The first line carrying the needle, for the record.</summary>
    private static string FirstMentioning(IReadOnlyList<string> lines, string needle)
    {
        for (int index = 0; index < lines.Count; index++)
        {
            if (lines[index].Contains(needle, StringComparison.Ordinal))
                return lines[index];
        }
        return "(none)";
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

    /// <summary>Every entity the session currently holds.</summary>
    private static uint[] SpawnedGuids(HeadlessSessionHost session)
    {
        var collector = new VtProofEntityCollector();
        session.Runtime.Entities.Visit(collector);
        return [.. collector.Guids];
    }

    /// <summary>
    /// Where each entity that appeared since the given set stands. This is
    /// the server's own idea of the character's position made visible: a
    /// staged monster is placed relative to it.
    /// </summary>
    private static string[] SpawnedSince(
        HeadlessSessionHost session,
        uint[] before)
    {
        var collector = new VtProofEntityCollector(before);
        session.Runtime.Entities.Visit(collector);
        return collector.Described.Count == 0
            ? ["nothing new appeared"]
            : [.. collector.Described];
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
        StageProfileFixtures(string root, RynthifyProofInput rynthify)
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
            // The controlled native meta is an opt-in import source, not one
            // of the ordinary proof's three selected profile fixtures.
            if (relative.Equals(
                    RynthifyMetaProfileName + ".met",
                    StringComparison.Ordinal))
            {
                continue;
            }
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

        if (rynthify.Enabled)
        {
            string stagedLoot = Path.Combine(
                vtankRoot,
                RynthifyLootProfileName + ".utl");
            File.Copy(rynthify.LootPath!, stagedLoot, overwrite: true);
            Assert.True(
                File.ReadAllBytes(rynthify.LootPath!).SequenceEqual(
                    File.ReadAllBytes(stagedLoot)),
                "The externally emitted RYNTHIFY loot profile changed while staging.");

            string importDirectory = Path.Combine(
                pluginStorageRoot,
                "acdream.mosstank",
                "imports");
            Directory.CreateDirectory(importDirectory);
            File.Copy(
                Path.Combine(source, RynthifyMetaProfileName + ".met"),
                Path.Combine(importDirectory, RynthifyMetaProfileName + ".met"),
                overwrite: true);
        }
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
/// The opt-in boundary for the RYNTHIFY workflow. A configured value must be
/// a real absolute .utl path; malformed input fails before a session starts.
/// </summary>
internal readonly record struct RynthifyProofInput(bool Enabled, string? LootPath)
{
    internal const string EnvironmentVariable = "ACDREAM_RYNTHIFY_LOOT_PATH";

    internal string LootProfileName => Enabled
        ? "rynthify-rynth-emitted-loot"
        : "vt-proof-loot";

    internal static RynthifyProofInput FromEnvironment() =>
        FromConfiguredPath(Environment.GetEnvironmentVariable(EnvironmentVariable));

    internal static RynthifyProofInput FromConfiguredPath(
        string? configured,
        Func<string, bool>? exists = null)
    {
        if (configured is null)
            return new RynthifyProofInput(false, null);
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                $"{EnvironmentVariable} was set but is empty.");
        }
        string trimmed = configured.Trim();
        if (!Path.IsPathFullyQualified(trimmed))
        {
            throw new InvalidOperationException(
                $"{EnvironmentVariable} must name an absolute .utl file.");
        }
        string fullPath = Path.GetFullPath(trimmed);
        if (!Path.GetExtension(fullPath).Equals(".utl", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{EnvironmentVariable} must name an absolute .utl file.");
        }
        if (!(exists ?? File.Exists)(fullPath))
        {
            throw new InvalidOperationException(
                $"{EnvironmentVariable} does not exist: {fullPath}");
        }
        return new RynthifyProofInput(true, fullPath);
    }
}

internal static class VtProofProfileIdentity
{
    internal static bool IsReady(
        IAutomationSurface automation,
        string expectedCharacter) =>
        IsReady(
            automation.IsAvailable,
            automation.Character.Name,
            automation.Character.WorldName,
            expectedCharacter);

    internal static bool IsReady(
        bool automationAvailable,
        string characterName,
        string worldName,
        string expectedCharacter) =>
        automationAvailable
        && string.Equals(
            characterName,
            expectedCharacter,
            StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(worldName);

    internal static string Describe(IAutomationSurface automation) =>
        $"available={automation.IsAvailable}, name="
        + $"'{automation.Character.Name}', world='{automation.Character.WorldName}'";
}

internal readonly record struct VtProofProfileBinding(
    string? Settings,
    string? Loot,
    string? Route,
    string? Meta)
{
    internal static VtProofProfileBinding Read(
        string profileRoot,
        string world,
        string character)
    {
        if (string.IsNullOrWhiteSpace(world)
            || string.IsNullOrWhiteSpace(character))
        {
            return default;
        }
        string path = Path.Combine(profileRoot, $"{world}_{character}.cdf");
        if (!File.Exists(path))
            return default;
        string[] lines = File.ReadAllText(path)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n');
        if (lines.Length < 4
            || !string.Equals(lines[0], "uTank2 CDF 1.0", StringComparison.Ordinal))
        {
            return default;
        }
        return new VtProofProfileBinding(
            lines[1],
            lines[2],
            lines[3],
            lines.Length >= 5 && lines[4].Length > 0 ? lines[4] : null);
    }

    internal bool Matches(
        string settings,
        string loot,
        string route,
        string meta) =>
        string.Equals(Settings, settings, StringComparison.Ordinal)
        && string.Equals(Loot, loot, StringComparison.Ordinal)
        && string.Equals(Route, route, StringComparison.Ordinal)
        && string.Equals(Meta, meta, StringComparison.Ordinal);

    public override string ToString() =>
        $"settings={Settings ?? "unread"},loot={Loot ?? "unread"},"
        + $"route={Route ?? "unread"},meta={Meta ?? "unread"}";
}

/// <summary>The four extra outcomes required by the opt-in full workflow.</summary>
internal sealed class RynthifyProofProgress
{
    internal const string Title =
        "Rynth-emitted loot, two kills and corpse receipts, route progress, "
        + "and a native meta host change all execute";

    internal bool MetaExecuted { get; set; }
    internal bool TwoKills { get; set; }
    internal bool TwoCorpsePickups { get; set; }
    internal bool RouteAdvanced { get; set; }
    internal string MetaEvidence { get; set; } = "meta not reached";
    internal string KillEvidence { get; set; } = "fight not reached";
    internal string LootEvidence { get; set; } = "loot not reached";
    internal string RouteEvidence { get; set; } = "route not reached";

    internal static bool HasTwoRealKills(
        IReadOnlyList<string> verdicts,
        IReadOnlyCollection<uint> newCorpseIds) =>
        verdicts.Count >= 2 && newCorpseIds.Distinct().Count() >= 2;

    internal static bool CanEnableMeta(bool rynthifyEnabled, bool macroStarted) =>
        rynthifyEnabled && macroStarted;

    internal static bool HasTwoCorpsePickups(
        IReadOnlyCollection<uint> openedCorpseIds,
        IReadOnlyCollection<uint> completedCorpseIds,
        int pickupAcknowledgements) =>
        pickupAcknowledgements >= 2
        && openedCorpseIds.Distinct().Count() >= 2
        && completedCorpseIds.Intersect(openedCorpseIds).Distinct().Count() >= 2;

    internal void Report(VtProofLedger ledger, Func<string> evidence)
    {
        bool corePassed = ledger.AllPassed;
        string summary = string.Join(
            "; ",
            $"core P1-P9={(corePassed ? "pass" : "fail")}",
            MetaEvidence,
            KillEvidence,
            LootEvidence,
            RouteEvidence);
        if (corePassed && MetaExecuted && TwoKills && TwoCorpsePickups && RouteAdvanced)
            ledger.Pass("RYNTHIFY", Title, summary);
        else
            ledger.Fail("RYNTHIFY", Title, summary, evidence());
    }
}

/// <summary>
/// Correlates authoritative pickup completions to the corpse that held the
/// exact source item. Counts are per container, so two items from one corpse
/// cannot masquerade as a two-corpse workflow.
/// </summary>
internal sealed class VtProofPickupReceipts
{
    private readonly Dictionary<uint, uint> _originByItem = [];
    private readonly HashSet<uint> _completedCorpses = [];
    private long _lastCompletionRevision;
    private int _successfulPickupCount;

    internal IReadOnlyCollection<uint> CompletedCorpseIds => _completedCorpses;
    internal int SuccessfulPickupCount => _successfulPickupCount;

    internal void Observe(
        uint currentContainerId,
        IReadOnlyList<PluginInventoryItem> contents,
        PluginInventoryCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(contents);
        if (currentContainerId != 0u)
        {
            foreach (PluginInventoryItem item in contents)
                _originByItem.TryAdd(item.ObjectId, currentContainerId);
        }

        if (completion.Revision <= _lastCompletionRevision)
            return;
        _lastCompletionRevision = completion.Revision;
        if (completion.Kind == PluginInventoryCommandKind.Pickup
            && completion.IsSuccess
            && _originByItem.TryGetValue(completion.SourceObjectId, out uint corpseId))
        {
            _successfulPickupCount++;
            _completedCorpses.Add(corpseId);
        }
    }
}

internal static class VtProofLootEvidence
{
    private const string Opening = "LootCorpse: opening ";

    internal static IReadOnlyCollection<uint> OpenedCorpseIds(
        IReadOnlyList<string> chat)
    {
        ArgumentNullException.ThrowIfNull(chat);
        var result = new HashSet<uint>();
        foreach (string line in chat)
        {
            if (!line.Contains(Opening, StringComparison.Ordinal))
                continue;
            int start = line.LastIndexOf("(0x", StringComparison.Ordinal);
            int end = start < 0 ? -1 : line.IndexOf(')', start);
            if (start < 0 || end <= start + 3)
                continue;
            if (uint.TryParse(
                    line.AsSpan(start + 3, end - start - 3),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out uint objectId))
            {
                result.Add(objectId);
            }
        }
        return result;
    }

    internal static string Hex(IEnumerable<uint> ids)
    {
        string[] values = ids
            .Distinct()
            .Order()
            .Select(static id => $"0x{id:X8}")
            .ToArray();
        return values.Length == 0 ? "(none)" : string.Join(",", values);
    }

    internal static string OpenedCountFailure(int actual, int required) =>
        $"{actual} of {required} required corpse(s) opened";

    internal static string DescribeCorpses(
        IEnumerable<PluginLootContainer> corpses,
        IEnumerable<uint> expectedIds,
        uint currentContainerId,
        PluginAppraisalState appraisal)
    {
        Dictionary<uint, PluginLootContainer> byId = corpses
            .GroupBy(static corpse => corpse.ObjectId)
            .ToDictionary(static group => group.Key, static group => group.First());
        string[] details = expectedIds
            .Distinct()
            .Order()
            .Select(id => byId.TryGetValue(id, out PluginLootContainer corpse)
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"0x{id:X8}[position={corpse.HasPosition},distance={corpse.Distance:0.00}m,identified={corpse.IsIdentified},description='{Clean(corpse.LongDescription)}',opened={corpse.HasBeenOpened},requested={corpse.IsRequested},current={corpse.IsCurrent}]")
                : $"0x{id:X8}[missing]")
            .ToArray();
        return "currentContainer=" + $"0x{currentContainerId:X8}; "
            + $"appraisal[awaiting=0x{appraisal.AwaitingObjectId:X8},"
            + $"current=0x{appraisal.CurrentObjectId:X8}]; corpses="
            + (details.Length == 0 ? "(none expected)" : string.Join(" / ", details));
    }

    private static string Clean(string value) => value
        .Replace('\r', ' ')
        .Replace('\n', ' ')
        .Replace('\'', '`');
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
        => Lines(chat).FirstOrDefault();

    internal static string[] Lines(IReadOnlyList<string> chat)
    {
        ArgumentNullException.ThrowIfNull(chat);
        var found = new List<string>();
        foreach (string line in chat)
        {
            foreach (string verdict in Verdicts)
            {
                if (line.Contains(verdict, StringComparison.Ordinal))
                {
                    found.Add(line);
                    break;
                }
            }
        }
        return [.. found];
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

/// <summary>
/// What a character has to be wearing before an item-enchantment buff row
/// can settle.
/// <para>
/// The buff profile's Bane rows are aimed at the character, and the server
/// redirects each one onto the armour and clothing the character is wearing.
/// With nothing worn there is nothing to enchant: the server applies nothing
/// and answers nothing, so the cast's result never arrives, the row records
/// no coverage, and the next pass asks for it again. That is a buff pass
/// with no end, which is why the run dresses the character before it starts
/// the macro.
/// </para>
/// </summary>
/// <summary>
/// The proof's arena housekeeping. The run stages deaths and monsters in one
/// landblock over and over, so it has to recognise its own leavings and
/// nothing else: the server refuses to delete a player, but it will happily
/// delete somebody else's corpse, so the name test is the safety rail.
/// </summary>

internal static class VtProofArena
{
    /// <summary>
    /// The name the server gives a corpse of the named character. The server
    /// spells a privileged name with a marker in front of it and drops the
    /// marker once the character is made to appear as an ordinary player, so
    /// a run can meet both spellings of the same character's corpse -- and
    /// the server itself treats the two as one name when it looks one up.
    /// </summary>
    internal static string OwnCorpseName(string characterName) =>
        "Corpse of " + characterName;

    /// <summary>
    /// The next corpse of the named character in the reported set, or none
    /// when the set holds nothing of theirs. Monster corpses, and the corpses
    /// of other characters, are never returned: the whole name has to match,
    /// marker aside, so a longer name that merely starts the same is not the
    /// character's.
    /// </summary>
    internal static PluginLootContainer? NextOwnCorpse(
        IReadOnlyList<PluginLootContainer> reported,
        string characterName)
    {
        ArgumentNullException.ThrowIfNull(reported);
        if (string.IsNullOrWhiteSpace(characterName))
            return null;
        string marked = OwnCorpseName(characterName.TrimStart('+'));
        string plain = OwnCorpseName("+" + characterName.TrimStart('+'));
        for (int index = 0; index < reported.Count; index++)
        {
            string name = reported[index].Name;
            if (string.Equals(name, marked, StringComparison.Ordinal)
                || string.Equals(name, plain, StringComparison.Ordinal))
            {
                return reported[index];
            }
        }
        return null;
    }

    /// <summary>
    /// The next corpse in the reported set that belongs to NO character the
    /// run knows of — the previous run's monsters, which rot on their own but
    /// not before the next run has begun and its looter has found them.
    /// <para>
    /// The guard is the same one <see cref="NextOwnCorpse"/> makes, inverted
    /// and widened: a corpse whose name is "Corpse of " plus any of the named
    /// characters, in either spelling of the marker, is never returned. A
    /// corpse whose name does not begin "Corpse of " at all is not returned
    /// either — whatever it is, it is not a corpse this sweep understands.
    /// </para>
    /// </summary>
    internal static PluginLootContainer? NextForeignCorpse(
        IReadOnlyList<PluginLootContainer> reported,
        IReadOnlyList<string> characterNames)
    {
        ArgumentNullException.ThrowIfNull(reported);
        ArgumentNullException.ThrowIfNull(characterNames);
        for (int index = 0; index < reported.Count; index++)
        {
            string name = reported[index].Name;
            if (!name.StartsWith("Corpse of ", StringComparison.Ordinal))
                continue;
            if (BelongsToAnyone(name, characterNames))
                continue;
            return reported[index];
        }
        return null;
    }

    /// <summary>
    /// How far to step, and in which direction, to stand a given distance away
    /// from the nearest corpse — along the line that already separates the two,
    /// so the step is always AWAY from it whichever side of the character it
    /// fell on. The answer is in metres along the cell's own axes, which is
    /// the frame a placement command names.
    /// </summary>
    /// <returns>
    /// False when there is nothing to step away from, when the character is
    /// already at least that far, or when the two are on the same spot and
    /// there is no line to step along.
    /// </returns>
    internal static bool StepAwayFromCorpse(
        in PluginNavigationPosition stood,
        IReadOnlyList<PluginLootContainer> reported,
        double wantedMeters,
        out double eastWestMeters,
        out double northSouthMeters)
    {
        ArgumentNullException.ThrowIfNull(reported);
        eastWestMeters = 0d;
        northSouthMeters = 0d;

        PluginLootContainer? nearest = null;
        for (int index = 0; index < reported.Count; index++)
        {
            if (!reported[index].HasPosition)
                continue;
            if (nearest is null || reported[index].Distance < nearest.Value.Distance)
                nearest = reported[index];
        }
        if (nearest is not { } corpse)
            return false;

        // One unit of the map frame is 240 metres along the same axis the
        // cell's own coordinates run on, so the two frames differ by a scale
        // and nothing else.
        double dx = (stood.EastWest - corpse.Position.EastWest) * 240d;
        double dy = (stood.NorthSouth - corpse.Position.NorthSouth) * 240d;
        double apart = Math.Sqrt((dx * dx) + (dy * dy));
        if (apart < 0.05d)
            return false;
        double ux = dx / apart;
        double uy = dy / apart;

        // The direction is away from the nearest corpse; the length is
        // whatever it takes for EVERY placed corpse to end up the wanted
        // distance off -- two drudges that died a few metres apart along
        // the same line would otherwise have the character step off one
        // and onto the other.
        for (double step = 0d; step <= 30d; step += 0.25d)
        {
            double atX = (stood.EastWest * 240d) + (ux * step);
            double atY = (stood.NorthSouth * 240d) + (uy * step);
            bool clear = true;
            for (int index = 0; index < reported.Count; index++)
            {
                if (!reported[index].HasPosition)
                    continue;
                double cx = atX - (reported[index].Position.EastWest * 240d);
                double cy = atY - (reported[index].Position.NorthSouth * 240d);
                if (Math.Sqrt((cx * cx) + (cy * cy)) < wantedMeters)
                {
                    clear = false;
                    break;
                }
            }
            if (!clear)
                continue;
            if (step == 0d)
                return false;
            eastWestMeters = ux * step;
            northSouthMeters = uy * step;
            return true;
        }
        return false;
    }

    /// <summary>
    /// How far away the corpse named in an opening line lay when the milestone
    /// began, or a negative number when the line names no corpse this run was
    /// watching. The line's own text is the only record of WHICH corpse the
    /// looter chose, so the id is read back out of it.
    /// </summary>
    internal static double CorpseDistanceWhenOpened(
        string openedLine,
        IReadOnlyDictionary<uint, double> layAt)
    {
        ArgumentNullException.ThrowIfNull(layAt);
        if (string.IsNullOrEmpty(openedLine))
            return -1d;
        int open = openedLine.LastIndexOf("(0x", StringComparison.Ordinal);
        if (open < 0)
            return -1d;
        int close = openedLine.IndexOf(')', open);
        if (close < 0)
            return -1d;
        ReadOnlySpan<char> digits = openedLine
            .AsSpan(open + 3, close - open - 3);
        if (!uint.TryParse(
                digits,
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out uint objectId))
        {
            return -1d;
        }
        return layAt.TryGetValue(objectId, out double distance)
            ? distance
            : -1d;
    }

    private static bool BelongsToAnyone(
        string corpseName,
        IReadOnlyList<string> characterNames)
    {
        for (int index = 0; index < characterNames.Count; index++)
        {
            string character = characterNames[index];
            if (string.IsNullOrWhiteSpace(character))
                continue;
            string plain = character.TrimStart('+');
            if (string.Equals(
                    corpseName,
                    OwnCorpseName(plain),
                    StringComparison.Ordinal)
                || string.Equals(
                    corpseName,
                    OwnCorpseName("+" + plain),
                    StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }
}

internal static class VtProofVestments
{
    /// <summary>
    /// The slots a redirected item enchantment can land in: clothing, armour
    /// and the shield hand. Deliberately not the jewellery, weapon, ammunition
    /// or held slots — nothing a Bane is redirected to lives there.
    /// </summary>
    internal const uint Locations =
        (uint)(EquipMask.Clothing | EquipMask.Armor | EquipMask.Shield);

    internal static bool IsVestment(in PluginEquipmentItem item) =>
        (item.ValidLocations & Locations) != 0u;

    internal static bool IsWorn(PluginEquipmentItem item) =>
        item.IsEquipped && IsVestment(item);

    /// <summary>Is anything a Bane could land on actually being worn?</summary>
    internal static bool IsDressed(IReadOnlyList<PluginEquipmentItem> owned)
    {
        ArgumentNullException.ThrowIfNull(owned);
        foreach (PluginEquipmentItem item in owned)
        {
            if (IsWorn(item))
                return true;
        }
        return false;
    }

    /// <summary>
    /// The owned vestments that are not on yet, in the projection's own
    /// order. Pieces whose slots overlap will refuse each other, and that is
    /// fine — one piece is all a Bane needs.
    /// </summary>
    internal static IReadOnlyList<PluginEquipmentItem> NotYetWorn(
        IReadOnlyList<PluginEquipmentItem> owned)
    {
        ArgumentNullException.ThrowIfNull(owned);
        var pending = new List<PluginEquipmentItem>();
        foreach (PluginEquipmentItem item in owned)
        {
            if (IsVestment(item) && !item.IsEquipped)
                pending.Add(item);
        }
        return pending;
    }
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

    internal static IReadOnlyList<int> TargetSequence(
        IReadOnlyList<string> lines,
        int from,
        IReadOnlyList<VtProofRoutePoint> route)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(route);
        var result = new List<int>();
        for (int index = Math.Max(0, from); index < lines.Count; index++)
        {
            string line = lines[index];
            if (!line.Contains("(NavigateRouteIdle)", StringComparison.Ordinal)
                || IndexFromLine(line, route) is not { } target
                || (result.Count > 0 && result[^1] == target))
            {
                continue;
            }
            result.Add(target);
        }
        return result;
    }

    internal static bool HasCircularForwardAdvances(
        IReadOnlyList<int> targets,
        int waypointCount,
        int requiredAdvances)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (waypointCount <= 0 || requiredAdvances <= 0
            || targets.Count < requiredAdvances + 1)
        {
            return false;
        }
        for (int index = 1; index < targets.Count; index++)
        {
            int previous = targets[index - 1];
            int current = targets[index];
            if (previous < 0 || previous >= waypointCount
                || current != (previous + 1) % waypointCount)
            {
                return false;
            }
        }
        return true;
    }
}

/// <summary>
/// Reads the point nodes out of the proof route file. Only plain points are
/// read: the proof route is deliberately a bare loop so that "did the
/// character walk it?" has one unambiguous answer.
/// </summary>
internal static class VtProofRouteFixture
{
    internal static bool IsCircular(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (string raw in text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n'))
        {
            string line = raw.Trim();
            if (!line.StartsWith("NAV:", StringComparison.Ordinal))
                continue;
            return line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Contains("circular", StringComparer.OrdinalIgnoreCase);
        }
        return false;
    }

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
    public void RynthifyInputIsDisabledOnlyWhenTheVariableIsAbsent()
    {
        RynthifyProofInput disabled =
            RynthifyProofInput.FromConfiguredPath(null, static _ => true);
        string fullPath = Path.GetFullPath("rynth-emitted.utl");
        RynthifyProofInput enabled =
            RynthifyProofInput.FromConfiguredPath(fullPath, static _ => true);

        Assert.False(disabled.Enabled);
        Assert.True(enabled.Enabled);
        Assert.Equal(fullPath, enabled.LootPath);
        Assert.Equal("rynthify-rynth-emitted-loot", enabled.LootProfileName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative.utl")]
    [InlineData("relative.txt")]
    public void RynthifyInputFailsClosedOnMalformedConfiguredPaths(string configured)
    {
        Assert.Throws<InvalidOperationException>(() =>
            RynthifyProofInput.FromConfiguredPath(configured, static _ => true));
    }

    [Fact]
    public void RynthifyInputFailsClosedWhenTheDonorDoesNotExist()
    {
        string fullPath = Path.GetFullPath("missing.utl");

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            RynthifyProofInput.FromConfiguredPath(fullPath, static _ => false));

        Assert.Contains("does not exist", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RynthifyAcceptanceRequiresTwoVerdictsAndTwoNewCorpseGuids()
    {
        Assert.False(RynthifyProofProgress.HasTwoRealKills(
            ["first"],
            [0x81000001u, 0x81000002u]));
        Assert.False(RynthifyProofProgress.HasTwoRealKills(
            ["first", "second"],
            [0x81000001u]));
        Assert.True(RynthifyProofProgress.HasTwoRealKills(
            ["first", "second"],
            [0x81000001u, 0x81000002u]));
    }

    [Fact]
    public void RynthifyEnablesMetaOnlyAfterTheMacroStartWasObserved()
    {
        Assert.False(RynthifyProofProgress.CanEnableMeta(
            rynthifyEnabled: false,
            macroStarted: true));
        Assert.False(RynthifyProofProgress.CanEnableMeta(
            rynthifyEnabled: true,
            macroStarted: false));
        Assert.True(RynthifyProofProgress.CanEnableMeta(
            rynthifyEnabled: true,
            macroStarted: true));
    }

    [Theory]
    [InlineData(false, "Acdream", "Coldeve")]
    [InlineData(true, "", "Coldeve")]
    [InlineData(true, "Acdream", "")]
    [InlineData(true, "+Acdream", "Coldeve")]
    public void ProfileSelectionWaitsForTheCompletePlainCharacterIdentity(
        bool available,
        string character,
        string world)
    {
        Assert.False(VtProofProfileIdentity.IsReady(
            available,
            character,
            world,
            expectedCharacter: "Acdream"));
        Assert.True(VtProofProfileIdentity.IsReady(
            automationAvailable: true,
            characterName: "Acdream",
            worldName: "Coldeve",
            expectedCharacter: "Acdream"));
    }

    [Fact]
    public void PickupReceiptsAreAttributedToTwoDistinctOpenedCorpses()
    {
        const uint corpseOne = 0x81000001u;
        const uint corpseTwo = 0x81000002u;
        const uint itemOne = 0x82000001u;
        const uint itemTwo = 0x82000002u;
        var receipts = new VtProofPickupReceipts();
        PluginInventoryItem first = default(PluginInventoryItem) with { ObjectId = itemOne };
        PluginInventoryItem second = default(PluginInventoryItem) with { ObjectId = itemTwo };

        receipts.Observe(corpseOne, [first], default);
        receipts.Observe(
            corpseOne,
            [],
            new PluginInventoryCompletion(
                1,
                PluginInventoryCommandKind.Pickup,
                itemOne,
                0u));
        receipts.Observe(corpseTwo, [second], default);
        receipts.Observe(
            corpseTwo,
            [],
            new PluginInventoryCompletion(
                2,
                PluginInventoryCommandKind.Pickup,
                itemTwo,
                0u));
        // Polling the same completion again is not another acknowledgement.
        receipts.Observe(
            corpseTwo,
            [],
            new PluginInventoryCompletion(
                2,
                PluginInventoryCommandKind.Pickup,
                itemTwo,
                0u));

        Assert.Equal(2, receipts.SuccessfulPickupCount);
        Assert.True(RynthifyProofProgress.HasTwoCorpsePickups(
            [corpseOne, corpseTwo],
            receipts.CompletedCorpseIds,
            pickupAcknowledgements: receipts.SuccessfulPickupCount));
        Assert.False(RynthifyProofProgress.HasTwoCorpsePickups(
            [corpseOne, corpseTwo],
            [corpseOne],
            pickupAcknowledgements: 2));
        Assert.False(RynthifyProofProgress.HasTwoCorpsePickups(
            [corpseOne, corpseTwo],
            receipts.CompletedCorpseIds,
            pickupAcknowledgements: 1));
    }

    [Fact]
    public void CorpseOpenEvidenceKeepsDistinctContainerGuids()
    {
        string[] chat =
        [
            "[MossTank] LootCorpse: opening Corpse of Drudge (0x81000001)",
            "[MossTank] LootCorpse: opening Corpse of Drudge (0x81000002)",
            "[MossTank] LootCorpse: opening Corpse of Drudge (0x81000002)",
        ];

        Assert.Equal(
            [0x81000001u, 0x81000002u],
            VtProofLootEvidence.OpenedCorpseIds(chat).Order());
        Assert.Equal(
            "1 of 2 required corpse(s) opened",
            VtProofLootEvidence.OpenedCountFailure(actual: 1, required: 2));

        var corpse = new PluginLootContainer(
            0x81000001u,
            7u,
            "Corpse of Drudge",
            4.5f,
            HasBeenOpened: false,
            IsRequested: true,
            IsCurrent: false)
        {
            HasPosition = true,
            IsIdentified = true,
            LongDescription = "Killed by Acdream",
        };
        string state = VtProofLootEvidence.DescribeCorpses(
            [corpse],
            [0x81000001u, 0x81000002u],
            currentContainerId: 0u,
            new PluginAppraisalState(
                Revision: 5,
                AwaitingObjectId: 0x81000002u,
                CurrentObjectId: 0x81000001u));

        Assert.Contains("distance=4.50m", state, StringComparison.Ordinal);
        Assert.Contains("identified=True", state, StringComparison.Ordinal);
        Assert.Contains("description='Killed by Acdream'", state, StringComparison.Ordinal);
        Assert.Contains(
            "appraisal[awaiting=0x81000002,current=0x81000001]",
            state,
            StringComparison.Ordinal);
        Assert.Contains("0x81000002[missing]", state, StringComparison.Ordinal);
    }

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

        string text = File.ReadAllText(path);
        IReadOnlyList<VtProofRoutePoint> points =
            VtProofRouteFixture.ReadPoints(text);

        Assert.True(VtProofRouteFixture.IsCircular(text));
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
        Assert.True(VtProofRouteFixture.IsCircular(text));
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

    [Fact]
    public void CircularTargetProgressAcceptsForwardWrapping()
    {
        string[] lines =
        [
            "[MossTank] (OpenDoor) declined: Waypoint 2/4: 3.0m",
            "[MossTank] (NavigateRouteIdle) declined: Waypoint 3/4: 2.9m",
            "[MossTank] (NavigateRouteIdle) declined: Waypoint 3/4: 2.8m",
            "[MossTank] (NavigateRouteIdle) declined: Waypoint 4/4: 2.7m",
            "[MossTank] (NavigateRouteIdle) Running [targ range 12.000, "
                + "targ loc 33.790015, 42.105799, 0.401375 ]",
        ];

        IReadOnlyList<int> targets =
            VtProofRouteProgress.TargetSequence(lines, 0, ProgressRoute);

        Assert.Equal([2, 3, 0], targets);
        Assert.True(VtProofRouteProgress.HasCircularForwardAdvances(
            targets,
            waypointCount: 4,
            requiredAdvances: 2));
    }

    [Fact]
    public void CircularTargetProgressRejectsTheOldDistinctButBackwardBoundary()
    {
        int[] targets = [2, 3, 2];

        // Counting distinct adjacent samples alone called this two advances.
        Assert.Equal(2, targets.Length - 1);
        Assert.False(VtProofRouteProgress.HasCircularForwardAdvances(
            targets,
            waypointCount: 4,
            requiredAdvances: 2));
    }
}

/// <summary>
/// Reads the entity view: the guids it holds, and where anything that is not
/// in a given set stands.
/// </summary>
internal sealed class VtProofEntityCollector(uint[]? known = null)
    : IRuntimeEntityVisitor
{
    private readonly uint[] _known = known ?? [];

    internal List<uint> Guids { get; } = [];

    internal List<string> Described { get; } = [];

    public void Visit(in RuntimeEntitySnapshot entity)
    {
        Guids.Add(entity.Identity.ServerGuid);
        if (Array.IndexOf(_known, entity.Identity.ServerGuid) >= 0)
            return;
        string where = entity.Position is { } position
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"cell=0x{position.ObjCellId:X8} ({position.Frame.Origin.X:0.000},{position.Frame.Origin.Y:0.000},{position.Frame.Origin.Z:0.000})")
            : "no position yet";
        Described.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"0x{entity.Identity.ServerGuid:X8} {where}"));
    }
}
