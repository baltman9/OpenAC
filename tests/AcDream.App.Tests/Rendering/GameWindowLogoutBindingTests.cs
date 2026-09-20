using AcDream.App.Plugins;
using AcDream.App.Rendering;
using AcDream.App.Streaming;
using AcDream.App.Tests.Architecture;
using AcDream.Runtime.Plugins;
using AcDream.Runtime.Session;
using AcDream.Runtime.World;

namespace AcDream.App.Tests.Rendering;

public sealed class GameWindowLogoutBindingTests
{
    /// <summary>
    /// The window neither fills the seam nor decides what goes into it any
    /// more: the shared binding pass fills it, and what this host offers is
    /// built beside the host's declaration of what it can supply. The window
    /// still has to reach both, and the offer still has to be the teleport
    /// request behind the same four guards.
    /// </summary>
    [Fact]
    public void BindLogoutDelegatesUseTeleportRequestAndTheTransitAndWorldGuards()
    {
        var window = CompiledCallGraph.ReadOwned(typeof(GameWindow));

        Assert.Contains(
            window,
            call => call.Target.DeclaringType
                    == typeof(RuntimeAutomationBindings)
                && call.Target.Name == nameof(RuntimeAutomationBindings.Apply));
        Assert.Contains(
            window,
            call => call.Target.DeclaringType
                    == typeof(GraphicalAutomationCapabilities)
                && call.Target.Name
                    == nameof(GraphicalAutomationCapabilities.Build));

        var offer = CompiledCallGraph.ReadOwned(
            typeof(GraphicalAutomationCapabilities));

        Assert.Contains(
            offer,
            call => call.Target.DeclaringType
                    == typeof(RuntimeAutomationLogoutCommands)
                && call.Target.IsConstructor);
        Assert.Contains(
            offer,
            call => call.Target.DeclaringType == typeof(LocalPlayerTeleportController)
                && call.Target.Name
                    == nameof(LocalPlayerTeleportController.TryRequestLogout));
        Assert.Contains(
            offer,
            call => call.Target.DeclaringType == typeof(LiveSessionController)
                && call.Target.Name == "get_" + nameof(LiveSessionController.IsInWorld));
        Assert.Contains(
            offer,
            call => call.Target.DeclaringType == typeof(RuntimeWorldTransitState)
                && call.Target.Name == "get_" + nameof(RuntimeWorldTransitState.IsLogoutActive));
        Assert.Contains(
            offer,
            call => call.Target.DeclaringType == typeof(RuntimeWorldTransitState)
                && call.Target.Name == "get_" + nameof(RuntimeWorldTransitState.IsTeleportActive));
        Assert.Contains(
            offer,
            call => call.Target.DeclaringType == typeof(RuntimeWorldTransitState)
                && call.Target.Name
                    == "get_" + nameof(RuntimeWorldTransitState.HasPendingTeleportStart));
        Assert.DoesNotContain(
            offer,
            call => call.Target.DeclaringType == typeof(LocalPlayerTeleportController)
                && call.Target.Name == nameof(LocalPlayerTeleportController.RequestLogout));
    }
}
