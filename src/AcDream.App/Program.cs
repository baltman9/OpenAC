using AcDream.App;
using AcDream.App.Configuration;
using AcDream.App.Credentials;
using AcDream.App.Plugins;
using AcDream.App.Platform;
using AcDream.App.Rendering;
using AcDream.Platform;
using Serilog;

GraphicalHostPlatformServices graphicalPlatform =
    GraphicalHostPlatformServices.Resolve();
graphicalPlatform.ConfigureWindowBackend();
ApplicationPathSet applicationPaths = graphicalPlatform.Paths;
// A client started by hand before the launcher ever ran brings the old
// per-user folders into the install root itself; one started by the launcher
// is handed an explicit root and leaves that to the launcher.
InstallRootMigrationResult installRootMigration =
    InstallRootMigration.RunIfNeeded(applicationPaths);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console()
    .CreateLogger();
InstallRootMigrationLog.Write(
    installRootMigration,
    applicationPaths.RootDirectory,
    line => Log.Information("{Line}", line),
    line => Log.Warning("{Line}", line));
Log.Information(
    "graphical platform {RuntimeIdentifier}; native closure: {NativeDependencies}",
    graphicalPlatform.RuntimeIdentifier,
    string.Join(
        ", ",
        graphicalPlatform.NativeDependencies.Select(
            dependency =>
                $"{dependency.Feature}={dependency.PublishedFileName}")));

string? sessionConfigFlagPath = SessionConfigArgumentParsing.ExtractFlagValue(
    args, "--session-config", out bool sessionConfigFlagPresent);
if (sessionConfigFlagPath is null && sessionConfigFlagPresent)
{
    Log.Error(
        "--session-config requires a value (a path to the session-config document).");
    return 2;
}
string[] positionalArgs =
    SessionConfigArgumentParsing.WithoutFlagAndValue(args, "--session-config");

var datDirArg = positionalArgs.FirstOrDefault();
var envDatDir = Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR");

RuntimeOptions runtimeOptions;
if (sessionConfigFlagPath is not null)
{
    SessionConfiguration sessionConfig;
    SessionDescriptor session;
    try
    {
        (sessionConfig, session) = SessionConfigurationLoader.Load(sessionConfigFlagPath);
    }
    catch (Exception error)
        when (error is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or System.Text.Json.JsonException
            or SessionConfigurationException)
    {
        Log.Error("--session-config invalid: {Error}", error.Message);
        return 2;
    }

    string? resolvedDatDir =
        NullIfEmpty(sessionConfig.Process?.Content?.DatDirectory)
        ?? NullIfEmpty(datDirArg)
        ?? NullIfEmpty(envDatDir);
    if (resolvedDatDir is null)
    {
        Log.Error(
            "usage: AcDream.App <dat-directory>  (or set ACDREAM_DAT_DIR, "
            + "or supply process.content.datDirectory in --session-config)");
        return 2;
    }

    AppCredentialSecret? secret = null;
    try
    {
        var resolver = new AppCredentialResolver(
            Console.In,
            applicationPaths.ConfigDirectory,
            graphicalPlatform.OperatingSystem is
                GraphicalHostOperatingSystem.Linux or
                GraphicalHostOperatingSystem.MacOS);
        secret = resolver.Resolve(session.Id, session.Credential);
        runtimeOptions = RuntimeOptions.FromSessionConfig(
            resolvedDatDir,
            Environment.GetEnvironmentVariable,
            sessionConfigFlagPath,
            sessionConfig,
            session,
            secret.Reveal());
    }
    catch (AppCredentialException error)
    {
        Log.Error("--session-config credential unavailable: {Error}", error.Message);
        return 2;
    }
    // A settings map written into the document is refused here in the same
    // words a settings file is refused below, rather than ending the client
    // with a stack trace.
    catch (AcDream.Runtime.Plugins.PluginSessionSettingsException error)
    {
        Log.Error("--session-config plugin settings invalid: {Error}", error.Message);
        return 2;
    }
    finally
    {
        secret?.Dispose();
    }

    // Env-var flow untouched when the flag is absent; when both are present
    // the flag wins — this line makes that explicit rather than silent.
    Log.Information(
        "--session-config {Path} present; overriding ACDREAM_LIVE*/ACDREAM_TEST_* "
        + "env-var live-session settings",
        sessionConfigFlagPath);
}
else
{
    var datDir = datDirArg ?? envDatDir;
    if (string.IsNullOrWhiteSpace(datDir))
    {
        Log.Error("usage: AcDream.App <dat-directory>  (or set ACDREAM_DAT_DIR)");
        return 2;
    }
    runtimeOptions = RuntimeOptions.FromEnvironment(datDir);
}

