using System.Globalization;
using System.Text.Json;
using AcDream.Headless.Configuration;
using AcDream.Headless.Credentials;
using AcDream.Headless.Diagnostics;
using AcDream.Headless.Hosting;
using AcDream.Plugin.Abstractions;
using Xunit.Abstractions;

namespace AcDream.Headless.Tests;

/// <summary>
/// The windowless half of the castability evidence. A buff or attack spell is
/// only castable when the character's skill in that spell's school is at least
/// the spell's difficulty, and the surface now fails CLOSED when it cannot
/// name a school's skill — so a windowless bot whose skill table never
/// arrived answers "nothing is castable" rather than "everything is". This
/// pins that the windowless composition really does supply the skill table and
/// that a live character reports every magic school through it.
/// </summary>
[Trait("Lane", "Live")]
public sealed class HeadlessMagicSchoolSkillsLiveTests(ITestOutputHelper output)
{
    /// <summary>
    /// Creature Enchantment, Item Enchantment, Life, War and Void — the five
    /// schools a spell can belong to.
    /// </summary>
    private static readonly uint[] MagicSchoolSkillIds = [31u, 32u, 33u, 34u, 43u];

    [Fact]
    public void ALiveWindowlessSessionNamesEveryMagicSchoolSkill()
    {
        if (Environment.GetEnvironmentVariable("ACDREAM_LIVE") != "1")
        {
            Assert.Fail(
                "Lane=Live requires ACDREAM_LIVE=1 and a reachable configured server.");
        }

        string host = Environment.GetEnvironmentVariable("ACDREAM_TEST_HOST") ?? "127.0.0.1";
        string portText = Environment.GetEnvironmentVariable("ACDREAM_TEST_PORT") ?? "9000";
        string? user = Environment.GetEnvironmentVariable("ACDREAM_TEST_USER");
        string? pass = Environment.GetEnvironmentVariable("ACDREAM_TEST_PASS");
        Assert.False(string.IsNullOrEmpty(user));
        Assert.False(string.IsNullOrEmpty(pass));

        string temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "acdream-school-skills-pin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        string statusPath = Path.Combine(temporaryRoot, "status.jsonl");
        var descriptor = new HeadlessSessionDescriptor
        {
            Id = "school-skills-pin",
            Endpoint = new HeadlessEndpointDescriptor
            {
                Host = host,
                Port = int.Parse(portText, CultureInfo.InvariantCulture),
            },
            Account = user!,
            Character = new HeadlessCharacterSelector { Name = "+Acdream" },
            Policy = new HeadlessBotPolicyDescriptor { Id = "idle" },
            Credential = new HeadlessCredentialReference
            {
                Provider = HeadlessCredentialProviderKind.Environment,
                Reference = "ACDREAM_TEST_PASS",
            },
            StatusFile = statusPath,
        };
        var diagnosticsOutput = new StringWriter();

        using HeadlessProcessContentOwner content = OpenProcessContent(
            message => diagnosticsOutput.WriteLine("content: " + message));
        using HeadlessProcessContentOwner.HeadlessProcessContentLease lease =
            content.AcquireLease(descriptor.Id);

        using var session = new HeadlessSessionHost(
            descriptor,
            new HeadlessCredentialSecret("school-skills-pin", pass!),
            new HeadlessDiagnosticWriter(diagnosticsOutput),
            sessionOperations: null, // real network
            contentLease: lease);

        bool WaitUntil(TimeSpan timeout, Func<bool> condition)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                    return true;
                session.Tick(0.1d);
                Thread.Sleep(100);
            }
            return condition();
        }

        _ = session.Start();

        Assert.True(
            WaitUntil(
                TimeSpan.FromSeconds(45d),
                () => EventNames(statusPath).Contains("enteredWorld")),
            "The session never entered the world. Diagnostics: " + diagnosticsOutput);

        IAutomationSurface automation = session.Plugins.Host.Automation;
        Assert.True(
            WaitUntil(TimeSpan.FromSeconds(30d), () => automation.Character.Skills.Count > 0),
            "The windowless surface never reported a single skill, so the "
                + "installed skill table never reached it. Diagnostics: "
                + diagnosticsOutput);

        IReadOnlyList<PluginSkillInfo> skills = automation.Character.Skills;
        output.WriteLine(
            "skills=" + skills.Count + " schools=" + string.Join(
                ", ",
                MagicSchoolSkillIds.Select(id =>
                    automation.Character.TryGetSkill(id, out PluginSkillInfo s)
                        ? $"{id}:{s.Name}={s.Current}"
                        : $"{id}:absent")));

        var missing = new List<string>();
        foreach (uint schoolSkillId in MagicSchoolSkillIds)
        {
            if (!automation.Character.TryGetSkill(schoolSkillId, out PluginSkillInfo school))
            {
                missing.Add($"{schoolSkillId}: not reported at all");
                continue;
            }
            if (string.IsNullOrEmpty(school.Name))
                missing.Add($"{schoolSkillId}: unnamed (no skill table)");
            if (school.Current == 0u)
                missing.Add($"{schoolSkillId} ({school.Name}): level 0");
            Assert.Contains(skills, candidate => candidate.SkillId == schoolSkillId);
        }

        Assert.True(
            missing.Count == 0,
            "A live windowless session must answer every magic school so "
                + "castability has something to compare against; these did "
                + "not: " + string.Join("; ", missing));
    }

    private static HeadlessProcessContentOwner OpenProcessContent(
        Action<string> diagnostic)
    {
        string datDirectory =
            Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Documents",
                "Asheron's Call");
        string preparedAssetPath =
            Environment.GetEnvironmentVariable("ACDREAM_PAK_PATH")
            ?? Path.Combine(datDirectory, "acdream.pak");
        Assert.True(
            Directory.Exists(datDirectory),
            $"The pin needs the installed data directory: {datDirectory} "
                + "(set ACDREAM_DAT_DIR).");
        Assert.True(
            File.Exists(preparedAssetPath),
            $"The pin needs the prepared package: {preparedAssetPath} "
                + "(set ACDREAM_PAK_PATH).");
        return new HeadlessProcessContentOwner(
            new HeadlessContentDescriptor
            {
                DatDirectory = datDirectory,
                PreparedAssetPath = preparedAssetPath,
            },
            diagnostic);
    }

    private static string[] EventNames(string statusPath)
    {
        if (!File.Exists(statusPath))
            return [];
        var names = new List<string>();
        foreach (string line in LiveStatusFile.ReadAllLines(statusPath))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            using JsonDocument document = JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("e", out JsonElement name)
                && name.GetString() is { } text)
            {
                names.Add(text);
            }
        }
        return [.. names];
    }
}
