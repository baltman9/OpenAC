using AcDream.Content;
using AcDream.Core.Textures;
using AcDream.UI.Abstractions.Panels.Settings;
using BCnEncoder.Decoder;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using Chorizite.Core.Render.Enums;

namespace AcDream.App.Rendering.Wb;

/// <summary>
/// The texture-detail choice the world runs with, fixed when the world
/// starts: the landscape and environment scales from the two Config rows
/// (or Potato Mode's lowest values). Object textures are reduced here at
/// upload time before they enter a texture array; the terrain atlas reduces
/// its own layers with <see cref="LandscapeSize"/>. Texture merges and UI or
/// particle textures are not reduced.
/// </summary>
internal sealed record WorldTextureDetail(ImageScale Landscape, ImageScale Environment)
{
    public static WorldTextureDetail Full { get; } = new(ImageScale.Full, ImageScale.Full);

    public static WorldTextureDetail FromDisplay(DisplaySettings display)
    {
        ArgumentNullException.ThrowIfNull(display);
        return new WorldTextureDetail(
            TextureDetailScale.FromOption(display.LandscapeTextureDetail),
            TextureDetailScale.FromOption(display.EnvironmentTextureDetail));
    }

    public bool ReducesAnything => Landscape != ImageScale.Full || Environment != ImageScale.Full;

    /// <summary>One side of a landscape texture at this detail.</summary>
    public int LandscapeSize(int size) =>
        TextureDetailScale.Reduce(size, TextureDetailScale.LandscapeShift(Landscape));

    /// <summary>One side of an environment texture at this detail.</summary>
    public int EnvironmentSize(int size) =>
        TextureDetailScale.Reduce(size, TextureDetailScale.EnvironmentShift(Environment));

    /// <summary>A mesh texture ready for its texture array: the array key, the layer bytes, and the upload descriptor.</summary>
    internal readonly record struct ReducedTexture(
        (int Width, int Height, TextureFormat Format) Format,
        byte[] Data,
        UploadPixelFormat? PixelFormat,
        UploadPixelType? PixelType);

    /// <summary>
    /// Reduces one mesh texture to the environment scale. Uncompressed layers
    /// are box-averaged in place; compressed layers are decoded, averaged and
    /// encoded again in their own format so the array keeps its memory
    /// advantage. Anything this detail does not shrink comes back untouched.
    /// </summary>
    internal ReducedTexture ReduceEnvironment(
        (int Width, int Height, TextureFormat Format) format,
        byte[] data,
        UploadPixelFormat? pixelFormat,
        UploadPixelType? pixelType)
    {
        ArgumentNullException.ThrowIfNull(data);
        var unchanged = new ReducedTexture(format, data, pixelFormat, pixelType);
        if (Environment == ImageScale.Full)
            return unchanged;

        int width = EnvironmentSize(format.Width);
        int height = EnvironmentSize(format.Height);
        if (width == format.Width && height == format.Height)
            return unchanged;

        switch (format.Format)
        {
            case TextureFormat.RGBA8:
                return new ReducedTexture(
                    (width, height, format.Format),
                    TexturePixels.DownsampleBox(data, format.Width, format.Height, 4, width, height),
                    pixelFormat,
                    pixelType);
            case TextureFormat.RGB8:
                return new ReducedTexture(
                    (width, height, format.Format),
                    TexturePixels.DownsampleBox(data, format.Width, format.Height, 3, width, height),
                    pixelFormat,
                    pixelType);
            case TextureFormat.A8:
                return new ReducedTexture(
                    (width, height, format.Format),
                    TexturePixels.DownsampleBox(data, format.Width, format.Height, 1, width, height),
                    pixelFormat,
                    pixelType);
            case TextureFormat.DXT1:
            case TextureFormat.DXT3:
            case TextureFormat.DXT5:
            {
                // Block formats need whole 4x4 blocks; a side that would fall
                // below that keeps four texels, which the reduce floor
                // already guarantees for any source of at least four.
                CompressionFormat compression = format.Format switch
                {
                    TextureFormat.DXT1 => CompressionFormat.Bc1,
                    TextureFormat.DXT3 => CompressionFormat.Bc2,
                    _ => CompressionFormat.Bc3,
                };
                ColorRgba32[] pixels = new BcDecoder().DecodeRaw(
                    data,
                    format.Width,
                    format.Height,
                    compression);
                var rgba = new byte[pixels.Length * 4];
                for (int i = 0; i < pixels.Length; i++)
                {
                    rgba[i * 4] = pixels[i].r;
                    rgba[i * 4 + 1] = pixels[i].g;
                    rgba[i * 4 + 2] = pixels[i].b;
                    rgba[i * 4 + 3] = pixels[i].a;
                }
                byte[] reduced = TexturePixels.DownsampleBox(
                    rgba, format.Width, format.Height, 4, width, height);
                var encoder = new BcEncoder(compression);
                encoder.OutputOptions.GenerateMipMaps = false;
                byte[] encoded = encoder.EncodeToRawBytes(
                    reduced,
                    width,
                    height,
                    PixelFormat.Rgba32,
                    0,
                    out int encodedWidth,
                    out int encodedHeight);
                if (encodedWidth != width || encodedHeight != height)
                {
                    throw new InvalidOperationException(
                        $"Encoding a {width}x{height} {format.Format} layer produced {encodedWidth}x{encodedHeight}.");
                }
                return new ReducedTexture((width, height, format.Format), encoded, null, null);
            }
            default:
                return unchanged;
        }
    }
}
