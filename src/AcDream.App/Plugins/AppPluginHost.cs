using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Plugins;

namespace AcDream.App.Plugins;

public sealed class AppPluginHost : IPluginHost, IPerPluginSessionSettings
{
    // The one snapshot both clients answer a plugin from, so the settings a
    // run was started with read the same whichever client is running.
    private readonly PluginSessionSettings _sessionSettings;

    public AppPluginHost(
        IPluginLogger log,
        IGameState state,
        IEvents events,
        ISelectionService selection,
        IUiRegistry ui,
        IAutomationSurface automation,
        IPluginStorage? storage = null,
        IPluginCommandRegistry? commands = null,
        IPluginLootClassifierRegistry? lootClassifiers = null,
        IPluginStorage? vtankProfiles = null,
        IPluginClipboard? clipboard = null,
        IHotkeyRegistry? hotkeys = null,
        IHostWindow? window = null,
        IPluginWorldLines? worldLines = null,
        PluginSessionSettings? sessionSettings = null,
        IPluginMapRegistry? maps = null,
        IPluginMapResourceCatalog? mapResources = null,
        IPluginRenderRegistry? rendering = null)
    {
        Log = log;
        State = state;
        Events = events;
        Selection = selection;
        Ui = ui;
        Automation = automation;
        Storage = storage ?? NoOpPluginStorage.Instance;
        Commands = commands ?? NoOpPluginCommandRegistry.Instance;
        LootClassifiers = lootClassifiers
            ?? NoOpPluginLootClassifierRegistry.Instance;
        VtankProfiles = vtankProfiles ?? NoOpPluginStorage.Instance;
        Clipboard = clipboard ?? NoOpPluginClipboard.Instance;
        Hotkeys = hotkeys ?? NoOpHotkeyRegistry.Instance;
        Window = window ?? NoOpHostWindow.Instance;
        WorldLines = worldLines ?? NoOpPluginWorldLines.Instance;
        _sessionSettings = sessionSettings ?? PluginSessionSettings.Empty;
        Maps = maps ?? NoOpPluginMapRegistry.Instance;
        MapResources = mapResources ?? NoOpPluginMapResourceCatalog.Instance;
        Rendering = rendering ?? NoOpPluginRenderRegistry.Instance;
    }

    public bool HasUi => true;
    public IPluginWorldLines WorldLines { get; }
    public IPluginLogger Log { get; }
    public IGameState State { get; }
    public IEvents Events { get; }
    public ISelectionService Selection { get; }
    public IUiRegistry Ui { get; }
    public IAutomationSurface Automation { get; }
    public IPluginStorage Storage { get; }
    public IPluginCommandRegistry Commands { get; }
    public IPluginLootClassifierRegistry LootClassifiers { get; }
    public IPluginStorage VtankProfiles { get; }
    public IPluginClipboard Clipboard { get; }
    public IHotkeyRegistry Hotkeys { get; }
    public IHostWindow Window { get; }

    /// <summary>
    /// The startup settings this run was given for one plugin. Read through
    /// the per-plugin wrapper rather than directly.
    /// </summary>
    /// <param name="pluginId">Which plugin is asking.</param>
    public IReadOnlyDictionary<string, string> SessionSettingsFor(string pluginId) =>
        _sessionSettings.SessionSettingsFor(pluginId);
    public IPluginMapRegistry Maps { get; }
    public IPluginMapResourceCatalog MapResources { get; }
    public IPluginRenderRegistry Rendering { get; }
}
