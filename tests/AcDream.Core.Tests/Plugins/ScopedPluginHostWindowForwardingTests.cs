using System.Reflection;
using AcDream.Core.Plugins;
using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Tests.Plugins;

// IPluginHost.Window has a default interface implementation that falls
// back to NoOpHostWindow, exactly like Clipboard/VtankProfiles/Log/State
// already do. That means a forwarder ScopedPluginHost never writes
// compiles clean and silently hands every plugin the no-op window instead
// of the host's real one -- the same trap ScopedAutomationSurfaceTests and
// ScopedEventsTests exist to catch for their own interfaces. This test
// does the same for IPluginHost's own direct-forward members: it walks the
// interface by reflection and requires every member not named in the
// explicitly-wrapped set to be the exact same object the inner host
// returns, so a future direct-forward property cannot go unforwarded
// without a build-time-visible failure. A future member that legitimately
// needs its own wrapper (like Events, Selection, Ui, Automation, Storage,
// Commands, LootClassifiers, Hotkeys, and the per-plugin SessionSettings
// already do) is added to the wrapped set deliberately, in the same commit
// that adds the wrapper -- not silently skipped.
public sealed class ScopedPluginHostWindowForwardingTests
{
    private static readonly HashSet<string> WrappedMembers =
    [
        nameof(IPluginHost.HasUi), // bool value type; no host identity to preserve
        nameof(IPluginHost.Events),
        nameof(IPluginHost.Selection),
        nameof(IPluginHost.Ui),
        nameof(IPluginHost.Storage),
        nameof(IPluginHost.Commands),
        nameof(IPluginHost.LootClassifiers),
        nameof(IPluginHost.Hotkeys),
        nameof(IPluginHost.Automation),
        nameof(IPluginHost.SessionSettings),
    ];

    [Fact]
    public void EveryDirectForwardMemberIsTheHostsOwnObject()
    {
        var host = new StubHost();
        var scoped = new ScopedPluginHost(host, "example.plugin", "Example");

        PropertyInfo[] properties = typeof(IPluginHost).GetProperties();
        Assert.True(
            properties.Length >= 14,
            "IPluginHost should still have every member this test knows about.");

        var checkedMembers = new List<string>();
        foreach (PropertyInfo property in properties)
        {
            if (WrappedMembers.Contains(property.Name))
                continue;

            object? innerValue = property.GetValue(host);
            object? scopedValue = property.GetValue(scoped);
            Assert.True(
                ReferenceEquals(innerValue, scopedValue),
                "IPluginHost." + property.Name + " is not forwarded: the "
                    + "scoped host returned a different object than the "
                    + "real host's own property (likely the interface's "
                    + "no-op default).");
            checkedMembers.Add(property.Name);
        }

        // Log, State, VtankProfiles, Clipboard, Window.
        Assert.Equal(5, checkedMembers.Count);
        Assert.Contains(nameof(IPluginHost.Window), checkedMembers);

        scoped.Dispose();
    }

    private sealed class StubHost : IPluginHost
    {
        public bool HasUi => false;
        public IPluginLogger Log { get; } = new SilentLogger();
        public IGameState State { get; } = new EmptyGameState();
        public IEvents Events { get; } = new AcDream.Core.Plugins.WorldEvents();
        public ISelectionService Selection { get; } = new InertSelection();
        public IUiRegistry Ui { get; } = NoOpUiRegistry.Instance;
        public IAutomationSurface Automation { get; } = NoOpAutomationSurface.Instance;
        public IPluginStorage Storage { get; } = NoOpPluginStorage.Instance;
        public IPluginStorage VtankProfiles { get; } = NoOpPluginStorage.Instance;
        public IPluginClipboard Clipboard { get; } = NoOpPluginClipboard.Instance;
        public IHostWindow Window { get; } = new FakeHostWindow();

        private sealed class FakeHostWindow : IHostWindow;

        private sealed class SilentLogger : IPluginLogger
        {
            public void Info(string message) { }
            public void Warn(string message) { }
            public void Error(string message, Exception? error = null) { }
        }

        private sealed class EmptyGameState : IGameState
        {
            public IReadOnlyList<WorldEntitySnapshot> Entities { get; } = [];
        }

        private sealed class InertSelection : ISelectionService
        {
            public uint? SelectedObjectId => null;
            public uint? PreviousObjectId => null;
            public event Action<SelectionChangedEvent>? Changed;
            public bool Select(uint objectId)
            {
                Changed?.Invoke(default);
                return false;
            }
            public bool Clear() => false;
        }
    }
}
