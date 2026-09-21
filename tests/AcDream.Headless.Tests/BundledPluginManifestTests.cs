using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using AcDream.Core.Plugins;
using AcDream.Launcher.Core.Plugins;
using AcDream.PluginCheck;

namespace AcDream.Headless.Tests;

/// <summary>The bundled example plugin ships a real <c>plugin.json</c> beside its project, and the
/// build copies it into the plugin's output folder. Three separate readers decide whether a plugin
/// folder is usable - the client's, the launcher's, and the check tool a plugin author runs before
/// publishing - so all three run here over the folder the build actually produced, never over a
/// fixture that could drift from it.</summary>
public sealed class BundledPluginManifestTests
{
    private const string ExpectedId = "acdream.mosstank";
    private const string ExpectedEntryDll = "AcDream.Plugins.MossTank.dll";

    [Fact]
    public void TheBuiltPluginFolderCarriesItsManifestAndEntryAssembly()
    {
        string directory = BundledPluginOutputDirectory();

        Assert.True(
            File.Exists(Path.Combine(directory, "plugin.json")),
            $"No plugin.json in the bundled plugin's build output: {directory}");
        Assert.True(
            File.Exists(Path.Combine(directory, ExpectedEntryDll)),
            $"No {ExpectedEntryDll} in the bundled plugin's build output: {directory}");
    }

    /// <summary>The client's reader, plus the two gates it applies before loading code: the contract
    /// version has to be one this build provides, and the manifest has to admit both hosts.</summary>
    [Fact]
    public void TheClientReaderAcceptsTheBuiltManifestOnBothHosts()
    {
        PluginManifest manifest = PluginManifest.Parse(BuiltManifestJson());

        Assert.Equal(ExpectedId, manifest.Id);
        Assert.Equal(ExpectedEntryDll, manifest.EntryDll);
        Assert.Contains(PluginKind.Gameplay, manifest.Kinds);
        Assert.True(
            AcDream.Plugin.Abstractions.PluginApi.IsSupported(manifest.ApiVersion),
            $"apiVersion {manifest.ApiVersion} is outside the contract range this build provides.");

        Assert.Null(PluginHostCompatibility.Evaluate(
            manifest, PluginHostKind.Graphical, RunningHostVersion()));
        Assert.Null(PluginHostCompatibility.Evaluate(
            manifest, PluginHostKind.Headless, RunningHostVersion()));
    }

    /// <summary>The launcher's reader, including the stricter rules an install through the launcher
    /// adds on top: a namespaced id, a SemVer version, and a declared minHostVersion and hosts.</summary>
    [Fact]
    public void TheLauncherReaderAcceptsTheBuiltManifestForInstall()
    {
        LauncherPluginManifest manifest = LauncherPluginManifest.Parse(BuiltManifestJson());

        manifest.ValidateForInstall();

        Assert.Equal(ExpectedId, manifest.Id);
        Assert.NotNull(manifest.MinHostVersion);
        Assert.NotNull(manifest.Hosts);
        Assert.Equal(
            [LauncherPluginHostKind.Graphical, LauncherPluginHostKind.Headless],
            manifest.Hosts!);
    }

    [Fact]
    public async Task TheCheckToolWouldInstallTheBuiltPluginFolder()
    {
        PluginCheckReport report = await PluginCheckRunner.RunAsync(BundledPluginOutputDirectory());

        Assert.All(report.Checks, check => Assert.NotEqual(PluginCheckStatus.Fail, check.Status));
        Assert.Equal(PluginCheckVerdict.WouldInstall, report.Verdict);
    }

    /// <summary>The manifest's version and the assembly's come from the same place, the repository's
    /// single version property, but nothing substitutes that property into the checked-in manifest,
    /// so this is what stops a version bump that touched only one of them going unnoticed.</summary>
    [Fact]
    public void TheManifestVersionMatchesTheBuiltAssemblyVersion()
    {
        PluginManifest manifest = PluginManifest.Parse(BuiltManifestJson());

        string? assemblyVersion = FileVersionInfo
            .GetVersionInfo(Path.Combine(BundledPluginOutputDirectory(), ExpectedEntryDll))
            .ProductVersion;

        Assert.Equal(manifest.Version, VersionCore(assemblyVersion));
    }

