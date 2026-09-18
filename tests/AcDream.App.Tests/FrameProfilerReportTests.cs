using System.Globalization;
using AcDream.App.Diagnostics;
using Xunit;

namespace AcDream.App.Tests;

public class FrameProfilerReportTests
{
    [Fact]
    public void WriteHistoryCsv_WritesHeaderThenOneInvariantRowPerRecord()
    {
        var records = new[]
        {
            new FrameHistoryRecord(0, 12.5, 1000, 500, 2048, 100, 200, 30, 40, 500, 60, 7),
            new FrameHistoryRecord(1, 18.75, 1200, -1, -256, 110, 210, 31, 41, 510, 61, 8),
        };
        var buffer = new StringWriter(CultureInfo.InvariantCulture);

        FrameProfiler.WriteHistoryCsv(
            records,
            buffer,
            new DateTime(2026, 7, 24, 10, 0, 0, DateTimeKind.Utc));

        string[] lines = buffer.ToString().Split(
            Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.Equal(
            "frame,timestamp_ms,timestamp_utc,cpu_us,gpu_us,alloc_bytes,update_us,upload_us,imgui_us,pacing_us,render_us,present_us,diagnostics_us",
            lines[0]);
        Assert.Equal(
            "0,12.500,2026-07-24T10:00:00.0125000Z,1000,500,2048,100,200,30,40,500,60,7",
            lines[1]);
        Assert.Equal(
            "1,18.750,2026-07-24T10:00:00.0187500Z,1200,-1,-256,110,210,31,41,510,61,8",
            lines[2]);
    }


    [Fact]
    public void FormatReport_IsInvariantAndComplete()
    {
        var cpu = new FrameStatsBuffer(16);
        var gpu = new FrameStatsBuffer(16);
        var alloc = new FrameStatsBuffer(16);
        var stages = new[]
        {
            new FrameStatsBuffer(16),
            new FrameStatsBuffer(16),
            new FrameStatsBuffer(16),
            new FrameStatsBuffer(16),
            new FrameStatsBuffer(16),
            new FrameStatsBuffer(16),
            new FrameStatsBuffer(16),
        };
        for (long i = 1; i <= 10; i++)
        {
            cpu.Push(i * 1000);      // 1..10 ms in µs
            gpu.Push(i * 100);
            alloc.Push(i * 1024);    // bytes
            stages[0].Push(i * 200);
            stages[1].Push(i * 50);
            stages[2].Push(i * 10);
            stages[3].Push(i * 300);
            stages[4].Push(i * 400);
            stages[5].Push(i * 20);
            stages[6].Push(i * 40);
        }

        string line = FrameProfiler.FormatReport(
            frameCount: 10, cpu: cpu, gpu: gpu, gpuActive: true,
            alloc: alloc, gc0: 3, gc1: 1, gc2: 0, stages: stages);

        Assert.StartsWith("[frame-prof]", line);
        Assert.Contains("n=10", line);
        Assert.Contains("cpu_ms p50=5.0 p95=10.0 p99=10.0 max=10.0", line);
        Assert.Contains("gpu_ms p50=0.5", line);
        Assert.Contains("alloc_kb p50=5.0 max=10.0", line);
        Assert.Contains("gc=3/1/0", line);
        Assert.Contains("upd p50=1.0", line);   // stage 0: 200µs·5 = 1.0 ms
        Assert.Contains("pace p50=1.5", line);
        Assert.Contains("rnd p50=2.0", line);    // stage 4: 400µs·5 = 2.0 ms
        Assert.Contains("pres p50=0.1", line);   // stage 5: 20µs·5 = 0.1 ms
        Assert.Contains("diag p50=0.2", line);   // stage 6: 40µs·5 = 0.2 ms
        Assert.DoesNotContain(",0", line.Replace("gc=3/1/0", ""));
    }

    [Fact]
    public void FormatReport_GpuInactive_SaysWhy()
    {
        var empty = new FrameStatsBuffer(4);
        string line = FrameProfiler.FormatReport(
            frameCount: 0, cpu: empty, gpu: empty, gpuActive: false,
            alloc: empty, gc0: 0, gc1: 0, gc2: 0,
            stages: new[] { empty, empty, empty });
        Assert.Contains("gpu=off(wbdiag)", line);
    }
}
