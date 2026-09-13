using AcDream.Plugin.Abstractions;

namespace AcDream.Headless.Tests;

/// <summary>
/// The offline half of the session proof's arena housekeeping. The live run
/// deletes what this picks out, and a deletion cannot be taken back, so which
/// corpse it picks is worth pinning away from the server.
/// </summary>
public sealed class VtProofArenaTests
{
    /// <summary>
    /// The real set one run reported: the corpse of the monster the plugin
    /// killed, two corpses of other characters, and one of the character's
    /// own. Only the last is the run's to delete.
    ///
    /// Mutation: match on "Corpse of " alone, or drop the name test and take
    /// the first corpse reported, and the sweep deletes the drudge's corpse
    /// and then other players' corpses.
    /// </summary>
    [Fact]
    public void OnlyTheCharactersOwnCorpseIsSwept()
    {
        PluginLootContainer[] reported =
        [
            Corpse(0x80001828u, "Corpse of Drudge Skulker"),
            Corpse(0x800011C0u, "Corpse of Someone Else"),
            Corpse(0x80001AF9u, "Corpse of +Acdream"),
            Corpse(0x80006A14u, "Corpse of +Acdreamer"),
        ];

        PluginLootContainer? swept =
            VtProofArena.NextOwnCorpse(reported, "+Acdream");

        Assert.NotNull(swept);
        Assert.Equal(0x80001AF9u, swept!.Value.ObjectId);
    }

    /// <summary>
    /// The same character's corpse, spelled without the marker, is still the
    /// same character's: the run meets both spellings because the server
    /// drops the marker once the character appears as an ordinary player, and
    /// the server itself treats the two as one name.
    ///
    /// Mutation: compare against the given spelling alone, and every corpse
    /// left behind after the character was made ordinary survives the sweep.
    /// </summary>
    [Theory]
    [InlineData("+Acdream", "Corpse of Acdream")]
    [InlineData("+Acdream", "Corpse of +Acdream")]
    [InlineData("Acdream", "Corpse of Acdream")]
    [InlineData("Acdream", "Corpse of +Acdream")]
    public void EitherSpellingOfTheCharactersOwnCorpseIsSwept(
        string characterName,
        string corpseName)
    {
        PluginLootContainer[] reported =
        [
            Corpse(0x80001828u, "Corpse of Drudge Skulker"),
            Corpse(0x80001AF9u, corpseName),
        ];

        PluginLootContainer? swept =
            VtProofArena.NextOwnCorpse(reported, characterName);

        Assert.NotNull(swept);
        Assert.Equal(0x80001AF9u, swept!.Value.ObjectId);
    }

    /// <summary>
    /// A set with nothing of the character's in it stops the sweep rather
    /// than falling back on whatever is nearest.
    ///
    /// Mutation: return the first element when no name matches.
    /// </summary>
    [Fact]
    public void ASetWithoutTheCharactersOwnCorpseSweepsNothing()
    {
        PluginLootContainer[] reported =
        [
            Corpse(0x80001828u, "Corpse of Drudge Skulker"),
            Corpse(0x800011C0u, "Corpse of Someone Else"),
        ];

        Assert.Null(VtProofArena.NextOwnCorpse(reported, "+Acdream"));
        Assert.Null(VtProofArena.NextOwnCorpse(reported, string.Empty));
        Assert.Null(VtProofArena.NextOwnCorpse([], "+Acdream"));
    }

    /// <summary>
    /// Marker aside, the whole name has to match. A longer name that starts
    /// with the character's is somebody else, and so is a shorter one.
    /// </summary>
    [Fact]
    public void ANameThatMerelyStartsTheSameIsNotTheCharactersCorpse()
    {
        Assert.Equal("Corpse of +Acdream", VtProofArena.OwnCorpseName("+Acdream"));
        Assert.Null(VtProofArena.NextOwnCorpse(
            [
                Corpse(0x80001AF9u, "Corpse of Acdreamer"),
                Corpse(0x80001AFAu, "Corpse of +Acdreamer"),
                Corpse(0x80001AFBu, "Corpse of Acdrea"),
                Corpse(0x80001AFCu, "Corpse of  Acdream"),
            ],
            "+Acdream"));
    }

    /// <summary>
    /// The start-of-run sweep also takes what the LAST run left: a monster
    /// corpse belongs to nobody the run plays, and one still standing when the
    /// next run begins is the corpse that run's looter finds first.
    ///
    /// Mutation: drop the "belongs to anyone" guard and the first corpse
    /// returned is a player's.
    /// </summary>
    [Fact]
    public void TheLastRunsMonsterCorpseIsSweptAndNoPlayersIs()
    {
        PluginLootContainer[] reported =
        [
            Corpse(0x80001AF9u, "Corpse of +Acdream"),
            Corpse(0x80001AFAu, "Corpse of Horan"),
            Corpse(0x80001828u, "Corpse of Drudge Skulker"),
        ];

        PluginLootContainer? swept = VtProofArena.NextForeignCorpse(
            reported,
            ["Acdream", "Horan"]);

        Assert.NotNull(swept);
        Assert.Equal(0x80001828u, swept!.Value.ObjectId);
    }

    /// <summary>
    /// Both spellings of both characters are protected, and so is anything
    /// that is not a corpse at all. A set holding only those yields nothing.
    ///
    /// Mutation: check only the plain spelling, or only the first name, and a
    /// player's corpse is returned.
    /// </summary>
    [Fact]
    public void NoCorpseOfAnyCharacterTheRunPlaysIsEverForeign()
    {
        Assert.Null(VtProofArena.NextForeignCorpse(
            [
                Corpse(0x80001AF9u, "Corpse of Acdream"),
                Corpse(0x80001AFAu, "Corpse of +Acdream"),
                Corpse(0x80001AFBu, "Corpse of Horan"),
                Corpse(0x80001AFCu, "Corpse of +Horan"),
                Corpse(0x80001AFDu, "Treasure of Somebody"),
            ],
            ["Acdream", "+Horan"]));
    }

