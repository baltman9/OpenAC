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

    private static PluginLootContainer Corpse(uint objectId, string name) =>
        new(objectId, 1u, name, 3f, false, false, false);
}
