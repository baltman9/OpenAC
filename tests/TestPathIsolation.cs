using System.Runtime.CompilerServices;

namespace AcDream.Tests;

/// <summary>
/// Every test run gets a scratch install root of its own before any test
/// code runs. Anything that resolves the install folders without being handed
/// one (a default plugin-peer folder, a chat log, a journal) then writes into
/// that scratch root instead of the real per-user OpenAC folder. Child
/// processes a test starts inherit the same root.
/// </summary>
internal static class TestPathIsolation
{
    internal const string RootVariable = "ACDREAM_ROOT_DIR";

    private static readonly string[] MemberVariables =
    [
        "ACDREAM_CONFIG_DIR",
        "ACDREAM_DATA_DIR",
        "ACDREAM_CACHE_DIR",
    ];

    /// <summary>The scratch root this run uses.</summary>
    internal static string Root { get; private set; } = string.Empty;

    [ModuleInitializer]
    internal static void Initialize()
    {
        Root = Path.Combine(
            Path.GetTempPath(),
            "openac-tests",
            $"{Environment.ProcessId}-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable(RootVariable, Root);

        // A member variable exported by the shell running the tests would
        // otherwise still win over the scratch root for its member.
        foreach (string variable in MemberVariables)
        {
            Environment.SetEnvironmentVariable(variable, null);
        }

        AppDomain.CurrentDomain.ProcessExit += (_, _) => DeleteRoot();
    }

    private static void DeleteRoot()
    {
        try
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file still open at exit stays in the temp folder; it never
            // touches the real install.
        }
    }
}

/// <summary>
/// Fails the run when the scratch root is not in place, which is what keeps
/// tests out of the real per-user OpenAC folder.
/// </summary>
public sealed class TestPathIsolationTests
{
    /// <summary>
    /// Mutation: deleting the module initializer, or pointing it anywhere but
    /// the temp folder, fails this.
    /// </summary>
    [Fact]
    public void TheRunUsesAScratchInstallRoot()
    {
        string? root = Environment.GetEnvironmentVariable(TestPathIsolation.RootVariable);

        Assert.False(string.IsNullOrWhiteSpace(root));
        Assert.StartsWith(
            Path.GetFullPath(Path.GetTempPath()),
            Path.GetFullPath(root),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        string realDefault = OperatingSystem.IsWindows()
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenAC")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local",
                "share",
                "openac");
        Assert.False(
            Path.GetFullPath(root).StartsWith(realDefault, StringComparison.OrdinalIgnoreCase),
            "The scratch install root must not be the real per-user folder.");
        Assert.Null(Environment.GetEnvironmentVariable("ACDREAM_DATA_DIR"));
    }
}
