using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.Items;
using AcDream.Core.Selection;

namespace AcDream.App.Tests.UI.Layout;

public class PaperdollFigurePartsTests
{
    private const uint Player = 0x50000001u;

    private static ClientObjectTable Table()
    {
        var t = new ClientObjectTable();
        t.AddOrUpdate(new ClientObject { ObjectId = Player });
        return t;
    }

    private static void Wear(ClientObjectTable t, uint guid, EquipMask where)
    {
        t.AddOrUpdate(new ClientObject { ObjectId = guid, WielderId = Player });
        t.MoveItem(guid, Player, newSlot: -1, newEquipLocation: where);
    }

    private static int[] Parts(uint mask)
    {
        var parts = new List<int>();
        for (int i = 0; i < 32; i++)
            if ((mask & (1u << i)) != 0u) parts.Add(i);
        return parts.ToArray();
    }

    [Fact]
    public void AHelm_lightsTheHeadAlone()
    {
        var t = Table();
        Wear(t, 0xA01u, EquipMask.HeadWear);

        Assert.Equal(new[] { 0x10 }, Parts(PaperdollFigureParts.PartMaskFor(t, Player, 0xA01u)));
    }

    [Fact]
    public void AHauberk_lightsChestAbdomenAndBothArms()
    {
        var t = Table();
        Wear(
            t,
            0xA02u,
            EquipMask.ChestArmor
            | EquipMask.AbdomenArmor
            | EquipMask.UpperArmArmor
            | EquipMask.LowerArmArmor);

        // abdomen, upper arms, lower arms, chest - six parts.
        Assert.Equal(
            new[] { 0x00, 0x09, 0x0A, 0x0B, 0x0D, 0x0E },
            Parts(PaperdollFigureParts.PartMaskFor(t, Player, 0xA02u)));
    }

    [Fact]
    public void BootsLightFourParts_andGauntletsLightTwo()
    {
        var t = Table();
        Wear(t, 0xA03u, EquipMask.FootWear);
        Wear(t, 0xA04u, EquipMask.HandWear);

        Assert.Equal(
            new[] { 0x03, 0x04, 0x07, 0x08 },
            Parts(PaperdollFigureParts.PartMaskFor(t, Player, 0xA03u)));
        Assert.Equal(
            new[] { 0x0C, 0x0F },
            Parts(PaperdollFigureParts.PartMaskFor(t, Player, 0xA04u)));
    }

    [Fact]
    public void SelectingYourself_lightsTheWholeFigure()
    {
        var t = Table();

        Assert.Equal(
            PaperdollFigureParts.WholeFigure,
            PaperdollFigureParts.PartMaskFor(t, Player, Player));
    }

    [Fact]
    public void AnItemTheFigureDoesNotWear_lightsNothing()
    {
        var t = Table();
        t.AddOrUpdate(new ClientObject { ObjectId = 0xA05u, ValidLocations = EquipMask.HeadWear });

        Assert.Equal(0u, PaperdollFigureParts.PartMaskFor(t, Player, 0xA05u));
        Assert.Equal(0u, PaperdollFigureParts.PartMaskFor(t, Player, 0u));
    }

    [Fact]
    public void AnItemCoveredByAnother_lightsNothing_becauseItIsNotTheOutermost()
    {
        var t = Table();
        t.AddOrUpdate(new ClientObject { ObjectId = 0xA06u, WielderId = Player, Priority = 1u });
        t.MoveItem(0xA06u, Player, newSlot: -1, newEquipLocation: EquipMask.ChestWear);
        t.AddOrUpdate(new ClientObject { ObjectId = 0xA07u, WielderId = Player, Priority = 9u });
        t.MoveItem(0xA07u, Player, newSlot: -1, newEquipLocation: EquipMask.ChestWear);

        Assert.Equal(new[] { 0x09 }, Parts(PaperdollFigureParts.PartMaskFor(t, Player, 0xA07u)));
        Assert.Equal(0u, PaperdollFigureParts.PartMaskFor(t, Player, 0xA06u));
    }

    private sealed class RecordingFigureLighting : IPaperdollFigureLighting
    {
        public List<uint> Flashes { get; } = new();
        public void FlashParts(uint partMask) => Flashes.Add(partMask);
    }

    [Fact]
    public void AnySelectionChange_flashesTheWornPieces_andNothingForAnUnwornOne()
    {
        var t = Table();
        Wear(t, 0xA01u, EquipMask.HeadWear);
        t.AddOrUpdate(new ClientObject { ObjectId = 0xB01u });
        var lighting = new RecordingFigureLighting();
        var selection = new SelectionState();
        BindPanel(t, selection, lighting);

        selection.Select(0xA01u, SelectionChangeSource.Paperdoll);
        Assert.Equal(new[] { 1u << 0x10 }, lighting.Flashes);

        // An item the figure does not wear leaves the figure alone.
        selection.Select(0xB01u, SelectionChangeSource.World);
        Assert.Equal(new[] { 1u << 0x10 }, lighting.Flashes);
    }

    private sealed class RootElement : UiElement { }

    private static void BindPanel(
        ClientObjectTable objects,
        SelectionState selection,
        IPaperdollFigureLighting lighting)
    {
        var layout = new ImportedLayout(
            new RootElement { Width = 224, Height = 214 },
            new Dictionary<uint, UiElement>());
        var interaction = new ItemInteractionController(
            objects,
            new AcDream.Runtime.Gameplay.RuntimeInteractionTransactionState(
                new InventoryTransactionState(objects)),
            new InteractionState(),
            () => Player,
            sendUse: null, sendUseWithTarget: null, sendWield: null, sendDrop: null,
            sendExamine: null, sendPutItemInContainer: null, systemMessage: null);
        PaperdollController.Bind(
            layout, objects, () => Player,
            iconIds: (_, _, _, _, _) => 0u,
            selection: selection,
            itemInteraction: interaction,
            figureLighting: lighting);
    }
}
