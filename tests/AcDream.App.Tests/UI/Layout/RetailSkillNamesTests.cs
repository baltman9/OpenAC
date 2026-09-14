using System;
using System.Collections.Generic;
using System.IO;
using AcDream.App.UI.Layout;
using AcDream.Content.CharGen;
using AcDream.Core.CharGen;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;

namespace AcDream.App.Tests.UI.Layout;

// OpenAC #88: character creation listed "Creature Appraisal" and "Person
// Appraisal" because it named skills from a hand-written table instead of the
// authored skill data. The authored data spells them "Assess Creature" and
// "Assess Person". These rows pin the source, not the two strings.
public sealed class RetailSkillNamesTests
{
    [Fact]
    public void AuthoredName_WinsOverTheFallbackTable()
    {
        var options = ChargenOptions.Empty with
        {
            GlobalSkillDetailsBySkillId = new Dictionary<uint, ChargenSkillDetail>
            {
                [27u] = new ChargenSkillDetail(
                    27u, "Authored Name", MinLevel: 1u, Description: string.Empty, Formula: default),
            },
        };

        Assert.Equal("Authored Name", RetailSkillNames.Resolve(options, 27u));
    }

    [Fact]
    public void WithoutAuthoredData_TheFallbackNamesTheSameSkillsTheSameWay()
    {
        // A host with no client data still has to name a skill, and it must
        // not invent a different spelling from the one everything else shows.
        Assert.Equal("Assess Person", RetailSkillNames.Fallback(19));
        Assert.Equal("Assess Creature", RetailSkillNames.Fallback(27));
        Assert.Equal("Shield", RetailSkillNames.Fallback(48));

        Assert.Equal("Assess Person", RetailSkillNames.Resolve(ChargenOptions.Empty, 19u));
        Assert.Equal("Assess Creature", RetailSkillNames.Resolve(ChargenOptions.Empty, 27u));
        Assert.Equal("Shield", RetailSkillNames.Resolve(ChargenOptions.Empty, 48u));
    }

    [Fact]
    public void AnEmptyAuthoredName_FallsBackRatherThanShowingNothing()
    {
        var options = ChargenOptions.Empty with
        {
            GlobalSkillDetailsBySkillId = new Dictionary<uint, ChargenSkillDetail>
            {
                [19u] = new ChargenSkillDetail(
                    19u, "   ", MinLevel: 1u, Description: string.Empty, Formula: default),
            },
        };

        Assert.Equal("Assess Person", RetailSkillNames.Resolve(options, 19u));
    }

    [Fact]
    public void AnUnnamedSkill_IsReportedAsUnnamed()
    {
        Assert.True(RetailSkillNames.IsUnnamed(RetailSkillNames.Fallback(200)));
        Assert.False(RetailSkillNames.IsUnnamed(RetailSkillNames.Fallback(27)));
    }
}

[Trait("Lane", "InstalledDat")]
public sealed class RetailSkillNamesInstalledDatTests
{
    private const uint SkillTableDid = 0x0E000004u;

    [InstalledDatFact]
    public void CharacterCreation_NamesSkillsExactlyAsTheAuthoredDataDoes()
    {
        using var dats = new BoundedTestDatCollection(DatDirectory());

        SkillTable authored = Assert.IsType<SkillTable>(dats.Get<SkillTable>(SkillTableDid));
        ChargenOptions options = ChargenTableReader.Load(dats);

        // The projection really carries the authored name; a page that showed
        // the fallback instead would still read right today only because the
        // fallback was corrected to agree with it.
        Assert.True(options.TryGetSkillDetail(27u, out ChargenSkillDetail creature));
        Assert.Equal("Assess Creature", creature.Name);
        Assert.True(options.TryGetSkillDetail(19u, out ChargenSkillDetail person));
        Assert.Equal("Assess Person", person.Name);

        // The skills the report named wrongly, plus the one #83 fixed.
        Assert.Equal("Assess Creature", RetailSkillNames.Resolve(options, 27u));
        Assert.Equal("Assess Person", RetailSkillNames.Resolve(options, 19u));
        Assert.Equal("Shield", RetailSkillNames.Resolve(options, 48u));

        // And every other skill the authored data carries, character-by-character.
        foreach ((DatReaderWriter.Enums.SkillId id, SkillBase skill) in authored.Skills)
        {
            Assert.Equal(skill.Name.Value, RetailSkillNames.Resolve(options, (uint)id));
        }
    }

    [InstalledDatFact]
    public void Appraisals_NameSkillsTheSameWayCharacterCreationDoes()
    {
        using var dats = new BoundedTestDatCollection(DatDirectory());

        SkillTable authored = Assert.IsType<SkillTable>(dats.Get<SkillTable>(SkillTableDid));
        ChargenOptions options = ChargenTableReader.Load(dats);
        RetailAppraisalNameResolver names = RetailAppraisalNameResolver.Load(
            dats,
            CreatureDisplayNameResolver.Load(dats));

        // The resolver really loaded the authored table, rather than falling
        // back for everything.
        Assert.Equal(authored.Skills.Count, names.AuthoredSkillNameCount);
        Assert.Equal(0, RetailAppraisalNameResolver.Empty.AuthoredSkillNameCount);

        Assert.Equal("Assess Creature", names.ResolveSkill(27));
        Assert.Equal("Assess Person", names.ResolveSkill(19));
        Assert.Equal("Shield", names.ResolveSkill(48));

        foreach ((DatReaderWriter.Enums.SkillId id, SkillBase skill) in authored.Skills)
        {
            Assert.Equal(skill.Name.Value, names.ResolveSkill((int)id));
            Assert.Equal(
                RetailSkillNames.Resolve(options, (uint)id),
                names.ResolveSkill((int)id));
        }

        // A retired skill the authored data dropped still gets its offline name.
        Assert.False(authored.Skills.ContainsKey((DatReaderWriter.Enums.SkillId)1));
        Assert.Equal("Axe", names.ResolveSkill(1));
    }

    private static string DatDirectory()
    {
        string? configured = System.Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR");
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
            return configured;
        return Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            "Documents",
            "Asheron's Call");
    }
}
