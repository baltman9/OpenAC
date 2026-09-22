namespace AcDream.HostParity.Tests;

/// <summary>
/// A line the two clients are allowed to answer differently, because a known
/// difference says so. Tying it to an allow-list member is what makes the
/// annotation temporary: closing the row in the census makes this fail as
/// stale, so the annotation has to go with it.
/// </summary>
internal readonly record struct ParityExpectedDifference(
    string Key,
    string AllowListMember,
    string MissingHost);

/// <summary>
/// Runs the same scripted plugin session against both clients and requires
/// the two transcripts to be the same, line for line, except where a named
/// expected difference says otherwise -- and then only if the line really
/// does differ.
/// </summary>
internal static class ParityScenario
{
    /// <summary>
    /// Plays <paramref name="script"/> on a windowed arm and a windowless arm
    /// and compares what each wrote down.
    /// </summary>
    internal static void Run(
        Action<ParityArm, ParityTranscript> script,
        params ParityExpectedDifference[] expectedDifferences)
    {
        ArgumentNullException.ThrowIfNull(script);

        ParityTranscript windowed = Play(new WindowedArm(), script);
        ParityTranscript windowless = Play(new WindowlessArm(), script);

        CompareTranscripts(windowed, windowless, expectedDifferences);
    }

    /// <summary>
    /// The same, for a scenario about the arrival in the world itself. The
    /// script runs with the character not yet in, and walks it in when it is
    /// ready, so what it subscribes to beforehand is in place in time to hear
    /// the arrival.
    /// </summary>
    internal static void RunFromLogin(
        Action<ParityArm, ParityTranscript> script,
        params ParityExpectedDifference[] expectedDifferences)
    {
        ArgumentNullException.ThrowIfNull(script);

        ParityTranscript windowed =
            Play(new WindowedArm(), script, enterWorld: false);
        ParityTranscript windowless =
            Play(new WindowlessArm(), script, enterWorld: false);

        CompareTranscripts(windowed, windowless, expectedDifferences);
    }

    private static ParityTranscript Play(
        ParityArm arm,
        Action<ParityArm, ParityTranscript> script,
        bool enterWorld = true)
    {
        using (arm)
        {
            var transcript = new ParityTranscript();
            if (enterWorld)
            {
                arm.EnterWorld();
                // Arriving is itself something a client does out loud: it
                // asks the server about the allegiance. That belongs to the
                // arrival, not to the scenario, so what a scenario reads off
                // the wire afterwards is its own. A scenario about the
                // arrival itself takes RunFromLogin and keeps the record.
                _ = arm.Operations.TakeOutbound();
            }
            script(arm, transcript);
            return transcript;
        }
    }

    /// <summary>
    /// Compares what the two clients wrote down. Public to the harness so a
    /// scenario about something other than the plugin surface -- a creature's
    /// body, say -- compares its transcripts by the same rule.
    /// </summary>
    internal static void CompareTranscripts(
        ParityTranscript windowed,
        ParityTranscript windowless,
        IReadOnlyList<ParityExpectedDifference> expected)
    {
        AssertEveryExpectedDifferenceIsStillAllowListed(expected);

        Assert.True(
            windowed.Lines.Count == windowless.Lines.Count,
            "The two clients did not take the same steps.\n"
            + Side(ParityHost.Windowed, windowed)
            + Side(ParityHost.Windowless, windowless));

        var unexplained = new List<string>();
        var explainedButIdentical = new List<string>();
        var explained = expected.ToDictionary(
            static difference => difference.Key,
            StringComparer.Ordinal);

        for (int index = 0; index < windowed.Lines.Count; index++)
        {
            ParityTranscriptLine left = windowed.Lines[index];
            ParityTranscriptLine right = windowless.Lines[index];
            Assert.True(
                string.Equals(left.Key, right.Key, StringComparison.Ordinal),
                $"The two clients recorded different things at line {index}: "
                + $"{left.Key} against {right.Key}");

            bool differs = !string.Equals(
                left.Value, right.Value, StringComparison.Ordinal);
            bool isExplained = explained.ContainsKey(left.Key);
            if (differs && !isExplained)
            {
                unexplained.Add(
                    $"{left.Key}: windowed {left.Value}, "
                    + $"windowless {right.Value}");
            }
            else if (!differs && isExplained)
            {
                explainedButIdentical.Add(left.Key);
            }
        }

        Assert.True(
            unexplained.Count == 0,
            "The two clients answered a plugin differently and nothing says "
            + "why. Either make them agree, or record the difference against "
            + "the allow-list row that owns it:\n  "
            + string.Join("\n  ", unexplained));
        Assert.True(
            explainedButIdentical.Count == 0,
            "A difference is recorded for a line the two clients now answer "
            + "identically. Delete the annotation: "
            + string.Join(", ", explainedButIdentical));
    }

    /// <summary>
    /// An expected difference has to name a real allow-list row, so closing
    /// the row takes the annotation with it rather than leaving a scenario
    /// quietly excusing something that is no longer allowed.
    /// </summary>
    private static void AssertEveryExpectedDifferenceIsStillAllowListed(
        IReadOnlyList<ParityExpectedDifference> expected)
    {
        foreach (ParityExpectedDifference difference in expected)
        {
            bool listed = HostParityAllowList.Seams.Any(entry =>
                    entry.Member == difference.AllowListMember
                    && entry.MissingHost == difference.MissingHost)
                || HostParityAllowList.RuntimeDependencies.Any(entry =>
                    entry.Member == difference.AllowListMember
                    && entry.MissingHost == difference.MissingHost)
                || HostParityAllowList.ConditionalSeams.Any(entry =>
                    entry.Member == difference.AllowListMember
                    && entry.ConditionalHost == difference.MissingHost);
            Assert.True(
                listed,
                $"The scenario excuses {difference.Key} against "
                + $"{difference.AllowListMember} on {difference.MissingHost}, "
                + "which the allow-list no longer carries. The difference is "
                + "closed; delete the annotation.");
        }
    }

    private static string Side(string host, ParityTranscript transcript) =>
        $"\n--- {host}\n{transcript}\n";
}
