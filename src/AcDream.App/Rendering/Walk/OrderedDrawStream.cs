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
