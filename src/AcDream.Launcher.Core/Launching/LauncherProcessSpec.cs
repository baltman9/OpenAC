namespace AcDream.Launcher.Core.Launching;

public sealed record LauncherProcessSpec(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    bool SupportsConsoleGracefulStop = true,
    string? StderrLogPath = null,
    // The host reads commands from its input for as long as it runs, so the
    // pipe stays open after the password instead of being closed behind it.
    bool KeepStandardInputOpen = false);
