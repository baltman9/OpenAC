using AcDream.Core.Textures;
using Xunit;

namespace AcDream.Core.Tests.Textures;

public sealed class TextureDetailScaleTests
{
    [Theory]
    [InlineData(-1, ImageScale.Full)]
    [InlineData(0, ImageScale.Full)]
    [InlineData(1, ImageScale.Full)]
    [InlineData(2, ImageScale.Half)]
    [InlineData(3, ImageScale.Quarter)]
    [InlineData(4, ImageScale.Eighth)]
    [InlineData(9, ImageScale.Eighth)]
    public void FromOption_MapsTheStoredValue_0And1KeepFullSize(int detail, ImageScale expected)
    {
        Assert.Equal(expected, TextureDetailScale.FromOption(detail));
    }

    [Theory]
    [InlineData(ImageScale.Full, 0, 0)]
    [InlineData(ImageScale.Half, 1, 1)]
    [InlineData(ImageScale.Quarter, 2, 2)]
    [InlineData(ImageScale.Eighth, 3, 4)]
    public void Shifts_EnvironmentIsTheScale_LandscapeUsesTheWiderTable(
        ImageScale scale, int environment, int landscape)
    {
        Assert.Equal(environment, TextureDetailScale.EnvironmentShift(scale));
        Assert.Equal(landscape, TextureDetailScale.LandscapeShift(scale));
    }

    [Theory]
    [InlineData(512, 0, 512)]
    [InlineData(512, 3, 64)]
    [InlineData(512, 4, 32)]
    [InlineData(32, 3, 8)]   // 32 >> 3 = 4 is below the floor; the floor is 8
    [InlineData(64, 3, 8)]
    [InlineData(4, 3, 4)]    // a source smaller than the floor keeps its size
    [InlineData(12, 1, 8)]   // 12 >> 1 = 6 rises to the floor
    [InlineData(1, 8, 1)]
    public void Reduce_ShiftsAndKeepsTheFloor(int size, int shift, int expected)
    {
        Assert.Equal(expected, TextureDetailScale.Reduce(size, shift));
    }

    [Fact]
    public void Reduce_RejectsNonPositiveSizes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TextureDetailScale.Reduce(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextureDetailScale.Reduce(8, -1));
    }
}

public sealed class TexturePixelsTests
{
    [Fact]
    public void DownsampleBox_AveragesEachSourceBox()
    {
        // 4x4 single-channel checkerboard of 0 and 200: every 2x2 box averages to 100.
        var source = new byte[16];
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
                source[y * 4 + x] = (byte)(((x + y) % 2 == 0) ? 0 : 200);

        byte[] result = TexturePixels.DownsampleBox(source, 4, 4, 1, 2, 2);

        Assert.Equal(new byte[] { 100, 100, 100, 100 }, result);
    }

    [Fact]
    public void DownsampleBox_KeepsChannelsSeparate()
    {
        // 2x2 RGBA: red rows on top, blue on the bottom -> one purple-ish pixel.
        byte[] source =
        [
            255, 0, 0, 255,   255, 0, 0, 255,
            0, 0, 255, 255,   0, 0, 255, 255,
        ];

        byte[] result = TexturePixels.DownsampleBox(source, 2, 2, 4, 1, 1);

        Assert.Equal(new byte[] { 128, 0, 128, 255 }, result);
    }

    [Fact]
    public void DownsampleBox_CoversEverySourcePixelWhenSizesDoNotDivide()
    {
        // 12 wide to 8 wide: boxes of 1 or 2 pixels; a constant image stays constant.
        var source = new byte[12 * 1 * 3];
        Array.Fill(source, (byte)77);

        byte[] result = TexturePixels.DownsampleBox(source, 12, 1, 3, 8, 1);

        Assert.Equal(8 * 3, result.Length);
        Assert.All(result, value => Assert.Equal(77, value));
    }

    [Fact]
    public void DownsampleBox_SameSizeCopies_AndLargerIsRefused()
    {
        byte[] source = [1, 2, 3, 4];

        byte[] same = TexturePixels.DownsampleBox(source, 2, 2, 1, 2, 2);
        Assert.Equal(source, same);
        Assert.NotSame(source, same);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TexturePixels.DownsampleBox(source, 2, 2, 1, 4, 2));
        Assert.Throws<ArgumentException>(
            () => TexturePixels.DownsampleBox(source, 2, 2, 2, 1, 1));
    }
}
