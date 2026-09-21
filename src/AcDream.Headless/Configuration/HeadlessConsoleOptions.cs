namespace AcDream.Headless.Configuration;

/// <summary>Which stream the console's human-readable output goes to.</summary>
internal enum HeadlessConsoleStream
{
    /// <summary>Standard error — the default, so the machine-readable
    /// diagnostic lines have standard output to themselves.</summary>
    Error,

    /// <summary>Standard output, beside the diagnostic lines.</summary>
    Output,
}

internal static class HeadlessConsoleOptions
{
    internal const string EnvironmentVariable = "ACDREAM_HEADLESS_CONSOLE";

    internal const string StreamEnvironmentVariable =
        "ACDREAM_HEADLESS_CONSOLE_STREAM";

    internal const string StdoutValue = "stdout";

    internal const string StderrValue = "stderr";

    internal static bool Resolve(
        bool commandLineFlag,
        bool standardInputIsTerminal) =>
        Resolve(
            commandLineFlag,
            Environment.GetEnvironmentVariable,
            standardInputIsTerminal);

    internal static bool Resolve(
        bool commandLineFlag,
        Func<string, string?> env,
        bool standardInputIsTerminal)
    {
        ArgumentNullException.ThrowIfNull(env);
        if (commandLineFlag)
            return true;
        if (env(EnvironmentVariable) is null)
            return standardInputIsTerminal;
        return !string.Equals(
            env(EnvironmentVariable), "0", StringComparison.Ordinal);
    }

    internal static HeadlessConsoleStream ResolveStream(
        string? commandLineValue) =>
        ResolveStream(commandLineValue, Environment.GetEnvironmentVariable);

    internal static HeadlessConsoleStream ResolveStream(
        string? commandLineValue,
        Func<string, string?> env)
    {
        ArgumentNullException.ThrowIfNull(env);
        if (commandLineValue is not null)
            return ParseStream(commandLineValue);
        if (env(StreamEnvironmentVariable) is not { } configured)
            return HeadlessConsoleStream.Error;
        if (!TryParseStream(configured, out HeadlessConsoleStream stream))
        {
            throw new HeadlessConfigurationException(
                $"{StreamEnvironmentVariable} must be "
                + $"'{StdoutValue}' or '{StderrValue}'.");
        }
        return stream;
    }

    /// <summary>Parses a value already accepted by the command line.</summary>
    internal static HeadlessConsoleStream ParseStream(string value) =>
        TryParseStream(value, out HeadlessConsoleStream stream)
            ? stream
            : throw new ArgumentOutOfRangeException(nameof(value), value, null);

    internal static bool TryParseStream(
        string? value, out HeadlessConsoleStream stream)
    {
        if (string.Equals(value, StdoutValue, StringComparison.OrdinalIgnoreCase))
        {
            stream = HeadlessConsoleStream.Output;
            return true;
        }
        if (string.Equals(value, StderrValue, StringComparison.OrdinalIgnoreCase))
        {
            stream = HeadlessConsoleStream.Error;
            return true;
        }
        stream = HeadlessConsoleStream.Error;
        return false;
    }

    /// <summary>
    /// The writer the console prints to. The diagnostic lines keep standard
    /// output whatever this picks.
    /// </summary>
    internal static TextWriter SelectWriter(
        HeadlessConsoleStream stream,
        TextWriter standardOutput,
        TextWriter standardError)
    {
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);
        return stream == HeadlessConsoleStream.Output
            ? standardOutput
            : standardError;
    }
}
