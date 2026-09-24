using System;
using System.Numerics;

namespace AcDream.App.UI;

public class UiPanel : UiElement
{
    public Vector4 BackgroundColor { get; set; } = new(0f, 0f, 0f, 0.55f);

    public Vector4 BorderColor     { get; set; } = new(0.15f, 0.15f, 0.2f, 0.8f);

    /// <summary>
    /// Re-read every frame in place of <see cref="BackgroundColor"/> when it
    /// is set, the way <see cref="UiLabel.TextSource"/> stands in for a
    /// caption: a colour a player can change has to be asked for again, not
    /// captured once.
    /// </summary>
    public Func<Vector4>? BackgroundColorSource { get; set; }

    /// <summary>Re-read every frame in place of <see cref="BorderColor"/>.</summary>
    public Func<Vector4>? BorderColorSource { get; set; }

    public float BorderThickness   { get; set; } = 1f;

    public uint BackgroundSprite { get; set; }

    public Func<uint, (uint tex, int w, int h)>? SpriteResolve { get; set; }

    protected override void OnDraw(UiRenderContext ctx)
    {
        Vector4 background = BackgroundColorSource?.Invoke() ?? BackgroundColor;
        Vector4 border = BorderColorSource?.Invoke() ?? BorderColor;

        if (BackgroundSprite != 0 && SpriteResolve is { } sr)
        {
            var (tex, tw, th) = sr(BackgroundSprite);
            if (tex != 0 && tw != 0 && th != 0)
                ctx.DrawSprite(tex, 0, 0, Width, Height, 0, 0, Width / tw, Height / th, Vector4.One);
        }
        else if (background.W > 0f)
        {
            ctx.DrawFill(0, 0, Width, Height, background);
        }

        if (border.W > 0f && BorderThickness > 0f)
            ctx.DrawRectOutline(0, 0, Width, Height, border, BorderThickness);
    }
}

public class UiLabel : UiElement
{
    public string Text       { get; set; } = string.Empty;
    public Vector4 TextColor { get; set; } = new(1f, 1f, 1f, 1f);

    public Func<string?>? TextSource { get; set; }

    /// <summary>Re-read every frame in place of <see cref="TextColor"/>.</summary>
    public Func<Vector4>? TextColorSource { get; set; }

    public UiDatFont? DatFont { get; set; }

    public bool Outline { get; set; } = true;

    public UiLabel() { ClickThrough = true; }

    protected override bool ClipsChildren => false;

    protected override void OnDraw(UiRenderContext ctx)
    {
        string text = TextSource?.Invoke() ?? Text;
        Vector4 textColor = TextColorSource?.Invoke() ?? TextColor;
        float w = DatFont is { } df
            ? df.MeasureWidth(text)
            : (ctx.DefaultFont?.MeasureWidth(text) ?? text.Length * 7f);
        float h = DatFont?.LineHeight
            ?? ctx.DefaultFont?.LineHeight ?? 14f;
        if (w != Width) Width = w;
        if (h != Height) Height = h;
        if (DatFont is { } dat)
            ctx.DrawStringDat(dat, text, 0, 0, textColor, Outline);
        else
            ctx.DrawString(text, 0, 0, textColor);
    }
}

public class UiSimpleButton : UiPanel
{
    public string Text       { get; set; } = string.Empty;
    public Vector4 TextColor { get; set; } = new(1f, 1f, 1f, 1f);

    public Func<string?>? TextSource { get; set; }

    /// <summary>Re-read every frame in place of <see cref="TextColor"/>.</summary>
    public Func<Vector4>? TextColorSource { get; set; }

    public UiDatFont? DatFont { get; set; }

    public bool Outline { get; set; } = true;

    public Func<(uint tex, int w, int h)>? IconSource { get; set; }

    public event System.Action? Click;

    private readonly UiKeyboardActivation _keyboardActivation = new();

    public override bool HandlesClick => true;

    public UiSimpleButton()
    {
        BackgroundColor = new Vector4(0.1f, 0.1f, 0.15f, 0.8f);
        BorderColor     = new Vector4(0.45f, 0.45f, 0.55f, 1f);
    }

    public override bool OnEvent(in UiEvent e)
    {
        if (_keyboardActivation.HandleEvent(in e, TabStop, Enabled, () => Click?.Invoke()))
            return true;
        if (e.Type == UiEventType.Click && Enabled)
        {
            Click?.Invoke();
            return true;
        }
        return false;
    }

    protected override void OnDraw(UiRenderContext ctx)
    {
        base.OnDraw(ctx);
        if (_keyboardActivation.Focused)
            ctx.DrawRectOutline(1f, 1f, Width - 2f, Height - 2f,
                new Vector4(1f, 0.82f, 0.25f, 1f), 1f);

        float iconColumn = 0f;
        if (IconSource is { } iconSource)
        {
            float extent = MathF.Max(0f, MathF.Min(Width, Height) - 6f);
            iconColumn = extent + 6f;

            (uint tex, int w, int h) = iconSource();
            if (tex != 0u && w > 0 && h > 0)
            {
                float scale = MathF.Min(extent / w, extent / h);
                float drawWidth = w * scale;
                float drawHeight = h * scale;
                ctx.DrawSprite(
                    tex,
                    3f + (extent - drawWidth) * 0.5f,
                    (Height - drawHeight) * 0.5f,
                    drawWidth, drawHeight,
                    0f, 0f, 1f, 1f, Vector4.One);
            }
        }

        string caption = TextSource?.Invoke() ?? Text;
        if (caption.Length == 0) return;
        Vector4 textColor = TextColorSource?.Invoke() ?? TextColor;

        float captionAreaX = iconColumn;
        float captionAreaWidth = MathF.Max(0f, Width - iconColumn);
        if (DatFont is { } dat)
        {
            float datW = dat.MeasureWidth(caption);
            ctx.DrawStringDat(
                dat, caption,
                captionAreaX + (captionAreaWidth - datW) * 0.5f,
                (Height - dat.LineHeight) * 0.5f,
                textColor, Outline);
            return;
        }

        if (ctx.DefaultFont is null) return;
        float textW = ctx.DefaultFont.MeasureWidth(caption);
        float tx = captionAreaX + (captionAreaWidth - textW) * 0.5f;
        float ty = (Height - ctx.DefaultFont.LineHeight) * 0.5f;
        ctx.DrawString(caption, tx, ty, textColor);
    }
}

public class UiClickablePanel : UiPanel
{
    public Action? OnClick { get; set; }

    public string? TooltipText { get; set; }

    /// <inheritdoc />
    public override string? GetTooltipText() =>
        string.IsNullOrWhiteSpace(TooltipText) ? null : TooltipText;

    public UiClickablePanel()
    {
        ClickThrough = false;
    }

    public override bool HandlesClick => true;

    public override bool OnEvent(in UiEvent e)
    {
        if (e.Type == UiEventType.Click && Enabled)
        {
            OnClick?.Invoke();
            return true;
        }
        return false;
    }
}
