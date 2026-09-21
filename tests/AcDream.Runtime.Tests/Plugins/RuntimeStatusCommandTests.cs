using AcDream.Runtime;
using AcDream.Runtime.Chat;
using AcDream.Runtime.Plugins;
using AcDream.Runtime.Tests.Gameplay;

namespace AcDream.Runtime.Tests.Plugins;

/// <summary>
/// <c>/status</c> used to be a verb the headless console answered for itself,
/// before the router saw it, so it existed on a console and nowhere else and
/// no chat box could ask it. It is now a verb of the client's, registered by
/// the binding pass both clients run, with one answer built in one place.
/// </summary>
public sealed class RuntimeStatusCommandTests
{
    private static RuntimeAutomationHostCapabilities Capabilities() => new()
    {
        HostName = "a client under test",
        Declared = new HashSet<string>(StringComparer.Ordinal),
    };

    [Fact]
    public void TheBindingPassRegistersStatusOnTheOneRegistry()
    {
        using GameRuntime runtime = RuntimeCreatureDeathStateTests.Create();
        using var surface = new RuntimeAutomationSurface();

        IReadOnlySet<string> bound = RuntimeAutomationBindings.Apply(
            surface, runtime, Capabilities());

        Assert.Contains("BindStatusCommand", bound);
        Assert.True(surface.TryHandlePluginCommand("/status"));
        Assert.Contains(
            runtime.CommunicationOwner.Chat.Snapshot(),
            entry => entry.Text.Contains(
                "generation=", StringComparison.Ordinal)
                && entry.Text.Contains("position=", StringComparison.Ordinal));
    }

    /// <summary>
    /// The answer is US-formatted whatever the machine's locale is, and says
    /// the same things whichever front end asked.
    /// </summary>
    [Fact]
    public void TheAnswerNamesTheGenerationTheLifeAndThePlace()
    {
        using GameRuntime runtime = RuntimeCreatureDeathStateTests.Create();

        string text = RuntimeSessionStatusText.For(runtime);

        Assert.StartsWith("generation=", text, StringComparison.Ordinal);
        Assert.Contains("state=", text, StringComparison.Ordinal);
        Assert.Contains("position=unknown", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A front end with a verb of its own has to be able to ask whether a
    /// plugin took it first, or it would shadow the plugin silently.
    /// </summary>
    [Fact]
    public void TheRegistrySaysWhichVerbsAreAlreadySpokenFor()
    {
        using GameRuntime runtime = RuntimeCreatureDeathStateTests.Create();
        using var surface = new RuntimeAutomationSurface();
        RuntimeAutomationBindings.Apply(surface, runtime, Capabilities());

        Assert.True(surface.ClaimsPluginVerb("status"));
        Assert.True(surface.ClaimsPluginVerb("/nav"));
        Assert.True(surface.ClaimsPluginVerb("@motor"));
        Assert.False(surface.ClaimsPluginVerb("quit"));
        Assert.False(surface.ClaimsPluginVerb(string.Empty));
    }
}
