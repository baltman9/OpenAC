using System.Linq;
using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.App.UI;
using DatReaderWriter.Types;
using Xunit;

namespace AcDream.App.Tests.UI;

/// <summary>
/// A colour attribute may name a binding instead of a literal, so a plugin
/// that lets a player pick colours can show them. These tests build the
/// markup and then read the colours that actually reach the renderer: a
/// colour that is only stored on an element is a colour nobody sees.
/// </summary>
public sealed class MarkupColorBindingTests
{
    private sealed class ColorBinding
    {
        /// <summary>Packed 0xAARRGGBB: how a plugin stores a colour.</summary>
        public uint Panel { get; set; } = 0xFF112233u;

        /// <summary>The same packing in a signed field, which is what a
        /// setting round-tripped through a settings file usually holds.</summary>
        public int Edge { get; set; } = unchecked((int)0xFF445566);

        /// <summary>The same colour in the literal text form.</summary>
        public string Accent { get; set; } = "#FF7788AA";

        public uint Bar { get; set; } = 0xFF00FF00u;

        public float Half => 0.5f;

        public bool On => true;

        /// <summary>A bool is not a colour in any accepted form.</summary>
        public bool NotAColor => true;
    }

    private sealed class NullGpuFrameSource : ICurrentGpuFrameSource
    {
        public IGpuFrame? CurrentFrame => null;
    }

    private static Vector4 Argb(uint packed) => new(
        ((packed >> 16) & 0xFFu) / 255f,
        ((packed >> 8) & 0xFFu) / 255f,
        (packed & 0xFFu) / 255f,
        ((packed >> 24) & 0xFFu) / 255f);

    private const uint FillTexture = 0u;
    private const uint GlyphTexture = 1u;

    private static UiDatFont MakeAsciiFont()
    {
        var glyphs = new Dictionary<char, FontCharDesc>();
        foreach (char c in "ABCDEFGH")
            glyphs[c] = new FontCharDesc { Unicode = c, Width = 6, Height = 8 };
        return new UiDatFont(
            fgTex: GlyphTexture, fgW: 32, fgH: 32,
            bgTex: 0, bgW: 0, bgH: 0,
            lineHeight: 16f, baselineOffset: 12f,
            glyphs);
    }

    private static UiNineSlicePanel Build(string xml, ColorBinding binding) =>
        MarkupDocument.Build(xml, binding, _ => (1u, 32, 32), null, MakeAsciiFont());

    private static List<(uint Texture, Vector4 Color)> DrawnVertices(UiElement element)
    {
        var device = new RecordingGpuDevice();
        var renderer = new TextRenderer(device, new NullGpuFrameSource(), "unused");
        renderer.Begin(new Vector2(800f, 600f));
        var ctx = new UiRenderContext(renderer, new Vector2(800f, 600f));
        element.DrawSelfAndChildren(ctx);

        var drawn = new List<(uint, Vector4)>();
        foreach ((uint texture, IReadOnlyList<float> verts) in renderer.DebugSpriteSegmentVerts)
        {
            for (int i = 0; i + 8 <= verts.Count; i += 8)
            {
                drawn.Add((
                    texture,
                    new Vector4(verts[i + 4], verts[i + 5], verts[i + 6], verts[i + 7])));
            }
        }
        return drawn;
    }

    private static bool Close(Vector4 a, Vector4 b) =>
        MathF.Abs(a.X - b.X) < 0.002f && MathF.Abs(a.Y - b.Y) < 0.002f
        && MathF.Abs(a.Z - b.Z) < 0.002f && MathF.Abs(a.W - b.W) < 0.002f;

    private static void AssertDrawnIn(UiElement element, uint texture, Vector4 expected)
    {
        List<(uint Texture, Vector4 Color)> drawn = DrawnVertices(element);
        Assert.True(
            drawn.Any(v => v.Texture == texture && Close(v.Color, expected)),
            $"expected something on texture {texture} coloured {expected}, drew "
            + string.Join(
                ", ",
                drawn.Select(v => $"{v.Texture}:{v.Color}").Distinct()));
    }

    [Fact]
    public void GroupBackground_BoundToAPackedValue_IsDrawnAndFollowsTheProperty()
    {
        const string xml = """
            <panel x="0" y="0" w="120" h="60">
              <group x="0" y="0" w="100" h="40" background="{Panel}"/>
            </panel>
            """;
        var binding = new ColorBinding();
        UiNineSlicePanel panel = Build(xml, binding);
        var group = Assert.IsType<UiPanel>(panel.Children[0]);

        AssertDrawnIn(group, FillTexture, Argb(0xFF112233u));

        binding.Panel = 0x80204060u;
        AssertDrawnIn(group, FillTexture, Argb(0x80204060u));
    }

