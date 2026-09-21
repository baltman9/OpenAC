using System.Collections.Generic;
using System.Numerics;
using AcDream.Content;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;

namespace AcDream.App.Interaction;

/// <summary>
/// How tall a model is in its own frame, read from the installed data files:
/// the highest point of any part's drawing sphere with the parts placed at
/// rest. It is the third rung of the label height chain, for a thing the
/// physics owner has no body for and whose author gave it no selection
/// sphere -- a sign, a chest, a pile of coins.
///
/// <para>Spheres rather than vertices, because the question is "where is the
/// top" to within a few centimetres and reading every part's geometry to
/// answer it would cost as much as drawing the thing. The answer is cached
/// per model, misses included, since a model with no drawable parts stays
/// that way for the session.</para>
/// </summary>
internal sealed class SetupModelHeightResolver
{
    private const int MaximumPartDepth = 50;

    private readonly IDatReaderWriter _dats;
    private readonly object _datLock;
    private readonly Dictionary<uint, float?> _cache = [];

    internal SetupModelHeightResolver(IDatReaderWriter dats, object datLock)
    {
        ArgumentNullException.ThrowIfNull(dats);
        ArgumentNullException.ThrowIfNull(datLock);
        _dats = dats;
        _datLock = datLock;
    }

    /// <summary>
    /// The top of the model in its own metres, unscaled, or null when the id
    /// names nothing drawable.
    /// </summary>
    internal float? Resolve(uint id)
    {
        lock (_datLock)
        {
            if (_cache.TryGetValue(id, out float? cached))
                return cached;
            float top = float.NegativeInfinity;
            CollectTop(id, Matrix4x4.Identity, ref top, depth: 0);
            float? answer = float.IsFinite(top) && top > 0f ? top : null;
            _cache[id] = answer;
            return answer;
        }
    }

    private void CollectTop(uint id, Matrix4x4 placement, ref float top, int depth)
    {
        if (depth > MaximumPartDepth
            || !_dats.TryResolvePreferred(id, out IDatDatabase? db, out DBObjType type))
        {
            return;
        }

        if (type == DBObjType.GfxObj)
        {
            if (!db.TryGet<GfxObj>(id, out GfxObj? gfxObj)
                || gfxObj.DrawingBSP?.Root?.BoundingSphere is not { } sphere)
            {
                return;
            }
            Vector3 center = Vector3.Transform(sphere.Origin, placement);
            top = MathF.Max(top, center.Z + sphere.Radius);
            return;
        }

        if (type != DBObjType.Setup || !db.TryGet<Setup>(id, out Setup? setup))
            return;

        // The same rest pose the drawn model is built from: resting first,
        // then the default, then whatever the author listed first.
        if (!setup.PlacementFrames.TryGetValue(Placement.Resting, out var frame)
            && !setup.PlacementFrames.TryGetValue(Placement.Default, out frame))
        {
            frame = setup.PlacementFrames.Values.FirstOrDefault();
        }
        if (frame is null)
            return;

        for (int index = 0; index < setup.Parts.Count; index++)
        {
            Matrix4x4 partPlacement = Matrix4x4.Identity;
            if (setup.Flags.HasFlag(SetupFlags.HasDefaultScale)
                && setup.DefaultScale.Count > index)
            {
                partPlacement *= Matrix4x4.CreateScale(setup.DefaultScale[index]);
            }
            if (frame.Frames is not null && index < frame.Frames.Count)
            {
                var orientation = new Quaternion(
                    (float)frame.Frames[index].Orientation.X,
                    (float)frame.Frames[index].Orientation.Y,
                    (float)frame.Frames[index].Orientation.Z,
                    (float)frame.Frames[index].Orientation.W);
                partPlacement *= Matrix4x4.CreateFromQuaternion(orientation)
                    * Matrix4x4.CreateTranslation(frame.Frames[index].Origin);
            }
            CollectTop(setup.Parts[index], partPlacement * placement, ref top, depth + 1);
        }
    }
}
