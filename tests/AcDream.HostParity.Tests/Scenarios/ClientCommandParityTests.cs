using AcDream.Core.Chat;
using AcDream.Core.Net;
using AcDream.Runtime.Chat;

namespace AcDream.HostParity.Tests;

/// <summary>
/// Every client-side slash command, on a client with a window and a client
/// without one.
///
/// What it was before: the windowed host had one dispatcher with seventy
/// commands and the windowless host had a second, smaller copy of it whose
/// default arm threw. Twenty-eight commands worked in a chat box and threw at
/// a console: Away, ChatLogFile, Consent, Die, Endurance, FillComponents,
/// Filter, Friends, FriendsAdd, FriendsRemove, HouseAbandon, ListEmotes,
/// ListMessageTypes, LoadAutoUi, LoadUi, RenderOption, SaveAutoUi, SaveUi,
/// SetChatTitle, ShowLastCorpseLocation, ShowLocation, ShowVersion, Speaker,
/// Squelch, ToggleFrameRate, ToggleUiLock, Unfilter, Unsquelch.
///
/// What it is now: one dispatcher in the runtime, which both clients build.
/// The only difference left is the ten actions that need something to look
/// at, and a client with nothing to look at answers those with a line rather
/// than throwing. Every id is walked here, not a sample, and an id that
/// throws on either client fails the test.
///
/// Mutation check (2026-09-20): making the shared dispatcher throw on the
/// twenty-eight ids the windowless copy used to throw on -- its old default
/// arm -- turned this red and naming each id; removing it turned it green.
/// </summary>
public sealed class ClientCommandParityTests
{
    /// <summary>
    /// The commands that genuinely need something to look at. A client with
    /// nothing to look at answers each of these with one line and sends
    /// nothing, which is a difference a plugin or a player can read, not a
    /// crash.
    /// </summary>
    private static readonly ClientCommandId[] NeedAWindow =
    [
        ClientCommandId.ToggleFrameRate,
        ClientCommandId.RenderOption,
        ClientCommandId.SaveUi,
        ClientCommandId.LoadUi,
        ClientCommandId.SaveAutoUi,
        ClientCommandId.LoadAutoUi,
        ClientCommandId.Die,
        ClientCommandId.HouseAbandon,
        // FillComponents is not here: it refuses for want of an open vendor
        // before it ever reaches a buy list, so both clients say the same
        // thing and it is compared with the rest.
    ];

    /// <summary>
    /// Arguments that reach past the first refusal for the commands that need
    /// them. Anything not listed is run with no arguments, which is what a
    /// player typing the bare verb does.
    /// </summary>
    private static string ArgumentsFor(ClientCommandId id) => id switch
    {
        ClientCommandId.RenderOption => "radius 12",
        ClientCommandId.SaveUi or ClientCommandId.LoadUi => "layout",
        ClientCommandId.FillComponents => "taper 500",
        ClientCommandId.Squelch or ClientCommandId.Unsquelch => "Bob",
        ClientCommandId.Filter or ClientCommandId.Unfilter => "-Speech",
        ClientCommandId.FriendsAdd or ClientCommandId.FriendsRemove => "Bob",
        ClientCommandId.Friends => "online",
        ClientCommandId.Away => "msg back soon",
        ClientCommandId.Consent => "who",
        ClientCommandId.Emote => "wave",
        ClientCommandId.SetChatTitle => "Adventurer",
        ClientCommandId.ChatToggle => "off",
        ClientCommandId.NoTellToggle => "on",
        ClientCommandId.JoinChannel or ClientCommandId.LeaveChannel => "general",
        ClientCommandId.ListChannel
            or ClientCommandId.OnChannel
            or ClientCommandId.OffChannel => "general",
        ClientCommandId.HouseAvailableList => "cottage",
        ClientCommandId.Permit => "add Bob",
        ClientCommandId.AllegianceBoot => "Bob",
        ClientCommandId.AllegianceBan => "list",
        ClientCommandId.AllegianceOfficer => "list",
        ClientCommandId.AllegianceOfficerTitle => "list",
        ClientCommandId.AllegianceName => "",
        ClientCommandId.AllegianceLock => "on",
        ClientCommandId.AllegianceHouse => "",
        ClientCommandId.AllegianceMotd => "",
        ClientCommandId.AllegianceBroadcast => "hello",
        ClientCommandId.AllegianceChat => "on",
        ClientCommandId.HouseOpenStatus => "open",
        ClientCommandId.HouseStorage => "list",
        ClientCommandId.HouseBoot => "Bob",
        ClientCommandId.HouseHooks => "on",
        _ => string.Empty,
    };

    /// <summary>What one client did when it was handed one command.</summary>
    private readonly record struct Outcome(
        string Sent, string Said, string? Threw);

    private static Outcome Run(
        ParityArm arm,
        ClientCommandId id,
        RuntimeClientCommandHostBindings? host)
    {
        WorldSession session = arm.Runtime.Session.CurrentSession
            ?? throw new InvalidOperationException(
                "The arm is not holding a world connection.");
        var said = new List<string>();
        using IDisposable listening = ListenToChat(arm, said);
        _ = arm.Operations.TakeOutbound();

        var dispatcher = new RuntimeClientCommandDispatcher(
            RuntimeClientCommandBindings.Build(arm.Runtime, session, host));

        string? threw = null;
        try
        {
            dispatcher.Execute(
                new ExecuteClientCommandCmd(id, ArgumentsFor(id)));
        }
        catch (Exception error)
        {
            threw = error.GetType().Name + ": " + error.Message;
        }

        return new Outcome(
            string.Join(
                " | ",
                arm.Operations.TakeOutbound().Select(sent => sent.ToString())),
            string.Join(" | ", said),
            threw);
    }

