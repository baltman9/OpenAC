using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Plugins;

namespace AcDream.Runtime.Tests.Plugins;

/// <summary>
/// The shared plugin surface's side of chat input interception: what a plugin
/// registers through <see cref="IPluginChat"/> is what the chat route on
/// either client is answered with, in order, with a throwing interceptor
/// logged once and skipped rather than breaking chat.
/// </summary>
public sealed class RuntimeAutomationSurfaceChatInterceptorTests
{
    [Fact]
    public void WithNoInterceptorEveryLinePasses()
    {
        using var surface = new RuntimeAutomationSurface();

        Assert.Equal(
            PluginChatInputAction.Pass,
            surface.InterceptChatInput("hello").Action);
    }

    [Fact]
    public void ARegisteredInterceptorDecidesTheLine()
    {
        using var surface = new RuntimeAutomationSurface();
        using IDisposable registration = surface.Chat.RegisterInputInterceptor(
            static typed => typed == "hello"
                ? PluginChatInputDecision.Rewrite("hi")
                : PluginChatInputDecision.Pass);

        PluginChatInputDecision decision = surface.InterceptChatInput("hello");

        Assert.Equal(PluginChatInputAction.Rewrite, decision.Action);
        Assert.Equal("hi", decision.Text);
        Assert.Equal(
            PluginChatInputAction.Pass,
            surface.InterceptChatInput("other").Action);
    }

    [Fact]
    public void TheFirstInterceptorThatDoesNotPassDecides()
    {
        using var surface = new RuntimeAutomationSurface();
        using IDisposable first = surface.Chat.RegisterInputInterceptor(
            static _ => PluginChatInputDecision.Pass);
        using IDisposable second = surface.Chat.RegisterInputInterceptor(
            static _ => PluginChatInputDecision.Suppress);
        using IDisposable third = surface.Chat.RegisterInputInterceptor(
            static _ => PluginChatInputDecision.Rewrite("never"));

        Assert.Equal(
            PluginChatInputAction.Suppress,
            surface.InterceptChatInput("hello").Action);
    }

    [Fact]
    public void DisposingTheRegistrationRemovesTheInterceptor()
    {
        using var surface = new RuntimeAutomationSurface();
        IDisposable registration = surface.Chat.RegisterInputInterceptor(
            static _ => PluginChatInputDecision.Suppress);

        registration.Dispose();

        Assert.Equal(
            PluginChatInputAction.Pass,
            surface.InterceptChatInput("hello").Action);
    }

    /// <summary>
    /// A plugin's interceptor that throws is a plugin bug, not a chat
    /// outage: the line carries on to the next interceptor, and the fault
    /// is reported the first time only so a broken plugin cannot flood the
    /// log once per keystroke.
    /// </summary>
    [Fact]
    public void AThrowingInterceptorIsLoggedOnceAndSkipped()
    {
        using var surface = new RuntimeAutomationSurface();
        var reported = new List<(string Verb, Exception Error)>();
        surface.ReportPluginCommandFailuresTo((verb, error) => reported.Add((verb, error)));
        using IDisposable broken = surface.Chat.RegisterInputInterceptor(
            static _ => throw new InvalidOperationException("boom"));
        using IDisposable next = surface.Chat.RegisterInputInterceptor(
            static _ => PluginChatInputDecision.Suppress);

        PluginChatInputDecision first = surface.InterceptChatInput("one");
        PluginChatInputDecision second = surface.InterceptChatInput("two");

        Assert.Equal(PluginChatInputAction.Suppress, first.Action);
        Assert.Equal(PluginChatInputAction.Suppress, second.Action);
        (string verb, Exception error) = Assert.Single(reported);
        Assert.Equal("boom", error.Message);
        Assert.NotEmpty(verb);
    }

    [Fact]
    public void AnInterceptorInstalledBeforeAnySessionStillApplies()
    {
        using var surface = new RuntimeAutomationSurface();
        using IDisposable registration = surface.Chat.RegisterInputInterceptor(
            static _ => PluginChatInputDecision.Suppress);

        using GameRuntime runtime = Gameplay.RuntimeCreatureDeathStateTests.Create();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);

        Assert.Equal(
            PluginChatInputAction.Suppress,
            surface.InterceptChatInput("hello").Action);

        surface.Unbind();
        // The interceptor belongs to the plugin, not the session: it is
        // still there for the next one.
        Assert.Equal(
            PluginChatInputAction.Suppress,
            surface.InterceptChatInput("hello").Action);
    }
}
