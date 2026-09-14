using AcDream.App.Input;

namespace AcDream.App.Tests.Input;

public sealed class WindowFocusRouterTests
{
    [Fact]
    public void FocusReachesTheSettingsEvenBeforeThePointerOwnerIsComposed()
    {
        var seen = new List<bool>();
        int resolved = 0;
        var router = new WindowFocusRouter(
            () => { resolved++; return null; },
            seen.Add);

        router.HandleFocusChanged(false);
        router.HandleFocusChanged(true);

        Assert.Equal([false, true], seen);
        Assert.Equal(2, resolved); // resolved per call, never captured
    }

    [Fact]
    public void RejectsMissingOwners()
    {
        Assert.Throws<ArgumentNullException>(() => new WindowFocusRouter(null!, _ => { }));
        Assert.Throws<ArgumentNullException>(() => new WindowFocusRouter(() => null, null!));
    }
}
