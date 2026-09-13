using System.Globalization;
using System.Text.Json;
using AcDream.Core.Items;
using AcDream.Headless.Configuration;
using AcDream.Headless.Credentials;
using AcDream.Headless.Diagnostics;
using AcDream.Headless.Hosting;
using AcDream.Core.Net.Messages;
using AcDream.Runtime.Chat;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime;
using AcDream.Runtime.Entities;
using Xunit.Abstractions;

namespace AcDream.Headless.Tests;

/// <summary>
/// The live half of "one plugin, two hosts": a windowless session picks a
/// real object up off the ground of a real server and then uses it, with the
/// server confirming both. Everything here answered <c>Unavailable</c> while
/// item requests reached the client only through callbacks the graphical
/// host installed, so this is the pin that says a bot can actually loot.
/// </summary>
[Trait("Lane", "Live")]
public sealed class HeadlessItemInteractionLiveTests(ITestOutputHelper output)
{
    /// <summary>The same recorded flat standing point the session proof uses.</summary>
    private const string ArenaTeleport =
        "@teleloc A9B40029 133.603592 17.391838 96.330009 1 0 0 0";

    /// <summary>
    /// A healing kit: an ordinary carryable the server drops at the
    /// player's feet and accepts a use for.
    /// </summary>
    private const string GroundItemWeenie = "632";

    /// <summary>Server-spawned objects carry identifiers above this.</summary>
    private const uint DynamicObjectFloor = 0x80000000u;

    [Fact]
    public void AWindowlessSessionPicksUpAndUsesAnObjectStagedOnTheServer()
    {
        if (Environment.GetEnvironmentVariable("ACDREAM_LIVE") != "1")
        {
            Assert.Fail(
                "Lane=Live requires ACDREAM_LIVE=1 and a reachable configured server.");
        }

        string host = Environment.GetEnvironmentVariable("ACDREAM_TEST_HOST")
            ?? "127.0.0.1";
        string portText = Environment.GetEnvironmentVariable("ACDREAM_TEST_PORT")
            ?? "9000";
        string? user = Environment.GetEnvironmentVariable("ACDREAM_TEST_USER");
        string? pass = Environment.GetEnvironmentVariable("ACDREAM_TEST_PASS");
        Assert.False(string.IsNullOrEmpty(user));
        Assert.False(string.IsNullOrEmpty(pass));

        string temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "acdream-item-pin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        string statusPath = Path.Combine(temporaryRoot, "status.jsonl");
        var descriptor = new HeadlessSessionDescriptor
        {
            Id = "item-interaction-pin",
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
            new HeadlessCredentialSecret("item-interaction-pin", pass!),
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
        _ = WaitUntil(
            TimeSpan.FromSeconds(30d),
            () => session.Runtime.Movement.Snapshot.Position.ObjCellId
                == 0xA9B40029u);

        IAutomationSurface automation = session.Plugins.Host.Automation;
        Assert.True(
            automation.Items.IsAvailable,
            "A windowless session must be able to make item requests.");
        Assert.True(
            automation.Equipment.IsAvailable,
            "A windowless session must be able to make equipment requests.");

        uint player = session.Runtime.PlayerIdentity.ServerGuid;
        ClientObjectTable objects = session.Runtime.InventoryOwner.Objects;
        var before = new HashSet<uint>(
            objects.Objects.Select(static item => item.ObjectId));

        // Stage the object on the ground under the player's feet.
        Assert.Equal(
            SubmitOutcome.Sent,
            session.SubmitConsoleLine("@create " + GroundItemWeenie));

        uint staged = 0u;
        Assert.True(
            WaitUntil(TimeSpan.FromSeconds(20d), () =>
            {
                foreach (ClientObject candidate in objects.Objects)
                {
                    // Landblock scenery streams in alongside the staged
                    // object; only a server-spawned object carries a
                    // dynamic identifier.
                    if (before.Contains(candidate.ObjectId)
                        || candidate.ObjectId < DynamicObjectFloor
                        || candidate.ContainerId != 0u
                        || candidate.WielderId != 0u
                        || !candidate.Name.Contains(
                            "Healing Kit",
                            StringComparison.Ordinal))
                    {
                        continue;
                    }
                    staged = candidate.ObjectId;
                    return true;
                }
                return false;
            }),
            "The staged object never arrived on the ground.");
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"staged 0x{staged:X8} \"{objects.Get(staged)?.Name}\" "
                + $"at {Where(session, staged)}; player at "
                + $"{Where(session, player)}"));

