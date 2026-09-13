using AcDream.App.Plugins;
using AcDream.Tests.Fixtures.AutomationParity;

namespace AcDream.App.Tests.Plugins;

/// <summary>
/// The graphical half of the shared parity script. It runs the same
/// requests, over the same runtime fixture, as
/// <c>HeadlessAutomationSurfaceParityTests</c>, and must record the same
/// answers: a plugin sees one client, not one per host.
/// </summary>
public sealed class AppAutomationSurfaceParityTests
{
    [Fact]
    public void TheGraphicalHostGivesTheSharedParityAnswers()
    {
        using var fixture = new AutomationParityRuntimeFixture();
        using var surface = new AppAutomationSurface();
        surface.Bind(
            fixture.Runtime,
            fixture.Runtime.CharacterOwner,
            fixture.Runtime.ActionOwner.SpellCast);

        IReadOnlyList<string> answers = AutomationSurfaceParityScript.Run(
            fixture.Runtime,
            surface,
            () => fixture.GameActions.Count);

        Assert.Equal(AutomationSurfaceParityScript.Expected, answers);
    }
}
