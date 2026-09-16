using AcDream.Launcher.Core.Profiles;
using AcDream.Launcher.Core.Updates;

namespace AcDream.Launcher.Core.Plugins;

/// <summary>Checks a launcher-read manifest's declared client versions and hosts against the
/// installed client. Reason strings match <c>AcDream.Core.Plugins.PluginHostCompatibility</c>
/// (L-303), except for the client-not-installed case, which that host never sees.</summary>
public static class LauncherPluginCompatibility
{
    public const string ClientNotInstalled = "client not installed";

    /// <summary>A row's compatibility line and whether it should read as a warning.</summary>
    /// <summary>How <see cref="Describe"/> opens the note for a plugin that runs everywhere, so a
    /// card can leave that routine case unsaid.</summary>
    public const string CompatiblePrefix = "Compatible with OpenAC ";

    public readonly record struct CompatibilityDescription(string Text, bool IsWarning);

    public static string? Evaluate(
        LauncherPluginManifest manifest,
        LaunchMode launchMode,
        LauncherVersion? clientVersion)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        LauncherPluginHostKind host = ToHostKind(launchMode);
        IReadOnlyList<LauncherPluginHostKind> hosts = manifest.Hosts
            ?? [LauncherPluginHostKind.Graphical, LauncherPluginHostKind.Headless];
        if (!hosts.Contains(host))
        {
            return $"runs only on the "
                + $"{string.Join(" or ", hosts.Select(DescribeHost))} host";
        }

        return VersionOnlyReason(manifest, clientVersion);
    }

    /// <summary>The Discover/Installed row's compatibility line: always populated, unlike
    /// <see cref="Evaluate"/>'s single-host verdict, so a host restriction is never hidden behind a
    /// blank line. A version failure is reported even on a manifest's only supported host, since
    /// switching host would not fix it; <see cref="CompatibilityDescription.IsWarning"/> is true only
    /// for that case, so a host restriction or a missing client keep the row's usual styling.</summary>
    public static CompatibilityDescription Describe(
        LauncherPluginManifest manifest,
        LauncherVersion? clientVersion)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (clientVersion is null)
        {
            return new CompatibilityDescription("Client not installed", IsWarning: false);
        }

        if (VersionOnlyReason(manifest, clientVersion) is { } versionReason)
        {
            return new CompatibilityDescription(versionReason, IsWarning: true);
        }

        IReadOnlyList<LauncherPluginHostKind> hosts = manifest.Hosts
            ?? [LauncherPluginHostKind.Graphical, LauncherPluginHostKind.Headless];
        bool graphical = hosts.Contains(LauncherPluginHostKind.Graphical);
        bool headless = hosts.Contains(LauncherPluginHostKind.Headless);
        if (graphical && headless)
        {
            return new CompatibilityDescription($"{CompatiblePrefix}{clientVersion}", IsWarning: false);
        }

        return new CompatibilityDescription(graphical ? "Graphical only" : "Headless only", IsWarning: false);
    }

    /// <summary>A reason without its trailing "(this is x)", for a card that already shows versions.
    /// The full reason stays in refusals and in the client's own text, which parity tests pin.</summary>
    public static string WithoutClientVersion(string reason)
    {
        int cut = reason.LastIndexOf(" (this is ", StringComparison.Ordinal);
        return cut > 0 && reason.EndsWith(')') ? reason[..cut] : reason;
    }

    private static string? VersionOnlyReason(LauncherPluginManifest manifest, LauncherVersion? clientVersion)
    {
        LauncherPluginHostVersion? installed = LauncherPluginHostVersion.FromLauncherVersion(clientVersion);
        if (installed is not { } version)
            return ClientNotInstalled;

        if (manifest.MinHostVersion is { } min && version.CompareTo(min) < 0)
            return $"requires OpenAC {min} or newer (this is {version})";

        if (manifest.MaxHostVersion is { } max && version.CompareTo(max) > 0)
            return $"supports OpenAC up to {max} (this is {version})";

        if (manifest.SkipHostVersions.Contains(version))
            return $"is marked broken on OpenAC {version}";

        return null;
    }

    private static LauncherPluginHostKind ToHostKind(LaunchMode mode) => mode switch
    {
        LaunchMode.Headless => LauncherPluginHostKind.Headless,
        _ => LauncherPluginHostKind.Graphical,
    };

    private static string DescribeHost(LauncherPluginHostKind host) => host switch
    {
        LauncherPluginHostKind.Graphical => "graphical",
        LauncherPluginHostKind.Headless => "headless",
        _ => host.ToString(),
    };
}
