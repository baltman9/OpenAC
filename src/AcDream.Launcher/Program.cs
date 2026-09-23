using AcDream.Launcher.Core.Updates;
using AcDream.Platform;
using Avalonia;

namespace AcDream.Launcher;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            LauncherStartupOptions options = LauncherStartupOptions.Parse(args);
            if (options.Mode == LauncherStartupMode.VerifyPublish)
            {
                // A display-free execution probe for the packaged artifact.
                // Parsing above deliberately never resolves user paths.
                return 0;
            }

            // The old per-user folders come into the install root before any
            // store opens a file there, the self-update state included.
            InstallRootMigrationResult migration =
                InstallRootMigration.RunIfNeeded(options.Paths);
            InstallRootMigrationLog.Write(
                migration,
                options.Paths.RootDirectory,
                Console.WriteLine,
                Console.Error.WriteLine);

            using var httpClient = new HttpClient();
            var selfUpdates = new LauncherSelfUpdateManager(options.Paths, httpClient);
            string executable = Environment.ProcessPath
                ?? throw new InvalidOperationException(
                    "The launcher executable path is unavailable.");
            LauncherInstallationLayout layout = LauncherInstallationLayout.Detect(
                AppContext.BaseDirectory,
                LauncherRuntimeIdentity.DetectRid());
            SelfUpdateStartupResult startup = LauncherSelfUpdateBootstrap.HandleAsync(
                    args,
                    selfUpdates,
                    layout,
                    executable)
                .GetAwaiter()
                .GetResult();
            if (startup.ShouldExit)
            {
                return startup.ExitCode;
            }

            RequireUnchangedPublicArguments(options, startup);

            return BuildAvaloniaApp(options, migration).StartWithClassicDesktopLifetime([]);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Launcher startup failed safely: {ex.Message}");
            string? report = TryWriteCrashReport(args, ex);
            Console.Error.WriteLine(report is null
                ? "No crash report could be written."
                : $"Crash report: {report}");
            return 74;
        }
    }

    internal static string? TryWriteCrashReport(string[] args, Exception failure)
    {
        try
        {
            string directory;
            try
            {
                directory = LauncherStartupOptions.Parse(args).Paths.CrashReportsDirectory;
            }
            catch
            {
                directory = TryReadRequestedRoot(args) is { } requested
                    ? ApplicationPathSet.ForRoot(requested).CrashReportsDirectory
                    : ResolveCrashReportsDirectory();
            }

            Directory.CreateDirectory(directory);
            string path = Path.Combine(
                directory,
                $"launcher-crash-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.log");
            File.WriteAllText(
                path,
                $"""
                 OpenAC launcher crash report
                 utc: {DateTime.UtcNow:O}
                 os: {Environment.OSVersion}
                 rid: {System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}
                 version: {typeof(Program).Assembly.GetName().Version}

                 {failure}
                 """);
            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The root named by <c>--root-dir</c> or, failing that, <c>--data-dir</c>.</summary>
    private static string? TryReadRequestedRoot(string[] args)
    {
        foreach (string option in new[] { "--root-dir", "--data-dir" })
        {
            for (int index = 0; index + 1 < args.Length; index++)
            {
                if (string.Equals(args[index], option, StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(args[index + 1])
                    && Path.IsPathFullyQualified(args[index + 1]))
                {
                    return args[index + 1];
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The resolved crash folder, or the default root's when resolving is
    /// what failed (an unreadable pointer file, say).
    /// </summary>
    private static string ResolveCrashReportsDirectory()
    {
        try
        {
            return ApplicationPathSet.Resolve().CrashReportsDirectory;
        }
        catch (InvalidOperationException)
        {
            return ApplicationPathSet.ForRoot(ApplicationPathSet.ResolveDefaultRoot())
                .CrashReportsDirectory;
        }
    }

    internal static AppBuilder BuildAvaloniaApp(
        LauncherStartupOptions options,
        InstallRootMigrationResult? migration = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        return AppBuilder.Configure(() => new App(options, migration))
            .UsePlatformDetect();
    }

    internal static void RequireUnchangedPublicArguments(
        LauncherStartupOptions options,
        SelfUpdateStartupResult startup)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(startup);
        if (!startup.RemainingArguments.SequenceEqual(
                options.PublicArguments,
                StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "The self-update bootstrap changed validated launcher arguments.");
        }
    }
}
