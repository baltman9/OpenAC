namespace AcDream.HostParity.Tests;

/// <summary>
/// What each client does with the server's word about where another creature
/// is. The bookkeeping -- where the word put it, when it arrived, how fast it
/// is therefore travelling -- is shared, but the ORDER of those steps around
/// the two things that read them matters: the routing that decides whether
/// the body catches up or is put down outright, and the adoption of the cell
/// the wire named. Running the same steps in a different order is a
/// difference a plugin meets as a creature's position being tenths of a
/// second stale on one client and not the other.
///
/// Each client still has its own inbound sink, so this pins the order rather
/// than the code: the shared steps, the routing and the adoption, read off
/// each sink in the order they are written, have to read the same. It also
/// refuses an inlined copy of the bookkeeping, which is what the two clients
/// had before the steps were shared and is how they came apart.
///
/// Mutation check (2026-09-20), both run: moving either client's stamp above
/// its wire-cell adoption turned <see cref="BothSinksRunTheStepsInOneOrder"/>
/// red; writing the arrival time into the state by hand in either sink
/// turned <see cref="NeitherSinkKeepsItsOwnCopyOfTheBookkeeping"/> red.
/// </summary>
public sealed class RemoteServerPositionOrderPinTests
{
    private const string WindowedSink =
        "src/AcDream.App/Physics/LiveEntityNetworkUpdateController.cs";

    private const string WindowlessSink =
        "src/AcDream.Runtime/Session/RuntimeLiveEntitySessionController.cs";

    /// <summary>
    /// The step each call stands for. Two clients name the same step
    /// differently where one of them wraps it in a local helper, so the step
    /// is what is compared, not the spelling.
    /// </summary>
    private static readonly (string Call, string Step)[] Steps =
    [
        ("RuntimeRemoteServerPosition.StampAirborneLeftover(", "airborne-leftover"),
        ("RuntimeRemoteServerPosition.DeriveVelocity(", "derive-velocity"),
        ("ApplyRemoteContactRouting(", "contact-routing"),
        ("RunRemoteArmTail(", "contact-routing"),
        ("TryArmConstraintAfterOperation(", "arm-constraint"),
        ("TryAdoptWireCellAfterRouting(", "adopt-wire-cell"),
        ("RuntimeRemoteServerPosition.Stamp(", "record-the-word"),
    ];

    /// <summary>
    /// Fields of the shared bookkeeping. A sink that writes one of these
    /// itself has grown its own copy of a step back again.
    /// </summary>
    private static readonly string[] BookkeepingWrites =
    [
        "LastServerPos =",
        "LastServerPosTime =",
        "ServerVelocity =",
        "HasServerVelocity =",
    ];

    [Fact]
    public void BothSinksRunTheStepsInOneOrder()
    {
        Assert.Equal(
            StepsOf(WindowedSink),
            StepsOf(WindowlessSink));
    }

    /// <summary>
    /// A sequence that came out empty would make the comparison above true
    /// and say nothing, so the shape is asserted here on its own.
    /// </summary>
    [Fact]
    public void TheStepsAreTheOnesBothSinksAreSupposedToRun()
    {
        Assert.Equal(
            [
                "airborne-leftover",
                "airborne-leftover",
                "derive-velocity",
                "contact-routing",
                "arm-constraint",
                "adopt-wire-cell",
                "record-the-word",
            ],
            StepsOf(WindowlessSink));
    }

    [Fact]
    public void NeitherSinkKeepsItsOwnCopyOfTheBookkeeping()
    {
        string[] offenders =
        [
            .. new[] { WindowedSink, WindowlessSink }
                .SelectMany(sink => BookkeepingWrites
                    .Where(write => Window(sink).Contains(
                        write, StringComparison.Ordinal))
                    .Select(write => $"{sink}: {write}"))
                .Order(StringComparer.Ordinal),
        ];

        Assert.True(
            offenders.Length == 0,
            "An inbound sink writes the shared bookkeeping about the "
            + "server's last word itself instead of running the shared step, "
            + "which is how the two clients came to disagree about how stale "
            + "a creature's position is: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// The run of a sink that handles one accepted position: from the first
    /// shared step to the last. Everything before it is how the sink got
    /// there, and everything after it is what that client does with the
    /// result.
    /// </summary>
    private static string Window(string relative)
    {
        string source = File.ReadAllText(Locate(relative));
        int start = source.IndexOf(
            Steps[0].Call, StringComparison.Ordinal);
        Assert.True(start >= 0, relative + " runs no shared step at all.");
        int end = source.LastIndexOf(
            Steps[^1].Call, StringComparison.Ordinal);
        Assert.True(end > start, relative + " never records the word.");
        return source[start..(end + Steps[^1].Call.Length)];
    }

    private static string[] StepsOf(string relative)
    {
        string window = Window(relative);
        var found = new List<(int At, string Step)>();
        foreach ((string call, string step) in Steps)
        {
            int at = 0;
            while ((at = window.IndexOf(call, at, StringComparison.Ordinal)) >= 0)
            {
                found.Add((at, step));
                at += call.Length;
            }
        }

        return [.. found.OrderBy(entry => entry.At).Select(entry => entry.Step)];
    }

    private static string Locate(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(
                directory.FullName,
                relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            relative + " was not found above " + AppContext.BaseDirectory);
    }
}
