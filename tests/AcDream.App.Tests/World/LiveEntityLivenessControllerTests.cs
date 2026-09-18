using AcDream.App.World;
using AcDream.Runtime.Entities;

namespace AcDream.App.Tests.World;

public sealed class LiveEntityLivenessControllerTests
{
    [Fact]
    public void PruneBacklogHandsOutAtMostOneFrameBatchAtATime()
    {
        var backlog = new LiveEntityPruneBacklog();
        var due = new List<LiveEntityPruneCandidate>();
        for (uint i = 0; i < 10; i++)
            due.Add(new LiveEntityPruneCandidate(new RuntimeEntityKey(i, 1), i));
        backlog.Add(due);
        Assert.Equal(10, backlog.Count);

        var batch = new List<LiveEntityPruneCandidate>();
        backlog.TakeFrameBatch(batch);
        Assert.Equal(LiveEntityPruneBacklog.MaxPerFrame, batch.Count);
        Assert.Equal(10 - LiveEntityPruneBacklog.MaxPerFrame, backlog.Count);
        // Oldest deadline first, and each candidate is handed out once.
        Assert.Equal(new uint[] { 0, 1, 2, 3 }, batch.Select(c => c.ServerGuid));

        backlog.TakeFrameBatch(batch);
        Assert.Equal(new uint[] { 4, 5, 6, 7 }, batch.Select(c => c.ServerGuid));
        backlog.TakeFrameBatch(batch);
        Assert.Equal(new uint[] { 8, 9 }, batch.Select(c => c.ServerGuid));
        Assert.Equal(0, backlog.Count);

        backlog.TakeFrameBatch(batch);
        Assert.Empty(batch);
    }

    [Fact]
    public void PruneBacklogIsDroppedOnClear()
    {
        var backlog = new LiveEntityPruneBacklog();
        backlog.Add([new LiveEntityPruneCandidate(new RuntimeEntityKey(1, 1), 1)]);
        backlog.Clear();
        Assert.Equal(0, backlog.Count);
    }

    [Fact]
    public void OutOfRangeWorldEntityExpiresAfterTwentyFiveSeconds()
    {
        var tracker = new LiveEntityLivenessTracker();
        var samples = new[] { Sample(0x7000_0001u, generation: 4, visible: false) };

        Assert.Empty(tracker.Tick(10.0, samples));
        Assert.Empty(tracker.Tick(34.999, samples));
        Assert.Equal(
            new LiveEntityPruneCandidate(
                new RuntimeEntityKey(0x7000_0001u, 4),
                0x7000_0001u),
            Assert.Single(tracker.Tick(35.0, samples)));
        Assert.Equal(0, tracker.DeadlineCount);
    }

    [Fact]
    public void ReturningToVisibilityCancelsTheDeadline()
    {
        var tracker = new LiveEntityLivenessTracker();
        Assert.Empty(tracker.Tick(0.0, [Sample(1, 1, visible: false)]));
        Assert.Empty(tracker.Tick(20.0, [Sample(1, 1, visible: true)]));
        Assert.Empty(tracker.Tick(40.0, [Sample(1, 1, visible: false)]));
        Assert.Empty(tracker.Tick(64.9, [Sample(1, 1, visible: false)]));
        Assert.Single(tracker.Tick(65.0, [Sample(1, 1, visible: false)]));
    }

    [Fact]
    public void NonWorldRetentionCancelsTheDeadline()
    {
        var tracker = new LiveEntityLivenessTracker();
        Assert.Empty(tracker.Tick(0.0, [Sample(1, 1, visible: false)]));
        Assert.Empty(tracker.Tick(30.0, [Sample(1, 1, visible: false, retained: true)]));
        Assert.Equal(0, tracker.DeadlineCount);
    }

    [Fact]
    public void ReusedGuidGetsANewGenerationDeadline()
    {
        var tracker = new LiveEntityLivenessTracker();
        Assert.Empty(tracker.Tick(0.0, [Sample(1, 1, visible: false)]));
        Assert.Empty(tracker.Tick(24.0, [Sample(1, 2, visible: false)]));
        Assert.Empty(tracker.Tick(25.0, [Sample(1, 2, visible: false)]));
        Assert.Equal(
            new LiveEntityPruneCandidate(new RuntimeEntityKey(1, 2), 1),
            Assert.Single(tracker.Tick(49.0, [Sample(1, 2, visible: false)])));
    }

    [Fact]
    public void RemovedRecordDropsItsDeadline()
    {
        var tracker = new LiveEntityLivenessTracker();
        Assert.Empty(tracker.Tick(0.0, [Sample(1, 1, visible: false)]));

        Assert.Empty(tracker.Tick(30.0, []));

        Assert.Equal(0, tracker.DeadlineCount);
    }

    [Fact]
    public void VisibilityIsThePlayersLandblockAndItsEightNeighbours()
    {
        const uint player = 0x3032_0001u;

        Assert.True(LiveEntityLivenessController.IsWithinVisibleLandblocks(player, 0x3032_00A7u));
        Assert.True(LiveEntityLivenessController.IsWithinVisibleLandblocks(player, 0x3132_0001u));
        Assert.True(LiveEntityLivenessController.IsWithinVisibleLandblocks(player, 0x2F31_0001u));
        Assert.True(LiveEntityLivenessController.IsWithinVisibleLandblocks(player, 0x3133_00FFu));
        Assert.False(LiveEntityLivenessController.IsWithinVisibleLandblocks(player, 0x3232_0001u));
        Assert.False(LiveEntityLivenessController.IsWithinVisibleLandblocks(player, 0x3034_0001u));
        Assert.False(LiveEntityLivenessController.IsWithinVisibleLandblocks(player, 0x2E30_0001u));
    }

    [Fact]
    public void VisibilityInsideADungeonIsTheDungeonsOwnLandblock()
    {
        const uint playerCell = 0x01D9_0102u;

        Assert.True(LiveEntityLivenessController.IsWithinVisibleLandblocks(playerCell, 0x01D9_0140u));
        Assert.True(LiveEntityLivenessController.IsWithinVisibleLandblocks(playerCell, 0x01D9_FFFFu));
        Assert.False(LiveEntityLivenessController.IsWithinVisibleLandblocks(playerCell, 0xA9B4_0001u));
    }

    [Fact]
    public void VisibilityDoesNotDependOnDistanceInsideTheNeighbourhood()
    {
        // The far corner of a diagonal neighbour is ~543 m away and used to
        // fall outside the old 384 m sphere while the server still knew it.
        Assert.True(LiveEntityLivenessController.IsWithinVisibleLandblocks(0x3032_0001u, 0x3133_0001u));
    }

    private static LiveEntityLivenessSample Sample(
        uint guid,
        ushort generation,
        bool visible,
        bool retained = false) =>
        new(
            new RuntimeEntityKey(guid, generation),
            guid,
            visible,
            retained);
}
