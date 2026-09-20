namespace AcDream.HostParity.Tests;

/// <summary>
/// How often a plugin is ticked, and how much elapsed time each tick
/// carries. The two clients run at completely different rates -- one updates
/// every time it draws a frame, the other takes a turn on a fixed schedule
/// -- so this is the one scenario here that deliberately drives the two arms
/// differently: the windowed arm takes a thousand short frames of varying
/// length, the windowless arm takes turns of its scheduler's own period, and
/// the two cover the same three seconds.
///
/// A plugin loaded into either one must come out of that with the same
/// number of ticks and the same total elapsed time, because everything a
/// plugin paces off the tick -- a countdown, a rule it re-evaluates, a total
/// it accumulates -- is otherwise a different plugin on each client.
///
/// This belongs in the parity-evidence list rather than the regression
/// guards: the two arms really are fed differently here, so agreeing says
/// something.
///
/// Mutation check (2026-09-20), run: raising the tick once per feed with the
/// fed delta, the way both clients used to, turned this red on the tick
/// count and on the elapsed total.
/// </summary>
public sealed class PluginTickCadenceParityTests
{
    /// <summary>
    /// One windowed frame pattern: a client whose frames vary between about
    /// 2 and 4.5 ms, which is what a client that draws as fast as it can
    /// looks like.
    /// </summary>
    private static readonly double[] WindowedFrameSeconds =
        [0.0021, 0.0044, 0.0033, 0.0026];

    /// <summary>250 rounds of that pattern is 3.1 seconds of drawing.</summary>
    private const int WindowedRounds = 250;

    /// <summary>The windowless client's own turn, and how many make 3.09 s.</summary>
    private const double WindowlessTurnSeconds = 0.015;
    private const int WindowlessTurns = 206;

    [Fact]
    public void APluginIsTickedTheSameNumberOfTimesOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            int ticks = 0;
            double elapsedTotal = 0d;
            var elapsedValues = new HashSet<double>();
            arm.Host.Events.Tick += seconds =>
            {
                ticks++;
                elapsedTotal += seconds;
                elapsedValues.Add(seconds);
            };

            transcript.Step("three seconds, at this client's own rate");
            if (arm is WindowedArm)
            {
                for (int round = 0; round < WindowedRounds; round++)
                {
                    foreach (double frame in WindowedFrameSeconds)
                        arm.Advance(frame);
                }
            }
            else
            {
                for (int turn = 0; turn < WindowlessTurns; turn++)
                    arm.Advance(WindowlessTurnSeconds);
            }

            transcript.Record("ticks", ticks);
            transcript.Record("elapsed seconds", Math.Round(elapsedTotal, 6));
            transcript.Record(
                "distinct elapsed values",
                string.Join(
                    ", ",
                    elapsedValues.Order().Select(
                        static seconds => seconds.ToString(
                            "0.######",
                            System.Globalization.CultureInfo.InvariantCulture))));

            // Both arms covered the same stretch of time, so both owe the
            // same number of ticks, each carrying exactly one step.
            Assert.Equal(206, ticks);
            Assert.Equal(3.09, elapsedTotal, 6);
            Assert.Equal(
                [AcDream.Runtime.Plugins.RuntimePluginTickClock.StepSeconds],
                elapsedValues);
        });
}