// The startup settings each plugin was given. A session-config document that
// names them is the more specific instruction and outranks the launch option
// pointing at a file. A named file that cannot be read is a startup fault and
// says so: a plugin whose settings silently came out empty would behave
// differently here than it does on the client with no window, which is the
// one thing these settings exist to prevent.
AcDream.Runtime.Plugins.PluginSessionSettings pluginSessionSettings;
try
{
    pluginSessionSettings =
        runtimeOptions.SessionPluginSettings
        ?? (runtimeOptions.PluginSettingsFile is { } settingsPath
            ? AcDream.Runtime.Plugins.PluginSessionSettings.ReadFile(settingsPath)
            : AcDream.Runtime.Plugins.PluginSessionSettings.Empty);
}
catch (AcDream.Runtime.Plugins.PluginSessionSettingsException error)
{
    Log.Error("ACDREAM_PLUGIN_SETTINGS_FILE invalid: {Error}", error.Message);
    return 2;
}

if (runtimeOptions.DevTools)
{
    Log.Information(
        "ACDREAM_DEVTOOLS=1: enables " +
        "the optional Vulkan validation/debug-utils extensions.");
}

var worldGameState = new AcDream.Core.Plugins.WorldGameState();
// A plugin handler that throws is skipped, not hidden: a plugin author
// whose handler stopped running needs to be told why, in the same words
// whichever client is running.
var worldEvents = new AcDream.Core.Plugins.WorldEvents(
    line => Log.Warning("{Line}", line));
var uiRegistry = new AcDream.App.Plugins.BufferedUiRegistry();
using var renderPackRegistry = new AcDream.App.Plugins.BufferedRenderPackRegistry();
using IDisposable atmosphericPackRegistration = renderPackRegistry.Register(
    AcDream.App.Rendering.Packs.BuiltInAtmosphericRenderPack.Descriptor,
    AcDream.App.Rendering.Packs.BuiltInAtmosphericRenderPack.CreateAssets(
        Path.Combine(
            AppContext.BaseDirectory,
            "Rendering",
            "Shaders",
            "spv")));
// One factory, shared with the windowless host: what the surface needs
// before a plugin can reach it cannot be present on one client and quietly
// absent on the other.
using var automation = AcDream.Runtime.Plugins.RuntimeAutomationBindings
    .CreateSurface(
        AcDream.App.Plugins.GraphicalAutomationCapabilities.BuildSurfaceInputs(
            new AcDream.App.Plugins.GraphicalSurfaceInputParts
            {
                Events = worldEvents,
                PeerDirectory = applicationPaths.PluginPeersDirectory,
                PluginTags = runtimeOptions.PluginTags,
            }));
var lootClassifiers = new AcDream.Core.Plugins.PluginLootClassifierRegistry();
var hotkeyRegistry = new AcDream.App.Input.AppHotkeyRegistry(
    Path.Combine(applicationPaths.ConfigDirectory, "plugin-hotkeys.json"));
using var window = new GameWindow(
    runtimeOptions,
    worldGameState,
    worldEvents,
    uiRegistry,
    graphicalPlatform,
    automation,
    renderPackRegistry,
    hotkeyRegistry);
var host = new AppPluginHost(
    new SerilogAdapter(Log.Logger),
    worldGameState,
    worldEvents,
    window.Selection,
    uiRegistry,
    automation,
    new AcDream.Core.Plugins.FilePluginStorage(
        Path.Combine(applicationPaths.ConfigDirectory, "plugins")),
    automation.PluginCommands,
    lootClassifiers,
    new AcDream.Core.Plugins.FilePluginStorage(
        runtimeOptions.VtankProfileDirectoryOverride
            ?? applicationPaths.VtankProfilesDirectory),
    new AcDream.App.Plugins.WindowPluginClipboard(
        () => window.ClipboardKeyboard,
        () => window.ClipboardDispatch),
    hotkeyRegistry,
    new AcDream.App.Plugins.WindowPluginHostWindow(
        () => window.PluginWindowHandle,
        () => window.PluginWindowIsMinimized,
        () => window.ClipboardDispatch),
    window.WorldLines,
    pluginSessionSettings);
GraphicalPluginSession pluginSession = GraphicalPluginSession.Create(
    applicationPaths,
    runtimeOptions.Plugins,
    runtimeOptions.SessionId ?? "app",
    host,
    window.StatusWriter,
    renderPackRegistry);
window.StartPluginHosting(pluginSession);

try
{
    try
    {
        window.Run();
    }
    catch (NotSupportedException error)
    {
        Log.Error("{GraphicalStartupFailure}", error.Message);
        return 4;
    }
}
finally
{
    Log.CloseAndFlush();
}

return 0;

static string? NullIfEmpty(string? value) =>
    string.IsNullOrWhiteSpace(value) ? null : value;
