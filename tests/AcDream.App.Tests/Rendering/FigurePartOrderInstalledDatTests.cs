using System.IO;
using AcDream.App.UI.Layout;
using AcDream.Core.Meshing;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;

namespace AcDream.App.Tests.Rendering;

/// <summary>
/// The figure's parts are flashed by index, so the index the panel names has to
/// be the same index the figure's part list uses. Nothing here is a fixture: the
/// setup comes from the installed data.
/// </summary>
[Trait("Lane", "InstalledDat")]
public sealed class FigurePartOrderInstalledDatTests
{
    // The human male body the paperdoll draws.
    private const uint HumanMaleSetupId = 0x0200004Eu;

    [Fact]
    public void FlattenedMeshRefs_AreTheSetupPartsInOrder()
    {
        string? datDir = ResolveDatDir();
        if (datDir is null)
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var dats = new AcDream.App.Tests.BoundedTestDatCollection(datDir!);
        Setup? setup = dats.Get<Setup>(HumanMaleSetupId);
        Assert.True(setup is not null, $"setup 0x{HumanMaleSetupId:X8} is not in the installed data.");

        IReadOnlyList<MeshRef> flattened = SetupMesh.Flatten(setup!);

        // One mesh ref per part, in part order, nothing merged and nothing moved:
        // this is what makes "part N" mean the same thing on both sides.
        Assert.Equal(setup!.Parts.Count, flattened.Count);
        for (int i = 0; i < setup.Parts.Count; i++)
            Assert.Equal((uint)setup.Parts[i], flattened[i].GfxObjId);
    }

    [Fact]
    public void EveryPartTheFlashCanName_ExistsOnTheFigure()
    {
        string? datDir = ResolveDatDir();
        if (datDir is null)
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var dats = new AcDream.App.Tests.BoundedTestDatCollection(datDir!);
        Setup? setup = dats.Get<Setup>(HumanMaleSetupId);
        Assert.True(setup is not null, $"setup 0x{HumanMaleSetupId:X8} is not in the installed data.");

        Assert.True(
            setup!.Parts.Count > PaperdollFigureParts.HighestPartIndex,
            $"the figure has {setup.Parts.Count} parts but the flash can name part "
            + $"0x{PaperdollFigureParts.HighestPartIndex:X}, so the mapping is for a "
            + "different body than the one being drawn.");

        // Every bit the whole-figure mask sets has to be a real part.
        for (int part = 0; part <= PaperdollFigureParts.HighestPartIndex; part++)
            Assert.True(
                (PaperdollFigureParts.WholeFigure & (1u << part)) != 0u,
                $"part {part} is on the figure but the whole-figure mask misses it.");
    }

    private static string? ResolveDatDir()
    {
        string? fromEnvironment = System.Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnvironment) && Directory.Exists(fromEnvironment))
            return fromEnvironment;

        string installed = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            "Documents",
            "Asheron's Call");
        return Directory.Exists(installed) ? installed : null;
    }
}
