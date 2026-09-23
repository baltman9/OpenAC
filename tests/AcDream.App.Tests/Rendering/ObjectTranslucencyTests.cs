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

/// <summary>
/// The lookup the world draw is given: the player's camera fade applies to
/// the player only, every object keeps at least its own translucency, and
/// an examine copy answers for the object it copies.
/// Mutation: drop the stand-in mapping and the copy row turns red; apply
/// the fade to everyone and the other-object row turns red.
/// </summary>
public sealed class LiveObjectTranslucencyTests
{
    private const uint Player = 0x5000000Au;
    private const uint Shield = 0x80001234u;
    private const uint Copy = 0xDA11_D027u;

    private static LiveObjectTranslucency Build(float fade) => new(
        () => Player,
        () => fade,
        guid => guid == Shield ? 0.5f : null,
        guid => guid == Copy ? Shield : null);

    [Fact]
    public void ThePlayerFadesAndOtherObjectsKeepTheirOwnValue()
    {
        LiveObjectTranslucency lookup = Build(fade: 0.8f);

        Assert.Equal(0.8f, lookup.For(Player));
        Assert.Equal(0.5f, lookup.For(Shield));
        Assert.Equal(0f, lookup.For(0x80009999u));
        Assert.Equal(0f, lookup.For(0u));
    }

    [Fact]
    public void AnExamineCopyTakesTheValueOfWhatItCopies() =>
        Assert.Equal(0.5f, Build(fade: 0f).For(Copy));
}
