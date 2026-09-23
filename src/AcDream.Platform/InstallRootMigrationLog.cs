namespace AcDream.Platform;

/// <summary>
/// The same few lines about a migration run for every host, so the client,
/// the windowless host and the launcher describe it in one set of words.
/// </summary>
public static class InstallRootMigrationLog
{
    /// <summary>Writes what a run did; nothing for a run that had nothing to do.</summary>
    public static void Write(
        InstallRootMigrationResult result,
        string root,
        Action<string> information,
        Action<string> warning)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(information);
        ArgumentNullException.ThrowIfNull(warning);
        switch (result.Outcome)
        {
            case InstallRootMigrationOutcome.Migrated:
                information(
                    $"install folder: moved {result.Moved.Count} item(s) "
                    + $"from the old per-user folders into {root}");
                break;
            case InstallRootMigrationOutcome.Incomplete:
                warning(
                    $"install folder: moving the old per-user folders into {root} "
                    + $"is incomplete ({result.Failures.Count} item(s) could not be moved; "
                    + "close every OpenAC client and start again to finish)");
                foreach (string failure in result.Failures)
                    warning("install folder: " + failure);
                break;
            case InstallRootMigrationOutcome.Busy:
                information(
                    $"install folder: another OpenAC process is moving the old folders into {root}");
                break;
        }

        foreach (string line in result.Warnings)
            warning("install folder: " + line);
    }
}
