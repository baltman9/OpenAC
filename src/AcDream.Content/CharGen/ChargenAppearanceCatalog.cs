using System.Collections.Concurrent;
using System.Collections.Frozen;
using AcDream.Core.CharGen;
using DatClothingTable = DatReaderWriter.DBObjs.ClothingTable;
using DatPalette = DatReaderWriter.DBObjs.Palette;
using DatPalSet = DatReaderWriter.DBObjs.PalSet;
using DatCloObjectEffect = DatReaderWriter.Types.CloObjectEffect;
using DatCloSubPalette = DatReaderWriter.Types.CloSubPalette;

namespace AcDream.Content.CharGen;

public sealed class ChargenAppearanceCatalog :
    IChargenPalSetSource, IChargenClothingTableSource, IChargenPaletteColorSource
{
    private readonly IDatReaderWriter _dats;
    private readonly object? _readLock;
    private readonly ConcurrentDictionary<uint, ChargenPalSet?> _palSets = new();
    private readonly ConcurrentDictionary<uint, ChargenClothingTable?> _clothingTables = new();
    private readonly ConcurrentDictionary<uint, DatPalette?> _palettes = new();

    /// <param name="dats">The installed data files the catalogue reads.</param>
    /// <param name="readLock">
    /// The lock the host's other readers of the same files hold; every read
    /// this catalogue makes is taken under it. Null when the host reads the
    /// files from one thread only.
    /// </param>
    public ChargenAppearanceCatalog(IDatReaderWriter dats, object? readLock = null)
    {
        _dats = dats ?? throw new ArgumentNullException(nameof(dats));
        _readLock = readLock;
    }

    public ChargenPalSet? TryGetPalSet(uint palSetId) =>
        _palSets.GetOrAdd(palSetId, LoadPalSet);

    public ChargenClothingTable? TryGetClothingTable(uint clothingTableId) =>
        _clothingTables.GetOrAdd(clothingTableId, LoadClothingTable);

    public bool TryGetColor(uint paletteId, int index, out ChargenSwatchRgb color)
    {
        color = default;
        DatPalette? palette = _palettes.GetOrAdd(paletteId, Read<DatPalette>);
        if (palette is null || index < 0 || index >= palette.Colors.Count)
            return false;

        DatReaderWriter.Types.ColorARGB c = palette.Colors[index];
        color = new ChargenSwatchRgb(c.Red, c.Green, c.Blue);
        return true;
    }

    private T? Read<T>(uint id) where T : DatReaderWriter.Lib.IO.IDBObj
    {
        if (_readLock is null)
            return _dats.Get<T>(id);
        lock (_readLock)
            return _dats.Get<T>(id);
    }

    private ChargenPalSet? LoadPalSet(uint id)
    {
        DatPalSet? palSet = Read<DatPalSet>(id);
        if (palSet is null)
            return null;

        var ids = new uint[palSet.Palettes.Count];
        for (int i = 0; i < palSet.Palettes.Count; i++)
            ids[i] = palSet.Palettes[i].DataId;
        return new ChargenPalSet(Array.AsReadOnly(ids));
    }

    private ChargenClothingTable? LoadClothingTable(uint id)
    {
        DatClothingTable? table = Read<DatClothingTable>(id);
        if (table is null)
            return null;

        var baseEffects = new Dictionary<uint, ChargenClothingBaseEffect>(
            table.ClothingBaseEffects.Count);
        foreach (var pair in table.ClothingBaseEffects)
            baseEffects[pair.Key.DataId] = ProjectBaseEffect(pair.Value.CloObjectEffects);

        var templates = new Dictionary<uint, ChargenClothingPaletteTemplate>(
            table.ClothingSubPalEffects.Count);
        foreach (var pair in table.ClothingSubPalEffects)
            templates[pair.Key] = ProjectPaletteTemplate(pair.Value.CloSubPalettes);

        return new ChargenClothingTable(
            baseEffects.ToFrozenDictionary(),
            templates.ToFrozenDictionary());
    }

    private static ChargenClothingBaseEffect ProjectBaseEffect(
        IReadOnlyList<DatCloObjectEffect> objectEffects)
    {
        var partChanges = new List<ChargenAnimPartChange>(objectEffects.Count);
        var textureChanges = new List<ChargenTextureChange>();
        foreach (DatCloObjectEffect effect in objectEffects)
        {
            var partIndex = (byte)effect.Index;
            partChanges.Add(new ChargenAnimPartChange(partIndex, effect.ModelId.DataId));
            foreach (var tex in effect.CloTextureEffects)
            {
                textureChanges.Add(new ChargenTextureChange(
                    partIndex, tex.OldTexture.DataId, tex.NewTexture.DataId));
            }
        }
        return new ChargenClothingBaseEffect(
            Array.AsReadOnly(partChanges.ToArray()),
            Array.AsReadOnly(textureChanges.ToArray()));
    }

    private static ChargenClothingPaletteTemplate ProjectPaletteTemplate(
        IReadOnlyList<DatCloSubPalette> subPalettes)
    {
        var choices = new ChargenClothingSubPaletteChoice[subPalettes.Count];
        for (int i = 0; i < subPalettes.Count; i++)
        {
            DatCloSubPalette sub = subPalettes[i];
            var ranges = new ChargenClothingSubPaletteRange[sub.Ranges.Count];
            for (int j = 0; j < sub.Ranges.Count; j++)
                ranges[j] = new ChargenClothingSubPaletteRange(sub.Ranges[j].Offset, sub.Ranges[j].NumColors);
            choices[i] = new ChargenClothingSubPaletteChoice(sub.PaletteSet.DataId, Array.AsReadOnly(ranges));
        }
        return new ChargenClothingPaletteTemplate(Array.AsReadOnly(choices));
    }
}
