using AcDream.Headless.Configuration;
using AcDream.Headless.Credentials;
using AcDream.Headless.Hosting;
using AcDream.Headless.Platform;
using AcDream.Platform;

namespace AcDream.Headless;

internal static class HeadlessEntryPoint
{
    private const string HelpText =
        """
        acdream-headless

        Usage:
          acdream-headless --help
          acdream-headless validate --config <path> [path overrides]
          acdream-headless run --config <path> [path overrides] [credentials]

        Commands:
          validate   Validate a versioned headless configuration without connecting.
          run        Run exactly one configured no-window session.

        Path overrides:
          --root-dir <path>              The whole install folder.
          --config-dir <path>
          --data-dir <path>
          --cache-dir <path>

        Console options (run mode):
          --console                      Read typed lines and print chat.
          --console-stream <stdout|stderr>
                                         Where the chat front end prints;
                                         stderr by default, so the JSON
                                         diagnostic lines keep stdout.

        Direct single-session credentials:
          -user <account> -password <password>
          --user <account> --password <password>
        """;

    internal static int Run(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error) =>
        Run(
            arguments,
            TextReader.Null,
            output,
            error,
            CancellationToken.None);

    internal static int Run(
        IReadOnlyList<string> arguments,
        TextReader standardInput,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken,
        bool standardInputIsTerminal = false,
        bool standardOutputIsTerminal = false)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(standardInput);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (arguments.Count == 0
            || IsHelp(arguments[0]))
        {
            output.WriteLine(HelpText);
            return (int)HeadlessExitCode.Success;
        }

        try
        {
            HeadlessCommandLine commandLine =
                HeadlessCommandLine.Parse(arguments);
            HeadlessConfiguration configuration =
                HeadlessConfigurationLoader.Load(
                    commandLine.ConfigurationPath);
            HeadlessPathOverrides configuredPaths =
                configuration.Process?.Paths
                ?? new HeadlessPathOverrides();
            HeadlessPathSet paths = HeadlessPathSet.Resolve(
                configuredPaths.Merge(commandLine.Paths));
            if (commandLine.Command == "run")
            {
                // A bot started by hand before the launcher ever ran brings
                // the old per-user folders into the install root itself; one
                // the launcher starts is handed an explicit root.
                InstallRootMigrationResult migration =
                    InstallRootMigration.RunIfNeeded(paths.Application);
                InstallRootMigrationLog.Write(
                    migration,
                    paths.DataDirectory,
                    error.WriteLine,
                    error.WriteLine);
                if (InstallRootMigration.StartupBlockReason(
                        migration,
                        paths.DataDirectory) is { } migrationBlock)
                {
                    error.WriteLine(migrationBlock);
                    return (int)HeadlessExitCode.ConfigurationError;
                }
                bool consoleEnabled = HeadlessConsoleOptions.Resolve(
                    commandLine.ConsoleEnabled,
                    standardInputIsTerminal);
                HeadlessConsoleStream consoleStream =
                    HeadlessConsoleOptions.ResolveStream(
                        commandLine.ConsoleStream);
                TextWriter consoleOutput =
                    HeadlessConsoleOptions.SelectWriter(
                        consoleStream, output, error);
                using var host = new HeadlessProcessHost(
                    configuration,
                    paths,
                    standardInput,
                    output,
                    directCredentials:
                        commandLine.DirectCredentials,
                    consoleEnabled: consoleEnabled,
                    standardOutputIsTerminal: standardOutputIsTerminal,
                    consoleOutput: consoleOutput);
                return (int)host.RunAsync(cancellationToken)
                    .GetAwaiter()
                    .GetResult();
            }

            output.WriteLine(
                $"Configuration valid: version {configuration.Version}, "
                + $"{configuration.Sessions.Count} session(s).");
            return (int)HeadlessExitCode.Success;
        }
        catch (HeadlessCommandLineException)
        {
            error.WriteLine("Invalid command. Run --help for usage.");
            return (int)HeadlessExitCode.UsageError;
        }
        catch (System.Text.Json.JsonException)
        {
            error.WriteLine(
                "Configuration invalid: the JSON document is invalid.");
            return (int)HeadlessExitCode.ConfigurationError;
        }
        catch (Exception exception)
            when (exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException
                or HeadlessConfigurationException)
        {
            error.WriteLine($"Configuration invalid: {exception.Message}");
            return (int)HeadlessExitCode.ConfigurationError;
        }
        catch (HeadlessCredentialException exception)
        {
            error.WriteLine($"Credential unavailable: {exception.Message}");
            return (int)HeadlessExitCode.CredentialError;
        }
        catch (Exception exception)
        {
            error.WriteLine(
                $"Headless runtime failed: {exception.GetType().FullName}");
            return (int)HeadlessExitCode.RuntimeError;
        }
    }

    private static bool IsHelp(string argument) =>
        string.Equals(argument, "--help", StringComparison.Ordinal)
        || string.Equals(argument, "-h", StringComparison.Ordinal);
}
