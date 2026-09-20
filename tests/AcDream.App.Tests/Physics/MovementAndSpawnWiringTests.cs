using System.Reflection;
using AcDream.App.Net;
using AcDream.App.Physics;
using AcDream.App.Tests.Architecture;
using AcDream.Core.Physics;
using AcDream.Runtime.Gameplay;

namespace AcDream.App.Tests.Physics;

public sealed class MovementAndSpawnWiringTests
{
    /// <summary>
    /// The two occasions to re-derive how fast the character moves -- a skill
    /// raise and a movement-stat update -- are named where the character
    /// bindings are built, and both hosts build them from the runtime's own
    /// owner. The owner itself, and resetting it with the session, is pinned
    /// beside that owner.
    /// </summary>
    [Fact]
    public void MovementStats_AreReDerivedOnBothOccasionsFromTheRuntimeOwner()
    {
        MethodInfo buildCharacterBindings = typeof(AcDream.App.Plugins
            .GraphicalAutomationCapabilities).GetMethod(
                "BuildCharacterSessionBindings",
                BindingFlags.Static | BindingFlags.NonPublic)!;
        MethodBase[] reasons = CompiledCallGraph
            .ReadMethodReferences(buildCharacterBindings)
            .Select(call => call.Target)
            .Where(target => target.Name.Contains('<', StringComparison.Ordinal)
                && target.GetMethodBody() is not null)
            .Distinct()
            .ToArray();
        _ = Assert.Single(
            reasons,
            reason => CompiledCallGraph.ReadStringLiterals(reason)
                .Contains("skills"));
        _ = Assert.Single(
            reasons,
            reason => CompiledCallGraph.ReadStringLiterals(reason)
                .Contains("stats"));
        Assert.DoesNotContain(
            CompiledCallGraph.ReadDeclared(typeof(LiveSessionRuntimeFactory)),
            call => call.Target.DeclaringType == typeof(MotionInterpreter)
                && call.Target.Name == nameof(MotionInterpreter.ReportExhaustion));
    }

    [Fact]
    public void RemoteSpawnSettle_IsRetriedAndCoversBothCreationRoutes()
    {
        const BindingFlags flags = BindingFlags.Instance
            | BindingFlags.Static
            | BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.DeclaredOnly;
        MethodInfo[] callers = typeof(LiveEntityNetworkUpdateController)
            .GetMethods(flags)
            .Where(method => method.GetMethodBody() is not null)
            .Where(method => CompiledCallGraph.Read(method).Any(call =>
                call.Target.DeclaringType
                    == typeof(LiveEntityNetworkUpdateController)
                && call.Target.Name == "SeedRemoteSpawnPlacement"))
            .ToArray();

        Assert.Equal(2, callers.Length);
        Assert.Equal(
            ["DispatchRemoteInboundMotion", "OnPosition"],
            callers.Select(method => method.Name).Order().ToArray());
        MethodInfo retryingInboundRoute = Assert.Single(
            callers,
            method => method.Name == "DispatchRemoteInboundMotion");
        IReadOnlyList<CompiledCall> retryCalls =
            CompiledCallGraph.Read(retryingInboundRoute);
        int contact = CompiledCallGraph.IndexOf(
            retryCalls,
            typeof(PhysicsBody),
            "get_InContact");
        int settle = CompiledCallGraph.IndexOf(
            retryCalls,
            typeof(LiveEntityNetworkUpdateController),
            "SeedRemoteSpawnPlacement");
        Assert.True(contact >= 0 && settle > contact);
        MethodInfo seed = typeof(LiveEntityNetworkUpdateController).GetMethod(
            "SeedRemoteSpawnPlacement",
            flags)
            ?? throw new MissingMethodException(
                typeof(LiveEntityNetworkUpdateController).FullName,
                "SeedRemoteSpawnPlacement");
        Assert.Contains(
            CompiledCallGraph.Read(seed),
            call => call.Target.DeclaringType == typeof(SpawnPlacementSettler)
                && call.Target.Name == nameof(SpawnPlacementSettler.TrySettle));
    }
}
