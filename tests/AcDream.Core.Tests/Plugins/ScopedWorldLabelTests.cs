using System.Numerics;
using AcDream.Core.Plugins;
using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Tests.Plugins;

// A host whose labels can tell plugins apart hands each plugin its own view
// through the scoped host, and takes that plugin's labels down when the
// plugin is disposed. A host whose labels cannot is forwarded as it is.
public sealed class ScopedWorldLabelTests
{
    [Fact]
    public void APluginGetsItsOwnLabelViewAndItIsReleasedWhenThePluginGoes()
    {
        var labels = new ScopingLabels();
        var host = new StubHost(labels);
        var scoped = new ScopedPluginHost(host, "example.plugin", "Example");

        IWorldLabelAutomation first = scoped.Automation.Labels;
        IWorldLabelAutomation again = scoped.Automation.Labels;

        Assert.Same(first, again);
        Assert.NotSame(labels, first);
        Assert.Equal(["example.plugin"], labels.Scoped);
        Assert.Empty(labels.Released);

        scoped.Dispose();

        Assert.Equal(["example.plugin"], labels.Released);
    }

    [Fact]
    public void AHostWhoseLabelsCannotTellPluginsApartIsForwardedAsItIs()
    {
        var host = new StubHost(NoOpAutomationSurface.Instance.Labels);
        var scoped = new ScopedPluginHost(host, "example.plugin", "Example");

        Assert.Same(host.Automation.Labels, scoped.Automation.Labels);

        scoped.Dispose();
    }

    [Fact]
    public void ADisposedPluginPushesIntoNothing()
    {
        var labels = new ScopingLabels();
        var host = new StubHost(labels);
        var scoped = new ScopedPluginHost(host, "example.plugin", "Example");
        scoped.Dispose();

        Assert.False(scoped.Automation.Labels.ShowLabels(
            [new PluginWorldLabel(1u, "x", Vector4.One)]));
        Assert.Empty(labels.Scoped);
    }

    private sealed class ScopingLabels : IWorldLabelAutomation, IScopedWorldLabelSource
    {
        public List<string> Scoped { get; } = [];
        public List<string> Released { get; } = [];

        public IWorldLabelAutomation ScopeTo(string ownerId)
        {
            Scoped.Add(ownerId);
            return new View();
        }

        public void Release(string ownerId) => Released.Add(ownerId);

        private sealed class View : IWorldLabelAutomation
        {
            public bool ShowLabels(IReadOnlyList<PluginWorldLabel> labels) => true;
        }
    }

    private sealed class StubHost(IWorldLabelAutomation labels) : IPluginHost
    {
        public bool HasUi => false;
        public IPluginLogger Log { get; } = new InertLogger();
        public IGameState State { get; } = new EmptyGameState();
        public IEvents Events { get; } = new WorldEvents();
        public ISelectionService Selection { get; } = new InertSelection();
        public IUiRegistry Ui => NoOpUiRegistry.Instance;
        public IPluginStorage Storage => NoOpPluginStorage.Instance;
        public IPluginStorage VtankProfiles => NoOpPluginStorage.Instance;
        public IPluginCommandRegistry Commands => NoOpPluginCommandRegistry.Instance;
        public IAutomationSurface Automation { get; } = new Surface(labels);

        private sealed class Surface(IWorldLabelAutomation labels) : IAutomationSurface
        {
            public bool IsAvailable => false;
            public ICharacterInfo Character => NoOpAutomationSurface.Instance.Character;
            public ISpellCatalog Spells => NoOpAutomationSurface.Instance.Spells;
            public IMagicCommands Magic => NoOpAutomationSurface.Instance.Magic;
            public IPluginChat Chat => NoOpAutomationSurface.Instance.Chat;
            public IWorldLabelAutomation Labels => labels;
        }
    }

    private sealed class InertLogger : IPluginLogger
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
