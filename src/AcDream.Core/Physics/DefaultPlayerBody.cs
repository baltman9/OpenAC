namespace AcDream.Core.Physics;

/// <summary>
/// How wide and how tall an ordinary person on two legs is, in metres. This is
/// the body a client sweeps the local character as, and the shape a creature
/// is settled at when its own authored shape is not to hand.
///
/// One pair of numbers, in the lowest layer every caller can reach, because
/// the movement controller, the body armed for a creature, the drive that
/// puts the character into the world, the navigation grid it walks on and the
/// diagnostic overlay all have to use the SAME body. They were eight separate
/// literals: changing one of them would have moved the character through the
/// world at a different size from the grid its route was planned on, and
/// nothing would have said so.
/// </summary>
public static class DefaultPlayerBody
{
    /// <summary>Half the character's width, in metres.</summary>
    public const float Radius = 0.48f;

    /// <summary>The character's full height, in metres.</summary>
    public const float Height = 1.835f;
}
