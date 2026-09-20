// Copyright (c) OpenAC contributors.
// Distributed under the terms of the MIT license.

using AcDream.Plugin.Abstractions;
using AcDream.Plugin.Tests.Fixtures;

namespace AcDream.Plugin.Tests;

public sealed class FakePluginHostContractTests
{
    // ── Inert / default behavior ─────────────────────────────────────────

    [Fact]
    public void DefaultHostHasNoUi()
    {
        var host = new FakePluginHost();
        Assert.False(host.HasUi);
    }

    [Fact]
    public void DefaultAutomationSurfaceIsAvailable()
    {
        var host = new FakePluginHost();
        Assert.True(host.Automation.IsAvailable);
    }

    [Fact]
    public void DefaultStorageIsAvailable()
    {
        var host = new FakePluginHost();
        Assert.True(host.Storage.IsAvailable);
    }

    [Fact]
    public void RecallSurfaceIsInertWhenUnavailable()
    {
        var host = new FakePluginHost();
        Assert.False(host.Automation.Recalls.IsAvailable);
        Assert.False(host.Automation.Recalls.Recall(PluginRecallKind.House).Accepted);
        Assert.Equal(
            PluginRecallStatus.Unavailable,
            host.Automation.Recalls.Recall(PluginRecallKind.Allegiance).Status);
    }

    [Fact]
    public void DefaultUiRegistryIsNoOp()
    {
        var host = new FakePluginHost();
        Assert.False(host.Ui.ViewExists("any"));
    }

    [Fact]
    public void VtankProfilesAreNoOp()
    {
        var host = new FakePluginHost();
        Assert.False(host.VtankProfiles.IsAvailable);
    }

    [Fact]
    public void ClipboardIsNoOp()
    {
        var host = new FakePluginHost();
        Assert.False(host.Clipboard.TrySetText("text"));
    }

    [Fact]
    public void WindowIsNoOp()
    {
        var host = new FakePluginHost();
        Assert.False(host.Window.IsMinimized);
    }

    // ── Settable state ────────────────────────────────────────────────────

    [Fact]
    public void StorageReadsAndWritesRoundTrip()
    {
        var host = new FakePluginHost();
        host.PluginStorage.WriteText("key", "value");
        Assert.Equal("value", host.PluginStorage.ReadText("key"));
    }

    [Fact]
    public void StorageDeleteRemovesValue()
    {
        var host = new FakePluginHost();
        host.PluginStorage.WriteText("key", "value");
        host.PluginStorage.Delete("key");
        Assert.Null(host.PluginStorage.ReadText("key"));
    }

    [Fact]
    public void StorageListReturnsPrefixedKeys()
    {
        var host = new FakePluginHost();
        host.PluginStorage.WriteText("a/1", "one");
        host.PluginStorage.WriteText("a/2", "two");
        host.PluginStorage.WriteText("b/1", "other");
        Assert.Equal(["a/1", "a/2"], host.PluginStorage.List("a"));
    }

    [Fact]
    public void StorageCanBeMarkedUnavailable()
    {
        var host = new FakePluginHost();
        host.PluginStorage.Available = false;
        Assert.False(host.Storage.IsAvailable);
        Assert.Throws<NotSupportedException>(
            () => host.Storage.WriteText("key", "value"));
    }

    // ── Event dispatch ────────────────────────────────────────────────────

    [Fact]
    public void TickFiresRegisteredHandlers()
    {
        var host = new FakePluginHost();
        int count = 0;
        host.Events.Tick += _ => count++;
        host.PluginEvents.RaiseTick(0.1);
        Assert.Equal(1, count);
    }

    [Fact]
    public void TickDoesNotFireAfterUnsubscribe()
    {
        var host = new FakePluginHost();
        int count = 0;
        Action<double> handler = _ => count++;
        host.Events.Tick += handler;
        host.PluginEvents.RaiseTick(0.1);
        host.Events.Tick -= handler;
        host.PluginEvents.RaiseTick(0.1);
        Assert.Equal(1, count);
    }

    [Fact]
    public void NavigationChangedFiresRegisteredHandlers()
    {
        var host = new FakePluginHost();
        PluginGoToReport received = default;
        host.Events.NavigationChanged += r => received = r;
        var report = new PluginGoToReport(
            1, PluginGoToState.Planning, 0u, float.NaN, 0, "planning");
        host.PluginEvents.RaiseNavigationChanged(report);
        Assert.Equal(PluginGoToState.Planning, received.State);
    }

    [Fact]
    public void ObjectChangedFiresRegisteredHandlers()
    {
        var host = new FakePluginHost();
        PluginObjectChange? received = null;
        host.Events.ObjectChanged += c => received = c;
        host.PluginEvents.RaiseObjectChanged(
            new PluginObjectChange(0x50000001u, PluginObjectChangeKind.Created));
        Assert.NotNull(received);
        Assert.Equal(0x50000001u, received.Value.ObjectId);
    }

    [Fact]
    public void PortalTransitionFiresRegisteredHandlers()
    {
        var host = new FakePluginHost();
        PluginPortalTransition? received = null;
        host.Events.PortalTransition += transition => received = transition;
        host.PluginEvents.RaisePortalTransition(
            new PluginPortalTransition(4, 9, 0x1234u, true, true, false, false));
        Assert.NotNull(received);
        Assert.Equal(9, received.Value.Generation);
        Assert.Equal(0x1234u, received.Value.DestinationCell);
    }

