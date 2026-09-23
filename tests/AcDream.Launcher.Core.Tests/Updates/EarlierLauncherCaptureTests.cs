using System.Text.Json;
using System.Text.Json.Nodes;
using AcDream.Launcher.Core.Updates;
using AcDream.Platform;

namespace AcDream.Launcher.Core.Tests.Updates;

/// <summary>
/// What a real 0.1.16 launcher leaves when it updates itself, captured from
/// that build (Fixtures/EarlierLauncher0116): the plan and folder tree its own
/// update code staged for a small launcher archive whose two files are in
/// payload.json, and the argument vector it started the staged helper with.
/// Paths are templated: {{DATA}} its data folder, {{TARGET}} its launcher
/// folder, {{TRANSACTION}} the transaction, {{PARENT_PID}} its process.
/// </summary>
public sealed class EarlierLauncherCaptureTests : IDisposable
{
    private static readonly string FixtureDirectory = Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "EarlierLauncher0116");

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "openac-earlier-capture-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    /// <summary>
    /// This version finds the captured transaction where 0.1.16 put it, reads
    /// its plan, and takes the captured helper arguments and helper location
    /// for that plan's. Mutation: looking under app/ for the earlier update
    /// folder, or a helper location other than the staged payload, fails this.
    /// </summary>
    [Fact]
    public async Task TheCapturedUpdateIsFoundAndItsHelperArgumentsMatchIt()
    {
        Captured captured = Materialize();
        using var http = new HttpClient();

        LauncherSelfUpdateManager earlier = Assert.Single(
            EarlierLayoutSelfUpdate.ManagersFor(captured.Paths, http, captured.Platform));
        SelfUpdatePlan plan = Assert.IsType<SelfUpdatePlan>(await earlier.LoadPendingAsync());

        Assert.Equal(captured.PendingPlanPath, earlier.PendingPlanPath);
        Assert.Equal(SelfUpdatePlanState.Staged, plan.State);
        Assert.Equal("0.1.17", plan.Version);
        Assert.Equal(captured.Target, plan.TargetDirectory);

        (string[] arguments, string workingDirectory, string processPath) = captured.Helper(plan.TransactionId);
        Assert.Equal(LauncherSelfUpdateBootstrap.HelperArgument, arguments[0]);
        Assert.True(int.TryParse(arguments[1], out _));
        Assert.Equal(plan.TargetDirectory, arguments[2]);
        Assert.Equal(plan.TransactionId, arguments[3]);
        Assert.Equal(earlier.GetStagedLauncherPath(plan), processPath);
        Assert.Equal(Path.GetDirectoryName(processPath), workingDirectory);
    }

    /// <summary>
    /// The captured update, applied by its helper, is confirmed by this
    /// version's installed copy and finished in the earlier folder; the
    /// confirmed launcher carries on with the captured public arguments.
    /// Mutation: confirming against the new install folder fails this.
    /// </summary>
    [Fact]
    public async Task TheCapturedUpdateIsConfirmedAndFinishedWhereItLives()
    {
        Captured captured = Materialize();
        using var http = new HttpClient();
        LauncherSelfUpdateManager earlier = LauncherSelfUpdateManager.ForEarlierLayout(
            LegacyApplicationLayout.Detect(captured.Platform).DataDirectory,
            http);
        UpdateSessionBarrier.ExclusiveLease helperLease = earlier.Barrier.AcquireExclusive();
        SelfUpdatePlan applied = await earlier.ApplyPendingAsync(captured.Target);
        string[] publicArguments = captured.Helper(applied.TransactionId).Arguments[4..];
        Task helper = Task.Run(async () =>
        {
            try
            {
                DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(20);
                while (!earlier.IsConfirmed(applied.TransactionId) && DateTimeOffset.UtcNow < deadline)
                    await Task.Delay(20);
                await earlier.CompleteConfirmedAsync(applied.TransactionId, captured.Target);
            }
            finally
            {
                helperLease.Dispose();
            }
        });

        SelfUpdateStartupResult result = await LauncherSelfUpdateBootstrap.HandleAsync(
            [LauncherSelfUpdateBootstrap.ConfirmArgument, applied.TransactionId, .. publicArguments],
            captured.Paths,
            http,
            captured.Target,
            Path.Combine(captured.Target, "acdream-launcher.exe"),
            new SelfUpdateStartSettings { Platform = captured.Platform });
        await helper.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.False(result.ShouldExit);
        Assert.Equal(publicArguments, result.RemainingArguments);
        Assert.Equal(
            "fixture-new-launcher",
            await File.ReadAllTextAsync(Path.Combine(captured.Target, "acdream-launcher.exe")));
        Assert.False(File.Exists(captured.PendingPlanPath));
        Assert.False(Directory.Exists(earlier.GetTransactionDirectory(applied.TransactionId)));
    }

    /// <summary>Lays the captured state out in a scratch profile, exactly as 0.1.16 left it.</summary>
    private Captured Materialize()
    {
        var platform = new ProfileEnvironment(Path.Combine(_root, "profile"));
        string data = LegacyApplicationLayout.Detect(platform).DataDirectory;
        string target = Path.Combine(_root, "launcher");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "acdream-launcher.exe"), "old-launcher");

        string plan = File.ReadAllText(Path.Combine(FixtureDirectory, "pending.json"))
            .Replace("{{TARGET}}", JsonEncodedText.Encode(target).ToString(), StringComparison.Ordinal);
        string transaction = JsonNode.Parse(plan)!["transactionId"]!.GetValue<string>();
        var payload = JsonNode.Parse(File.ReadAllText(Path.Combine(FixtureDirectory, "payload.json")))!
            .AsObject();
        JsonArray files = JsonNode.Parse(plan)!["files"]!.AsArray();

        foreach (string line in File.ReadAllLines(Path.Combine(FixtureDirectory, "staged-tree.txt")))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            string relative = line.Replace("{{TRANSACTION}}", transaction, StringComparison.Ordinal);
            string path = Path.Combine(data, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string name = Path.GetFileName(relative);
            if (relative == "launcher-update/pending.json")
            {
                File.WriteAllText(path, plan);
            }
            else if (relative.Contains("/payload/", StringComparison.Ordinal))
            {
                File.WriteAllText(path, payload[name]!.GetValue<string>());
                if (!OperatingSystem.IsWindows())
                {
                    int mode = files.Single(file => file!["path"]!.GetValue<string>() == name)!
                        ["unixMode"]!.GetValue<int>();
                    File.SetUnixFileMode(path, (UnixFileMode)mode);
                }
            }
            else
            {
                File.WriteAllBytes(path, []);
            }
        }

        return new Captured(
            platform,
            ApplicationPathSet.Resolve(platform: platform),
            data,
            target,
            Path.Combine(data, "launcher-update", "pending.json"));
    }

    private sealed record Captured(
        ProfileEnvironment Platform,
        ApplicationPathSet Paths,
        string Data,
        string Target,
        string PendingPlanPath)
    {
        /// <summary>The captured helper start, filled in for this transaction.</summary>
        public (string[] Arguments, string WorkingDirectory, string ProcessPath) Helper(string transaction)
        {
            JsonObject captured = JsonNode.Parse(
                File.ReadAllText(Path.Combine(FixtureDirectory, "helper-arguments.json")))!.AsObject();
            string Fill(string value, bool isPath) =>
                (isPath ? value.Replace('\\', Path.DirectorySeparatorChar) : value)
                    .Replace("{{DATA}}", Data, StringComparison.Ordinal)
                    .Replace("{{TARGET}}", Target, StringComparison.Ordinal)
                    .Replace("{{TRANSACTION}}", transaction, StringComparison.Ordinal)
                    .Replace("{{PARENT_PID}}", "4242", StringComparison.Ordinal);
            string[] arguments = captured["arguments"]!.AsArray()
                .Select(node => Fill(node!.GetValue<string>(), isPath: false))
                .ToArray();
            return (
                arguments,
                Fill(captured["workingDirectory"]!.GetValue<string>(), isPath: true),
                Fill(captured["processPath"]!.GetValue<string>(), isPath: true));
        }
    }
}