    private static IDisposable ListenToChat(ParityArm arm, List<string> said)
    {
        ChatLog log = arm.Runtime.CommunicationOwner.Chat;
        void OnAppended(ChatEntry entry) => said.Add(entry.Text);
        log.EntryAppended += OnAppended;
        return new Unsubscribe(() => log.EntryAppended -= OnAppended);
    }

    private sealed class Unsubscribe(Action stop) : IDisposable
    {
        public void Dispose() => stop();
    }

    /// <summary>
    /// Everything a window can lend the dispatcher, each one recording that it
    /// was asked rather than doing it.
    /// </summary>
    private static RuntimeClientCommandHostBindings WindowHooks(
        List<string> asked) => new()
    {
        ToggleFrameRate = () => asked.Add("toggleFrameRate"),
        SetUiLocked = locked => asked.Add($"setUiLocked:{locked}"),
        ShowConfirmation = (message, _) => asked.Add($"confirm:{message}"),
        SaveUi = name => asked.Add($"saveUi:{name}"),
        LoadUi = name => asked.Add($"loadUi:{name}"),
        SaveAutoUi = () => asked.Add("saveAutoUi"),
        LoadAutoUi = () => asked.Add("loadAutoUi"),
        FillComponentBuyList = (category, price) =>
            asked.Add($"fillComponents:{category}:{price}"),
        SetLandscapeRadius = radius => asked.Add($"radius:{radius}"),
        SetFieldOfView = degrees => asked.Add($"fov:{degrees}"),
    };

    [Fact]
    public void NoClientCommandThrowsOnEitherClient()
    {
        var threw = new List<string>();
        foreach (ClientCommandId id in Enum.GetValues<ClientCommandId>())
        {
            using var windowed = new WindowedArm();
            windowed.EnterWorld();
            using var windowless = new WindowlessArm();
            windowless.EnterWorld();

            Outcome withAWindow = Run(windowed, id, WindowHooks([]));
            Outcome without = Run(windowless, id, host: null);

            if (withAWindow.Threw is { } left)
                threw.Add($"{id} with a window: {left}");
            if (without.Threw is { } right)
                threw.Add($"{id} without one: {right}");
        }

        Assert.True(
            threw.Count == 0,
            "A client command threw into whoever typed it. Every command has "
            + "to answer, even if the answer is that it cannot:\n  "
            + string.Join("\n  ", threw));
    }

    [Fact]
    public void EveryClientCommandThatDoesNotNeedAWindowBehavesIdentically()
    {
        var differences = new List<string>();
        foreach (ClientCommandId id in Enum.GetValues<ClientCommandId>())
        {
            if (NeedAWindow.Contains(id))
                continue;

            using var windowed = new WindowedArm();
            windowed.EnterWorld();
            using var windowless = new WindowlessArm();
            windowless.EnterWorld();

            Outcome withAWindow = Run(windowed, id, WindowHooks([]));
            Outcome without = Run(windowless, id, host: null);

            if (!string.Equals(
                    withAWindow.Sent, without.Sent, StringComparison.Ordinal))
            {
                differences.Add(
                    $"{id} sent \"{withAWindow.Sent}\" with a window and "
                    + $"\"{without.Sent}\" without one");
            }
            if (!string.Equals(
                    withAWindow.Said, without.Said, StringComparison.Ordinal))
            {
                differences.Add(
                    $"{id} said \"{withAWindow.Said}\" with a window and "
                    + $"\"{without.Said}\" without one");
            }
        }

        Assert.True(
            differences.Count == 0,
            "A client command did something different on the two clients:\n  "
            + string.Join("\n  ", differences));
    }

    [Fact]
    public void ACommandThatNeedsAWindowSaysSoRatherThanDoingNothing()
    {
        var unexplained = new List<string>();
        foreach (ClientCommandId id in NeedAWindow)
        {
            using var windowed = new WindowedArm();
            windowed.EnterWorld();
            using var windowless = new WindowlessArm();
            windowless.EnterWorld();

            var asked = new List<string>();
            Outcome withAWindow = Run(windowed, id, WindowHooks(asked));
            Outcome without = Run(windowless, id, host: null);

            if (asked.Count == 0)
                unexplained.Add($"{id} asked a window for nothing");
            if (!without.Said.Contains(
                    RuntimeClientCommandBindings.NotAvailableWithoutAWindow,
                    StringComparison.Ordinal))
            {
                unexplained.Add(
                    $"{id} said \"{without.Said}\" without a window instead of "
                    + "saying it needs one");
            }
            if (without.Sent.Length != 0
                && !string.Equals(
                    withAWindow.Sent, without.Sent, StringComparison.Ordinal))
            {
                unexplained.Add(
                    $"{id} sent \"{without.Sent}\" without a window");
            }
        }

        Assert.True(
            unexplained.Count == 0,
            "A command that needs a window did not behave as declared:\n  "
            + string.Join("\n  ", unexplained));
    }
}
