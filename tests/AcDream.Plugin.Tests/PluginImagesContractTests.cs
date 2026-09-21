// Copyright (c) OpenAC contributors.
// Distributed under the terms of the MIT license.

using AcDream.Plugin.Abstractions;
using AcDream.Plugin.Tests.Fixtures;

namespace AcDream.Plugin.Tests;

/// <summary>
/// The image surface is additive with inert defaults: a host that never
/// heard of it answers every request with no image and holds nothing.
/// </summary>
public sealed class PluginImagesContractTests
{
    private sealed class BareRegistry : IUiRegistry
    {
        public void AddMarkupPanel(string markupPath, object binding)
        {
        }
    }

    private sealed class BareScopedRegistry : IScopedUiRegistry
    {
        public void AddMarkupPanel(string markupPath, object binding)
        {
        }

        public IDisposable RegisterMarkupPanel(string markupPath, object binding) =>
            NoOpUiRegistration.Instance;
    }

    [Fact]
    public void NoImageIsNotValid()
    {
        Assert.False(PluginImage.None.IsValid);
        Assert.Equal(0, PluginImage.None.Handle);
        Assert.True(new PluginImage(1, 4, 4).IsValid);
    }

    [Fact]
    public void ARegistryThatNeverHeardOfImagesHandsOutTheInertSurface()
    {
        IUiRegistry registry = new BareRegistry();
        IScopedUiRegistry scoped = new BareScopedRegistry();

        Assert.Same(NoOpPluginImages.Instance, registry.Images);
        Assert.Same(NoOpPluginImages.Instance, scoped.ImagesFor(new PluginUiOwner("p", "P")));
        Assert.Same(NoOpPluginImages.Instance, ((IUiRegistry)NoOpUiRegistry.Instance).Images);
    }

    [Fact]
    public void TheInertSurfaceRefusesEverythingAndHoldsNothing()
    {
        IPluginImages images = NoOpPluginImages.Instance;
        bool opened = false;

        Assert.False(images.IsAvailable);
        Assert.Equal(PluginImage.None, images.FromClientArt(0x06001234u));
        Assert.Equal(PluginImage.None, images.FromSpellIcon(1u));
        Assert.Equal(PluginImage.None, images.FromObjectIcon(0x80000001u));
        Assert.Equal(PluginImage.None, images.FromStream("map.png", () =>
        {
            opened = true;
            return new MemoryStream();
        }));
        Assert.False(opened);
        Assert.False(images.Release(new PluginImage(1, 4, 4)));
        Assert.Equal(0, images.Count);
        Assert.Equal(0, images.MaximumCount);
        Assert.Equal(0L, images.MaximumBytes);
        Assert.Equal(0, images.MaximumDimension);
    }

    [Fact]
    public void TheFakeHostAnswersInertly()
    {
        var host = new FakePluginHost();

        Assert.False(host.Ui.Images.IsAvailable);
        Assert.Equal(PluginImage.None, host.Ui.Images.FromClientArt(1u));
    }
}
