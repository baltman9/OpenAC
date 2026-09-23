using AcDream.App.Rendering;

namespace AcDream.App.Tests.Rendering;

/// <summary>
/// OpenAC #175: an object that arrives translucent (a phantom weapon, a
/// shade) is drawn at that translucency, and a translucency asked for later
/// raises it but never takes it below.
/// Mutation: ignore the object's own value and the phantom rows turn red;
/// let the request win outright and the "never below" row turns red.
/// </summary>
public sealed class ObjectTranslucencyTests
{
    [Theory]
    [InlineData(0f, null, 0f)]
    [InlineData(0f, 0.5f, 0.5f)]
    [InlineData(0.2f, 0.5f, 0.5f)]
    [InlineData(0.8f, 0.5f, 0.8f)]
    [InlineData(1f, 0.5f, 1f)]
    [InlineData(0.3f, float.NaN, 0.3f)]
    public void AnObjectIsNeverDrawnLessTranslucentThanItArrived(
        float requested,
        float? original,
        float expected)
    {
        Assert.Equal(expected, ObjectTranslucency.Effective(requested, original));
    }
}
