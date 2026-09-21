using AcDream.Runtime.Session;

namespace AcDream.Runtime.Tests.Session;

/// <summary>
/// The standing declarations the host-parity census reads are written beside
/// the code, not derived from a run, so a client that declares a binding and
/// then hands in nothing for it leaves the binding empty with nothing said.
/// This is the check that notices, at the moment the record is built.
///
/// Mutation check (2026-09-20), run: having the check report every declared
/// member whether or not it was filled turned
/// <see cref="NothingIsSaidWhenEveryDeclaredBindingWasFilled"/> red; reading
/// a filled binding as empty and an empty one as filled turned the other
/// three red as well as that one. Restoring the check turned them green.
/// </summary>
public sealed class LiveSessionBindingDeclarationTests
{
    private sealed record Inner(Action? Deep);

    private sealed record Outer(Action? Shallow, Inner Nested);

    private static readonly IReadOnlyDictionary<string, string> NoConditions =
        new Dictionary<string, string>(StringComparer.Ordinal);

    [Fact]
    public void ADeclaredBindingThatArrivedEmptyIsReportedAsADefect()
    {
        var reported = new List<string>();

        Report(new Outer(null, new Inner(() => { })), ["Shallow"], reported);

        string only = Assert.Single(reported);
        Assert.Contains("Shallow", only, StringComparison.Ordinal);
        Assert.Contains("unconditionally", only, StringComparison.Ordinal);
    }

    /// <summary>
    /// The bindings are nested, and a declaration names members of the inner
    /// records too, so the check has to follow the nesting.
    /// </summary>
    [Fact]
    public void ADeclaredBindingInsideANestedRecordIsReportedToo()
    {
        var reported = new List<string>();

        Report(new Outer(() => { }, new Inner(null)), ["Deep"], reported);

        string only = Assert.Single(reported);
        Assert.Contains("Deep", only, StringComparison.Ordinal);
    }

    [Fact]
    public void ABindingWithANamedConditionIsReportedAsAConfiguration()
    {
        var reported = new List<string>();

        LiveSessionBindingDeclarations.ReportDeclaredButUnfilled(
            "test",
            "binding",
            new Outer(null, new Inner(() => { })),
            new HashSet<string>(StringComparer.Ordinal) { "Shallow" },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Shallow"] = "there is nothing to draw on",
            },
            reported.Add);

        string only = Assert.Single(reported);
        Assert.Contains(
            "there is nothing to draw on", only, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "unconditionally", only, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingIsSaidWhenEveryDeclaredBindingWasFilled()
    {
        var reported = new List<string>();

        Report(
            new Outer(() => { }, new Inner(() => { })),
            ["Shallow", "Deep"],
            reported);

        Assert.Empty(reported);
    }

    private static void Report(
        Outer bindings,
        string[] declared,
        List<string> reported) =>
        LiveSessionBindingDeclarations.ReportDeclaredButUnfilled(
            "test",
            "binding",
            bindings,
            new HashSet<string>(declared, StringComparer.Ordinal),
            NoConditions,
            reported.Add);
}
