// Copyright (c) OpenAC contributors.
// Distributed under the terms of the MIT license.

using AcDream.Plugin.Abstractions;

namespace AcDream.Plugin.Tests.Fixtures;

/// <summary>
/// An in-memory <see cref="IPluginStorage"/> for tests. Every string value
/// is kept in a <see cref="Dictionary{TKey, TValue}"/> and can be inspected
/// after the test runs.
/// </summary>
public sealed class FakePluginStorage : IPluginStorage
{
    private readonly Dictionary<string, string> _store =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Controls the value returned by <see cref="IPluginStorage.IsAvailable"/>.
    /// Set to false to simulate an unavailable storage backend.
    /// </summary>
    public bool Available { get; set; } = true;

    /// <summary>
    /// The underlying dictionary. Mutate it directly to pre-populate values
    /// before a test exercises a read path.
    /// </summary>
    public IDictionary<string, string> Store => _store;

    /// <inheritdoc/>
    public bool IsAvailable => Available;

    /// <inheritdoc/>
    public string? ReadText(string key)
    {
        return _store.TryGetValue(key, out string? value) ? value : null;
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> List(string prefix)
    {
        return _store.Keys
            .Where(key => prefix.Length == 0
                || key.StartsWith(prefix + "/", StringComparison.Ordinal))
            .OrderBy(static key => key, StringComparer.Ordinal)
            .ToArray();
    }

    /// <inheritdoc/>
    public void WriteText(string key, string content)
    {
        if (!Available)
            throw new NotSupportedException("Plugin storage is unavailable.");
        _store[key] = content;
    }

    /// <inheritdoc/>
    public bool Delete(string key)
    {
        return _store.Remove(key);
    }
}