    [Fact]
    public void GroupBorder_BoundToASignedPackedValue_IsDrawn()
    {
        const string xml = """
            <panel x="0" y="0" w="120" h="60">
              <group x="0" y="0" w="100" h="40" border="{Edge}"/>
            </panel>
            """;
        var binding = new ColorBinding();
        UiNineSlicePanel panel = Build(xml, binding);
        var group = Assert.IsType<UiPanel>(panel.Children[0]);

        AssertDrawnIn(group, FillTexture, Argb(0xFF445566u));
    }

    [Fact]
    public void ButtonBackgroundBorderAndCaption_Bound_AreAllDrawnFromTheBinding()
    {
        const string xml = """
            <panel x="0" y="0" w="120" h="60">
              <button x="0" y="0" w="90" h="24" text="ABC"
                      background="{Panel}" border="{Edge}" color="{Accent}"/>
            </panel>
            """;
        var binding = new ColorBinding();
        UiNineSlicePanel panel = Build(xml, binding);
        var button = Assert.IsType<UiSimpleButton>(panel.Children[0]);

        AssertDrawnIn(button, FillTexture, Argb(0xFF112233u));
        AssertDrawnIn(button, FillTexture, Argb(0xFF445566u));
        AssertDrawnIn(button, GlyphTexture, Argb(0xFF7788AAu));
    }

    [Fact]
    public void LabelColor_BoundToTheLiteralTextForm_IsDrawnAndFollowsTheProperty()
    {
        const string xml = """
            <panel x="0" y="0" w="120" h="60">
              <label x="0" y="0" text="ABC" color="{Accent}"/>
            </panel>
            """;
        var binding = new ColorBinding();
        UiNineSlicePanel panel = Build(xml, binding);
        var label = Assert.IsType<UiLabel>(panel.Children[0]);

        AssertDrawnIn(label, GlyphTexture, Argb(0xFF7788AAu));

        binding.Accent = "#FF010203";
        AssertDrawnIn(label, GlyphTexture, Argb(0xFF010203u));
    }

    [Fact]
    public void ToggleColor_Bound_IsDrawnOnItsCaption()
    {
        const string xml = """
            <panel x="0" y="0" w="120" h="60">
              <toggle x="0" y="0" w="100" h="18" text="ABC" checked="{On}"
                      color="{Accent}"/>
            </panel>
            """;
        var binding = new ColorBinding();
        UiNineSlicePanel panel = Build(xml, binding);
        var toggle = Assert.IsType<UiMarkupToggle>(panel.Children[0]);

        AssertDrawnIn(toggle, GlyphTexture, Argb(0xFF7788AAu));
    }

    [Fact]
    public void MeterColor_Bound_IsDrawnOnTheFilledPart()
    {
        const string xml = """
            <panel x="0" y="0" w="120" h="60">
              <meter x="0" y="0" w="100" h="12" fill="{Half}" color="{Bar}"/>
            </panel>
            """;
        var binding = new ColorBinding();
        UiNineSlicePanel panel = Build(xml, binding);
        var meter = Assert.IsType<UiMeter>(panel.Children[0]);

        AssertDrawnIn(meter, FillTexture, Argb(0xFF00FF00u));

        binding.Bar = 0xFF0000FFu;
        AssertDrawnIn(meter, FillTexture, Argb(0xFF0000FFu));
    }

    [Fact]
    public void FieldBackgroundAndTextColor_Bound_AreBothDrawn()
    {
        const string xml = """
            <panel x="0" y="0" w="160" h="60">
              <field x="0" y="0" w="120" h="20" text="ABC"
                     background="{Panel}" color="{Accent}"/>
            </panel>
            """;
        var binding = new ColorBinding();
        UiNineSlicePanel panel = Build(xml, binding);
        var field = Assert.IsType<UiField>(panel.Children[0]);

        AssertDrawnIn(field, FillTexture, Argb(0xFF112233u));
        AssertDrawnIn(field, GlyphTexture, Argb(0xFF7788AAu));
    }

    [Fact]
    public void ABoundColorIsOnTheElementBeforeTheFirstFrameIsDrawn()
    {
        const string xml = """
            <panel x="0" y="0" w="120" h="60">
              <button x="0" y="0" w="90" h="24" text="ABC"
                      background="{Panel}" border="{Edge}" color="{Accent}"/>
            </panel>
            """;
        UiNineSlicePanel panel = Build(xml, new ColorBinding());
        var button = Assert.IsType<UiSimpleButton>(panel.Children[0]);

        Assert.Equal(Argb(0xFF112233u), button.BackgroundColor);
        Assert.Equal(Argb(0xFF445566u), button.BorderColor);
        Assert.Equal(Argb(0xFF7788AAu), button.TextColor);
    }