    [Fact]
    public void ExceptionInOneTickHandlerDoesNotStopOthers()
    {
        var host = new FakePluginHost();
        int count = 0;
        host.Events.Tick += _ => throw new InvalidOperationException("first fails");
        host.Events.Tick += _ => count++;
        host.PluginEvents.RaiseTick(0.1);
        Assert.Equal(1, count);
    }

    // ── Command registry ──────────────────────────────────────────────────

    [Fact]
    public void CommandRegistryRecordsRegistrations()
    {
        var host = new FakePluginHost();
        host.Commands.Register("test", _ => { });
        Assert.Contains(host.PluginCommands.Registrations,
            r => r.Verb == "test");
    }

    [Fact]
    public void CommandRegistryInvokesHandler()
    {
        var host = new FakePluginHost();
        PluginCommand? received = null;
        host.Commands.Register("go", cmd => received = cmd);
        host.PluginCommands.Invoke("go", "Holtburg");
        Assert.NotNull(received);
        Assert.Equal("Holtburg", received.Value.Arguments);
    }

    [Fact]
    public void CommandRegistryDisposeRemovesRegistration()
    {
        var host = new FakePluginHost();
        IDisposable token = host.Commands.Register("test", _ => { });
        token.Dispose();
        bool invoked = host.PluginCommands.Invoke("test", "");
        Assert.False(invoked);
    }

    // ── Chat ──────────────────────────────────────────────────────────────

    [Fact]
    public void ChatCapturesSystemMessages()
    {
        var host = new FakePluginHost();
        host.PluginChat.PostSystemMessage("hello");
        Assert.Contains("hello", host.PluginChat.SystemMessages);
    }

    [Fact]
    public void ChatSubmitsText()
    {
        var host = new FakePluginHost();
        host.Automation.Chat.Submit("/go Holtburg");
        Assert.Contains("/go Holtburg", host.PluginChat.SubmittedText);
    }

    [Fact]
    public void ChatLinkClickedFires()
    {
        var host = new FakePluginHost();
        PluginChatLinkClicked? received = null;
        host.PluginChat.LinkClicked += link => received = link;
        var link = new PluginChatLinkClicked(
            PluginChatLinkKind.Coordinate,
            "42.1N, 33.6E",
            new PluginChatCoordinate(33.6, 42.1));
        host.PluginChat.RaiseLinkClicked(link);
        Assert.NotNull(received);
        Assert.Equal(PluginChatLinkKind.Coordinate, received.Value.Kind);
    }

    // ── Navigation ────────────────────────────────────────────────────────

    [Fact]
    public void NavigationSnapshotIsSettable()
    {
        var host = new FakePluginHost();
        var snap = new PluginNavigationSnapshot(
            IsAvailable: true,
            IsPortalSpace: false,
            LocalObjectId: 0x50000001u,
            Position: default,
            IsMoving: false,
            IsAirborne: false);
        host.PluginNavigation.SnapshotValue = snap;
        Assert.True(host.Automation.Navigation.Snapshot.IsAvailable);
        Assert.Equal(0x50000001u, host.Automation.Navigation.Snapshot.LocalObjectId);
    }

    [Fact]
    public void NavigationGoToRecordsCall()
    {
        var host = new FakePluginHost();
        host.Automation.Navigation.GoTo(0x50000001u, 2.5f);
        Assert.Contains((0x50000001u, 2.5f), host.PluginNavigation.GoToCalls);
    }

    [Fact]
    public void NavigationGoToReturnsConfiguredAnswer()
    {
        var host = new FakePluginHost();
        host.PluginNavigation.GoToAnswer =
            PluginNavigationCommandStatus.Rejected;
        Assert.Equal(
            PluginNavigationCommandStatus.Rejected,
            host.Automation.Navigation.GoTo(0x50000001u, 2.5f));
    }

    [Fact]
    public void NavigationSnapshotChangedFires()
    {
        var host = new FakePluginHost();
        PluginNavigationSnapshot? received = null;
        host.Automation.Navigation.SnapshotChanged += s => received = s;
        var snap = new PluginNavigationSnapshot(
            true, false, 0u, default, false, false);
        host.PluginNavigation.RaiseSnapshotChanged(snap);
        Assert.NotNull(received);
    }

    [Fact]
    public void NavigationTryGetObjectReturnsConfiguredObjects()
    {
        var host = new FakePluginHost();
        var obj = new PluginNavigationObject(
            0x50000001u, "Test", default);
        host.PluginNavigation.Objects[0x50000001u] = obj;
        Assert.True(
            host.Automation.Navigation.TryGetObject(
                0x50000001u, out PluginNavigationObject found));
        Assert.Equal("Test", found.Name);
    }

    // ── Logger ────────────────────────────────────────────────────────────

    [Fact]
    public void LoggerCapturesWarnings()
    {
        var host = new FakePluginHost();
        host.Log.Warn("something went wrong");
        Assert.Contains("something went wrong",
            host.PluginLogger.Warnings);
    }

    [Fact]
    public void LoggerCapturesErrors()
    {
        var host = new FakePluginHost();
        var ex = new InvalidOperationException("test");
        host.Log.Error("failed", ex);
        Assert.Contains(host.PluginLogger.Errors,
            e => e.Message == "failed" && e.Exception == ex);
    }
}