        // VTank walks its own bot to what it wants to lift, and the
        // windowless client sends the request where it stands rather than
        // walking for it, so put the player on the object before asking.
        Assert.True(
            session.Runtime.EntityObjects.Entities.TryGetActive(
                staged,
                out RuntimeEntityRecord stagedRecord)
            && stagedRecord.Snapshot.Position is not null,
            "The staged object arrived without a position.");
        CreateObject.ServerPosition at = stagedRecord.Snapshot.Position!.Value;
        Assert.Equal(
            SubmitOutcome.Sent,
            session.SubmitConsoleLine(string.Create(
                CultureInfo.InvariantCulture,
                $"@teleloc {at.LandblockId:X8} {at.PositionX} {at.PositionY} "
                    + $"{at.PositionZ} 1 0 0 0")));
        Assert.True(
            WaitUntil(TimeSpan.FromSeconds(30d), () =>
            {
                RuntimeMovementSnapshot moved = session.Runtime.Movement.Snapshot;
                return moved.Position.ObjCellId == at.LandblockId
                    && Math.Abs(moved.Position.Frame.Origin.X - at.PositionX) < 0.5f
                    && Math.Abs(moved.Position.Frame.Origin.Y - at.PositionY) < 0.5f;
            }),
            "The player never reached the staged object: player at "
                + Where(session, player));

        // The pickup itself: the runtime's own item owner, with no route a
        // window would have installed.
        Assert.True(
            session.Runtime.ItemInteractionOwner.PlaceWorldItemInBackpack(staged),
            "The pickup request was refused before it reached the server.");
        InventoryTransactionState transactions =
            session.Runtime.InventoryOwner.Transactions;
        bool reserved = transactions.TryGetPending(
            out PendingInventoryRequest pending);
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"after request: reserved={reserved} kind={(reserved ? pending.Kind : default)} "
                + $"item=0x{(reserved ? pending.ItemId : 0u):X8} "
                + $"dispatched={(reserved && pending.Dispatched)}"));
        Assert.True(
            reserved && pending.ItemId == staged && pending.Dispatched,
            "The pickup never became a dispatched inventory request.");
        Assert.True(
            WaitUntil(
                TimeSpan.FromSeconds(20d),
                () => IsCarriedBy(objects, staged, player)),
            "The server never moved the staged object into the player's "
                + "inventory: " + Describe(objects, staged, player)
                + " | " + RecentChat(session));
        output.WriteLine("after pickup: " + Describe(objects, staged, player));

        // And the plugin contract can use what it just picked up. A kit is
        // used on someone, so this is the targeted form.
        Assert.True(
            WaitUntil(TimeSpan.FromSeconds(10d), () => !automation.Items.IsBusy),
            "The pickup transaction never settled.");
        PluginItemCommandResult used = automation.Items.Apply(staged, player);
        output.WriteLine($"use: {used.Status} {used.Notice}");
        Assert.Equal(PluginItemCommandStatus.Started, used.Status);

        // Equipment: a request about a real wieldable gets a real verdict
        // rather than "this host cannot do that".
        PluginEquipmentItem? wieldable = automation.Equipment
            .CaptureOwnedEquipment()
            .FirstOrDefault(static item => item.ValidLocations != 0u);
        if (wieldable is { } equipment)
        {
            PluginEquipmentCommandResult equipped =
                automation.Equipment.Equip(equipment.ObjectId);
            output.WriteLine(
                $"equip \"{equipment.Name}\": {equipped.Status}");
            Assert.NotEqual(
                PluginEquipmentCommandStatus.Unavailable,
                equipped.Status);
            Assert.NotEqual(
                PluginEquipmentCommandStatus.InvalidItem,
                equipped.Status);
        }
    }

    /// <summary>Where the server last said an object is.</summary>
    private static string Where(HeadlessSessionHost session, uint objectId)
    {
        if (!session.Runtime.EntityObjects.Entities.TryGetActive(
                objectId,
                out RuntimeEntityRecord record)
            || record.Snapshot.Position is not { } position)
        {
            return "unknown";
        }
        return string.Create(
            CultureInfo.InvariantCulture,
            $"cell=0x{position.LandblockId:X8} "
                + $"({position.PositionX:0.00},"
                + $"{position.PositionY:0.00},"
                + $"{position.PositionZ:0.00})");
    }

    /// <summary>What the server said while the request was in flight.</summary>
    private static string RecentChat(HeadlessSessionHost session)
    {
        AcDream.Core.Chat.ChatEntry[] entries =
            session.Runtime.CommunicationOwner.Chat.Snapshot();
        return "chat: " + string.Join(
            " | ",
            entries
                .Skip(Math.Max(0, entries.Length - 6))
                .Select(static entry => entry.Text));
    }

    private static bool IsCarriedBy(
        ClientObjectTable objects,
        uint itemId,
        uint player)
    {
        ClientObject? item = objects.Get(itemId);
        if (item is null || player == 0u)
            return false;
        uint container = item.ContainerId;
        for (int hops = 0; container != 0u && hops < 8; hops++)
        {
            if (container == player)
                return true;
            container = objects.Get(container)?.ContainerId ?? 0u;
        }
        return false;
    }

    private static string Describe(
        ClientObjectTable objects,
        uint itemId,
        uint player)
    {
        ClientObject? item = objects.Get(itemId);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"item=0x{itemId:X8} present={item is not null} "
                + $"container=0x{item?.ContainerId ?? 0u:X8} "
                + $"player=0x{player:X8}");
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
