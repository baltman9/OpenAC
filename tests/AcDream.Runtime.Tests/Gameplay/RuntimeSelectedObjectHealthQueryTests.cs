using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Selection;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Session;

namespace AcDream.Runtime.Tests.Gameplay;

/// <summary>
/// Asking the server to stream the selected creature's health is a question
/// the client asks, not something a window draws, so a host with no window
/// asks it too. Mutation: move the subscription back behind a window binding
/// and every one of these goes red on the no-window runtime.
/// </summary>
public sealed class RuntimeSelectedObjectHealthQueryTests
{
    private const uint Player = 0x50000001u;
    private const uint Monster = 0x50000020u;
    private const uint Crate = 0x70000030u;

    [Fact]
    public void SelectingACreatureAsksForItsHealth()
    {
        var sent = new List<uint>();
        var selection = new SelectionState();
        using RuntimeSelectedObjectHealthQuery query = Query(selection, sent);

        selection.Select(Monster, SelectionChangeSource.World);

        Assert.Equal(new[] { Monster }, sent);
        Assert.Equal(Monster, query.StreamingObjectId);
    }

    [Fact]
    public void ClearingTheSelectionStopsTheStream()
    {
        var sent = new List<uint>();
        var selection = new SelectionState();
        using RuntimeSelectedObjectHealthQuery query = Query(selection, sent);
        selection.Select(Monster, SelectionChangeSource.World);

        selection.Clear(SelectionChangeSource.System);

        Assert.Equal(new[] { Monster, 0u }, sent);
        Assert.Equal(0u, query.StreamingObjectId);
    }

    [Fact]
    public void MovingToAnotherCreatureClosesTheFirstStreamAndOpensTheSecond()
    {
        var sent = new List<uint>();
        var selection = new SelectionState();
        using RuntimeSelectedObjectHealthQuery query = Query(selection, sent);
        selection.Select(Monster, SelectionChangeSource.World);

        selection.Select(Crate, SelectionChangeSource.World);

        // The crate is not something whose health the server would report, so
        // the stream is closed and no new one is asked for.
        Assert.Equal(new[] { Monster, 0u }, sent);
        Assert.Equal(0u, query.StreamingObjectId);
    }

    [Fact]
    public void AnObjectWithNoHealthIsNeverAskedAbout()
    {
        var sent = new List<uint>();
        var selection = new SelectionState();
        using RuntimeSelectedObjectHealthQuery query = Query(selection, sent);

        selection.Select(Crate, SelectionChangeSource.World);

        Assert.Empty(sent);
        Assert.Equal(0u, query.StreamingObjectId);
    }

    /// <summary>
    /// The binding itself: a runtime with no window, routed exactly as the
    /// no-window host routes it, asks for the selected creature's health.
    /// </summary>
    [Fact]
    public void TheNoWindowRuntimeAsksThroughTheSharedRouting()
    {
        using GameRuntime runtime = RuntimeCreatureDeathStateTests.Create();
        using WorldSession session = RuntimeCreatureDeathStateTests.NewSession();
        var sent = new List<byte[]>();
        session.GameMessageCapture = (body, _) => sent.Add(body);
        runtime.PlayerIdentity.ServerGuid = Player;
        RuntimeCreatureDeathStateTests.AddPlayer(runtime);
        RuntimeCreatureDeathStateTests.AddMonster(runtime, Monster);
        using LiveSessionEventRouter router =
            RuntimeCreatureDeathStateTests.Router(session, runtime);
        router.Attach();

        runtime.ActionOwner.Selection.Select(Monster, SelectionChangeSource.World);

        Assert.NotEmpty(sent);
    }

    private static RuntimeSelectedObjectHealthQuery Query(
        SelectionState selection,
        List<uint> sent)
    {
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = Player,
            Type = ItemType.Creature,
            PublicWeenieBitfield = SelectedObjectHealthPolicy.BfPlayer,
        });
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = Monster,
            Type = ItemType.Creature,
            PublicWeenieBitfield = SelectedObjectHealthPolicy.BfAttackable,
        });
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = Crate,
            Type = ItemType.Container,
        });
        return new RuntimeSelectedObjectHealthQuery(
            selection,
            objects,
            () => Player,
            sent.Add);
    }
}