    /// <summary>
    /// The pin for "additive only": a literal colour lands on exactly the
    /// value it landed on before a colour could name a binding, on every
    /// element that takes one.
    /// </summary>
    [Fact]
    public void EveryLiteralColorAttributeStillParsesToTheSameValue()
    {
        const string xml = """
            <panel x="0" y="0" w="200" h="200">
              <group x="0" y="0" w="100" h="40"
                     background="#FF112233" border="#FF445566"/>
              <label x="0" y="44" text="ABC" color="#FF7788AA"/>
              <button x="0" y="60" w="90" h="24" text="ABC"
                      background="#FF102030" border="#FF405060" color="#FF708090"/>
              <toggle x="0" y="88" w="100" h="18" text="ABC" checked="{On}"
                      color="#FFAABBCC"/>
              <meter x="0" y="110" w="100" h="12" fill="{Half}" color="#FF00FF00"/>
              <field x="0" y="126" w="120" h="20" text="ABC"
                     background="#FF010203" color="#FF040506"/>
            </panel>
            """;
        UiNineSlicePanel panel = Build(xml, new ColorBinding());

        var group = Assert.IsType<UiPanel>(panel.Children[0]);
        Assert.Equal(Argb(0xFF112233u), group.BackgroundColor);
        Assert.Equal(Argb(0xFF445566u), group.BorderColor);
        Assert.Equal(1f, group.BorderThickness);
        Assert.Equal(Argb(0xFF7788AAu), Assert.IsType<UiLabel>(panel.Children[1]).TextColor);
        var button = Assert.IsType<UiSimpleButton>(panel.Children[2]);
        Assert.Equal(Argb(0xFF102030u), button.BackgroundColor);
        Assert.Equal(Argb(0xFF405060u), button.BorderColor);
        Assert.Equal(Argb(0xFF708090u), button.TextColor);
        Assert.Equal(
            Argb(0xFFAABBCCu),
            Assert.IsType<UiMarkupToggle>(panel.Children[3]).TextColor);
        Assert.Equal(Argb(0xFF00FF00u), Assert.IsType<UiMeter>(panel.Children[4]).BarColor);
        var field = Assert.IsType<UiField>(panel.Children[5]);
        Assert.Equal(Argb(0xFF010203u), field.BackgroundColor);
        Assert.Equal(Argb(0xFF040506u), field.TextColor);
    }

    /// <summary>
    /// The pin for the elements whose colour attribute is absent: each one
    /// keeps its own default rather than picking up a parsed colour.
    /// </summary>
    [Fact]
    public void AnAbsentColorAttributeStillLeavesTheElementDefault()
    {
        const string xml = """
            <panel x="0" y="0" w="200" h="200">
              <group x="0" y="0" w="100" h="40"/>
              <field x="0" y="44" w="120" h="20" text="ABC"/>
            </panel>
            """;
        UiNineSlicePanel panel = Build(xml, new ColorBinding());

        var group = Assert.IsType<UiPanel>(panel.Children[0]);
        Assert.Equal(Vector4.Zero, group.BackgroundColor);
        Assert.Equal(Vector4.Zero, group.BorderColor);
        Assert.Equal(0f, group.BorderThickness);
        var field = Assert.IsType<UiField>(panel.Children[1]);
        Assert.Equal(new Vector4(0f, 0f, 0f, 0.9f), field.BackgroundColor);
        Assert.Equal(new Vector4(0.91f, 0.87f, 0.76f, 1f), field.TextColor);
    }

    /// <summary>
    /// A colour binding that names nothing is silent, the way an unresolved
    /// <c>text</c> binding is: the attribute falls back to what the literal
    /// parser makes of that text, which is opaque white.
    /// </summary>
    [Fact]
    public void AnUnresolvedColorBindingIsSilentAndFallsBackToTheLiteralParser()
    {
        const string xml = """
            <panel x="0" y="0" w="120" h="60">
              <group x="0" y="0" w="100" h="40" background="{NoSuchSetting}"/>
            </panel>
            """;
        UiNineSlicePanel panel = Build(xml, new ColorBinding());
        var group = Assert.IsType<UiPanel>(panel.Children[0]);

        Assert.Equal(Vector4.One, group.BackgroundColor);
        AssertDrawnIn(group, FillTexture, Vector4.One);
    }

    /// <summary>
    /// A colour bound to a member that is not a colour in any accepted form
    /// lands on the same fallback instead of throwing at build.
    /// </summary>
    [Fact]
    public void AColorBoundToSomethingThatIsNotAColorFallsBackInsteadOfThrowing()
    {
        const string xml = """
            <panel x="0" y="0" w="120" h="60">
              <group x="0" y="0" w="100" h="40" background="{NotAColor}"/>
            </panel>
            """;
        UiNineSlicePanel panel = Build(xml, new ColorBinding());
        var group = Assert.IsType<UiPanel>(panel.Children[0]);

        Assert.Equal(Vector4.One, group.BackgroundColor);
        AssertDrawnIn(group, FillTexture, Vector4.One);
    }
}
