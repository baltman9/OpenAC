using System.Collections;
using AcDream.Launcher.Core.Launching;
using AcDream.Platform;

namespace AcDream.Launcher.Core.Tests.Launching;

public sealed class LauncherChildEnvironmentTests
{
    /// <summary>Mutation: leaving out the root variable fails this.</summary>
    [Fact]
    public void EveryChildIsToldTheRootAndEachMember()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "openac-child-env"));
        ApplicationPathSet paths = ApplicationPathSet.ForRoot(root) with
        {
            ConfigDirectory = Path.Combine(root, "elsewhere"),
        };

        IReadOnlyDictionary<string, string> environment = LauncherChildEnvironment.For(paths);

        Assert.Equal(root, environment["ACDREAM_ROOT_DIR"]);
        Assert.Equal(Path.Combine(root, "elsewhere"), environment["ACDREAM_CONFIG_DIR"]);
        Assert.Equal(root, environment["ACDREAM_DATA_DIR"]);
        Assert.Equal(Path.Combine(root, "cache"), environment["ACDREAM_CACHE_DIR"]);
    }

    /// <summary>Mutation: not copying the spec's environment into the start info fails this.</summary>
    [Fact]
    public void PortableChildCarriesTheOverridesIntoItsStartInfo()
    {
        using var child = new SystemChildProcess(
            new LauncherProcessSpec("host", [])
            {
                Environment = new Dictionary<string, string>
                {
                    ["ACDREAM_ROOT_DIR"] = "/openac",
                },
            });

        Assert.Equal("/openac", child.StartInfo.Environment["ACDREAM_ROOT_DIR"]);
    }

    /// <summary>
    /// Mutation: letting the inherited value win, comparing names with case,
    /// or dropping the final terminator fails this.
    /// </summary>
    [Fact]
    public void WindowsBlockAppliesOverridesSortedAndDoubleTerminated()
    {
        var current = new Hashtable
        {
            ["Path"] = "C:\\bin",
            ["acdream_root_dir"] = "C:\\inherited",
            ["ZETA"] = "z",
        };

        string block = WindowsEnvironmentBlock.Build(
            current,
            new Dictionary<string, string> { ["ACDREAM_ROOT_DIR"] = "C:\\OpenAC" });

        Assert.Equal(
            "acdream_root_dir=C:\\OpenAC\0Path=C:\\bin\0ZETA=z\0\0",
            block);
    }

    /// <summary>Mutation: accepting a name with '=' in it fails this.</summary>
    [Fact]
    public void WindowsBlockRefusesANameThatWouldCorruptIt()
    {
        Assert.Throws<ArgumentException>(() => WindowsEnvironmentBlock.Build(
            new Hashtable(),
            new Dictionary<string, string> { ["A=B"] = "x" }));
    }

    /// <summary>
    /// A real console child started the way the launcher starts a windowless
    /// host sees the override. Mutation: passing a null environment block to
    /// the create call fails this.
    /// </summary>
    [Fact]
    [Trait("Lane", "Windows")]
    public void WindowsConsoleChildSeesTheRootVariable()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Lane=Windows requires a Windows host.");

        string scratch = Path.Combine(Path.GetTempPath(), "openac-child-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        string log = Path.Combine(scratch, "err.log");
        try
        {
            string powershell = Path.Combine(
                Environment.SystemDirectory,
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");
            var child = new WindowsSystemChildProcess(
                new LauncherProcessSpec(
                    powershell,
                    [
                        "-NoProfile",
                        "-NonInteractive",
                        "-Command",
                        "[Console]::Error.WriteLine('root=' + $env:ACDREAM_ROOT_DIR)",
                    ],
                    StderrLogPath: log)
                {
                    Environment = new Dictionary<string, string>
                    {
                        ["ACDREAM_ROOT_DIR"] = scratch,
                    },
                });
            using (child)
            {
                child.Start();
                child.StandardInput.Close();
                Assert.True(child.WaitForExit(TimeSpan.FromSeconds(20)));
            }

            Assert.Contains($"root={scratch}", File.ReadAllText(log), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }
}
