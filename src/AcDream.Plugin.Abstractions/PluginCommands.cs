namespace AcDream.Plugin.Abstractions;

/// <summary>One invocation of a plugin command.</summary>
/// <param name="Verb">The command word the player typed, without its leading slash.</param>
/// <param name="Arguments">Everything after the verb, as typed.</param>
/// <param name="RawText">The whole line as the player typed it.</param>
public readonly record struct PluginCommand(string Verb, string Arguments, string RawText)
{
    /// <summary>Parses shell-like arguments, preserving quoted multi-word values.</summary>
    /// <returns>The arguments in order; empty when there are none.</returns>
    /// <exception cref="FormatException">A quote is opened and never closed.</exception>
    public IReadOnlyList<string> ParseArguments() => PluginCommandLineParser.Parse(Arguments);
}

/// <summary>Result returned by a typed command definition.</summary>
/// <param name="Success">Whether the command was carried out.</param>
/// <param name="Message">What to tell the player, or null for nothing.</param>
public readonly record struct PluginCommandResult(bool Success, string? Message = null)
{
    /// <summary>The command was carried out.</summary>
    /// <param name="message">What to tell the player, or null for nothing.</param>
    /// <returns>A successful result.</returns>
    public static PluginCommandResult Accepted(string? message = null) => new(true, message);

    /// <summary>The command was refused.</summary>
    /// <param name="message">Why, in words for the player.</param>
    /// <returns>A failed result.</returns>
    public static PluginCommandResult Rejected(string message) => new(false, message);
}

/// <summary>A completion candidate returned for a partially typed command.</summary>
/// <param name="Text">The text the completion would put in place of what was typed.</param>
/// <param name="Description">A short explanation shown beside the candidate, or null for none.</param>
public readonly record struct PluginCommandCompletion(string Text, string? Description = null);

/// <summary>Describes a command, its aliases, invocation, and completion behavior.</summary>
public interface IPluginCommandDefinition
{
    /// <summary>The command word, without a leading slash.</summary>
    string Verb { get; }

    /// <summary>One line saying what the command does, used for help.</summary>
    string Description { get; }

    /// <summary>Other words the same command answers to; empty for none.</summary>
    IReadOnlyList<string> Aliases { get; }

    /// <summary>Carries the command out.</summary>
    /// <param name="command">What the player typed.</param>
    /// <returns>Whether it was carried out, and what to tell the player.</returns>
    PluginCommandResult Invoke(PluginCommand command);

    /// <summary>Offers completions for a partially typed command.</summary>
    /// <param name="command">What the player has typed so far.</param>
    /// <returns>The candidates, best first; empty for none.</returns>
    IReadOnlyList<PluginCommandCompletion> Complete(PluginCommand command);
}

/// <summary>Lets a plugin claim chat commands, including aliases and typed completion.</summary>
public interface IPluginCommandRegistry
{
    /// <summary>Claims one command word.</summary>
    /// <param name="verb">The command word, without a leading slash.</param>
    /// <param name="handler">Called each time the player types the command.</param>
    /// <returns>Dispose it to give the word up.</returns>
    IDisposable Register(string verb, Action<PluginCommand> handler);

    /// <summary>Registers a command and all its aliases as one owned registration.</summary>
    /// <param name="definition">The command, its aliases and its completion.</param>
    /// <returns>Dispose it to give the command and its aliases up.</returns>
    /// <remarks>A host that does not implement this registers the verb alone: no aliases, no completion, no help.</remarks>
    IDisposable Register(IPluginCommandDefinition definition) =>
        Register(definition.Verb, definition.InvokeCommand);
    /// <summary>Returns definitions matching a verb, or all definitions for help.</summary>
    IReadOnlyList<IPluginCommandDefinition> Definitions => Array.Empty<IPluginCommandDefinition>();
    /// <summary>Completes a partially typed command without blocking the caller.</summary>
    /// <param name="command">What the player has typed so far.</param>
    /// <param name="cancellationToken">Cancels the completion.</param>
    /// <returns>The candidates; empty on a host that does not implement completion.</returns>
    ValueTask<IReadOnlyList<PluginCommandCompletion>> CompleteAsync(PluginCommand command, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<PluginCommandCompletion>>(Array.Empty<PluginCommandCompletion>());
    /// <summary>Creates help text from registered command descriptions.</summary>
    /// <returns>One line per command; empty on a host that does not implement help.</returns>
    string GetHelp() => string.Empty;
}

internal static class PluginCommandDefinitionExtensions
{
    public static void InvokeCommand(this IPluginCommandDefinition definition, PluginCommand command) => definition.Invoke(command);
}

/// <summary>Parses quoted and escaped command arguments.</summary>
public static class PluginCommandLineParser
{
    /// <summary>Splits an argument string into its arguments.</summary>
    /// <param name="text">The text after the command word.</param>
    /// <returns>The arguments in order; empty for null or blank text.</returns>
    /// <remarks>
    /// Arguments are separated by white space. Single or double quotes keep
    /// white space inside one argument, and a backslash takes the next
    /// character literally.
    /// </remarks>
    /// <exception cref="FormatException">A quote is opened and never closed.</exception>
    public static IReadOnlyList<string> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
        var result = new List<string>();
        var token = new System.Text.StringBuilder();
        char quote = '\0';
        bool escaped = false;
        foreach (char c in text)
        {
            if (escaped) { token.Append(c); escaped = false; continue; }
            if (c == '\\') { escaped = true; continue; }
            if (quote != '\0') { if (c == quote) quote = '\0'; else token.Append(c); continue; }
            if (c is '\'' or '"') { quote = c; continue; }
            if (char.IsWhiteSpace(c)) { if (token.Length != 0) { result.Add(token.ToString()); token.Clear(); } continue; }
            token.Append(c);
        }
        if (escaped) token.Append('\\');
        if (quote != '\0') throw new FormatException("Unterminated quoted command argument.");
        if (token.Length != 0) result.Add(token.ToString());
        return result;
    }
}

/// <summary>Inert command registry for hosts without a command line.</summary>
public sealed class NoOpPluginCommandRegistry : IPluginCommandRegistry
{
    /// <summary>The one shared instance.</summary>
    public static NoOpPluginCommandRegistry Instance { get; } = new();
    private NoOpPluginCommandRegistry() { }

    /// <inheritdoc />
    /// <remarks>Checks its arguments and claims nothing; the handler is never called.</remarks>
    public IDisposable Register(string verb, Action<PluginCommand> handler) { ArgumentException.ThrowIfNullOrWhiteSpace(verb); ArgumentNullException.ThrowIfNull(handler); return NoOpLease.Instance; }
    private sealed class NoOpLease : IDisposable { public static NoOpLease Instance { get; } = new(); public void Dispose() { } }
}
