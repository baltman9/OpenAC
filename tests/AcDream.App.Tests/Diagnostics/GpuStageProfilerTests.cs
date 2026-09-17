using AcDream.App.Diagnostics;

namespace AcDream.App.Tests.Diagnostics;

public sealed class GpuStageProfilerTests
{
    [Fact]
    public void RepeatedStageWithinOneFrame_GetsItsOwnKey()
    {
        var profiler = new GpuStageProfiler();

        Assert.Equal("ui-text", profiler.NextKey("ui-text"));
        Assert.Equal("ui-text#2", profiler.NextKey("ui-text"));
        Assert.Equal("ui-text#3", profiler.NextKey("ui-text"));
    }

    [Fact]
    public void StageKeysRestartEachFrame()
    {
        var profiler = new GpuStageProfiler();

        Assert.Equal("world", profiler.NextKey("world"));
        Assert.Equal("world#2", profiler.NextKey("world"));

        profiler.BeginFrame();

        Assert.Equal("world", profiler.NextKey("world"));
    }

    [Fact]
    public void ReportCarriesMedianMillisecondsInFirstSeenOrder()
    {
        var profiler = new GpuStageProfiler();

        profiler.Record("shadow", 0.4);
        profiler.Record("world", 1.25);
        profiler.Record("shadow", 0.6);
        // Two samples: the median takes the lower of the pair.
        profiler.Record("world", 1.25);

        string report = profiler.FormatReport();

        Assert.Contains("shadow p50=0.400", report, StringComparison.Ordinal);
        Assert.Contains("world p50=1.250", report, StringComparison.Ordinal);
        Assert.True(
            report.IndexOf("shadow", StringComparison.Ordinal)
                < report.IndexOf("world", StringComparison.Ordinal),
            "Stages are reported in the order they were first seen.");
    }

    [Fact]
    public void ResettingTheWindowEmptiesTheReport()
    {
        var profiler = new GpuStageProfiler();

        profiler.Record("volumetric", 0.2);
        Assert.Contains("volumetric", profiler.FormatReport(), StringComparison.Ordinal);

        profiler.ResetWindow();

        Assert.DoesNotContain("volumetric", profiler.FormatReport(), StringComparison.Ordinal);
    }
}
