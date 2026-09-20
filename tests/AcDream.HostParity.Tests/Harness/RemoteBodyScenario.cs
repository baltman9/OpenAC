namespace AcDream.HostParity.Tests;

/// <summary>
/// Runs the same script about another creature's body against both clients
/// and requires the two transcripts to be the same, line for line.
/// </summary>
/// <remarks>
/// There is no allowance for an expected difference here, on purpose. A body
/// carried by one client and not the other was the difference this harness
/// exists to close; a scenario that had to excuse a line would mean it is not
/// closed.
/// </remarks>
internal static class RemoteBodyScenario
{
    internal static void Run(Action<RemoteBodyArm, ParityTranscript> script)
    {
        ArgumentNullException.ThrowIfNull(script);

        ParityTranscript windowed = Play(new WindowedBodyArm(), script);
        ParityTranscript windowless = Play(new WindowlessBodyArm(), script);

        ParityScenario.CompareTranscripts(windowed, windowless, []);
    }

    private static ParityTranscript Play(
        RemoteBodyArm arm,
        Action<RemoteBodyArm, ParityTranscript> script)
    {
        using (arm)
        {
            var transcript = new ParityTranscript();
            script(arm, transcript);
            return transcript;
        }
    }
}
