using System.Text.Json;

namespace AcDream.PluginCheck;

/// <summary>Argument parsing and rendering, split from <c>Program.cs</c> so a test can drive it
/// against in-memory writers instead of a spawned process (mirrors
/// <c>AcDream.Headless.HeadlessEntryPoint</c>).</summary>
internal static class PluginCheckEntryPoint
{
    private const string UsageText = "usage: acdream-plugincheck <path> [--json]";

    internal static async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        bool json = false;
        string? path = null;
        foreach (string argument in arguments)
        {
            if (argument == "--json")
            {
                json = true;
            }
            else if (path is null)
            {
                path = argument;
            }
            else
            {
                error.WriteLine(UsageText);
                return 2;
            }
        }

        if (path is null)
        {
            error.WriteLine(UsageText);
            return 2;
        }

        try
        {
            PluginCheckReport report = await PluginCheckRunner.RunAsync(path, cancellationToken)
                .ConfigureAwait(false);

            if (json)
            {
                WriteJson(report, output);
            }
            else
            {
                WriteHuman(report, output);
            }

            return report.Verdict == PluginCheckVerdict.WouldInstall ? 0 : 1;
        }
        catch (PluginCheckUsageException ex)
        {
            error.WriteLine(ex.Message);
            return 2;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error.WriteLine(ex.Message);
            return 2;
        }
    }

    private static void WriteHuman(PluginCheckReport report, TextWriter output)
    {
        output.WriteLine($"OpenAC PluginCheck — {report.Path}");
        output.WriteLine(report.Mode == PluginCheckMode.Directory
            ? "Mode: unzipped plugin folder (a hand install)"
            : "Mode: plugin .zip (a release asset)");
        output.WriteLine();

        foreach (PluginCheckItem item in report.Checks)
        {
            string tag = item.Status switch
            {
                PluginCheckStatus.Pass => "PASS",
                PluginCheckStatus.Fail => "FAIL",
                _ => "SKIP",
            };
            output.WriteLine($"[{tag}] {item.Check}");
            output.WriteLine($"       {item.Message}");
        }

        output.WriteLine();
        output.WriteLine(report.Verdict == PluginCheckVerdict.WouldInstall
            ? "Verdict: this plugin would install."
            : "Verdict: this plugin would be refused.");
        output.WriteLine();
        output.WriteLine(
            "This checks the plugin payload and manifest only. It cannot validate the GitHub "
            + "release layout (tag naming, asset names, immutable releases, prerelease flags) from "
            + "a local path — see docs/plugin-manifest.md.");
    }

    private static void WriteJson(PluginCheckReport report, TextWriter output)
    {
        var document = new
        {
            schemaVersion = report.SchemaVersion,
            mode = report.Mode == PluginCheckMode.Directory ? "directory" : "zip",
            path = report.Path,
            verdict = report.Verdict == PluginCheckVerdict.WouldInstall
                ? "would-install"
                : "would-be-refused",
            checks = report.Checks.Select(item => new
            {
                check = item.Check,
                status = item.Status switch
                {
                    PluginCheckStatus.Pass => "pass",
                    PluginCheckStatus.Fail => "fail",
                    _ => "skip",
                },
                message = item.Message,
            }),
        };

        output.Write(JsonSerializer.Serialize(document));
        output.Write('\n');
    }
}
