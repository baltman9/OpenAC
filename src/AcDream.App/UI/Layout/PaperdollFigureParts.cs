using AcDream.Core.Items;

namespace AcDream.App.UI.Layout;

/// <summary>
/// Which of the figure's parts belong to the item the player just selected.
/// <para>
/// Selecting an item flashes the parts of the rendered figure that item covers -
/// a helm lights the head alone, a hauberk lights chest, abdomen and both arms.
/// Two steps decide that: first the selected item is matched against every body
/// location it is the outermost worn item at, which yields a set of body-location
/// bits; then each of those bits names the parts of the figure that location is
/// drawn from. Selecting the player themselves lights the whole figure.
/// </para>
/// </summary>
internal static class PaperdollFigureParts
{
    /// <summary>Every part of a humanoid figure, for the select-yourself case.</summary>
    public const uint WholeFigure = 0x0001_FFFFu;   // parts 0 through 0x10

    /// <summary>A body location, and the bit that stands for it. The location
    /// masks pair each wear slot with its armor slot, because one item can be
    /// the outermost thing at either.</summary>
    private static readonly (EquipMask Location, int Bit)[] BodyLocations =
    {
        (EquipMask.HeadWear, 0),                                          // 0x0001
        (EquipMask.ChestWear    | EquipMask.ChestArmor, 1),               // 0x0202
        (EquipMask.AbdomenWear  | EquipMask.AbdomenArmor, 2),             // 0x0404
        (EquipMask.UpperArmWear | EquipMask.UpperArmArmor, 3),            // 0x0808
        (EquipMask.LowerArmWear | EquipMask.LowerArmArmor, 4),            // 0x1010
        (EquipMask.HandWear, 5),                                          // 0x0020
        (EquipMask.UpperLegWear | EquipMask.UpperLegArmor, 6),            // 0x2040
        (EquipMask.LowerLegWear | EquipMask.LowerLegArmor, 7),            // 0x4080
        (EquipMask.FootWear, 8),                                          // 0x0100
    };

    /// <summary>The parts each body-location bit is drawn from, by index into the
    /// figure's part list. Limbs come in pairs; the feet carry four.</summary>
    private static readonly int[][] PartsByBodyLocation =
    {
        new[] { 0x10 },                 // head
        new[] { 0x09 },                 // chest
        new[] { 0x00 },                 // abdomen
        new[] { 0x0A, 0x0D },           // upper arms
        new[] { 0x0B, 0x0E },           // lower arms
        new[] { 0x0C, 0x0F },           // hands
        new[] { 0x01, 0x05 },           // upper legs
        new[] { 0x02, 0x06 },           // lower legs
        new[] { 0x03, 0x07, 0x04, 0x08 },   // feet
    };

    /// <summary>Highest part index this mapping can name, so a caller can check
    /// the figure it is lighting actually has them.</summary>
    public const int HighestPartIndex = 0x10;

    /// <summary>The body-location bits the selected object is the outermost worn
    /// item at. Selecting the player gives every bit; an item worn nowhere on the
    /// figure, or nothing at all, gives none.</summary>
    public static uint BodyLocationMask(
        ClientObjectTable objects,
        uint playerId,
        uint selectedObjectId)
    {
        if (objects is null || playerId == 0u || selectedObjectId == 0u)
            return 0u;
        if (selectedObjectId == playerId)
            return uint.MaxValue >> 1;          // the whole figure

        uint mask = 0u;
        foreach ((EquipMask location, int bit) in BodyLocations)
        {
            if (PaperdollSelectionPolicy.GetUpperInventoryObject(objects, playerId, location)
                == selectedObjectId)
                mask |= 1u << bit;
        }
        return mask;
    }

    /// <summary>Turn body-location bits into the parts of the figure to flash.</summary>
    public static uint PartMask(uint bodyLocationMask)
    {
        if (bodyLocationMask == 0u)
            return 0u;
        if (bodyLocationMask == uint.MaxValue >> 1)
            return WholeFigure;

        uint parts = 0u;
        for (int bit = 0; bit < PartsByBodyLocation.Length; bit++)
        {
            if ((bodyLocationMask & (1u << bit)) == 0u)
                continue;
            foreach (int part in PartsByBodyLocation[bit])
                parts |= 1u << part;
        }
        return parts;
    }

    /// <summary>The parts to flash for a selected object, or 0 for one the figure
    /// does not wear.</summary>
    public static uint PartMaskFor(
        ClientObjectTable objects,
        uint playerId,
        uint selectedObjectId)
        => PartMask(BodyLocationMask(objects, playerId, selectedObjectId));
}
