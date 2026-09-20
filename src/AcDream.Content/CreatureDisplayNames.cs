using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;

namespace AcDream.Content;

/// <summary>
/// The display name for a creature kind, read from the installed data files.
/// The table is a chain of enum mappings, each naming the one it extends, so
/// a name the first file carries wins over the one it was layered on.
/// </summary>
/// <remarks>
/// This lives beside the other content catalogues rather than beside anything
/// that draws, because both clients need it: a bot asking what it is looking
/// at and a window putting a caption on a panel are the same question.
/// </remarks>
public sealed class CreatureDisplayNameResolver
{
    /// <summary>The first mapping in the chain.</summary>
    public const uint MapperDid = 0x2200000Eu;

    private readonly IReadOnlyDictionary<uint, string> _names;

    public CreatureDisplayNameResolver(
        IReadOnlyDictionary<uint, string> names)
    {
        _names = names ?? throw new ArgumentNullException(nameof(names));
    }

    /// <summary>A resolver that names nothing, for a client without content.</summary>
    public static CreatureDisplayNameResolver Empty { get; } =
        new(new Dictionary<uint, string>());

    public static CreatureDisplayNameResolver Load(IDatReaderWriter dats)
    {
        ArgumentNullException.ThrowIfNull(dats);
        var names = new Dictionary<uint, string>();
        var visited = new HashSet<uint>();
        uint did = MapperDid;
        while (did != 0u && visited.Add(did))
        {
            EnumMapper? mapper = dats.Get<EnumMapper>(did);
            if (mapper is null)
                break;
            foreach ((uint id, PStringBase<byte> text) in mapper.IdToStringMap)
                names.TryAdd(id, text.Value.Replace('_', ' '));
            did = mapper.BaseEnumMap;
        }
        return new CreatureDisplayNameResolver(names);
    }

    public string Resolve(int creatureType)
        => creatureType > 0
            && _names.TryGetValue((uint)creatureType, out string? name)
                ? name
                : string.Empty;
}
