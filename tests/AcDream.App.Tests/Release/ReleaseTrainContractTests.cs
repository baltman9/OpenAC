using System.Diagnostics;
using System.Text.RegularExpressions;

namespace AcDream.App.Tests.Release;

/// <summary>
/// Pins the two release trains in the CI workflow. A plain <c>vX.Y.Z</c> tag publishes the release
/// every launcher polls; a tag whose version carries a SemVer pre-release part publishes a test
/// build that is never the latest release, which is what keeps it away from players, because the
/// update feed is read through the "latest release" route and that route skips pre-releases. These
/// read repository text, and run the retention rule's own checks.
/// </summary>
public sealed class ReleaseTrainContractTests
{
    [Fact]
    public void TheTrainIsDecidedOnceAndLatestIsTheOppositeOfPreRelease()
    {
        string step = TrainStep();

        Assert.Contains(
            "prerelease=$(if ($isPreRelease) { 'true' } else { 'false' })",
            step,
            StringComparison.Ordinal);
        Assert.Contains(
            "make_latest=$(if ($isPreRelease) { 'false' } else { 'true' })",
            step,
            StringComparison.Ordinal);

        // Nothing may state the pair outright. A literal value here is what would
        // publish a test build as the release everybody updates to.
        string workflow = Workflow();
        foreach (string literal in new[]
                 {
                     "prerelease: false", "prerelease: true",
                     "make_latest: true", "make_latest: false",
                 })
        {
            Assert.DoesNotContain(literal, workflow, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryStepThatTouchesTheReleaseReadsThatOneDecision()
    {
        string job = ReleaseJob();
        string[] steps = ReleaseActionSteps(job);

        // Two today: the release itself, and the step that attaches the second
        // platform's assets. A third would have to carry the pair as well, or it
        // would silently reset the flags on the release it appends to.
        Assert.Equal(2, steps.Length);
        foreach (string step in steps)
        {
            Assert.Contains(
                "prerelease: ${{ steps.train.outputs.prerelease }}",
                step,
                StringComparison.Ordinal);
            Assert.Contains(
                "make_latest: ${{ steps.train.outputs.make_latest }}",
                step,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ATagPushedFromTheWrongBranchFailsInsteadOfPublishing()
    {
        string step = TrainStep();

        Assert.Contains(
            "$branch = if ($isPreRelease) { 'dev' } else { 'main' }",
            step,
            StringComparison.Ordinal);
        Assert.Contains("merge-base --is-ancestor", step, StringComparison.Ordinal);

        // The guard walks history, so a shallow checkout would make it meaningless.
        Assert.Contains("fetch-depth: 0", ReleaseJob(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("v0.1.13", true, false)]
    [InlineData("v1.0.0", true, false)]
    [InlineData("v0.1.13-dev.1", true, true)]
    [InlineData("v0.1.13-dev.10", true, true)]
    [InlineData("v0.1.13-rc.2", true, true)]
    [InlineData("v0.1.13.4", false, false)]
    [InlineData("0.1.13", false, false)]
    [InlineData("v0.1.13-", false, false)]
    [InlineData("v01.1.13", false, false)]
    [InlineData("v0.1", false, false)]
    public void TheWorkflowsTagShapeTellsTheTwoTrainsApart(
        string tag,
        bool accepted,
        bool preRelease)
    {
        Match match = new Regex(TagShape(), RegexOptions.CultureInvariant).Match(tag);

        Assert.Equal(accepted, match.Success);
        if (accepted)
        {
            Assert.Equal(preRelease, match.Groups["pre"].Success);
        }
    }

    [Fact]
    public void ThePluginApiPackageIsAReleaseAssetOnBothTrains()
    {
        string job = ReleaseJob();

        int pack = job.IndexOf("- name: Pack the plugin API package", StringComparison.Ordinal);
        int publish = job.IndexOf("- name: Publish the GitHub Release", StringComparison.Ordinal);
        Assert.True(pack > 0, "Could not locate the step that packs the plugin API package.");
        Assert.True(publish > pack, "The package has to be packed before the release is published.");

        Assert.Contains("bin/package/*.nupkg\n", job, StringComparison.Ordinal);
        Assert.Contains("bin/package/*.nupkg.sha256\n", job, StringComparison.Ordinal);

        // No condition on the step: both trains carry the package.
        Assert.DoesNotContain("if:", Step(job, "Pack the plugin API package"), StringComparison.Ordinal);
    }

    [Fact]
    public void RetentionRunsOnlyAfterAPreReleaseHasPublished()
    {
        string workflow = Workflow();
        string job = JobBody(workflow, "prune-pre-releases");

        Assert.Contains("needs: [release]", job, StringComparison.Ordinal);
        Assert.Contains("needs.release.result == 'success'", job, StringComparison.Ordinal);
        Assert.Contains("needs.release.outputs.prerelease == 'true'", job, StringComparison.Ordinal);
        Assert.Contains("./tools/prune-dev-prereleases.ps1 -Keep 5", job, StringComparison.Ordinal);

        // It can only read that answer because the release job publishes it.
        Assert.Contains(
            "prerelease: ${{ steps.train.outputs.prerelease }}",
            ReleaseJobHeader(),
            StringComparison.Ordinal);
    }

    /// <summary>The retention rule decides which releases and tags are deleted, so its own checks
    /// run here rather than only by hand.</summary>
    [Fact]
    public void TheRetentionSelectionRulePassesItsOwnChecks()
    {
        string root = RepositoryRoot();
        string script = Path.Combine(root, "tools", "prune-dev-prereleases.ps1");
        Assert.True(File.Exists(script), $"Missing retention script: {script}");

        var startInfo = new ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = root,
        };
        foreach (string argument in new[]
                 {
                     "-NoProfile", "-NonInteractive", "-File", script, "-SelfTest",
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start PowerShell 7.");
        string output = process.StandardOutput.ReadToEnd();
        string errors = process.StandardError.ReadToEnd();
        Assert.True(
            process.WaitForExit(120_000),
            "The retention selection checks did not finish within two minutes.");
        Assert.Equal(0, process.ExitCode);
        Assert.Contains("all cases pass", output + errors, StringComparison.Ordinal);
    }

    /// <summary>The tag shape the workflow itself uses, read out of it rather than restated, so
    /// this suite cannot agree with a rule the workflow no longer applies.</summary>
    private static string TagShape()
    {
        Match assignment = Regex.Match(
            TrainStep(),
            @"\$shape = '(?<first>[^']+)' \+\s*\r?\n\s*'(?<second>[^']+)'",
            RegexOptions.CultureInvariant);
        Assert.True(assignment.Success, "Could not locate the tag shape in the release train step.");
        return assignment.Groups["first"].Value + assignment.Groups["second"].Value;
    }

    private static string Workflow() =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), ".github", "workflows", "ci.yml"));

    private static string ReleaseJob() => JobBody(Workflow(), "release");

    private static string ReleaseJobHeader()
    {
        string job = ReleaseJob();
        int steps = job.IndexOf("\n    steps:", StringComparison.Ordinal);
        Assert.True(steps > 0, "Could not locate the release job's steps.");
        return job[..steps];
    }

    private static string TrainStep() => Step(ReleaseJob(), "Decide the release train");

    private static string[] ReleaseActionSteps(string job) =>
        Regex.Matches(job, @"- name: (?<name>[^\n]+)", RegexOptions.CultureInvariant)
            .Select(match => Step(job, match.Groups["name"].Value))
            .Where(step => step.Contains("softprops/action-gh-release@", StringComparison.Ordinal))
            .ToArray();

    private static string Step(string job, string name)
    {
        int start = job.IndexOf($"- name: {name}", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not locate the '{name}' step.");
        int next = job.IndexOf("\n      - ", start, StringComparison.Ordinal);
        return next > start ? job[start..next] : job[start..];
    }

    private static string JobBody(string workflow, string job)
    {
        string[] lines = workflow.Split('\n');
        int start = Array.FindIndex(lines, line => line == $"  {job}:");
        Assert.True(start >= 0, $"Could not locate the {job} job in ci.yml.");

        int end = start + 1;
        while (end < lines.Length && !StartsAJob(lines[end]))
        {
            end++;
        }

        return string.Join('\n', lines[start..end]);
    }

    private static bool StartsAJob(string line) =>
        line.Length > 2
        && line.StartsWith("  ", StringComparison.Ordinal)
        && line[2] != ' ';

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AcDream.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}