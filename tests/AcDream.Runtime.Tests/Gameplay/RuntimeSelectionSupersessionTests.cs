using AcDream.Core.Physics;
using AcDream.Core.Selection;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Tests.Support;

namespace AcDream.Runtime.Tests.Gameplay;

/// <summary>
/// The server re-sending an object the player has picked out. It arrives as
/// a fresh incarnation under the same id, which retires the previous one
/// before registering the new one, so the selection hears "that object is
/// gone" immediately followed by "that object is here". Letting go on the
/// first half leaves the player with nothing selected every time the server
/// re-describes what they are looking at, and a plugin reading the selection
/// gets a zero.
///
/// Mutation check (2026-09-20), run: dropping the replacement case from the
/// runtime's selection follower turned
/// <see cref="ReSendingTheSelectedObjectKeepsItSelected"/> red and left
/// <see cref="TakingTheSelectedObjectOutOfTheWorldStillClearsIt"/> green.
/// </summary>
public sealed class RuntimeSelectionSupersessionTests
{
    private const uint Monster = 0x50000020u;

    [Fact]
    public void ReSendingTheSelectedObjectKeepsItSelected()
    {
        using var host = new NoWindowGameRuntimeHost();
        GameRuntime runtime = host.Runtime;
        RuntimeEntityTestSpawns.Add(
            runtime, Monster, 10f, 10f, RuntimeEntityTestSpawns.Monster(Monster));
        Assert.True(runtime.ActionOwner.Selection.Select(
            Monster, SelectionChangeSource.World));

        ReSend(runtime, Monster);

        Assert.Equal(Monster, runtime.ActionOwner.Selection.SelectedObjectId);
    }

    /// <summary>
    /// The object really leaving is still let go of: a follower that kept
    /// everything would pass the first test just as well.
    /// </summary>
    [Fact]
    public void TakingTheSelectedObjectOutOfTheWorldStillClearsIt()
    {
        using var host = new NoWindowGameRuntimeHost();
        GameRuntime runtime = host.Runtime;
        RuntimeEntityTestSpawns.Add(
            runtime, Monster, 10f, 10f, RuntimeEntityTestSpawns.Monster(Monster));
        Assert.True(runtime.ActionOwner.Selection.Select(
            Monster, SelectionChangeSource.World));

        Assert.True(runtime.EntityObjects.TryAcceptDelete(
            new AcDream.Core.Net.Messages.DeleteObject.Parsed(Monster, 1),
            isLocalPlayer: false,
            removeRetainedObject: true,
            out RuntimeEntityDeleteAcceptance acceptance));
        runtime.EntityObjects.CompleteAcceptedDelete(acceptance);

        Assert.Null(runtime.ActionOwner.Selection.SelectedObjectId);
    }

    /// <summary>
    /// A second create for an id already in the world, carrying a later
    /// instance than the first, which is how the server says "this is a new
    /// incarnation of that object".
    /// </summary>
    private static void ReSend(GameRuntime runtime, uint guid)
    {
        RuntimeEntityRecord record = runtime.EntityObjects
            .RegisterEntity(RuntimeEntityTestSpawns.Spawn(
                guid,
                0x01010001u,
                10f,
                10f,
                state: 0,
                z: 5f,
                instance: 2))
            .Canonical!;
        runtime.EntityObjects.ApplyAcceptedSpawn(
            record,
            record.CreateIntegrationVersion,
            record.Snapshot,
            replaceGeneration: true);
    }
}
