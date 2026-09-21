using System.Security.Cryptography;
using AcDream.Launcher.Core.Plugins;
using AcDream.Launcher.Core.Updates;

namespace AcDream.PluginCheck;

/// <summary>Validates a plugin against the launcher's own install rules — never a copy of them.
/// Every check below calls into <c>AcDream.Launcher.Core.Plugins</c> directly, the same code
/// <see cref="DirectInstallCheck"/> and <see cref="PluginInstaller"/> run at install time, so a rule
/// change there changes this tool's verdict automatically.</summary>
internal static class PluginCheckRunner
{
    public const int SchemaVersion = 1;

    public static async Task<PluginCheckReport> RunAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);

        if (Directory.Exists(fullPath))
        {
            return RunDirectory(fullPath);
        }

        if (File.Exists(fullPath))
        {
            if (!string.Equals(Path.GetExtension(fullPath), ".zip", StringComparison.OrdinalIgnoreCase))
            {
                throw new PluginCheckUsageException(
                    $"'{path}' is a file but not a .zip. Pass an unzipped plugin folder or a "
                    + "plugin .zip.");
            }

            return await RunZipAsync(fullPath, cancellationToken).ConfigureAwait(false);
        }

        throw new PluginCheckUsageException($"'{path}' does not exist.");
    }

    private static PluginCheckReport RunDirectory(string directory)
    {
        var checks = new List<PluginCheckItem>();

        string manifestPath = Path.Combine(directory, "plugin.json");
        if (!File.Exists(manifestPath))
        {
            checks.Add(Fail(
                "plugin.json present",
                "No 'plugin.json' was found at the root of the plugin folder.",
                "Add a plugin.json at the folder's root."));
            return Finish(PluginCheckMode.Directory, directory, checks);
        }

        checks.Add(Pass("plugin.json present", "Found plugin.json at the folder root."));

        LauncherPluginManifest? manifest = TryParseManifest(manifestPath, checks);
        if (manifest is null)
        {
            return Finish(PluginCheckMode.Directory, directory, checks);
        }

        AddValidateForInstall(manifest, checks);

        string? refusal = DirectInstallCheck.Refusal(directory, manifest);
        checks.Add(refusal is null
            ? Pass(
                "folder passes the direct-install checks",
                "Content, entry DLL, icon, and path/link safety all check out.")
            : Fail(
                "folder passes the direct-install checks",
                refusal,
                "This is the exact refusal a hand-unzipped install would show; fix it and re-run."));

        return Finish(PluginCheckMode.Directory, directory, checks);
    }

    private static async Task<PluginCheckReport> RunZipAsync(
        string zipPath,
        CancellationToken cancellationToken)
    {
        var checks = new List<PluginCheckItem>();

        long zipLength = new FileInfo(zipPath).Length;
        if (zipLength > PluginInstaller.ContractLimits.MaximumZipBytes)
        {
            checks.Add(Fail(
                "zip within size cap",
                $"The archive is {zipLength} bytes, over the launcher's "
                + $"{PluginInstaller.ContractLimits.MaximumZipBytes / (1024 * 1024)} MiB download cap.",
                "Shrink the archive's contents."));
            return Finish(PluginCheckMode.Zip, zipPath, checks);
        }

        checks.Add(Pass("zip within size cap", $"{zipLength} bytes."));

        string stagingDirectory = Path.Combine(
            Path.GetTempPath(), "acdream-plugincheck", Guid.NewGuid().ToString("N"));
        try
        {
            var extractor = new SafeZipExtractor(
                PluginInstaller.ContractLimits.Extraction,
                ignoreDeclaredModes: true);
            IReadOnlyList<ExtractedFileRecord> extracted;
            try
            {
                extracted = await extractor.ExtractAsync(
                        zipPath, stagingDirectory, executableNames: null, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (LauncherUpdateException ex)
            {
                checks.Add(Fail(
                    "zip extracts safely",
                    ex.Message,
                    "Rebuild the archive without symlinks, path traversal, or entries over the "
                    + "launcher's size/count limits."));
                return Finish(PluginCheckMode.Zip, zipPath, checks);
            }

            checks.Add(Pass(
                "zip extracts safely",
                $"{extracted.Count} file(s) extracted within the launcher's limits."));

            string manifestPath = Path.Combine(stagingDirectory, "plugin.json");
            if (!File.Exists(manifestPath))
            {
                checks.Add(Fail(
                    "plugin.json present",
                    ContentPolicyMessageForMissingManifest(extracted),
                    "Add a plugin.json at the root of the archive."));
                return Finish(PluginCheckMode.Zip, zipPath, checks);
            }

            checks.Add(Pass("plugin.json present", "Found plugin.json at the archive root."));

            LauncherPluginManifest? manifest = TryParseManifest(manifestPath, checks);
            if (manifest is null)
            {
                return Finish(PluginCheckMode.Zip, zipPath, checks);
            }

            AddValidateForInstall(manifest, checks);

            try
            {
                PluginContentPolicy.Validate(extracted, manifest.EntryDll);
                checks.Add(Pass(
                    "plugin content policy",
                    "Every file has an allowed extension, no 'runtimes' folder, and the "
                    + "declared entry DLL is present."));
            }
            catch (LauncherUpdateException ex)
            {
                checks.Add(Fail(
                    "plugin content policy",
                    ex.Message,
                    "Remove disallowed files, drop any 'runtimes' folder, and ship the declared "
                    + "entryDll."));
            }

            checks.Add(CheckIcon(stagingDirectory, extracted));
            checks.Add(CheckSha256Sidecar(zipPath));

            return Finish(PluginCheckMode.Zip, zipPath, checks);
        }
        finally
        {
            SafeZipExtractor.TryDeleteDirectory(stagingDirectory);
        }
    }

    private static PluginCheckItem CheckIcon(
        string stagingDirectory,
        IReadOnlyList<ExtractedFileRecord> extracted)
    {
        if (!extracted.Any(file =>
                string.Equals(file.Path, LauncherPluginIcon.FileName, StringComparison.Ordinal)))
        {
            return Skip("plugin icon", "No icon.png at the archive root; it is optional.");
        }

        byte[] iconBytes = File.ReadAllBytes(Path.Combine(stagingDirectory, LauncherPluginIcon.FileName));
        try
        {
            LauncherPluginIcon.Validate(iconBytes);
            return Pass(
                "plugin icon",
                $"icon.png is a valid {LauncherPluginIcon.Extent}x{LauncherPluginIcon.Extent} PNG, "
                + $"{iconBytes.Length} bytes.");
        }
        catch (LauncherUpdateException ex)
        {
            return Fail(
                "plugin icon",
                ex.Message,
                $"Make icon.png a non-animated {LauncherPluginIcon.Extent}x{LauncherPluginIcon.Extent} "
                + $"PNG under {LauncherPluginIcon.MaximumBytes / 1024} KiB.");
        }
    }

    /// <summary>The <c>.sha256</c> sidecar is a release asset, not part of the zip itself, so this
    /// looks beside the given zip path rather than inside it. Absent locally, it only matters once a
    /// release is published, so a missing sidecar is not refused.</summary>
    private static PluginCheckItem CheckSha256Sidecar(string zipPath)
    {
        string sidecarPath = zipPath + ".sha256";
        if (!File.Exists(sidecarPath))
        {
            return Skip(
                "sha256 sidecar",
                "No '<zip>.sha256' beside the archive; only required once this ships as a "
                + "GitHub release, not for a local check.");
        }

        PluginSha256File sidecar;
        try
        {
            sidecar = PluginSha256File.Parse(File.ReadAllText(sidecarPath));
        }
        catch (LauncherUpdateException ex)
        {
            return Fail(
                "sha256 sidecar",
                ex.Message,
                "Regenerate the sidecar with `shasum -a 256 <zip>`.");
        }

        string expectedName = Path.GetFileName(zipPath);
        try
        {
            sidecar.RequireMatches(expectedName);
        }
        catch (LauncherUpdateException ex)
        {
            return Fail(
                "sha256 sidecar",
                ex.Message,
                $"Name the sidecar's target '{expectedName}', or regenerate it beside the "
                + "renamed archive.");
        }

        string actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(zipPath)));
        if (!string.Equals(actual, sidecar.Sha256, StringComparison.Ordinal))
        {
            return Fail(
                "sha256 sidecar",
                $"The sidecar declares {sidecar.Sha256}, but the archive hashes to {actual}.",
                "Regenerate the sidecar with `shasum -a 256 <zip>`.");
        }

        return Pass("sha256 sidecar", $"Matches the archive's SHA-256 ({sidecar.Sha256}).");
    }

    private static string ContentPolicyMessageForMissingManifest(IReadOnlyList<ExtractedFileRecord> files)
    {
        try
        {
            // No manifest means no real entryDll to check; PluginContentPolicy.Validate rejects a
            // missing 'plugin.json' before it ever looks at that argument, so the placeholder never
            // surfaces.
            PluginContentPolicy.Validate(files, "unknown.dll");
            return "The plugin archive has no 'plugin.json' at its root.";
        }
        catch (LauncherUpdateException ex)
        {
            return ex.Message;
        }
    }

    private static LauncherPluginManifest? TryParseManifest(string manifestPath, List<PluginCheckItem> checks)
    {
        try
        {
            LauncherPluginManifest manifest = LauncherPluginManifest.Parse(File.ReadAllText(manifestPath));
            checks.Add(Pass(
                "plugin.json parses",
                $"id={manifest.Id}, version={manifest.Version}, entryDll={manifest.EntryDll}."));
            return manifest;
        }
        catch (LauncherPluginManifestException ex)
        {
            // Parse reports JSON syntax errors and content rules (a link in a capability note, a
            // duplicate property) through the same exception, so the hint must hold for both: the
            // launcher's own message above already names the actual problem.
            checks.Add(Fail(
                "plugin.json parses",
                ex.Message,
                "Correct the problem named above in plugin.json; the field rules are in "
                + "docs/plugin-manifest.md. Parsing stops at the first problem, so run this again "
                + "after each fix."));
            return null;
        }
    }

    private static void AddValidateForInstall(LauncherPluginManifest manifest, List<PluginCheckItem> checks)
    {
        try
        {
            manifest.ValidateForInstall();
            checks.Add(Pass(
                "manifest satisfies install rules",
                "id, version, minHostVersion, hosts, apiVersion and capabilities are all valid."));
        }
        catch (LauncherPluginManifestException ex)
        {
            checks.Add(Fail(
                "manifest satisfies install rules",
                ex.Message,
                "Correct the field named above; the launcher's install rules are under "
                + "\"Publishing for the launcher\" in docs/plugin-manifest.md. These rules stop at "
                + "the first problem, so run this again after each fix."));
        }
    }

    private static PluginCheckReport Finish(PluginCheckMode mode, string path, List<PluginCheckItem> checks)
    {
        bool anyFail = checks.Any(check => check.Status == PluginCheckStatus.Fail);
        return new PluginCheckReport(
            SchemaVersion,
            mode,
            path,
            anyFail ? PluginCheckVerdict.WouldBeRefused : PluginCheckVerdict.WouldInstall,
            checks);
    }

    private static PluginCheckItem Pass(string check, string message) =>
        new(check, PluginCheckStatus.Pass, message);

    private static PluginCheckItem Skip(string check, string message) =>
        new(check, PluginCheckStatus.Skip, message);

    private static PluginCheckItem Fail(string check, string reason, string remediation)
    {
        string trimmed = reason.TrimEnd();
        string terminator = trimmed.EndsWith('.') || trimmed.EndsWith('!') || trimmed.EndsWith('?')
            ? string.Empty
            : ".";
        return new(check, PluginCheckStatus.Fail, $"{trimmed}{terminator} Fix: {remediation}");
    }
}
