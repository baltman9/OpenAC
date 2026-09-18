using System.Collections.Frozen;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json.Serialization;

namespace AcDream.App.UI.Layout;

public enum UiPropertyKind : byte
{
    Enum,
    Bool,
    DataId,
    Float,
    Integer,
    StringInfo,
    Color,
    Array,
    Struct,
    Vector,
    Bitfield32,
    Bitfield64,
    InstanceId,
}

public readonly record struct UiColorValue(byte Blue, byte Green, byte Red, byte Alpha);

public readonly record struct UiStringInfoValue(
    byte Token,
    uint StringId,
    uint TableId,
    byte Override,
    byte English,
    byte Comment);

public sealed class UiPropertyValue
{
    public UiPropertyKind Kind;
    public uint MasterPropertyId;
    public ulong UnsignedValue;
    public int IntegerValue;
    public float FloatValue;
    public bool BoolValue;
    public UiStringInfoValue StringInfoValue;
    public UiColorValue ColorValue;
    public Vector3 VectorValue;

    // Most authored properties are a scalar, a colour or a string: of the
    // thirty-five thousand values an interface loads, only a handful are an
    // array or a struct. Holding an empty list and an empty dictionary in
    // every one of them cost several megabytes of nothing, so both are built
    // on the first write. Reads see a shared empty view, which has no
    // mutators, so a write cannot reach one.
    private List<UiPropertyValue>? _arrayValue;
    private Dictionary<uint, UiPropertyValue>? _structValue;

    /// <summary>Members of an array property, empty for anything else.</summary>
    [JsonIgnore]
    public IReadOnlyList<UiPropertyValue> ArrayValue =>
        _arrayValue ?? (IReadOnlyList<UiPropertyValue>)Array.Empty<UiPropertyValue>();

    /// <summary>Members of a struct property, empty for anything else.</summary>
    [JsonIgnore]
    public IReadOnlyDictionary<uint, UiPropertyValue> StructValue =>
        _structValue ?? (IReadOnlyDictionary<uint, UiPropertyValue>)EmptyStruct;

    // The layout fixtures are this type serialised, so the storage keeps the
    // names the fixtures use. Reading either back gives null when nothing was
    // written, which is what keeps an untouched value free of collections.
    [JsonInclude]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("ArrayValue")]
    internal List<UiPropertyValue>? SerializedArrayValue
    {
        get => _arrayValue;
        set => _arrayValue = value;
    }

    [JsonInclude]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("StructValue")]
    internal Dictionary<uint, UiPropertyValue>? SerializedStructValue
    {
        get => _structValue;
        set => _structValue = value;
    }

    /// <summary>Appends an array member, building the array on first use.</summary>
    public void AddArrayItem(UiPropertyValue item)
    {
        ArgumentNullException.ThrowIfNull(item);
        (_arrayValue ??= []).Add(item);
    }

    /// <summary>Sets a struct member, building the struct on first use.</summary>
    public void SetStructMember(uint key, UiPropertyValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        (_structValue ??= [])[key] = value;
    }

    /// <summary>Whether this value has built an array or a struct at all.
    /// Diagnostic: nothing in the interface should depend on it.</summary>
    internal bool HasArrayStorage => _arrayValue is not null;

    internal bool HasStructStorage => _structValue is not null;

    // Frozen, not a plain dictionary: this one instance stands in for every
    // value that never wrote a struct member, so a cast back to a mutable
    // dictionary would give one caller a handle on every empty value in the
    // interface at once.
    private static readonly FrozenDictionary<uint, UiPropertyValue> EmptyStruct =
        FrozenDictionary<uint, UiPropertyValue>.Empty;

    public UiPropertyValue Clone()
    {
        var clone = new UiPropertyValue
        {
            Kind = Kind,
            MasterPropertyId = MasterPropertyId,
            UnsignedValue = UnsignedValue,
            IntegerValue = IntegerValue,
            FloatValue = FloatValue,
            BoolValue = BoolValue,
            StringInfoValue = StringInfoValue,
            ColorValue = ColorValue,
            VectorValue = VectorValue,
        };

        if (_arrayValue is { Count: > 0 } items)
        {
            for (int i = 0; i < items.Count; i++)
                clone.AddArrayItem(items[i].Clone());
        }

        if (_structValue is { Count: > 0 } members)
        {
            foreach (var (key, value) in members)
                clone.SetStructMember(key, value.Clone());
        }

        return clone;
    }
}

public sealed class UiPropertyBag
{
    // Frozen for the same reason as the value's empty struct above.
    private static readonly FrozenDictionary<uint, UiPropertyValue> Empty =
        FrozenDictionary<uint, UiPropertyValue>.Empty;

    // Built on the first write, for the same reason as the value's array and
    // struct above: an element that overrides nothing carries no dictionary.
    private Dictionary<uint, UiPropertyValue>? _values;

