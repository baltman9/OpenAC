namespace AcDream.HostParity.Tests;

/// <summary>
/// Whether the world's clock moves this frame is one rule, and it lives in
/// the runtime. Each client asks the runtime for its frame; neither keeps a
/// copy of the rule, because a copy is how the two drifted apart in the
/// first place -- one client held the clock while the world was being handed
/// over and the other ran it straight through, so a plugin reading elapsed
/// time got a different answer depending on which client it was loaded into.
///
/// This is a pin on the shape of the source rather than on behaviour: a
/// scenario cannot catch a client that inlines the rule correctly today and
/// is edited tomorrow.
///
/// Mutation check (2026-09-20), both run: putting a direct clock advance
/// back into either client's frame step turned
/// <see cref="NeitherClientAdvancesTheWorldClockItself"/> red; removing the
/// runtime call from either turned
/// <see cref="BothClientsTakeTheirFrameFromTheRuntime"/> red.
/// </summary>
public sealed class FrameClockPinTests
{
    private const string RuntimeFrameStep = "AdvanceFrameClock";

    /// <summary>The frame step of each client, by file.</summary>
    private static readonly string[] HostFrameSteps =
    [
        "src/AcDream.App/Update/UpdateFrameOrchestrator.cs",
        "src/AcDream.Headless/Hosting/HeadlessSessionHost.cs",
    ];

    /// <summary>Every source directory a client owns.</summary>
    private static readonly string[] HostProjects =
    [
        "src/AcDream.App",
        "src/AcDream.Headless",
    ];

    [Fact]
    public void BothClientsTakeTheirFrameFromTheRuntime()
    {
        foreach (string relative in HostFrameSteps)
        {
            Assert.Contains(
                RuntimeFrameStep,
                File.ReadAllText(Locate(relative)),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NeitherClientAdvancesTheWorldClockItself()
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
                    .Contains(".Clock.Advance(", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal),
        ];

        Assert.True(
            offenders.Length == 0,
            "A client steps the simulation clock itself instead of asking "
            + "the runtime for its frame, which is how the two clients came "
            + "to disagree about elapsed time: "
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