    /// <summary>What ships in the plugin folder is the other half of the contract boundary: the
    /// plugin compiles against one contract assembly, and its dependency list has to say the same,
    /// or the host would be asked to load a client assembly out of a plugin folder.</summary>
    [Fact]
    public void TheBuiltPluginFolderShipsNoClientAssemblyBesideThePlugin()
    {
        string directory = BundledPluginOutputDirectory();

        string[] clientAssemblies = Directory
            .EnumerateFiles(directory, "AcDream.*.dll", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .Select(name => name!)
            .Where(name => !string.Equals(name, ExpectedEntryDll, StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(clientAssemblies);

        string dependencyList = File.ReadAllText(
            Path.Combine(directory, "AcDream.Plugins.MossTank.deps.json"));
        Assert.DoesNotContain("AcDream.Core", dependencyList);
        Assert.DoesNotContain("AcDream.Runtime", dependencyList);
        Assert.DoesNotContain("AcDream.Plugin.Abstractions", dependencyList);
    }

    /// <summary>The windowless host scans a <c>plugins</c> folder beside its own executable, so its
    /// build output has to hold a folder the client's discovery accepts, with no manifest written by
    /// hand.</summary>
    [Fact]
    public void TheWindowlessHostsOutputStagesADiscoverablePluginFolder() =>
        AssertHostStagesTheBundledPlugin("AcDream.Headless");

    /// <summary>Both hosts stage the plugin from one shared set of build rules, so what the graphical
    /// client hands a plugin cannot drift from what the windowless host hands it. This suite does not
    /// build the graphical host, so it pins the shared rules rather than that host's output folder.
    /// </summary>
    [Theory]
    [InlineData("AcDream.App")]
    [InlineData("AcDream.Headless")]
    public void EveryHostStagesTheBundledPluginFromTheSameBuildRules(string hostProjectName)
    {
        string projectFile = Path.Combine(
            FindRepositoryRoot(), "src", hostProjectName, hostProjectName + ".csproj");

        Assert.Contains("BundledPlugin.targets", File.ReadAllText(projectFile));
    }

    private static void AssertHostStagesTheBundledPlugin(string hostProjectName)
    {
        string pluginsRoot = Path.Combine(
            FindRepositoryRoot(),
            "src",
            hostProjectName,
            "bin",
            Configuration(),
            TargetFramework(),
            "plugins");

        IReadOnlyList<PluginDiscoveryResult> discovered = PluginDiscovery.Scan(pluginsRoot);

        PluginDiscoveryResult result = Assert.Single(
            discovered, candidate => candidate.Manifest?.Id == ExpectedId);
        Assert.True(result.Success, result.Error?.Message);
    }

    private static PluginHostVersion? RunningHostVersion() =>
        PluginHostVersion.FromInformationalVersion(
            typeof(HeadlessExitCode).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion);

    private static string BuiltManifestJson() =>
        File.ReadAllText(Path.Combine(BundledPluginOutputDirectory(), "plugin.json"));

    /// <summary>Reduces an informational version to the <c>MAJOR.MINOR.PATCH</c> core a manifest
    /// version names, the way the hosts reduce their own.</summary>
    private static string VersionCore(string? informationalVersion)
    {
        Assert.NotNull(informationalVersion);
        int cut = informationalVersion.IndexOfAny(['-', '+']);
        return cut < 0 ? informationalVersion : informationalVersion[..cut];
    }

    private static string BundledPluginOutputDirectory() =>
        Path.Combine(
            FindRepositoryRoot(),
            "src",
            "AcDream.Plugins.MossTank",
            "bin",
            Configuration(),
            TargetFramework());

    /// <summary>This suite's own output path ends in bin/[configuration]/[framework], and every
    /// project in the repository shares that layout, so the configuration and framework the run was
    /// built in are read from it rather than guessed.</summary>
    private static DirectoryInfo TestOutputDirectory() =>
        new(AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    private static string TargetFramework() => TestOutputDirectory().Name;

    private static string Configuration() => TestOutputDirectory().Parent!.Name;

    private static string FindRepositoryRoot([CallerFilePath] string sourcePath = "")
    {
        string[] starts =
        [
            Path.GetDirectoryName(sourcePath) ?? string.Empty,
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory,
        ];
        foreach (string start in starts)
        {
            if (string.IsNullOrEmpty(start))
            {
                continue;
            }

            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "AcDream.slnx")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not find AcDream.slnx above the source, working, or output directory.");
    }
}
