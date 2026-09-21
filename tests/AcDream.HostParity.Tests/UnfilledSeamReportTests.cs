using AcDream.Runtime;
using AcDream.Runtime.Plugins;

namespace AcDream.HostParity.Tests;

/// <summary>
/// The census reads declarations, so a host that declares a capability and
/// then hands in nothing for it would leave a seam empty with nothing said.
/// The binding pass is what notices, and these pin both directions: it speaks
/// up when a declared seam comes out unfilled, and it stays quiet when every
/// declared seam was filled.
///
/// Mutation check (2026-09-20): making the binding pass skip the report
/// entirely turned <see cref="ADeclaredCapabilityThatArrivesEmptyIsReported"/>
/// and <see cref="AConditionalCapabilityIsReportedWithItsCondition"/> red;
/// reporting every declared seam whether or not it was filled turned
/// <see cref="NothingIsReportedWhenEveryDeclaredSeamWasFilled"/> red.
/// </summary>
public sealed class UnfilledSeamReportTests
{
    private const string MagicCatalogCapability =
        nameof(RuntimeAutomationHostCapabilities.MagicCatalog);

    private static RuntimeAutomationHostCapabilities Record(
        Action<string> warn,
        IReadOnlyDictionary<string, string>? conditional = null) =>
        new()
        {
            HostName = "test",
            Declared = new HashSet<string>(StringComparer.Ordinal)
            {
                MagicCatalogCapability,
            },
            Conditional = conditional
                ?? new Dictionary<string, string>(StringComparer.Ordinal),
            Warn = warn,
        };

    [Fact]
    public void ADeclaredCapabilityThatArrivesEmptyIsReported()
    {
        var reported = new List<string>();
        using var runtime = new GameRuntime(
            HarnessRuntime.Dependencies());
        using var surface = new RuntimeAutomationSurface();

        RuntimeAutomationBindings.Apply(
            surface, runtime, Record(reported.Add));

        string only = Assert.Single(reported);
        Assert.Contains("BindMagicCatalog", only, StringComparison.Ordinal);
        Assert.Contains("unconditionally", only, StringComparison.Ordinal);
    }

    [Fact]
    public void AConditionalCapabilityIsReportedWithItsCondition()
    {
        var reported = new List<string>();
        using var runtime = new GameRuntime(HarnessRuntime.Dependencies());
        using var surface = new RuntimeAutomationSurface();

        RuntimeAutomationBindings.Apply(
            surface,
            runtime,
            Record(
                reported.Add,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [MagicCatalogCapability] = "there is no spell table here",
                }));

        string only = Assert.Single(reported);
        Assert.Contains(
            "there is no spell table here", only, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "unconditionally", only, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingIsReportedWhenEveryDeclaredSeamWasFilled()
    {
        var reported = new List<string>();
        using var runtime = new GameRuntime(HarnessRuntime.Dependencies());
        using var surface = new RuntimeAutomationSurface();

        RuntimeAutomationBindings.Apply(
            surface,
            runtime,
            new RuntimeAutomationHostCapabilities
            {
                HostName = "test",
                Declared = new HashSet<string>(StringComparer.Ordinal),
                Warn = reported.Add,
            });

        Assert.Empty(reported);
    }

    [Fact]
    public void ACapabilityWhoseConditionIsNamedButWhichIsNotDeclaredIsRefused()
    {
        using var runtime = new GameRuntime(HarnessRuntime.Dependencies());
        using var surface = new RuntimeAutomationSurface();

        InvalidOperationException error =
            Assert.Throws<InvalidOperationException>(() =>
                RuntimeAutomationBindings.Apply(
                    surface,
                    runtime,
                    new RuntimeAutomationHostCapabilities
                    {
                        HostName = "test",
                        Declared = new HashSet<string>(StringComparer.Ordinal),
                        Conditional =
                            new Dictionary<string, string>(StringComparer.Ordinal)
                            {
                                [MagicCatalogCapability] = "no spell table",
                            },
                    }));

        Assert.Contains(
            MagicCatalogCapability, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AShutDownSurfaceBindsNothingAndSaysSo()
    {
        var reported = new List<string>();
        using var runtime = new GameRuntime(HarnessRuntime.Dependencies());
        var surface = new RuntimeAutomationSurface();
        surface.Dispose();

        IReadOnlySet<string> bound = RuntimeAutomationBindings.Apply(
            surface,
            runtime,
            new RuntimeAutomationHostCapabilities
            {
                HostName = "test",
                Declared = new HashSet<string>(StringComparer.Ordinal),
                Warn = reported.Add,
            });

        Assert.Empty(bound);
        Assert.Single(reported);
    }
}