    /// <summary>The properties this bag carries, empty when it carries none.</summary>
    [JsonIgnore]
    public IReadOnlyDictionary<uint, UiPropertyValue> Values =>
        _values ?? (IReadOnlyDictionary<uint, UiPropertyValue>)Empty;

    // The layout fixtures are this type serialised; the storage keeps the name
    // they use.
    [JsonInclude]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("Values")]
    internal Dictionary<uint, UiPropertyValue>? SerializedValues
    {
        get => _values;
        set => _values = value;
    }

    /// <summary>Whether this bag has built its dictionary at all. Diagnostic:
    /// nothing in the interface should depend on it.</summary>
    internal bool HasStorage => _values is not null;

    /// <summary>Sets one property, building the dictionary on first use.</summary>
    public void Set(uint id, UiPropertyValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        (_values ??= [])[id] = value;
    }

    public bool TryGetValue(uint id, out UiPropertyValue value)
    {
        if (_values is not null)
            return _values.TryGetValue(id, out value!);
        value = null!;
        return false;
    }

    public UiPropertyBag Clone()
    {
        var clone = new UiPropertyBag();
        if (_values is { Count: > 0 } values)
        {
            foreach (var (key, value) in values)
                clone.Set(key, value.Clone());
        }
        return clone;
    }

    public static UiPropertyBag Merge(UiPropertyBag baseProperties, UiPropertyBag derivedProperties)
    {
        ArgumentNullException.ThrowIfNull(baseProperties);
        ArgumentNullException.ThrowIfNull(derivedProperties);
        UiPropertyBag merged = baseProperties.Clone();
        foreach (var (key, value) in derivedProperties.Values)
            merged.Set(key, value.Clone());
        return merged;
    }
}

public readonly record struct UiImageMedia(uint File, int DrawMode);

public sealed record UiLoopingImageAnimation(uint[] Frames, float Duration, int DrawMode)
{
    public uint Sample(double elapsedSeconds)
    {
        if (Frames.Length == 0 || !float.IsFinite(Duration) || Duration <= 0f)
            return 0u;
        double time = double.IsFinite(elapsedSeconds) ? Math.Max(0d, elapsedSeconds) : 0d;
        float phase = (float)(time % Duration / Duration);
        return Frames[(int)(phase * (double)(Frames.Length - 1) + 0.5d)];
    }
}

/// <summary>What one entry of a state's media sequence does.</summary>
public enum UiMediaStepKind
{
    /// <summary>Anything we do not act on (sound, movie, message, ...).</summary>
    Other,

    /// <summary>Show this image.</summary>
    Image,

    Pause,

    /// <summary>Branch to another entry — what makes a sequence loop.</summary>
    Jump,

    /// <summary>Hand the element to another STATE when the sequence ends.</summary>
    State,
}

public readonly record struct UiMediaStep(
    UiMediaStepKind Kind,
    uint File,
    int DrawMode,
    float MinDuration,
    float MaxDuration,
    uint JumpIndex,
    float Probability,
    int RawType = 0);

public sealed class UiStateInfo
{
    public const uint DirectStateId = uint.MaxValue;

    public uint Id;
    public string Name = "";
    public bool PassToChildren;
    public uint IncorporationFlags;
    public UiImageMedia? Image;
    public UiLoopingImageAnimation? LoopingAnimation;

    public IReadOnlyList<UiMediaStep> MediaSteps = Array.Empty<UiMediaStep>();
    public UiCursorMedia? Cursor;
    public UiPropertyBag Properties = new();

    public int MediaCount;

    public int ImageMediaCount;

    public UiStateInfo Clone()
        => new()
        {
            Id = Id,
            Name = Name,
            PassToChildren = PassToChildren,
            IncorporationFlags = IncorporationFlags,
            Image = Image,
            LoopingAnimation = LoopingAnimation,
            MediaSteps = MediaSteps,
            Cursor = Cursor,
            Properties = Properties.Clone(),
            MediaCount = MediaCount,
            ImageMediaCount = ImageMediaCount,
        };

    public static UiStateInfo Merge(UiStateInfo baseState, UiStateInfo derivedState)
        => new()
        {
            Id = derivedState.Id,
            Name = derivedState.Name,
            PassToChildren = derivedState.PassToChildren,
            IncorporationFlags = derivedState.IncorporationFlags,
            Image = derivedState.Image ?? baseState.Image,
            LoopingAnimation = derivedState.LoopingAnimation ?? baseState.LoopingAnimation,
            Cursor = derivedState.Cursor ?? baseState.Cursor,
            Properties = UiPropertyBag.Merge(baseState.Properties, derivedState.Properties),
            MediaCount = baseState.MediaCount + derivedState.MediaCount,
            ImageMediaCount = baseState.ImageMediaCount + derivedState.ImageMediaCount,
        };
}
