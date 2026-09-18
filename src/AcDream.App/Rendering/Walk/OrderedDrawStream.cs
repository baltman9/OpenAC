using System.Numerics;
using System.Runtime.InteropServices;
using AcDream.App.Rendering.Wb;

namespace AcDream.App.Rendering.Walk;

internal enum WalkDrawStage : byte
{
    Terrain,

    OutdoorStatic,

    CellStatic,

    BuildingShell,

    PortalPunch,

    LookInStatic,

    /// <summary>Every non-static draw the walk visits last: entities,
    /// monsters, items, and the meshes particles ride.</summary>
    Dynamic,
}

internal readonly record struct OrderedDrawCommand(
    GroupKey Key,
    Matrix4x4 Transform,
    WalkDrawStage Stage,
    uint CellId,
    uint ClipSlot,
    WbDrawDispatcher.InstanceLightSet Lights,
    uint IndoorFlag,
    float Alpha,
    Vector2 SelectionLighting,
    uint DetailCategory,
    bool AllowInstanceMerge = false);

/// <summary>
/// A run of ordered draw commands built once and appended to the frame's
/// stream many times.
///
/// A producer that already knows its commands cannot change -- the
/// far-landscape cache holds classified batches until their records are
/// written -- rebuilds this once and then hands whole runs of it to the
/// stream, instead of building and appending a command at a time. The stream
/// receives exactly the bytes the per-command path produced; the block is a
/// faster way to say the same thing, not a different thing to say.
/// </summary>
internal sealed class OrderedDrawCommandBlock
{
    public GroupKey[] Keys = [];
    public Matrix4x4[] Transforms = [];
    public WalkDrawStage[] Stages = [];
    public uint[] CellIds = [];
    public uint[] ClipSlots = [];
    public WbDrawDispatcher.InstanceLightSet[] Lights = [];
    public uint[] IndoorFlags = [];
    public float[] Alphas = [];
    public Vector2[] SelectionLighting = [];
    public uint[] DetailCategories = [];
    public bool[] AllowInstanceMerges = [];

    /// <summary>How many leading slots carry a command.</summary>
    public int Count;

    public void EnsureCapacity(int capacity)
    {
        if (Keys.Length >= capacity)
            return;

        int grown = Math.Max(capacity, Math.Max(16, Keys.Length * 2));
        Array.Resize(ref Keys, grown);
        Array.Resize(ref Transforms, grown);
        Array.Resize(ref Stages, grown);
        Array.Resize(ref CellIds, grown);
        Array.Resize(ref ClipSlots, grown);
        Array.Resize(ref Lights, grown);
        Array.Resize(ref IndoorFlags, grown);
        Array.Resize(ref Alphas, grown);
        Array.Resize(ref SelectionLighting, grown);
        Array.Resize(ref DetailCategories, grown);
        Array.Resize(ref AllowInstanceMerges, grown);
    }

    public void Set(int index, in OrderedDrawCommand command)
    {
        Keys[index] = command.Key;
        Transforms[index] = command.Transform;
        Stages[index] = command.Stage;
        CellIds[index] = command.CellId;
        ClipSlots[index] = command.ClipSlot;
        Lights[index] = command.Lights;
        IndoorFlags[index] = command.IndoorFlag;
        Alphas[index] = command.Alpha;
        SelectionLighting[index] = command.SelectionLighting;
        DetailCategories[index] = command.DetailCategory;
        AllowInstanceMerges[index] = command.AllowInstanceMerge;
    }
}

internal sealed class OrderedDrawStream
{
    public readonly List<GroupKey> Keys = new();
    public readonly List<Matrix4x4> Transforms = new();
    public readonly List<WalkDrawStage> Stages = new();
    public readonly List<uint> CellIds = new();
    public readonly List<uint> ClipSlots = new();
    public readonly List<WbDrawDispatcher.InstanceLightSet> Lights = new();
    public readonly List<uint> IndoorFlags = new();
    public readonly List<float> Alphas = new();
    public readonly List<Vector2> SelectionLighting = new();
    public readonly List<uint> DetailCategories = new();
    public readonly List<bool> AllowInstanceMerges = new();

    public int Count => Keys.Count;

    // Contiguous views for the upload path, which sends each array to the GPU
    // as one block rather than walking the stream command by command.
    public ReadOnlySpan<Matrix4x4> TransformSpan =>
        CollectionsMarshal.AsSpan(Transforms);

    public ReadOnlySpan<uint> ClipSlotSpan => CollectionsMarshal.AsSpan(ClipSlots);

    public ReadOnlySpan<WbDrawDispatcher.InstanceLightSet> LightSpan =>
        CollectionsMarshal.AsSpan(Lights);

    public ReadOnlySpan<uint> IndoorFlagSpan => CollectionsMarshal.AsSpan(IndoorFlags);

    public ReadOnlySpan<float> AlphaSpan => CollectionsMarshal.AsSpan(Alphas);

    public ReadOnlySpan<Vector2> SelectionLightingSpan =>
        CollectionsMarshal.AsSpan(SelectionLighting);

    public ReadOnlySpan<uint> DetailCategorySpan =>
        CollectionsMarshal.AsSpan(DetailCategories);

    public void Append(in OrderedDrawCommand command)
    {
        Keys.Add(command.Key);
        Transforms.Add(command.Transform);
        Stages.Add(command.Stage);
        CellIds.Add(command.CellId);
        ClipSlots.Add(command.ClipSlot);
        Lights.Add(command.Lights);
        IndoorFlags.Add(command.IndoorFlag);
        Alphas.Add(command.Alpha);
        SelectionLighting.Add(command.SelectionLighting);
        DetailCategories.Add(command.DetailCategory);
        AllowInstanceMerges.Add(command.AllowInstanceMerge);
    }

    /// <summary>Appends <paramref name="count"/> commands of a prebuilt block
    /// starting at <paramref name="start"/>, in block order.</summary>
    public void AppendRange(OrderedDrawCommandBlock block, int start, int count)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (start > block.Count - count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(count),
                "An ordered-draw block range must stay inside the block.");
        }
        if (count == 0)
            return;

        Keys.AddRange(block.Keys.AsSpan(start, count));
        Transforms.AddRange(block.Transforms.AsSpan(start, count));
        Stages.AddRange(block.Stages.AsSpan(start, count));
        CellIds.AddRange(block.CellIds.AsSpan(start, count));
        ClipSlots.AddRange(block.ClipSlots.AsSpan(start, count));
        Lights.AddRange(block.Lights.AsSpan(start, count));
        IndoorFlags.AddRange(block.IndoorFlags.AsSpan(start, count));
        Alphas.AddRange(block.Alphas.AsSpan(start, count));
        SelectionLighting.AddRange(block.SelectionLighting.AsSpan(start, count));
        DetailCategories.AddRange(block.DetailCategories.AsSpan(start, count));
        AllowInstanceMerges.AddRange(block.AllowInstanceMerges.AsSpan(start, count));
    }

    public void Reset()
    {
        Keys.Clear();
        Transforms.Clear();
        Stages.Clear();
        CellIds.Clear();
        ClipSlots.Clear();
        Lights.Clear();
        IndoorFlags.Clear();
        Alphas.Clear();
        SelectionLighting.Clear();
        DetailCategories.Clear();
        AllowInstanceMerges.Clear();
    }
}
