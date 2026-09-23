using AcDream.Launcher.Core.Installation;
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

            RepairContentRecords(options.Paths);

            using var httpClient = new HttpClient();
            string executable = Environment.ProcessPath
                ?? throw new InvalidOperationException(
                    "The launcher executable path is unavailable.");
            LauncherInstallationLayout layout = LauncherInstallationLayout.Detect(
                AppContext.BaseDirectory,
                LauncherRuntimeIdentity.DetectRid());
            // Picks the self-update state by where the transaction lives: an
            // update started by a launcher from before the single install
            // folder is finished in that launcher's data folder.
            SelfUpdateStartupResult startup = LauncherSelfUpdateBootstrap.HandleAsync(
                    args,
                    options.Paths,
                    httpClient,
                    layout,
                    executable)
                .GetAwaiter()
                .GetResult();
            if (startup.ShouldExit)
            {
                return startup.ExitCode;
            }

            RequireUnchangedPublicArguments(options, startup);

            return BuildAvaloniaApp(options).StartWithClassicDesktopLifetime([]);
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

    /// <summary>
    /// Points the install record at this root's own content when a move of the
    /// root left it naming the old one; the launcher would otherwise reject
    /// the install as not at its canonical path.
    /// </summary>
    private static void RepairContentRecords(ApplicationPathSet paths)
    {
        try
        {
            foreach (string repaired in InstallRootMover.RepairContentRecords(paths))
            {
                Console.WriteLine($"install folder: {repaired} now names this folder's content");
            }
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or System.Text.Json.JsonException
                                   or InvalidOperationException)
        {
            // The install check that follows reports the record as it is.
            Console.Error.WriteLine($"install folder: the install record could not be updated: {ex.Message}");
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

    internal static AppBuilder BuildAvaloniaApp(LauncherStartupOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return AppBuilder.Configure(() => new App(options))
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
