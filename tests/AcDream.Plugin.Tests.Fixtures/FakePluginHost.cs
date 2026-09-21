// Copyright (c) OpenAC contributors.
// Distributed under the terms of the MIT license.

using AcDream.Plugin.Abstractions;

namespace AcDream.Plugin.Tests.Fixtures;

public sealed class FakePluginHost : IPluginHost
{
    public FakePluginHost()
    {
        PluginLogger = new FakePluginLogger();
        PluginStorage = new FakePluginStorage();
        PluginCommands = new FakePluginCommandRegistry();
        PluginEvents = new FakeEvents();
        PluginChat = new FakePluginChat();
        PluginNavigation = new FakeNavigationAutomation();
        SelectionValue = new NoOpSelectionService();
        StateValue = new NoOpGameState();
    }

    public bool HasUiValue { get; set; }
    public FakePluginLogger PluginLogger { get; set; }
    public FakePluginStorage PluginStorage { get; set; }
    public FakePluginCommandRegistry PluginCommands { get; set; }
    public FakeEvents PluginEvents { get; set; }
    public FakePluginChat PluginChat { get; set; }
    public FakeNavigationAutomation PluginNavigation { get; set; }
    public IAutomationSurface AutomationValue { get; set; } = null!;
    public IGameState StateValue { get; set; }
    public ISelectionService SelectionValue { get; set; }
    public IReadOnlyDictionary<string, string> SettingsValue { get; set; } =
        new Dictionary<string, string>();

    public bool HasUi => HasUiValue;
    public IPluginLogger Log => PluginLogger;
    public IGameState State => StateValue;
    public IEvents Events => PluginEvents;
    public ISelectionService Selection => SelectionValue;
    public IUiRegistry Ui => NoOpUiRegistry.Instance;
    public IPluginCommandRegistry Commands => PluginCommands;
    public IPluginStorage Storage => PluginStorage;
    public IAutomationSurface Automation =>
        AutomationValue ?? CreateDefaultAutomation();
    public IPluginStorage VtankProfiles => NoOpPluginStorage.Instance;
    public IPluginClipboard Clipboard => NoOpPluginClipboard.Instance;
    public IHostWindow Window => NoOpHostWindow.Instance;
    public IReadOnlyDictionary<string, string> SessionSettings => SettingsValue;

    private IAutomationSurface CreateDefaultAutomation()
    {
        var automation = new FakeAutomationSurface();
        automation.Navigation = PluginNavigation;
        automation.Chat = PluginChat;
        return automation;
    }

    public sealed class FakePluginLogger : IPluginLogger
    {
        public List<string> Warnings { get; } = [];
        public List<(string Message, Exception? Exception)> Errors { get; } = [];

        public void Info(string message) { }
        public void Warn(string message) => Warnings.Add(message);
        public void Error(string message, Exception? exception = null) =>
            Errors.Add((message, exception));
    }

    private sealed class NoOpGameState : IGameState
    {
        public static NoOpGameState Instance { get; } = new();
        public IReadOnlyList<WorldEntitySnapshot> Entities =>
            Array.Empty<WorldEntitySnapshot>();
        public IReadOnlyList<ContractSnapshot> Contracts =>
            Array.Empty<ContractSnapshot>();
    }

    private sealed class NoOpSelectionService : ISelectionService
    {
        public static NoOpSelectionService Instance { get; } = new();
        public uint? SelectedObjectId => null;
        public uint? PreviousObjectId => null;
        public event Action<SelectionChangedEvent> Changed
        {
            add { }
            remove { }
        }
        public bool Select(uint objectId) => false;
        public bool Clear() { return true; }
        public bool TryGetSelectedWorldPosition(
            out System.Numerics.Vector3 position,
            out uint cellId)
        {
            position = default;
            cellId = 0u;
            return false;
        }
    }
}
