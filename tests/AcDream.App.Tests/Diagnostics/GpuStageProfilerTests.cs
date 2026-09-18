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
    public void APassStageKeyNeverCollidesWithThePassTimerOfTheSameName()
    {
        // Both graphs measure a post-process pass twice: once as the pass timer
        // a render pack budgets against, once as a stage range. The backend
        // refuses two ranges under one name, so a call site that passed the
        // pass name straight through threw on its first frame.
        foreach (string passName in new[]
        {
            "atmospheric-filmic",
            "atmospheric-sun-rays",
            // The shape a declared (data-driven) pack gives its nodes.
            "pack-node-0",
            "bloom",
        })
        {
            Assert.NotEqual(passName, GpuStageProfiler.PassStageName(passName));
        }
    }

    [Fact]
    public void PassStageKeysAreStableAndDistinctPerPass()
    {
        string first = GpuStageProfiler.PassStageName("atmospheric-filmic");

        Assert.Same(first, GpuStageProfiler.PassStageName("atmospheric-filmic"));
        Assert.NotEqual(first, GpuStageProfiler.PassStageName("atmospheric-sun-rays"));
    }

    [Fact]
    public void ADroppedRangeIsNamedInTheReportRatherThanReadingAsFree()
    {
        var profiler = new GpuStageProfiler();
        profiler.Record("shadow", 0.4);

        Assert.DoesNotContain("DROPPED", profiler.FormatReport(), StringComparison.Ordinal);

        profiler.NoteDroppedRanges(12);

        Assert.Contains("DROPPED=12", profiler.FormatReport(), StringComparison.Ordinal);
    }

    [Fact]
    public void DropsAddUpAcrossTheFramesOfOneWindow()
    {
        var profiler = new GpuStageProfiler();
        profiler.Record("shadow", 0.4);

        profiler.NoteDroppedRanges(3);
        profiler.NoteDroppedRanges(4);

        Assert.Contains("DROPPED=7", profiler.FormatReport(), StringComparison.Ordinal);
    }

    [Fact]
    public void ACleanWindowAfterABusyOneSaysNothingAboutDrops()
    {
        // The count belongs to the window being reported, not to the session:
        // one busy frame must not mark every line after it.
        var profiler = new GpuStageProfiler();
        profiler.Record("shadow", 0.4);
        profiler.NoteDroppedRanges(12);
        Assert.Contains("DROPPED", profiler.FormatReport(), StringComparison.Ordinal);

        profiler.ResetWindow();
        profiler.Record("shadow", 0.4);

        Assert.DoesNotContain("DROPPED", profiler.FormatReport(), StringComparison.Ordinal);
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
