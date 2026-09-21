namespace AcDream.HostParity.Tests;

/// <summary>
/// How often a plugin is ticked, and how much elapsed time each tick
/// carries, is one rule, and it lives in the runtime. A client with a window
/// updates as fast as it draws; a client without one takes a turn on a fixed
/// schedule. Ticking plugins straight off either of those gave the same
/// plugin a different number of ticks per second, carrying different slices
/// of elapsed time, depending on which client it was loaded into.
///
/// Each client now hands the runtime's clock however long its own frame or
/// turn really took, and the clock raises the tick. Neither raises it
/// itself.
///
/// This is a pin on the shape of the source rather than on behaviour: a
/// scenario cannot catch a client that raises the tick correctly today and
/// is edited tomorrow.
///
/// Mutation check (2026-09-20), both run: putting a direct plugin-tick raise
/// back into either client's frame step turned
/// <see cref="NeitherClientRaisesThePluginTickItself"/> red; taking the
/// clock out of either turned
/// <see cref="BothClientsPaceThePluginTickFromTheRuntime"/> red.
/// </summary>
public sealed class PluginTickPinTests
{
    private const string RuntimeTickClock = "RuntimePluginTickClock";

    /// <summary>Where each client feeds the clock, by file.</summary>
    private static readonly string[] HostTickSteps =
    [
        "src/AcDream.App/Rendering/GameWindow.cs",
        "src/AcDream.Headless/Hosting/HeadlessSessionHost.cs",
    ];

    /// <summary>Every source directory a client owns.</summary>
    private static readonly string[] HostProjects =
    [
        "src/AcDream.App",
        "src/AcDream.Headless",
    ];

    [Fact]
    public void BothClientsPaceThePluginTickFromTheRuntime()
    {
        foreach (string relative in HostTickSteps)
        {
            string source = File.ReadAllText(Locate(relative));
            Assert.Contains(RuntimeTickClock, source, StringComparison.Ordinal);
            Assert.Contains("_pluginTick.Feed(", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NeitherClientRaisesThePluginTickItself()
    {
        string[] offenders =
        [
            .. HostProjects
                .SelectMany(project => Directory.EnumerateFiles(
                    Locate(project),
                    "*.cs",
                    SearchOption.AllDirectories))
                .Where(static path => !IsBuildOutput(path))
                .Where(static path => File.ReadAllText(path)
                    .Contains(".FireTick(", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal),
        ];

        Assert.True(
            offenders.Length == 0,
            "A client raises the plugin tick from its own frame instead of "
            + "feeding the runtime's clock, which is how the two clients "
            + "came to tick plugins at different rates: "
            + string.Join(", ", offenders));
    }

    private static bool IsBuildOutput(string path) =>
        path.Contains(
            Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar,
            StringComparison.Ordinal)
        || path.Contains(
            Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar,
            StringComparison.Ordinal);

    private static string Locate(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(
                directory.FullName,
                relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate) || Directory.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            relative + " was not found above " + AppContext.BaseDirectory);
    }
}
