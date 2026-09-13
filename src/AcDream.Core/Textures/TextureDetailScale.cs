namespace AcDream.Core.Textures;

/// <summary>
/// How far a texture is reduced from its source size: the four steps the
/// texture-detail options choose between.
/// </summary>
public enum ImageScale
{
    Full = 0,
    Half = 1,
    Quarter = 2,
    Eighth = 3,
}

/// <summary>
/// The texture-detail options' effect on texture size. The two Config rows
/// store 0..4 where 0 is the highest detail and 4 the lowest: 0 and 1 keep the
/// source size, 2 halves it, 3 quarters it, 4 takes an eighth. Environment
/// (object) textures shift by the scale directly; landscape textures use the
/// wider shift table, so their lowest step is a sixteenth. A reduced texture
/// never goes below <see cref="MinimumSize"/> on a side unless the source is
/// already smaller. See the research note on texture detail for the source.
/// </summary>
public static class TextureDetailScale
{
    /// <summary>The smallest side a reduced texture keeps, when the source has it.</summary>
    public const int MinimumSize = 8;

    // Indexed by ImageScale; one entry wider than the enum on purpose, matching
    // the source table.
    private static readonly int[] LandscapeShiftTable = [0, 1, 2, 4, 8];

    /// <summary>The scale a stored texture-detail option value selects.</summary>
    public static ImageScale FromOption(int detail) =>
        detail <= 0
            ? ImageScale.Full
            : (ImageScale)Math.Min((int)ImageScale.Eighth, detail - 1);

    /// <summary>Bits an environment (object, building, creature) texture side shifts right by.</summary>
    public static int EnvironmentShift(ImageScale scale) => (int)scale;

    /// <summary>Bits a landscape texture side shifts right by.</summary>
    public static int LandscapeShift(ImageScale scale) => LandscapeShiftTable[(int)scale];

    /// <summary>
    /// One texture side after the shift: never below <see cref="MinimumSize"/>
    /// unless the source side is smaller, and never below 1.
    /// </summary>
    public static int Reduce(int size, int shift)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(shift);
        if (shift == 0)
            return size;
        int reduced = size >> shift;
        if (reduced < MinimumSize)
            reduced = Math.Min(MinimumSize, size);
        return Math.Max(1, reduced);
    }
}
