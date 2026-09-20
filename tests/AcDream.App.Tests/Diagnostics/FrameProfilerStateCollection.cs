namespace AcDream.App.Tests;

/// <summary>
/// The frame profiler is switched on by a process-wide flag, so a test that
/// flips it runs alone: any other test constructing a profiler at that moment
/// would see a switched-on profiler it never asked for.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FrameProfilerStateCollection
{
    public const string Name = "Frame profiler process state";
}
