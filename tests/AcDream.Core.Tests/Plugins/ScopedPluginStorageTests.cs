using System.Reflection;
using AcDream.Core.Plugins;
using AcDream.Core.Selection;
using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Tests.Plugins;

public sealed class ScopedPluginStorageTests
{
    /// <summary>
    /// A plugin's keys land in &lt;id&gt;/files, the folder installs and updates
    /// never touch. Mutation: dropping the files segment fails this.
    /// </summary>
    [Fact]
    public void EnsureDirectoryLandsUnderThePluginsOwnFolder()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"acdream-plugin-storage-{Guid.NewGuid():N}");
        try
        {
            using var scope = new ScopedPluginHost(
                new StubHost(new FilePluginStorage(root)),
                "acdream.example",
                "Example Plugin");

            Assert.True(scope.Storage.EnsureDirectory("profiles/character/"));

            // The plugin asked for "profiles/character"; the wrapper is what
            // keeps it inside the plugin's own namespace.
            Assert.True(Directory.Exists(Path.Combine(
                root, "acdream.example", "files", "profiles", "character")));
            Assert.False(Directory.Exists(
                Path.Combine(root, "profiles")));
            Assert.False(Directory.Exists(
                Path.Combine(root, "acdream.example", "profiles")));

            scope.Storage.WriteText("a/b.json", "{}");
            Assert.Equal(["a/b.json"], scope.Storage.List("a"));
            Assert.True(File.Exists(Path.Combine(
                root, "acdream.example", "files", "a", "b.json")));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AnEmptyPrefixListsThePluginsWholeFolderAndNothingElse()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"acdream-plugin-storage-{Guid.NewGuid():N}");
        try
        {
            var shared = new FilePluginStorage(root);
            shared.WriteText("acdream.other/secret.txt", "not yours");
            using var scope = new ScopedPluginHost(
                new StubHost(shared),
                "acdream.example",
                "Example Plugin");
            scope.Storage.WriteText("settings.json", "{}");
            scope.Storage.WriteText("profiles/a.utl", "x");

            Assert.Equal(
                ["profiles/a.utl", "settings.json"],
                scope.Storage.List(string.Empty));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EveryStorageMemberIsForwardedByTheScopedWrapper()
    {
        using var scope = new ScopedPluginHost(
            new StubHost(NoOpPluginStorage.Instance),
            "acdream.example",
            "Example Plugin");

        Type wrapper = scope.Storage.GetType();
        InterfaceMapping map = wrapper.GetInterfaceMap(typeof(IPluginStorage));

        // OpenScope is deliberately left at the interface default: a scope
        // opened through the wrapper stays the wrapper, so a plugin cannot
        // step outside its own namespace by opening one.
        var knownUnforwarded = new[] { nameof(IPluginStorage.OpenScope) };

        var checkedMembers = new List<string>();
        for (int i = 0; i < map.InterfaceMethods.Length; i++)
        {
            MethodInfo declared = map.InterfaceMethods[i];
            string name = declared.Name;
            if (knownUnforwarded.Any(known =>
                    name.EndsWith(known, StringComparison.Ordinal)))
            {
                continue;
            }

            Assert.True(
                map.TargetMethods[i].DeclaringType == wrapper,
                "IPluginStorage." + name + " is not forwarded: the scoped "
                    + "storage fell through to the interface's inert default, "
                    + "so the plugin's namespace is bypassed or the call does "
                    + "nothing. Forward it in ScopedPluginHost.");
            checkedMembers.Add(name);
        }

        // Guards the loop against silently walking nothing, and fails when
        // the interface grows a member this census was written before.
        Assert.Equal(7, checkedMembers.Count);
    }

    private sealed class StubHost(IPluginStorage storage) : IPluginHost
    {
        public bool HasUi => false;
        public IPluginLogger Log { get; } = new StubLogger();
        public IGameState State { get; } = new WorldGameState();
        public IEvents Events { get; } = new WorldEvents();
        public ISelectionService Selection { get; } = new SelectionState();
        public IUiRegistry Ui { get; } = NoOpUiRegistry.Instance;
        public IPluginStorage Storage { get; } = storage;
        public IAutomationSurface Automation => NoOpAutomationSurface.Instance;
    }

    private sealed class StubLogger : IPluginLogger
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }
}