    /// <summary>
    /// A name that merely starts the same is not the character's here either,
    /// so the last run's monster is still swept when a similarly named
    /// character's corpse is beside it — and the similarly named character's
    /// corpse is swept too, because it is not one of the run's.
    /// </summary>
    [Fact]
    public void TheForeignTestMatchesTheWholeNameTheSameWay()
    {
        PluginLootContainer? swept = VtProofArena.NextForeignCorpse(
            [Corpse(0x80006A14u, "Corpse of Acdreamer")],
            ["Acdream"]);

        Assert.NotNull(swept);
        Assert.Equal(0x80006A14u, swept!.Value.ObjectId);
    }

    /// <summary>
    /// The step the loot milestone takes before it judges is away from the
    /// corpse along the line that already separates the two — on whichever
    /// side of the character it fell. A fixed compass direction steps a corpse
    /// that died on the wrong side TOWARDS the character, and an open that
    /// needed no walk would then pass as one that did.
    ///
    /// Mutation: negate the step, or fix it to one axis, and the corpse that
    /// lies south ends up nearer rather than further.
    /// </summary>
    [Theory]
    [InlineData(2d, 0d, 4.5d, 0d)]      // corpse due west -> step east
    [InlineData(-2d, 0d, -4.5d, 0d)]    // corpse due east -> step west
    [InlineData(0d, 2d, 0d, 4.5d)]      // corpse due south -> step north
    [InlineData(0d, -2d, 0d, -4.5d)]    // corpse due north -> step south
    public void TheStepBeforeTheJudgementIsAwayFromTheCorpse(
        double apartEastWest,
        double apartNorthSouth,
        double expectedEastWest,
        double expectedNorthSouth)
    {
        // The character stands at the origin of the map frame; the corpse is
        // the named number of metres off it.
        var stood = new PluginNavigationPosition(
            0xA9B40029u, 0d, 0d, 0d, 0f, true);
        PluginLootContainer corpse = Placed(
            0x80001828u,
            "Corpse of Drudge Skulker",
            -apartEastWest / 240d,
            -apartNorthSouth / 240d);

        Assert.True(VtProofArena.StepAwayFromCorpse(
            stood,
            [corpse],
            wantedMeters: 4.5d,
            out double eastWest,
            out double northSouth));

        // The step is what is left to travel, so the two together put the
        // character the wanted distance out.
        Assert.Equal(expectedEastWest - apartEastWest, eastWest, 3);
        Assert.Equal(expectedNorthSouth - apartNorthSouth, northSouth, 3);
    }

    /// <summary>
    /// A character already far enough out is not moved, and neither is one
    /// with nothing placed to step away from.
    /// </summary>
    [Fact]
    public void ThereIsNoStepWhenThereIsNothingToStepAwayFrom()
    {
        var stood = new PluginNavigationPosition(
            0xA9B40029u, 0d, 0d, 0d, 0f, true);

        Assert.False(VtProofArena.StepAwayFromCorpse(
            stood, [], 4.5d, out _, out _));
        Assert.False(VtProofArena.StepAwayFromCorpse(
            stood,
            [Corpse(0x80001828u, "Corpse of Drudge Skulker")],
            4.5d,
            out _,
            out _));
        Assert.False(VtProofArena.StepAwayFromCorpse(
            stood,
            [Placed(0x80001828u, "Corpse of Drudge Skulker", 9d / 240d, 0d)],
            4.5d,
            out _,
            out _));
    }

    /// <summary>
    /// The opening line names which corpse the looter chose, and the milestone
    /// reads the id back out of it to say how far away that corpse lay when
    /// the window opened. A line naming a corpse the run never saw, or no
    /// corpse at all, answers with a number no distance can be.
    ///
    /// Mutation: return the first recorded distance instead of the named
    /// one, and a corpse underfoot passes on another corpse's figure.
    /// </summary>
    [Fact]
    public void TheDistanceReportedIsTheOpenedCorpsesOwn()
    {
        var layAt = new Dictionary<uint, double>
        {
            [0x80001828u] = 0.4d,
            [0x80001631u] = 4.62d,
        };

        Assert.Equal(
            4.62d,
            VtProofArena.CorpseDistanceWhenOpened(
                "[MossTank] LootCorpse: opening Corpse of Drudge Skulker (0x80001631)",
                layAt),
            3);
        Assert.Equal(
            0.4d,
            VtProofArena.CorpseDistanceWhenOpened(
                "[MossTank] LootCorpse: opening Corpse of Drudge Skulker (0x80001828)",
                layAt),
            3);
        Assert.True(VtProofArena.CorpseDistanceWhenOpened(
            "[MossTank] LootCorpse: opening Corpse of Drudge Skulker (0x800099FF)",
            layAt) < 0d);
        Assert.True(
            VtProofArena.CorpseDistanceWhenOpened("(none)", layAt) < 0d);
        Assert.True(
            VtProofArena.CorpseDistanceWhenOpened(string.Empty, layAt) < 0d);
    }

    private static PluginLootContainer Corpse(uint objectId, string name) =>
        new(objectId, 1u, name, 3f, false, false, false);

    private static PluginLootContainer Placed(
        uint objectId,
        string name,
        double eastWest,
        double northSouth) =>
        new(objectId, 1u, name, 3f, false, false, false)
        {
            HasPosition = true,
            Position = new PluginNavigationPosition(
                0xA9B40029u, eastWest, northSouth, 0d, 0f, true),
        };
}
