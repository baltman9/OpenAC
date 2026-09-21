using AcDream.Content.Skills;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;

namespace AcDream.Content.Tests.Skills;

/// <summary>
/// The attribute-derived part of a skill's level, and the one rule that can
/// switch it off: a skill the character has not taken far enough to be
/// allowed to use contributes nothing at all, however high the attributes
/// behind it are.
/// </summary>
public sealed class LiveSkillCreditResolverTests
{
    private const uint RunSkillId = 24u;
    private const uint Strength = 1u;
    private const uint Coordination = 4u;

    private const uint Untrained = 1u;
    private const uint Trained = 2u;
    private const uint Specialized = 3u;

    /// <summary>Both attributes at 315, the game's practical ceiling.</summary>
    private static readonly Dictionary<uint, uint> MaxedAttributes = new()
    {
        [Strength] = 315u,
        [Coordination] = 315u,
    };

    [Theory]
    [InlineData(Untrained)]
    [InlineData(Trained)]
    [InlineData(Specialized)]
    public void AUsableSkillScoresTheAverageOfItsTwoAttributes(
        uint advancementClass)
    {
        var resolver = new LiveSkillCreditResolver(TableWithMinimum(Untrained));

        Assert.Equal(
            315u,
            resolver.Resolve(RunSkillId, advancementClass, MaxedAttributes));
    }

    [Theory]
    [InlineData(Untrained, Trained)]
    [InlineData(Untrained, Specialized)]
    [InlineData(Trained, Specialized)]
    public void ASkillBelowItsMinimumScoresNothing(
        uint advancementClass,
        uint minimum)
    {
        var resolver = new LiveSkillCreditResolver(TableWithMinimum(minimum));

        Assert.Equal(
            0u,
            resolver.Resolve(RunSkillId, advancementClass, MaxedAttributes));
    }

    [Fact]
    public void AtItsMinimumTheSkillScoresInFull()
    {
        var resolver = new LiveSkillCreditResolver(TableWithMinimum(Trained));

        Assert.Equal(315u, resolver.Resolve(RunSkillId, Trained, MaxedAttributes));
    }

    [Fact]
    public void ASkillTheDataDoesNotDescribeScoresNothing()
    {
        var resolver = new LiveSkillCreditResolver(TableWithMinimum(Untrained));

        Assert.Equal(0u, resolver.Resolve(0x999u, Specialized, MaxedAttributes));
        Assert.Equal(
            0u,
            new LiveSkillCreditResolver(null)
                .Resolve(RunSkillId, Specialized, MaxedAttributes));
    }

    /// <summary>(Strength + Coordination) / 2, usable from the given level.</summary>
    private static SkillTable TableWithMinimum(uint minimum)
    {
        var table = new SkillTable();
        table.Skills.Add((SkillId)RunSkillId, new SkillBase
        {
            MinLevel = minimum,
            Formula = new SkillFormula
            {
                AdditiveBonus = 0,
                Attribute1Multiplier = 1,
                Attribute2Multiplier = 1,
                Divisor = 2,
                Attribute1 = AttributeId.Strength,
                Attribute2 = AttributeId.Coordination,
            },
        });
        return table;
    }
}
