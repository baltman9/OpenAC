namespace AcDream.Core.Textures;

/// <summary>Pixel-array helpers shared by the texture reduction paths.</summary>
public static class TexturePixels
{
    /// <summary>
    /// Shrinks an interleaved 8-bit image to <paramref name="dstWidth"/> x
    /// <paramref name="dstHeight"/> by averaging the source box each output
    /// pixel covers. Boxes are integer ranges, so sizes that do not divide
    /// evenly still cover every source pixel exactly once. The destination
    /// must not be larger than the source on either side.
    /// </summary>
    public static byte[] DownsampleBox(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        int channels,
        int dstWidth,
        int dstHeight)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(dstWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(dstHeight, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dstWidth, width);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dstHeight, height);
        if (source.Length != width * height * channels)
        {
            throw new ArgumentException(
                $"Source has {source.Length} bytes; expected {width * height * channels} for "
                + $"{width}x{height} with {channels} channels.",
                nameof(source));
        }

        if (dstWidth == width && dstHeight == height)
            return source.ToArray();

        var result = new byte[dstWidth * dstHeight * channels];
        Span<int> sums = stackalloc int[channels];
        for (int dy = 0; dy < dstHeight; dy++)
        {
            int y0 = dy * height / dstHeight;
            int y1 = Math.Max(y0 + 1, (dy + 1) * height / dstHeight);
            for (int dx = 0; dx < dstWidth; dx++)
            {
                int x0 = dx * width / dstWidth;
                int x1 = Math.Max(x0 + 1, (dx + 1) * width / dstWidth);
                sums.Clear();
                for (int y = y0; y < y1; y++)
                {
                    int row = (y * width + x0) * channels;
                    for (int x = x0; x < x1; x++)
                    {
                        for (int c = 0; c < channels; c++)
                            sums[c] += source[row + c];
                        row += channels;
                    }
                }

                int count = (y1 - y0) * (x1 - x0);
                int at = (dy * dstWidth + dx) * channels;
                for (int c = 0; c < channels; c++)
                    result[at + c] = (byte)((sums[c] + count / 2) / count);
            }
        }

        return result;
    }
}
