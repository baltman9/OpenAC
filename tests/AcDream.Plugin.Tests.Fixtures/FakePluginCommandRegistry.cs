// Copyright (c) OpenAC contributors.
// Distributed under the terms of the MIT license.

using AcDream.Plugin.Abstractions;

namespace AcDream.Plugin.Tests.Fixtures;

/// <summary>
/// A recording <see cref="IPluginCommandRegistry"/> for tests. Every
/// registration is kept in a list so the test can inspect which verbs were
/// claimed, and every registered handler can be invoked on demand.
/// </summary>
public sealed class FakePluginCommandRegistry : IPluginCommandRegistry
{
    /// <summary>
    /// Every verb that was registered through this registry, in registration
    /// order. A test can inspect this list to verify which commands a plugin
    /// has claimed.
    /// </summary>
    public List<Registration> Registrations { get; } = [];

    /// <inheritdoc/>
    public IDisposable Register(string verb, Action<PluginCommand> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verb);
        ArgumentNullException.ThrowIfNull(handler);

        var registration = new Registration(verb, handler, Registrations);
        Registrations.Add(registration);
        return registration;
    }

    /// <summary>
    /// Invokes the handler registered for <paramref name="verb"/>, passing
    /// the given <paramref name="arguments"/>. Returns true when a handler
    /// was found and invoked; false when no handler is registered for that
    /// verb. Matching is case-insensitive.
    /// </summary>
    public bool Invoke(string verb, string arguments)
    {
        Registration? match = Registrations
            .LastOrDefault(r => string.Equals(
                r.Verb, verb, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return false;

        match.Handler(new PluginCommand(
            match.Verb,
            arguments,
            $"/{match.Verb} {arguments}"));
        return true;
    }

    /// <summary>
    /// A single registration returned by <see cref="Register"/>.
    /// Disposing it removes it from the parent registry's list.
    /// </summary>
    public sealed class Registration : IDisposable
    {
        /// <summary>The verb that was registered.</summary>
        public string Verb { get; }

        /// <summary>The handler that was registered.</summary>
        public Action<PluginCommand> Handler { get; }

        private readonly List<Registration> _owner;

        internal Registration(
            string verb,
            Action<PluginCommand> handler,
            List<Registration> owner)
        {
            Verb = verb;
            Handler = handler;
            _owner = owner;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            _owner.Remove(this);
        }
    }
}