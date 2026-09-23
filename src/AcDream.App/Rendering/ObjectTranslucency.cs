namespace AcDream.App.Rendering;

/// <summary>
/// How see-through a live object is drawn. An object can arrive already
/// translucent -- a phantom weapon, a shade -- and that value is its floor:
/// a translucency asked for later, such as the camera fading the player out
/// up close, raises it but never takes the object below it.
/// </summary>
internal static class ObjectTranslucency
{
    /// <param name="requested">The translucency asked for now, 0 when none.</param>
    /// <param name="original">The object's own translucency from its creation, when it has one.</param>
    internal static float Effective(float requested, float? original)
    {
        float floor = original is { } value && float.IsFinite(value) ? value : 0f;
        return requested < floor ? floor : requested;
    }
}

/// <summary>
/// The per-object translucency the world draw asks for: the camera's fade
/// of the player up close, raised to each object's own translucency. A
/// held-object copy in the examine view answers for the object it copies.
/// </summary>
internal sealed class LiveObjectTranslucency(
    Func<uint> playerGuid,
    Func<float> playerFade,
    Func<uint, float?> ownTranslucency,
    Func<uint, uint?> standInFor)
{
    public float For(uint serverGuid)
    {
        if (serverGuid == 0u)
            return 0f;
        float requested = serverGuid == playerGuid() ? playerFade() : 0f;
        uint source = standInFor(serverGuid) ?? serverGuid;
        return ObjectTranslucency.Effective(requested, ownTranslucency(source));
    }
}
