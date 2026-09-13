using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RynthCore.Loot.VTank;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Meta;

namespace AcDream.Tools.AutomationCompatibilityProbe;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static int Main(string[] args)
    {
        try
        {
            Options options = Options.Parse(args);
            if (options.ShowHelp)
            {
                Console.WriteLine(Options.Help);
                return 0;
            }

            ValidateOutputPaths(options);
            Evidence evidence = Run(options);
            string json = JsonSerializer.Serialize(evidence, JsonOptions);
            if (options.OutputPath is { Length: > 0 } outputPath)
            {
                string fullPath = Path.GetFullPath(outputPath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                File.WriteAllText(fullPath, json + Environment.NewLine, new UTF8Encoding(false));
            }
            if (!options.Quiet)
                Console.WriteLine(json);
            bool inputsPassed = evidence.Inputs.Count > 0
                && evidence.Inputs.All(static input => input.Success);
            bool mutationsPassed = !options.VerifyDetectors
                || (evidence.Mutations.Count > 0
                    && evidence.Mutations.All(static mutation => mutation.Detected));
            return inputsPassed && mutationsPassed ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
    }

    private static Evidence Run(Options options)
    {
        string sourceRoot = Path.GetFullPath(options.SourceRoot
            ?? throw new InvalidOperationException("The generated source-root build metadata was not supplied."));
        if (!File.Exists(Path.Combine(sourceRoot, "Plugins", "RynthCore.Plugin.RynthAi", "Meta", "MetFileParser.cs")))
            throw new InvalidOperationException($"Source root is missing required parser files: {sourceRoot}");

        string openAcRoot = FindRepositoryRoot(AppContext.BaseDirectory);
        var evidence = new Evidence
        {
            Scope = "offline-parser-compatibility-only",
            GeneratedUtc = DateTimeOffset.UtcNow,
            Revisions = new RevisionEvidence
            {
                DonorSource = ReadRevision(sourceRoot),
                DonorSourceDirty = ReadGit(sourceRoot, "status", "--porcelain").Length > 0,
                OpenAc = ReadRevision(openAcRoot),
                OpenAcDirty = ReadGit(openAcRoot, "status", "--porcelain").Length > 0,
                CurrentCheckoutSourceFileHashes = SourceFileHashes(sourceRoot, openAcRoot),
                ExecutingAssemblyHashes = ExecutingAssemblyHashes(),
            },
        };

        foreach (string path in options.MetaPaths)
            evidence.Inputs.Add(ProbeMeta(path, isAf: false));
        foreach (string path in options.AfPaths)
            evidence.Inputs.Add(ProbeMeta(path, isAf: true));
        if ((options.EmittedUtlPath is not null
                || options.EmittedRenamedUtlPath is not null
                || options.EmittedControlledUtlPath is not null)
            && options.LootPaths.Count != 1)
            throw new ArgumentException("An emitted UTL path requires exactly one --loot input.");
        foreach (string path in options.LootPaths)
            evidence.Inputs.Add(ProbeLoot(
                path,
                options.EmittedUtlPath,
                options.EmittedRenamedUtlPath,
                options.EmittedControlledUtlPath,
                options.ControlledItemPattern));

        if (options.VerifyDetectors)
            evidence.Mutations.AddRange(RunDetectorMutations(options));
        return evidence;
    }

    private static InputEvidence ProbeMeta(string inputPath, bool isAf)
    {
        string path = RequireInput(inputPath);
        byte[] bytes = File.ReadAllBytes(path);
        string text = File.ReadAllText(path);
        MossMetaParse original = isAf ? MossBridge.ParseAf(text) : MossBridge.ParseMet(text);
        LoadedMeta loaded = isAf ? AfFileParser.Load(path) : MetFileParser.Load(path);
        string emitted = AfFileWriter.SaveToString(loaded.Rules, loaded.EmbeddedNavs);
        MossMetaParse after = MossBridge.ParseAf(emitted);

        List<CanonicalMetaRule> expected = original.Rules;
        var routeErrors = new List<string>();
        List<CanonicalMetaRule> actual = RynthCanonicalizer.Meta(
            loaded.Rules,
            loaded.EmbeddedNavs,
            routeErrors);
        List<Difference> differences = Difference.Compare(expected, actual, "rules");
        bool emittedComparisonPerformed = original.Success && after.Success;
        List<Difference> emittedDifferences = emittedComparisonPerformed
            ? Difference.Compare(expected, after.Rules, "emittedRules")
            : [];
        var unsupported = new List<string>();

        int callCount = CountActions(expected, "CallMetaState");
        int lostCallCount = CountCallLosses(expected, actual);
        if (lostCallCount > 0)
            unsupported.Add($"CallMetaState target/return data changed in {lostCallCount} of {callCount} source actions.");

        int captureCount = CountConditions(expected, "ChatMessageCapture");
        int captureLossCount = CountConditionLosses(expected, actual, "ChatMessageCapture");
        if (captureLossCount > 0)
            unsupported.Add($"ChatMessageCapture changed or disappeared in {captureLossCount} of {captureCount} source conditions.");
        foreach (string warning in loaded.Warnings)
            unsupported.Add($"Parser warning: {warning}");
        foreach (string routeError in routeErrors)
            unsupported.Add($"Embedded route: {routeError}");

        bool success = original.Success
            && after.Success
            && loaded.Rules.Count > 0
            && loaded.Warnings.Count == 0
            && routeErrors.Count == 0
            && differences.Count == 0
            && emittedComparisonPerformed
            && emittedDifferences.Count == 0;

        return new InputEvidence
        {
            Kind = isAf ? "af" : "met",
            Path = path,
            Classification = Classify(path),
            Bytes = bytes.LongLength,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            Success = success,
            OriginalParserSuccess = original.Success,
            EmittedParserSuccess = after.Success,
            OriginalRuleCount = expected.Count,
            DonorRuleCount = loaded.Rules.Count,
            EmittedRuleCount = after.Success ? after.Rules.Count : 0,
            OriginalParserError = original.Error,
            EmittedParserError = after.Error,
            Warnings = loaded.Warnings.ToList(),
            SemanticDifferences = differences,
            EmittedSemanticComparisonPerformed = emittedComparisonPerformed,
            EmittedSemanticDifferences = emittedDifferences,
            UnsupportedMappings = unsupported,
            Checks =
            [
                new("nonempty-source", bytes.Length > 0, $"{bytes.Length} bytes"),
                new("source-parser-accepted", original.Success, original.Error),
                new("donor-produced-rules", loaded.Rules.Count > 0, $"{loaded.Rules.Count} rules"),
                new("donor-warnings-empty", loaded.Warnings.Count == 0, string.Join(" | ", loaded.Warnings)),
                new("embedded-routes-parseable", routeErrors.Count == 0, string.Join(" | ", routeErrors)),
                new("emitted-af-accepted", after.Success, after.Error),
                new("full-rule-tree-equivalent", differences.Count == 0, $"{differences.Count} differences"),
                new("emitted-full-rule-tree-equivalent", emittedComparisonPerformed && emittedDifferences.Count == 0,
                    emittedComparisonPerformed ? $"{emittedDifferences.Count} differences" : "not compared because emitted AF was rejected"),
                new("call-state-preserved", lostCallCount == 0, $"source={callCount}, changed={lostCallCount}"),
                new("chat-capture-preserved", captureLossCount == 0, $"source={captureCount}, changed={captureLossCount}"),
            ],
        };
    }

    private static InputEvidence ProbeLoot(
        string inputPath,
        string? emittedPath = null,
        string? emittedRenamedPath = null,
        string? emittedControlledPath = null,
        string? controlledItemPattern = null)
    {
        string path = RequireInput(inputPath);
        byte[] bytes = File.ReadAllBytes(path);
        string text = File.ReadAllText(path);
        MossLootParse original = MossBridge.ParseLoot(text);

        VTankLootProfile loaded;
        string emitted;
        string donorError = string.Empty;
        try
        {
            loaded = VTankLootParser.LoadFromText(text);
            emitted = VTankLootWriter.Serialize(loaded);
        }
        catch (Exception exception)
        {
            loaded = new VTankLootProfile();
            emitted = string.Empty;
            donorError = $"{exception.GetType().Name}: {exception.Message}";
        }

        MossLootParse after = MossBridge.ParseLoot(emitted);
        bool semanticComparisonPerformed = original.Success && after.Success;
        List<Difference> differences = semanticComparisonPerformed
            ? Difference.Compare(original.Profile, after.Profile, "profile")
            : [];
        string normalizedSource = NormalizeNewlines(text);
        string normalizedEmitted = NormalizeNewlines(emitted);
        string emittedArtifact = string.Empty;
        string emittedArtifactSha256 = string.Empty;
        if (emittedPath is not null)
            (emittedArtifact, emittedArtifactSha256) = WriteEmittedUtl(path, emittedPath, emitted);

        MossLootParse edited = new(false, null, "not attempted");
        var editDifferences = new List<Difference>();
        string edit = "not attempted";
        if (donorError.Length == 0 && loaded.Rules.Count > 0)
        {
            VTankLootProfile renameProfile = VTankLootParser.LoadFromText(text);
            string oldName = renameProfile.Rules[0].Name;
            string newName = oldName + " [compatibility probe]";
            renameProfile.Rules[0].Name = newName;
            string editedText = VTankLootWriter.Serialize(renameProfile);
            if (emittedRenamedPath is not null)
                (emittedArtifact, emittedArtifactSha256) = WriteEmittedUtl(path, emittedRenamedPath, editedText);
            edited = MossBridge.ParseLoot(editedText);
            CanonicalLootProfile? editedExpected = original.Profile?.WithFirstRuleName(newName);
            if (original.Success && edited.Success)
                editDifferences = Difference.Compare(editedExpected, edited.Profile, "editedProfile");
            edit = $"first rule name: '{oldName}' -> '{newName}'";
        }

        MossLootParse controlled = new(false, null, "not requested");
        var controlledDifferences = new List<Difference>();
        string controlledEdit = "not requested";
        string controlledArtifact = string.Empty;
        string controlledArtifactSha256 = string.Empty;
        if (emittedControlledPath is not null && controlledItemPattern is not null && donorError.Length == 0)
        {
            VTankLootProfile controlledProfile = VTankLootParser.LoadFromText(text);
            string controlledRuleName = $"[Compatibility Probe] Controlled Keep: {controlledItemPattern}";
            controlledProfile.Rules.Insert(0, new VTankLootRule
            {
                Name = controlledRuleName,
                CustomExpression = string.Empty,
                Priority = 0,
                Action = VTankLootAction.Keep,
                Conditions =
                [
                    new VTankLootCondition(
                        VTankNodeTypes.StringValueMatch,
                        (controlledItemPattern.Length + 3).ToString(CultureInfo.InvariantCulture),
                        [controlledItemPattern, "1"]),
                ],
            });
            string controlledText = VTankLootWriter.Serialize(controlledProfile);
            (controlledArtifact, controlledArtifactSha256) = WriteEmittedUtl(path, emittedControlledPath, controlledText);
            controlled = MossBridge.ParseLoot(controlledText);
            var expectedRule = new CanonicalLootRule(
                controlledRuleName,
                string.Empty,
                0,
                (int)VTankLootAction.Keep,
                1,
                [new(VTankNodeTypes.StringValueMatch, controlledItemPattern + "\r\n1\r\n")]);
            CanonicalLootProfile? controlledExpected = after.Profile?.WithPrependedRule(
                expectedRule,
                loaded.Rules.Select(static rule => string.IsNullOrWhiteSpace(rule.Name)).ToList());
            if (original.Success && controlled.Success)
                controlledDifferences = Difference.Compare(controlledExpected, controlled.Profile, "controlledProfile");
            controlledEdit = $"prepended item-name-pattern Keep rule '{controlledRuleName}'";
        }

        var unsupported = new List<string>();
        int? declaredRuleCount = ReadDeclaredLootRuleCount(text);
        if (declaredRuleCount != loaded.Rules.Count)
            unsupported.Add($"Declared rule count {declaredRuleCount?.ToString(CultureInfo.InvariantCulture) ?? "unreadable"} differs from donor result {loaded.Rules.Count}; partial input was not accepted.");
        if (original.Profile?.UnknownBlocks.Count > 0 && after.Profile?.UnknownBlocks.Count != original.Profile.UnknownBlocks.Count)
            unsupported.Add("One or more trailing unknown blocks were not preserved.");
        if (original.Profile is not null && after.Profile is not null
            && Difference.Compare(original.Profile.SalvageCombine, after.Profile.SalvageCombine, "salvageCombine").Count > 0)
            unsupported.Add("SalvageCombine data changed during the unmodified round trip.");
        if (differences.Any(static difference => difference.Path.Contains("customExpression", StringComparison.OrdinalIgnoreCase)))
            unsupported.Add("One or more custom expressions changed during the unmodified round trip.");

        bool success = original.Success
            && donorError.Length == 0
            && declaredRuleCount == loaded.Rules.Count
            && after.Success
            && semanticComparisonPerformed
            && differences.Count == 0
            && (loaded.Rules.Count == 0 || (edited.Success && editDifferences.Count == 0))
            && (emittedControlledPath is null
                || (controlled.Success && controlledDifferences.Count == 0));

        return new InputEvidence
        {
            Kind = "utl",
            Path = path,
            Classification = Classify(path),
            Bytes = bytes.LongLength,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            Success = success,
            OriginalParserSuccess = original.Success,
            EmittedParserSuccess = after.Success,
            OriginalRuleCount = original.Profile?.Rules.Count ?? 0,
            DonorRuleCount = loaded.Rules.Count,
            EmittedRuleCount = after.Profile?.Rules.Count ?? 0,
            OriginalParserError = original.Error,
            DonorParserError = donorError,
            EmittedParserError = after.Error,
            DeclaredRuleCount = declaredRuleCount,
            SemanticComparisonPerformed = semanticComparisonPerformed,
            NormalizedBytesEqual = normalizedSource == normalizedEmitted,
            NormalizedTextDifference = FirstTextDifference(normalizedSource, normalizedEmitted),
            EmittedArtifactPath = emittedArtifact,
            EmittedArtifactSha256 = emittedArtifactSha256,
            SemanticDifferences = differences,
            Edit = edit,
            EditParserSuccess = edited.Success,
            EditSemanticDifferences = editDifferences,
            ControlledEdit = controlledEdit,
            ControlledEditParserSuccess = controlled.Success,
            ControlledArtifactPath = controlledArtifact,
            ControlledArtifactSha256 = controlledArtifactSha256,
            ControlledEditSemanticDifferences = controlledDifferences,
            UnsupportedMappings = unsupported,
            Checks =
            [
                new("nonempty-source", bytes.Length > 0, $"{bytes.Length} bytes"),
                new("source-parser-accepted", original.Success, original.Error),
                new("donor-parser-accepted", donorError.Length == 0, donorError.Length == 0 ? $"{loaded.Rules.Count} rules" : donorError),
                new("declared-rule-count-matched", declaredRuleCount == loaded.Rules.Count, $"declared={declaredRuleCount?.ToString(CultureInfo.InvariantCulture) ?? "unreadable"}, parsed={loaded.Rules.Count}"),
                new("emitted-profile-accepted", after.Success, after.Error),
                new("unmodified-full-profile-equivalent", semanticComparisonPerformed && differences.Count == 0,
                    semanticComparisonPerformed ? $"{differences.Count} differences" : "not compared because source or emitted profile was rejected"),
                new("intentional-rename-only",
                    loaded.Rules.Count == 0 || (original.Success && edited.Success && editDifferences.Count == 0),
                    loaded.Rules.Count == 0
                        ? "not applicable because the profile declares zero rules"
                        : original.Success && edited.Success
                            ? $"{editDifferences.Count} unexpected differences"
                            : "not compared because source or edited profile was rejected"),
                new("controlled-keep-insertion-only",
                    emittedControlledPath is null || (original.Success && controlled.Success && controlledDifferences.Count == 0),
                    emittedControlledPath is null
                        ? "not requested"
                        : original.Success && controlled.Success
                            ? $"{controlledDifferences.Count} unexpected differences"
                            : "not compared because source or controlled profile was rejected"),
            ],
        };
    }

    private static IEnumerable<MutationEvidence> RunDetectorMutations(Options options)
    {
        string temporaryRoot = Path.Combine(Path.GetTempPath(), "acdream-compat-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            yield return MutateOutputPathCollision(temporaryRoot);
            if (options.LootPaths.FirstOrDefault() is { } lootPath)
                yield return MutateLootRuleCount(RequireInput(lootPath), temporaryRoot);
            var routeCandidates = options.MetaPaths
                .Select(static path => (Path: path, IsAf: false))
                .Concat(options.AfPaths.Select(static path => (Path: path, IsAf: true)))
                .ToList();
            if (routeCandidates.Count > 0)
                yield return MutateEmbeddedRouteWaypoint(routeCandidates, temporaryRoot);
            if (options.AfPaths.FirstOrDefault() is { } afPath)
                yield return MutateAfKeyword(RequireInput(afPath), temporaryRoot);
            if (options.MetaPaths.FirstOrDefault() is { } metaPath)
                yield return MutateMetTruncation(RequireInput(metaPath), temporaryRoot);
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    private static MutationEvidence MutateOutputPathCollision(string temporaryRoot)
    {
        string input = Path.Combine(temporaryRoot, "collision-input.utl");
        File.WriteAllText(input, "sentinel", new UTF8Encoding(false));
        string before = FileHash(input);
        bool inputCollisionRejected = RejectsPathCollision(
            [input],
            [("json evidence", Path.Combine(temporaryRoot, ".", "collision-input.utl"))]);
        bool outputCollisionRejected = RejectsPathCollision(
            [input],
            [
                ("json evidence", Path.Combine(temporaryRoot, "result.json")),
                ("emitted UTL", Path.Combine(temporaryRoot, "nested", "..", "result.json")),
            ]);
        string after = FileHash(input);
        bool unchanged = before == after;
        return new(
            "output-path-collision-preflight",
            inputCollisionRejected && outputCollisionRejected && unchanged,
            $"inputCollisionRejected={inputCollisionRejected}, outputCollisionRejected={outputCollisionRejected}, inputHashUnchanged={unchanged}",
            true);
    }

    private static bool RejectsPathCollision(
        IEnumerable<string> inputs,
        IEnumerable<(string Label, string? Path)> outputs)
    {
        try
        {
            ValidateNoOutputCollisions(inputs, outputs);
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static MutationEvidence MutateLootRuleCount(string source, string temporaryRoot)
    {
        InputEvidence baseline = ProbeLoot(source);
        string[] lines = NormalizeNewlines(File.ReadAllText(source)).Split('\n');
        int countIndex = lines.Length > 2 && lines[0] == "UTL" ? 2 : 0;
        int count = int.Parse(lines[countIndex], CultureInfo.InvariantCulture);
        lines[countIndex] = (count + 1).ToString(CultureInfo.InvariantCulture);
        string path = Path.Combine(temporaryRoot, "rule-count-plus-one.utl");
        File.WriteAllText(path, string.Join('\n', lines), new UTF8Encoding(false));
        InputEvidence result = ProbeLoot(path);
        bool baselineCheck = CheckPassed(baseline, "source-parser-accepted");
        bool mutantCheck = CheckPassed(result, "source-parser-accepted");
        return new("utl-header-rule-count-plus-one", baselineCheck && !mutantCheck,
            $"source-parser-accepted baseline={baselineCheck}, mutant={mutantCheck}", true);
    }

    private static MutationEvidence MutateAfKeyword(string source, string temporaryRoot)
    {
        InputEvidence baseline = ProbeMeta(source, isAf: true);
        string text = File.ReadAllText(source);
        int marker = text.IndexOf("IF:", StringComparison.Ordinal);
        string mutated = marker < 0 ? text + "\nIF: __unsupported__\n" : text.Insert(marker + 3, " __unsupported__");
        string path = Path.Combine(temporaryRoot, "unknown-condition.af");
        File.WriteAllText(path, mutated, new UTF8Encoding(false));
        InputEvidence result = ProbeMeta(path, isAf: true);
        bool baselineCheck = CheckPassed(baseline, "source-parser-accepted");
        bool mutantCheck = CheckPassed(result, "source-parser-accepted");
        return new("af-unknown-condition-keyword", baselineCheck && !mutantCheck,
            $"source-parser-accepted baseline={baselineCheck}, mutant={mutantCheck}", true);
    }

    private static MutationEvidence MutateEmbeddedRouteWaypoint(
        IEnumerable<(string Path, bool IsAf)> candidates,
        string temporaryRoot)
    {
        foreach ((string candidatePath, bool isAf) in candidates)
        {
            string source = RequireInput(candidatePath);
            string path = Path.Combine(temporaryRoot, $"route-waypoint-loss-{Guid.NewGuid():N}.{(isAf ? "af" : "met")}");
            File.Copy(source, path);
            MossMetaParse expected = isAf
                ? MossBridge.ParseAf(File.ReadAllText(path))
                : MossBridge.ParseMet(File.ReadAllText(path));
            LoadedMeta loaded = isAf ? AfFileParser.Load(path) : MetFileParser.Load(path);
            var routeErrors = new List<string>();
            List<CanonicalMetaRule> actual = RynthCanonicalizer.Meta(
                loaded.Rules,
                loaded.EmbeddedNavs,
                routeErrors);
            List<CanonicalMetaAction> expectedActions = Flatten(expected.Rules)
                .Select(static rule => rule.Action)
                .Where(static action => action.EmbeddedRoute?.Waypoints.Count > 0)
                .ToList();
            List<CanonicalMetaAction> actualActions = Flatten(actual)
                .Select(static rule => rule.Action)
                .Where(static action => action.EmbeddedRoute?.Waypoints.Count > 0)
                .ToList();
            for (int index = 0; index < Math.Min(expectedActions.Count, actualActions.Count); index++)
            {
                CanonicalMetaAction expectedAction = expectedActions[index];
                CanonicalMetaAction actualAction = actualActions[index];
                CanonicalNavigationRoute expectedRoute = expectedAction.EmbeddedRoute!;
                CanonicalNavigationRoute route = actualAction.EmbeddedRoute!;
                List<Difference> baselineDifferences = Difference.Compare(expectedRoute, route, "embeddedRoute");
                if (baselineDifferences.Count != 0)
                    continue;
                var mutatedRoute = route with { Waypoints = route.Waypoints.Skip(1).ToList() };
                var mutatedAction = actualAction with { EmbeddedRoute = mutatedRoute };
                List<Difference> mutantDifferences = Difference.Compare(
                    expectedRoute,
                    mutatedAction.EmbeddedRoute,
                    "embeddedRoute");
                bool nameUnchanged = actualAction.SecondaryText == mutatedAction.SecondaryText;
                return new(
                    "embedded-route-waypoint-loss",
                    mutantDifferences.Count > 0 && nameUnchanged,
                    $"fixture={Path.GetFileName(source)}, baselineDifferences=0, mutantDifferences={mutantDifferences.Count}, nameUnchanged={nameUnchanged}, waypoints={route.Waypoints.Count}->{mutatedRoute.Waypoints.Count}",
                    true);
            }
        }
        return new(
            "embedded-route-waypoint-loss",
            false,
            "no route with a source-equivalent typed payload was available for mutation",
            true);
    }

    private static MutationEvidence MutateMetTruncation(string source, string temporaryRoot)
    {
        InputEvidence baseline = ProbeMeta(source, isAf: false);
        byte[] bytes = File.ReadAllBytes(source);
        int length = Math.Max(1, bytes.Length - Math.Min(64, bytes.Length / 4));
        string path = Path.Combine(temporaryRoot, "truncated.met");
        File.WriteAllBytes(path, bytes[..length]);
        InputEvidence result = ProbeMeta(path, isAf: false);
        bool baselineCheck = CheckPassed(baseline, "source-parser-accepted");
        bool mutantCheck = CheckPassed(result, "source-parser-accepted");
        return new("met-truncated-tail", baselineCheck && !mutantCheck,
            $"source-parser-accepted baseline={baselineCheck}, mutant={mutantCheck}", true);
    }

    private static bool CheckPassed(InputEvidence input, string name) =>
        input.Checks.Single(check => check.Name == name).Passed;

    private static int CountActions(IEnumerable<CanonicalMetaRule> rules, string kind) =>
        Flatten(rules).Count(rule => rule.Action.Kind == kind);

    private static int CountConditions(IEnumerable<CanonicalMetaRule> rules, string kind) =>
        Flatten(rules).Count(rule => rule.Condition.Kind == kind);

    private static int CountCallLosses(IReadOnlyList<CanonicalMetaRule> expected, IReadOnlyList<CanonicalMetaRule> actual)
    {
        List<CanonicalMetaRule> left = Flatten(expected).Where(static rule => rule.Action.Kind == "CallMetaState").ToList();
        List<CanonicalMetaRule> right = Flatten(actual).Where(static rule => rule.Action.Kind == "CallMetaState").ToList();
        int lost = Math.Abs(left.Count - right.Count);
        for (int index = 0; index < Math.Min(left.Count, right.Count); index++)
            if (Difference.Compare(left[index].Action, right[index].Action, "action").Count > 0) lost++;
        return lost;
    }

    private static int CountConditionLosses(IReadOnlyList<CanonicalMetaRule> expected, IReadOnlyList<CanonicalMetaRule> actual, string kind)
    {
        List<CanonicalMetaRule> left = Flatten(expected).Where(rule => rule.Condition.Kind == kind).ToList();
        List<CanonicalMetaRule> right = Flatten(actual).Where(rule => rule.Condition.Kind == kind).ToList();
        int lost = Math.Abs(left.Count - right.Count);
        for (int index = 0; index < Math.Min(left.Count, right.Count); index++)
            if (Difference.Compare(left[index].Condition, right[index].Condition, "condition").Count > 0) lost++;
        return lost;
    }

    private static IEnumerable<CanonicalMetaRule> Flatten(IEnumerable<CanonicalMetaRule> rules)
    {
        foreach (CanonicalMetaRule rule in rules)
        {
            yield return rule;
            foreach (CanonicalMetaRule child in Flatten(rule.Condition.Children)) yield return child;
            foreach (CanonicalMetaRule child in Flatten(rule.Action.Children)) yield return child;
        }
    }

    private static string RequireInput(string inputPath)
    {
        string path = Path.GetFullPath(inputPath);
        if (!File.Exists(path))
            throw new FileNotFoundException("Input file does not exist.", path);
        return path;
    }

    private static void ValidateOutputPaths(Options options) =>
        ValidateNoOutputCollisions(
            options.MetaPaths.Concat(options.AfPaths).Concat(options.LootPaths),
            [
                ("JSON evidence", options.OutputPath),
                ("unmodified emitted UTL", options.EmittedUtlPath),
                ("renamed emitted UTL", options.EmittedRenamedUtlPath),
                ("controlled emitted UTL", options.EmittedControlledUtlPath),
            ]);

    private static void ValidateNoOutputCollisions(
        IEnumerable<string> inputPaths,
        IEnumerable<(string Label, string? Path)> outputPaths)
    {
        StringComparer comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var inputs = new HashSet<string>(
            inputPaths.Select(Path.GetFullPath),
            comparer);
        var outputs = new Dictionary<string, string>(comparer);
        foreach ((string label, string? path) in outputPaths)
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;
            string fullPath = Path.GetFullPath(path);
            if (inputs.Contains(fullPath))
                throw new ArgumentException($"{label} path must not overwrite an input file: {fullPath}");
            if (outputs.TryGetValue(fullPath, out string? prior))
                throw new ArgumentException($"{label} path collides with {prior}: {fullPath}");
            outputs.Add(fullPath, label);
        }
    }

    private static string Classify(string path) => path.Contains(
        $"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            ? "repository-fixture"
            : "owner-file-read-only";

    private static string NormalizeNewlines(string value) => value
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace('\r', '\n');

    private static int? ReadDeclaredLootRuleCount(string source)
    {
        string[] lines = NormalizeNewlines(source).Split('\n');
        int index = lines.Length > 0 && lines[0] == "UTL" ? 2 : 0;
        return index < lines.Length && int.TryParse(lines[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count)
            ? count
            : null;
    }

    private static (string Path, string Sha256) WriteEmittedUtl(string inputPath, string outputPath, string content)
    {
        string fullPath = Path.GetFullPath(outputPath);
        if (string.Equals(inputPath, fullPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The emitted UTL path must not overwrite the input file.");
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        byte[] bytes = new UTF8Encoding(false).GetBytes(content);
        File.WriteAllBytes(fullPath, bytes);
        return (fullPath, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    private static string FirstTextDifference(string left, string right)
    {
        int length = Math.Min(left.Length, right.Length);
        int offset = 0;
        while (offset < length && left[offset] == right[offset]) offset++;
        if (offset == left.Length && offset == right.Length) return string.Empty;
        int line = 1;
        for (int index = 0; index < offset; index++) if (left[index] == '\n') line++;
        static string Context(string value, int offset)
        {
            int start = Math.Max(0, offset - 40);
            int count = Math.Min(value.Length - start, 80);
            return value.Substring(start, count).Replace("\n", "\\n", StringComparison.Ordinal);
        }
        return $"offset={offset}, line={line}, source='{Context(left, offset)}', emitted='{Context(right, offset)}'";
    }

    private static string ReadRevision(string directory)
        => ReadGit(directory, "rev-parse", "HEAD") is { Length: > 0 } revision
            ? revision
            : "unavailable";

    private static string ReadGit(string directory, params string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add($"safe.directory={directory}");
            startInfo.ArgumentList.Add("-C");
            startInfo.ArgumentList.Add(directory);
            foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
            using var process = Process.Start(startInfo);
            if (process is null) return "unavailable";
            string output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(5_000);
            return process.ExitCode == 0 ? output : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static SortedDictionary<string, string> SourceFileHashes(string sourceRoot, string openAcRoot)
    {
        string[] donorFiles =
        [
            "Shared/RynthCore.LootSdk/VTank/VTankLootParser.cs",
            "Shared/RynthCore.LootSdk/VTank/VTankLootWriter.cs",
            "Plugins/RynthCore.Plugin.RynthAi/Meta/MetFileParser.cs",
            "Plugins/RynthCore.Plugin.RynthAi/Meta/AfFileParser.cs",
            "Plugins/RynthCore.Plugin.RynthAi/Meta/AfFileWriter.cs",
        ];
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string relative in donorFiles)
            result[$"donor/{relative}"] = FileHash(Path.Combine(sourceRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        result["openac/src/AcDream.Plugins.MossTank/VtankLootProfileSerializer.cs"] = FileHash(
            Path.Combine(openAcRoot, "src", "AcDream.Plugins.MossTank", "VtankLootProfileSerializer.cs"));
        result["openac/src/AcDream.Plugins.MossTank/VtankNavRouteSerializer.cs"] = FileHash(
            Path.Combine(openAcRoot, "src", "AcDream.Plugins.MossTank", "VtankNavRouteSerializer.cs"));
        result["openac/tools/AutomationCompatibilityProbe/Program.cs"] = FileHash(
            Path.Combine(openAcRoot, "tools", "AutomationCompatibilityProbe", "Program.cs"));
        return result;
    }

    private static SortedDictionary<string, string> ExecutingAssemblyHashes() => new(StringComparer.Ordinal)
    {
        ["probe"] = FileHash(Assembly.GetExecutingAssembly().Location),
        ["lootSdk"] = FileHash(typeof(VTankLootParser).Assembly.Location),
        ["mossTank"] = FileHash(typeof(AcDream.Plugins.MossTank.MossTankPlugin).Assembly.Location),
    };

    private static string FileHash(string path) => File.Exists(path)
        ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()
        : "missing";

    private static string FindRepositoryRoot(string start)
    {
        DirectoryInfo? directory = new(start);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git"))
                || File.Exists(Path.Combine(directory.FullName, ".git")))
                return directory.FullName;
            directory = directory.Parent;
        }
        return start;
    }

    private sealed class Options
    {
        public const string Help = """
            Usage:
              dotnet run --project tools/AutomationCompatibilityProbe -- [options]

            Options:
              --met PATH              Compare one .met import (repeatable).
              --af PATH               Compare one .af import/write (repeatable).
              --loot PATH             Compare one .utl import/write/edit (repeatable).
              --output PATH           Also write the JSON evidence to PATH.
              --emit-utl PATH         Write one unmodified donor UTL round trip.
              --emit-renamed-utl PATH Write one round trip with the first rule renamed.
              --emit-controlled-utl PATH
                                      Write one round trip with a controlled Keep rule prepended.
              --controlled-item-pattern REGEX
                                      Item-name pattern for --emit-controlled-utl.
              --verify-detectors      Mutate temporary copies and prove rejection checks fire.
              --quiet                 Suppress JSON on stdout (requires --output for retained evidence).
              --help                  Show this help.
            """;

        public List<string> MetaPaths { get; } = [];
        public List<string> AfPaths { get; } = [];
        public List<string> LootPaths { get; } = [];
        public string? OutputPath { get; private set; }
        public string? SourceRoot { get; private set; }
        public string? EmittedUtlPath { get; private set; }
        public string? EmittedRenamedUtlPath { get; private set; }
        public string? EmittedControlledUtlPath { get; private set; }
        public string? ControlledItemPattern { get; private set; }
        public bool VerifyDetectors { get; private set; }
        public bool Quiet { get; private set; }
        public bool ShowHelp { get; private set; }

        public static Options Parse(string[] args)
        {
            var sourceRoot = Assembly.GetExecutingAssembly()
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(static attribute => attribute.Key == "RynthSuiteRoot")
                .Value;
            var result = new Options { SourceRoot = sourceRoot };
            for (int index = 0; index < args.Length; index++)
            {
                string arg = args[index];
                switch (arg)
                {
                    case "--met": result.MetaPaths.Add(Next(args, ref index, arg)); break;
                    case "--af": result.AfPaths.Add(Next(args, ref index, arg)); break;
                    case "--loot": result.LootPaths.Add(Next(args, ref index, arg)); break;
                    case "--output": result.OutputPath = Next(args, ref index, arg); break;
                    case "--emit-utl": result.EmittedUtlPath = Next(args, ref index, arg); break;
                    case "--emit-renamed-utl": result.EmittedRenamedUtlPath = Next(args, ref index, arg); break;
                    case "--emit-controlled-utl": result.EmittedControlledUtlPath = Next(args, ref index, arg); break;
                    case "--controlled-item-pattern": result.ControlledItemPattern = Next(args, ref index, arg); break;
                    case "--verify-detectors": result.VerifyDetectors = true; break;
                    case "--quiet": result.Quiet = true; break;
                    case "--help" or "-h": result.ShowHelp = true; break;
                    default: throw new ArgumentException($"Unknown argument: {arg}");
                }
            }
            if (!result.ShowHelp && result.MetaPaths.Count + result.AfPaths.Count + result.LootPaths.Count == 0)
                throw new ArgumentException("At least one --met, --af, or --loot input is required.");
            int emitModes = (result.EmittedUtlPath is null ? 0 : 1)
                + (result.EmittedRenamedUtlPath is null ? 0 : 1)
                + (result.EmittedControlledUtlPath is null ? 0 : 1);
            if (emitModes > 1)
                throw new ArgumentException("Choose only one emitted UTL mode.");
            if ((result.EmittedControlledUtlPath is null) != (result.ControlledItemPattern is null))
                throw new ArgumentException("--emit-controlled-utl and --controlled-item-pattern must be supplied together.");
            return result;
        }

        private static string Next(string[] args, ref int index, string option)
        {
            if (++index >= args.Length) throw new ArgumentException($"{option} requires a path.");
            return args[index];
        }
    }
}

internal static class MossBridge
{
    private static readonly Assembly MossAssembly = typeof(AcDream.Plugins.MossTank.MossTankPlugin).Assembly;

    public static MossMetaParse ParseMet(string source)
    {
        Type serializer = RequiredType("AcDream.Plugins.MossTank.VtankMetaProfileSerializer");
        MethodInfo method = serializer.GetMethod("TryLoad", BindingFlags.Static | BindingFlags.Public,
            null, [typeof(string), RequiredType("AcDream.Plugins.MossTank.MetaProfile").MakeByRefType(), typeof(string).MakeByRefType()], null)
            ?? throw new MissingMethodException(serializer.FullName, "TryLoad");
        object?[] arguments = [source, null, null];
        bool success = (bool)method.Invoke(null, arguments)!;
        return new(success, success ? CanonicalizeMeta(arguments[1]!) : [], arguments[2]?.ToString() ?? string.Empty);
    }

    public static MossMetaParse ParseAf(string source)
    {
        Type serializer = RequiredType("AcDream.Plugins.MossTank.MetafSerializer");
        Type spellCatalog = typeof(AcDream.Plugin.Abstractions.ISpellCatalog);
        MethodInfo method = serializer.GetMethod("TryLoadMeta", BindingFlags.Static | BindingFlags.Public,
            null, [typeof(string), spellCatalog, RequiredType("AcDream.Plugins.MossTank.MetaProfile").MakeByRefType(), typeof(string).MakeByRefType()], null)
            ?? throw new MissingMethodException(serializer.FullName, "TryLoadMeta");
        Type noOp = serializer.GetNestedType("NoOpSpells", BindingFlags.NonPublic)
            ?? throw new MissingMemberException(serializer.FullName, "NoOpSpells");
        object spells = noOp.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)!.GetValue(null)!;
        object?[] arguments = [source, spells, null, null];
        bool success = (bool)method.Invoke(null, arguments)!;
        return new(success, success ? CanonicalizeMeta(arguments[2]!) : [], arguments[3]?.ToString() ?? string.Empty);
    }

    public static MossLootParse ParseLoot(string source)
    {
        Type serializer = RequiredType("AcDream.Plugins.MossTank.VtankLootProfileSerializer");
        Type profileType = RequiredType("AcDream.Plugins.MossTank.VtankLootProfile");
        MethodInfo method = serializer.GetMethod("TryRead", BindingFlags.Static | BindingFlags.Public,
            null, [typeof(string), profileType.MakeByRefType(), typeof(string).MakeByRefType()], null)
            ?? throw new MissingMethodException(serializer.FullName, "TryRead");
        object?[] arguments = [source, null, null];
        bool success = (bool)method.Invoke(null, arguments)!;
        return new(success, success ? CanonicalizeLoot(arguments[1]!) : null, arguments[2]?.ToString() ?? string.Empty);
    }

    public static MossRouteParse ParseNativeNav(string source)
    {
        Type serializer = RequiredType("AcDream.Plugins.MossTank.VtankNavRouteSerializer");
        Type settingsType = RequiredType("AcDream.Plugins.MossTank.NavigationSettings");
        Type spellCatalog = typeof(AcDream.Plugin.Abstractions.ISpellCatalog);
        MethodInfo method = serializer.GetMethod("TryLoad", BindingFlags.Static | BindingFlags.Public,
            null, [typeof(string), settingsType, spellCatalog, typeof(string).MakeByRefType()], null)
            ?? throw new MissingMethodException(serializer.FullName, "TryLoad");
        object settings = Activator.CreateInstance(settingsType, nonPublic: true)
            ?? throw new InvalidOperationException("Could not create navigation settings.");
        Type metaf = RequiredType("AcDream.Plugins.MossTank.MetafSerializer");
        Type noOp = metaf.GetNestedType("NoOpSpells", BindingFlags.NonPublic)
            ?? throw new MissingMemberException(metaf.FullName, "NoOpSpells");
        object spells = noOp.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)!.GetValue(null)!;
        object?[] arguments = [source, settings, spells, null];
        bool success = (bool)method.Invoke(null, arguments)!;
        return new(success, success ? CanonicalizeRoute(settings) : null, arguments[3]?.ToString() ?? string.Empty);
    }

    private static List<CanonicalMetaRule> CanonicalizeMeta(object profile) =>
        Items(Get(profile, "Rules")).Select(CanonicalizeMetaRule).ToList();

    private static CanonicalMetaRule CanonicalizeMetaRule(object rule) => new(
        Get(rule, "State")?.ToString() ?? string.Empty,
        (bool)(Get(rule, "Enabled") ?? true),
        CanonicalizeCondition(Get(rule, "Condition")!),
        CanonicalizeAction(Get(rule, "Action")!));

    private static CanonicalMetaCondition CanonicalizeCondition(object condition)
    {
        string kind = Get(condition, "Kind")?.ToString() ?? string.Empty;
        return new(kind,
            Get(condition, "Text")?.ToString() ?? string.Empty,
            Get(condition, "SecondaryText")?.ToString() ?? string.Empty,
            Number(Get(condition, "Number")),
            Number(Get(condition, "SecondaryNumber")),
            Number(Get(condition, "TertiaryNumber")),
            Items(Get(condition, "Children")).Select(child => new CanonicalMetaRule(
                string.Empty, true, CanonicalizeCondition(child), CanonicalMetaAction.Empty)).ToList());
    }

    private static CanonicalMetaAction CanonicalizeAction(object action) => new(
        Get(action, "Kind")?.ToString() ?? string.Empty,
        Get(action, "Text")?.ToString() ?? string.Empty,
        Get(action, "SecondaryText")?.ToString() ?? string.Empty,
        Number(Get(action, "Number")),
        Number(Get(action, "SecondaryNumber")),
        CanonicalizeRoute(Get(action, "EmbeddedRoute")),
        Items(Get(action, "Children")).Select(child => new CanonicalMetaRule(
            string.Empty, true, CanonicalMetaCondition.Empty, CanonicalizeAction(child))).ToList());

    private static CanonicalNavigationRoute? CanonicalizeRoute(object? route)
    {
        if (route is null)
            return null;
        return new(
            Get(route, "Mode")?.ToString() ?? string.Empty,
            Unsigned(Get(route, "FollowTargetObjectId")),
            Get(route, "FollowTargetName")?.ToString() ?? string.Empty,
            Items(Get(route, "Waypoints")).Select(CanonicalizeWaypoint).ToList());
    }

    private static CanonicalRouteWaypoint CanonicalizeWaypoint(object waypoint) => new(
        Get(waypoint, "Type")?.ToString() ?? string.Empty,
        CanonicalizePosition(Get(waypoint, "Position")),
        CanonicalizePosition(Get(waypoint, "ReferencePosition")),
        Unsigned(Get(waypoint, "ObjectId")),
        Get(waypoint, "ObjectName")?.ToString() ?? string.Empty,
        Convert.ToInt32(Get(waypoint, "LegacyObjectClass"), CultureInfo.InvariantCulture),
        Convert.ToBoolean(Get(waypoint, "LegacyReferenceValid"), CultureInfo.InvariantCulture),
        Get(waypoint, "Text")?.ToString() ?? string.Empty,
        Convert.ToInt32(Get(waypoint, "DurationMilliseconds"), CultureInfo.InvariantCulture),
        Get(waypoint, "Recall")?.ToString() ?? string.Empty,
        Unsigned(Get(waypoint, "RecallSpellId")),
        Get(waypoint, "RecallSpellName")?.ToString() ?? string.Empty,
        Number(Get(waypoint, "JumpHeadingDegrees")),
        Convert.ToBoolean(Get(waypoint, "JumpRun"), CultureInfo.InvariantCulture),
        Convert.ToInt32(Get(waypoint, "JumpChargeMilliseconds"), CultureInfo.InvariantCulture),
        Get(waypoint, "JumpDirection")?.ToString() ?? string.Empty);

    private static CanonicalNavigationPosition CanonicalizePosition(object? position) => new(
        Unsigned(Get(position!, "CellId")),
        Number(Get(position!, "EastWest")),
        Number(Get(position!, "NorthSouth")),
        Number(Get(position!, "Elevation")),
        Number(Get(position!, "HeadingDegrees")),
        Convert.ToBoolean(Get(position!, "IsOutdoor"), CultureInfo.InvariantCulture));

    private static CanonicalLootProfile CanonicalizeLoot(object profile)
    {
        var rules = new List<CanonicalLootRule>();
        foreach (object rule in Items(Get(profile, "Rules")))
        {
            rules.Add(new(
                Get(rule, "Name")?.ToString() ?? string.Empty,
                Get(rule, "CustomExpression")?.ToString() ?? string.Empty,
                Convert.ToInt32(Get(rule, "Priority"), CultureInfo.InvariantCulture),
                Convert.ToInt32(Get(rule, "Action"), CultureInfo.InvariantCulture),
                Convert.ToInt32(Get(rule, "KeepCount"), CultureInfo.InvariantCulture),
                Items(Get(rule, "VtankRequirements")).Select(requirement => new CanonicalLootRequirement(
                    Convert.ToInt32(Get(requirement, "Type"), CultureInfo.InvariantCulture),
                    Get(requirement, "Payload")?.ToString() ?? string.Empty)).ToList()));
        }

        object salvage = Get(profile, "SalvageCombine")!;
        var material = Dictionary(Get(salvage, "MaterialCombineStrings"));
        var modes = Dictionary(Get(salvage, "MaterialValueModeValues"));
        var blocks = Items(Get(profile, "UnknownBlocks")).Select(block => new CanonicalExtraBlock(
            Get(block, "Type")?.ToString() ?? string.Empty,
            Get(block, "Payload")?.ToString() ?? string.Empty)).ToList();
        return new(
            Convert.ToInt32(Get(profile, "SourceVersion"), CultureInfo.InvariantCulture),
            rules,
            new(Get(salvage, "DefaultCombineString")?.ToString() ?? string.Empty, material, modes),
            blocks);
    }

    private static SortedDictionary<string, string> Dictionary(object? value)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (value is IDictionary dictionary)
            foreach (DictionaryEntry entry in dictionary)
                result[entry.Key?.ToString() ?? string.Empty] = entry.Value?.ToString() ?? string.Empty;
        return result;
    }

    private static IEnumerable<object> Items(object? value) => value is IEnumerable sequence
        ? sequence.Cast<object>()
        : [];

    private static object? Get(object target, string property) =>
        target.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(target)
        ?? target.GetType().GetField(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(target);

    private static string Number(object? value) => Convert.ToDouble(value, CultureInfo.InvariantCulture)
        .ToString("R", CultureInfo.InvariantCulture);

    private static string Unsigned(object? value) => Convert.ToUInt32(value, CultureInfo.InvariantCulture)
        .ToString(CultureInfo.InvariantCulture);

    private static Type RequiredType(string name) => MossAssembly.GetType(name, throwOnError: true)!;
}

internal sealed record MossMetaParse(bool Success, List<CanonicalMetaRule> Rules, string Error);
internal sealed record MossLootParse(bool Success, CanonicalLootProfile? Profile, string Error);
internal sealed record MossRouteParse(bool Success, CanonicalNavigationRoute? Route, string Error);
internal sealed record CanonicalMetaRule(string State, bool Enabled, CanonicalMetaCondition Condition, CanonicalMetaAction Action);
internal sealed record CanonicalMetaCondition(string Kind, string Text, string SecondaryText, string Number, string SecondaryNumber, string TertiaryNumber, List<CanonicalMetaRule> Children)
{
    public static CanonicalMetaCondition Empty { get; } = new(string.Empty, string.Empty, string.Empty, "0", "0", "0", []);
}
internal sealed record CanonicalMetaAction(string Kind, string Text, string SecondaryText, string Number, string SecondaryNumber, CanonicalNavigationRoute? EmbeddedRoute, List<CanonicalMetaRule> Children)
{
    public static CanonicalMetaAction Empty { get; } = new(string.Empty, string.Empty, string.Empty, "0", "0", null, []);
}
internal sealed record CanonicalNavigationPosition(string CellId, string EastWest, string NorthSouth, string Elevation, string HeadingDegrees, bool IsOutdoor);
internal sealed record CanonicalRouteWaypoint(
    string Type,
    CanonicalNavigationPosition Position,
    CanonicalNavigationPosition ReferencePosition,
    string ObjectId,
    string ObjectName,
    int LegacyObjectClass,
    bool LegacyReferenceValid,
    string Text,
    int DurationMilliseconds,
    string Recall,
    string RecallSpellId,
    string RecallSpellName,
    string JumpHeadingDegrees,
    bool JumpRun,
    int JumpChargeMilliseconds,
    string JumpDirection);
internal sealed record CanonicalNavigationRoute(
    string Mode,
    string FollowTargetObjectId,
    string FollowTargetName,
    List<CanonicalRouteWaypoint> Waypoints);
internal sealed record CanonicalLootRequirement(int Type, string Payload);
internal sealed record CanonicalLootRule(string Name, string CustomExpression, int Priority, int Action, int KeepCount, List<CanonicalLootRequirement> Requirements);
internal sealed record CanonicalSalvage(string DefaultBands, SortedDictionary<string, string> PerMaterial, SortedDictionary<string, string> ValueModes);
internal sealed record CanonicalExtraBlock(string Type, string Payload);
internal sealed record CanonicalLootProfile(int Version, List<CanonicalLootRule> Rules, CanonicalSalvage SalvageCombine, List<CanonicalExtraBlock> UnknownBlocks)
{
    public CanonicalLootProfile WithFirstRuleName(string name)
    {
        List<CanonicalLootRule> rules = Rules.ToList();
        if (rules.Count > 0) rules[0] = rules[0] with { Name = name };
        return this with { Rules = rules };
    }

    public CanonicalLootProfile WithPrependedRule(CanonicalLootRule rule, IReadOnlyList<bool> sourceNameWasBlank)
    {
        List<CanonicalLootRule> rules = [rule];
        for (int index = 0; index < Rules.Count; index++)
        {
            CanonicalLootRule existing = Rules[index];
            if (index < sourceNameWasBlank.Count && sourceNameWasBlank[index])
                existing = existing with { Name = $"Rule {index + 2}" };
            rules.Add(existing);
        }
        return this with { Rules = rules };
    }
}

internal static class RynthCanonicalizer
{
    public static List<CanonicalMetaRule> Meta(
        IEnumerable<MetaRule> rules,
        IReadOnlyDictionary<string, List<string>> embeddedNavs,
        List<string> routeErrors) =>
        rules.Select((rule, index) => Rule(rule, embeddedNavs, routeErrors, $"rules[{index}]")).ToList();

    private static CanonicalMetaRule Rule(
        MetaRule rule,
        IReadOnlyDictionary<string, List<string>> embeddedNavs,
        List<string> routeErrors,
        string path) => new(
        rule.State ?? string.Empty,
        rule.Enabled,
        Condition(rule),
        Action(rule, embeddedNavs, routeErrors, path + ".action"));

    private static CanonicalMetaCondition Condition(MetaRule rule)
    {
        string kind = rule.Condition switch
        {
            MetaConditionType.PackSlots_LE => "PackSlotsLessThanOrEqual",
            MetaConditionType.SecondsInState_GE => "SecondsInStateGreaterThanOrEqual",
            MetaConditionType.NavrouteEmpty => "NavigationRouteEmpty",
            MetaConditionType.InventoryItemCount_LE => "InventoryItemCountLessThanOrEqual",
            MetaConditionType.InventoryItemCount_GE => "InventoryItemCountGreaterThanOrEqual",
            MetaConditionType.SecondsInStateP_GE => "PersistentSecondsInStateGreaterThanOrEqual",
            MetaConditionType.TimeLeftOnSpell_GE => "TimeLeftOnSpellGreaterThanOrEqual",
            MetaConditionType.TimeLeftOnSpell_LE => "UnsupportedTimeLeftOnSpellLessThanOrEqual",
            MetaConditionType.BurdenPercentage_GE => "BurdenPercentGreaterThanOrEqual",
            MetaConditionType.DistAnyRoutePT_GE => "DistanceFromAnyRoutePointGreaterThanOrEqual",
            MetaConditionType.Landblock_EQ => "LandblockEquals",
            MetaConditionType.Landcell_EQ => "LandcellEquals",
            MetaConditionType.MainHealthLE or MetaConditionType.MainHealthPHE
                or MetaConditionType.MainManaLE or MetaConditionType.MainManaPHE
                or MetaConditionType.MainStamLE or MetaConditionType.VitaePHE => $"Unsupported{rule.Condition}",
            _ => rule.Condition.ToString(),
        };
        string text = string.Empty;
        string secondaryText = string.Empty;
        double number = 0;
        double secondaryNumber = 0;
        double tertiaryNumber = 0;
        string data = rule.ConditionData ?? string.Empty;
        string[] parts;
        switch (rule.Condition)
        {
            case MetaConditionType.ChatMessage:
            case MetaConditionType.Expression:
            case MetaConditionType.ChatMessageCapture:
                text = data;
                break;
            case MetaConditionType.InventoryItemCount_LE:
            case MetaConditionType.InventoryItemCount_GE:
                parts = data.Split(',');
                text = Part(parts, 0);
                number = ParseNumber(Part(parts, 1));
                break;
            case MetaConditionType.MonsterNameCountWithinDistance:
                parts = data.Split(',');
                text = Part(parts, 0);
                secondaryNumber = ParseNumber(Part(parts, 1));
                number = ParseNumber(Part(parts, 2));
                break;
            case MetaConditionType.MonsterPriorityCountWithinDistance:
                parts = data.Split(',');
                number = ParseNumber(Part(parts, 0));
                secondaryNumber = ParseNumber(Part(parts, 1));
                break;
            case MetaConditionType.TimeLeftOnSpell_GE:
            case MetaConditionType.TimeLeftOnSpell_LE:
                parts = data.Split(',');
                number = ParseNumber(Part(parts, 0));
                secondaryNumber = ParseNumber(Part(parts, 1));
                break;
            case MetaConditionType.NoMonstersWithinDistance:
            case MetaConditionType.PackSlots_LE:
            case MetaConditionType.SecondsInState_GE:
            case MetaConditionType.SecondsInStateP_GE:
            case MetaConditionType.BurdenPercentage_GE:
            case MetaConditionType.DistAnyRoutePT_GE:
            case MetaConditionType.Landblock_EQ:
            case MetaConditionType.Landcell_EQ:
                number = ParseNumber(data);
                break;
        }
        return new(kind, text, secondaryText, Format(number), Format(secondaryNumber), Format(tertiaryNumber),
            rule.Children.Select(child => new CanonicalMetaRule(string.Empty, true, Condition(child), CanonicalMetaAction.Empty)).ToList());
    }

    private static CanonicalMetaAction Action(
        MetaRule rule,
        IReadOnlyDictionary<string, List<string>> embeddedNavs,
        List<string> routeErrors,
        string path)
    {
        string kind = rule.Action switch
        {
            MetaActionType.EmbeddedNavRoute => "LoadEmbeddedNavigationRoute",
            MetaActionType.GetRAOption => "GetVtankOption",
            MetaActionType.SetRAOption => "SetVtankOption",
            _ => rule.Action.ToString(),
        };
        string text = string.Empty;
        string secondaryText = string.Empty;
        double number = 0;
        double secondaryNumber = 0;
        string data = rule.ActionData ?? string.Empty;
        CanonicalNavigationRoute? embeddedRoute = null;
        string[] parts;
        switch (rule.Action)
        {
            case MetaActionType.SetMetaState:
            case MetaActionType.ChatCommand:
            case MetaActionType.ExpressionAction:
            case MetaActionType.ChatExpression:
            case MetaActionType.DestroyView:
                text = data;
                break;
            case MetaActionType.CallMetaState:
                text = data;
                secondaryText = rule.State ?? string.Empty;
                break;
            case MetaActionType.EmbeddedNavRoute:
                parts = data.Split(';');
                secondaryText = Part(parts, 0);
                embeddedRoute = ResolveEmbeddedRoute(secondaryText, embeddedNavs, routeErrors, path);
                break;
            case MetaActionType.SetWatchdog:
                parts = data.Split(';');
                text = Part(parts, 0);
                number = ParseNumber(Part(parts, 1));
                secondaryNumber = ParseNumber(Part(parts, 2));
                break;
            case MetaActionType.GetRAOption:
            case MetaActionType.SetRAOption:
                parts = data.Split(';');
                text = Part(parts, 0);
                secondaryText = Part(parts, 1);
                break;
            case MetaActionType.CreateView:
                int separator = data.IndexOf(';');
                text = separator < 0 ? data : data[..separator];
                secondaryText = separator < 0 ? string.Empty : data[(separator + 1)..];
                break;
        }
        return new(kind, text, secondaryText, Format(number), Format(secondaryNumber), embeddedRoute,
            rule.ActionChildren.Select((child, index) => new CanonicalMetaRule(
                string.Empty,
                true,
                CanonicalMetaCondition.Empty,
                Action(child, embeddedNavs, routeErrors, $"{path}.children[{index}].action"))).ToList());
    }

    private static CanonicalNavigationRoute? ResolveEmbeddedRoute(
        string name,
        IReadOnlyDictionary<string, List<string>> embeddedNavs,
        List<string> routeErrors,
        string path)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            routeErrors.Add($"{path} has no embedded route name.");
            return null;
        }
        if (!embeddedNavs.TryGetValue(name, out List<string>? lines))
        {
            routeErrors.Add($"{path} references missing route '{name}'.");
            return null;
        }
        string source = string.Join("\r\n", lines) + "\r\n";
        MossRouteParse parsed = MossBridge.ParseNativeNav(source);
        if (!parsed.Success || parsed.Route is null)
        {
            routeErrors.Add($"{path} route '{name}' failed native NAV parsing: {parsed.Error}");
            return null;
        }
        return parsed.Route;
    }

    private static string Part(string[] parts, int index) => index < parts.Length ? parts[index] : string.Empty;

    private static double ParseNumber(string value)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)) return number;
        string hex = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        if (uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint bits)) return unchecked((int)bits);
        return 0;
    }

    private static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}

internal sealed record CheckEvidence(string Name, bool Passed, string Detail);
internal sealed record MutationEvidence(string Name, bool Detected, string Detail, bool TemporaryCopyRemoved);
internal sealed record Difference(string Path, string Expected, string Actual)
{
    public static List<Difference> Compare<T>(T expected, T actual, string path)
    {
        using JsonDocument left = JsonDocument.Parse(JsonSerializer.Serialize(expected));
        using JsonDocument right = JsonDocument.Parse(JsonSerializer.Serialize(actual));
        var differences = new List<Difference>();
        CompareElement(left.RootElement, right.RootElement, path, differences);
        return differences;
    }

    private static void CompareElement(JsonElement left, JsonElement right, string path, List<Difference> output)
    {
        if (output.Count >= 500) return;
        if (left.ValueKind != right.ValueKind)
        {
            output.Add(new(path, left.ToString(), right.ToString()));
            return;
        }
        if (left.ValueKind == JsonValueKind.Object)
        {
            var leftProperties = left.EnumerateObject().ToDictionary(static item => item.Name, static item => item.Value);
            var rightProperties = right.EnumerateObject().ToDictionary(static item => item.Name, static item => item.Value);
            foreach (string name in leftProperties.Keys.Union(rightProperties.Keys).Order(StringComparer.Ordinal))
            {
                if (!leftProperties.TryGetValue(name, out JsonElement leftValue))
                    output.Add(new($"{path}.{name}", "<missing>", rightProperties[name].ToString()));
                else if (!rightProperties.TryGetValue(name, out JsonElement rightValue))
                    output.Add(new($"{path}.{name}", leftValue.ToString(), "<missing>"));
                else CompareElement(leftValue, rightValue, $"{path}.{name}", output);
            }
            return;
        }
        if (left.ValueKind == JsonValueKind.Array)
        {
            JsonElement.ArrayEnumerator leftItems = left.EnumerateArray();
            JsonElement.ArrayEnumerator rightItems = right.EnumerateArray();
            JsonElement[] leftArray = leftItems.ToArray();
            JsonElement[] rightArray = rightItems.ToArray();
            if (leftArray.Length != rightArray.Length)
                output.Add(new($"{path}.count", leftArray.Length.ToString(CultureInfo.InvariantCulture), rightArray.Length.ToString(CultureInfo.InvariantCulture)));
            for (int index = 0; index < Math.Min(leftArray.Length, rightArray.Length); index++)
                CompareElement(leftArray[index], rightArray[index], $"{path}[{index}]", output);
            return;
        }
        if (left.GetRawText() != right.GetRawText())
            output.Add(new(path, left.ToString(), right.ToString()));
    }
}

internal sealed class Evidence
{
    public string Scope { get; set; } = string.Empty;
    public DateTimeOffset GeneratedUtc { get; set; }
    public RevisionEvidence Revisions { get; set; } = new();
    public List<InputEvidence> Inputs { get; set; } = [];
    public List<MutationEvidence> Mutations { get; set; } = [];
}

internal sealed class RevisionEvidence
{
    public string DonorSource { get; set; } = string.Empty;
    public bool DonorSourceDirty { get; set; }
    public string OpenAc { get; set; } = string.Empty;
    public bool OpenAcDirty { get; set; }
    public SortedDictionary<string, string> CurrentCheckoutSourceFileHashes { get; set; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, string> ExecutingAssemblyHashes { get; set; } = new(StringComparer.Ordinal);
}

internal sealed class InputEvidence
{
    public string Kind { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Classification { get; set; } = string.Empty;
    public long Bytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public bool Success { get; set; }
    public bool OriginalParserSuccess { get; set; }
    public bool EmittedParserSuccess { get; set; }
    public int OriginalRuleCount { get; set; }
    public int DonorRuleCount { get; set; }
    public int EmittedRuleCount { get; set; }
    public int? DeclaredRuleCount { get; set; }
    public string OriginalParserError { get; set; } = string.Empty;
    public string DonorParserError { get; set; } = string.Empty;
    public string EmittedParserError { get; set; } = string.Empty;
    public bool? NormalizedBytesEqual { get; set; }
    public bool SemanticComparisonPerformed { get; set; }
    public bool EmittedSemanticComparisonPerformed { get; set; }
    public string NormalizedTextDifference { get; set; } = string.Empty;
    public string EmittedArtifactPath { get; set; } = string.Empty;
    public string EmittedArtifactSha256 { get; set; } = string.Empty;
    public string Edit { get; set; } = string.Empty;
    public bool EditParserSuccess { get; set; }
    public List<string> Warnings { get; set; } = [];
    public List<Difference> SemanticDifferences { get; set; } = [];
    public List<Difference> EmittedSemanticDifferences { get; set; } = [];
    public List<Difference> EditSemanticDifferences { get; set; } = [];
    public string ControlledEdit { get; set; } = string.Empty;
    public bool ControlledEditParserSuccess { get; set; }
    public string ControlledArtifactPath { get; set; } = string.Empty;
    public string ControlledArtifactSha256 { get; set; } = string.Empty;
    public List<Difference> ControlledEditSemanticDifferences { get; set; } = [];
    public List<string> UnsupportedMappings { get; set; } = [];
    public List<CheckEvidence> Checks { get; set; } = [];
}
