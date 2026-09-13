using AcDream.App.Rendering.Wb;
using AcDream.Content;
using AcDream.Core.Textures;
using AcDream.UI.Abstractions.Panels.Settings;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using Chorizite.Core.Render.Enums;

namespace AcDream.App.Tests.Rendering.Wb;

public sealed class WorldTextureDetailTests
{
    [Fact]
    public void FromDisplay_DefaultsKeepFullDetail_AndPotatoTakesTheLowest()
    {
        Assert.Equal(WorldTextureDetail.Full, WorldTextureDetail.FromDisplay(DisplaySettings.Default));
        Assert.False(WorldTextureDetail.Full.ReducesAnything);

        WorldTextureDetail potato = WorldTextureDetail.FromDisplay(
            (DisplaySettings.Default with { PotatoMode = true }).Effective);

        Assert.Equal(ImageScale.Eighth, potato.Landscape);
        Assert.Equal(ImageScale.Eighth, potato.Environment);
        Assert.True(potato.ReducesAnything);
        Assert.Equal(64, potato.EnvironmentSize(512));
        Assert.Equal(32, potato.LandscapeSize(512));
    }

    [Fact]
    public void ReduceEnvironment_DecodedTexture_ShrinksAtTheEnvironmentScale_AndKeepsTinyOnes()
    {
        var detail = new WorldTextureDetail(ImageScale.Full, ImageScale.Eighth);
        var pixels = new byte[128 * 64 * 4];
        Array.Fill(pixels, (byte)33);
        var decoded = new DecodedTexture(pixels, 128, 64);

        DecodedTexture reduced = detail.ReduceEnvironment(decoded);

        Assert.Equal(16, reduced.Width);
        Assert.Equal(8, reduced.Height);
        Assert.Equal(16 * 8 * 4, reduced.Rgba8.Length);
        Assert.All(reduced.Rgba8, value => Assert.Equal(33, value));
        Assert.Same(DecodedTexture.Magenta, detail.ReduceEnvironment(DecodedTexture.Magenta));
        Assert.Same(decoded, WorldTextureDetail.Full.ReduceEnvironment(decoded));
    }

    [Fact]
    public void ReduceEnvironment_AtFullDetail_ReturnsTheSameBytes()
    {
        var data = new byte[16 * 16 * 4];
        WorldTextureDetail.ReducedTexture result = WorldTextureDetail.Full.ReduceEnvironment(
            (16, 16, TextureFormat.RGBA8), data, UploadPixelFormat.Rgba, null);

        Assert.Same(data, result.Data);
        Assert.Equal((16, 16, TextureFormat.RGBA8), result.Format);
        Assert.Equal(UploadPixelFormat.Rgba, result.PixelFormat);
    }

    [Fact]
    public void ReduceEnvironment_ShrinksAnUncompressedLayer_AndKeepsItsDescriptor()
    {
        var detail = new WorldTextureDetail(ImageScale.Full, ImageScale.Quarter);
        var data = new byte[64 * 32 * 4];
        Array.Fill(data, (byte)90);

        WorldTextureDetail.ReducedTexture result = detail.ReduceEnvironment(
            (64, 32, TextureFormat.RGBA8), data, UploadPixelFormat.Rgba, null);

        Assert.Equal((16, 8, TextureFormat.RGBA8), result.Format);
        Assert.Equal(16 * 8 * 4, result.Data.Length);
        Assert.All(result.Data, value => Assert.Equal(90, value));
        Assert.Equal(UploadPixelFormat.Rgba, result.PixelFormat);
    }

    [Fact]
    public void ReduceEnvironment_KeepsTheFloor_SoASmallLayerIsNotShrunkBelowEight()
    {
        var detail = new WorldTextureDetail(ImageScale.Full, ImageScale.Eighth);
        var data = new byte[8 * 8];

        WorldTextureDetail.ReducedTexture result = detail.ReduceEnvironment(
            (8, 8, TextureFormat.A8), data, UploadPixelFormat.Red, null);

        Assert.Same(data, result.Data);
        Assert.Equal((8, 8, TextureFormat.A8), result.Format);
    }

    [Fact]
    public void ReduceEnvironment_ReencodesACompressedLayerInItsOwnFormat()
    {
        // 16x16 BC1 of a single flat colour: every block is colour0 = colour1
        // = pure red in 5:6:5 (0xF800), all indices 0.
        var blocks = new byte[(16 / 4) * (16 / 4) * 8];
        for (int i = 0; i < blocks.Length; i += 8)
        {
            blocks[i] = 0x00; blocks[i + 1] = 0xF8;
            blocks[i + 2] = 0x00; blocks[i + 3] = 0xF8;
        }
        var detail = new WorldTextureDetail(ImageScale.Full, ImageScale.Half);

        WorldTextureDetail.ReducedTexture result = detail.ReduceEnvironment(
            (16, 16, TextureFormat.DXT1), blocks, null, null);

        Assert.Equal((8, 8, TextureFormat.DXT1), result.Format);
        Assert.Equal((8 / 4) * (8 / 4) * 8, result.Data.Length);
        Assert.Null(result.PixelFormat);
        ColorRgba32[] pixels = new BcDecoder().DecodeRaw(result.Data, 8, 8, CompressionFormat.Bc1);
        Assert.All(pixels, pixel =>
        {
            Assert.True(pixel.r >= 240, $"red channel {pixel.r}");
            Assert.True(pixel.g <= 8 && pixel.b <= 8, $"green {pixel.g} blue {pixel.b}");
        });
    }
}
