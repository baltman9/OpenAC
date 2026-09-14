using System;
using System.Numerics;

namespace AcDream.App.UI.Layout;

public class UiDatElement : UiElement, IUiDatStateful
{
#pragma warning disable IDE0051 // private constants kept for documentation / Plan 2
    private const int DrawUndefined   = 0;
    private const int DrawNormal      = 1;
    private const int DrawOverlay     = 2;
    private const int DrawAlphablend  = 3;
#pragma warning restore IDE0051

    protected readonly ElementInfo Info;
    private readonly Func<uint, (uint tex, int w, int h)> _resolve;

    public uint ElementId => Info.Id;

    /// <summary>Which state name to render. <c>""</c> = the unnamed DirectState.
    /// Falls back to DirectState if the named state is absent.</summary>
    public string ActiveState { get; set; } = "";

    public uint ActiveRetailStateId
    {
        get
        {
            if (string.IsNullOrEmpty(ActiveState))
                return UiStateInfo.DirectStateId;
            foreach (var (id, state) in Info.States)
                if (string.Equals(state.Name, ActiveState, StringComparison.Ordinal))
                    return id;
            return UiButtonStateMachine.TryStateId(ActiveState, out uint standard)
                ? standard
                : RetailUiStateIds.TryStateId(ActiveState, out uint custom) ? custom : 0u;
        }
    }

    public override string ActiveCursorStateName => ActiveState;

    public bool TrySetRetailState(uint stateId)
    {
        uint appliedStateId = stateId;
        UiStateInfo? selectedState = null;
        if (stateId == UiStateInfo.DirectStateId)
        {
            if (!Info.States.TryGetValue(stateId, out selectedState)
                && !Info.StateMedia.ContainsKey(""))
                return false;
            ActiveState = "";
        }
        else if (Info.States.TryGetValue(stateId, out selectedState))
        {
            ActiveState = selectedState.Name;
        }
        else
        {
            string stateName = UiButtonStateMachine.StateName(stateId);
            if (string.IsNullOrEmpty(stateName))
                stateName = RetailUiStateIds.StateName(stateId);
            if (string.IsNullOrEmpty(stateName) || !Info.StateMedia.ContainsKey(stateName))
            {
                ActiveState = "";
                appliedStateId = UiStateInfo.DirectStateId;
                Info.States.TryGetValue(appliedStateId, out selectedState);
            }
            else
            {
                ActiveState = stateName;
            }
        }

        if (selectedState is not null
            && selectedState.Properties.TryGetValue(0x3Bu, out var invisibleProp)
            && invisibleProp.Kind == UiPropertyKind.Bool)
            Visible = !invisibleProp.BoolValue;

        if (selectedState?.PassToChildren == true)
        {
            foreach (UiElement child in Children)
                if (child is IUiDatStateful stateful)
                    stateful.TrySetRetailState(appliedStateId);
        }
        return true;
    }

    public UiDatElement(ElementInfo info, Func<uint, (uint tex, int w, int h)> resolve)
    {
        Info = info;
        _resolve = resolve;
        ClickThrough = true; // generic decoration; behavioral widgets opt back in

        if (!string.IsNullOrEmpty(info.DefaultStateName))
            ActiveState = info.DefaultStateName;
        else if (info.StateMedia.ContainsKey("Normal"))
            ActiveState = "Normal";
        // else ActiveState stays "" (DirectState)

        // Outline 0x21 / OutlineColor 0x22 from the effective-default state, mirroring
        // DatWidgetFactory.BuildText's seed of UiText (round-5 review S2).
        Outline = info.Outline;
        if (info.OutlineColor.HasValue)
            OutlineColor = info.OutlineColor.Value;
    }

    // exposed for unit testing
    public (uint File, int DrawMode) ActiveMedia()
        => Info.StateMedia.TryGetValue(ActiveState, out var m) ? m
         : Info.StateMedia.TryGetValue("", out var d) ? d
         : (0u, 0);

    public Action? OnClick { get; set; }
    public Action<int, int>? OnClickAt { get; set; }

    // ── Pointer surface beyond the plain click ───────────────────────────────
    // A plain authored element can also be the interactive mask over an image
    // map, where the region under the pointer decides what a press, a right
    // click or a drop means. Every hook below is null for ordinary decoration,
    // so a decoration element keeps handling the click and nothing else.

    /// <summary>Right click at element-local (x, y).</summary>
    public Action<int, int>? OnRightClickAt { get; set; }

    /// <summary>Drag payload for a press at element-local (x, y). A non-null hook
    /// makes this element a drag source; returning null refuses the lift.</summary>
    public Func<int, int, object?>? DragPayloadAt { get; set; }

    /// <summary>Ghost art for a drag lifted at element-local (x, y).</summary>
    public Func<int, int, (uint tex, int w, int h)?>? DragGhostAt { get; set; }

    /// <summary>A drag carrying the payload is over element-local (x, y).</summary>
    public Action<object?, int, int>? OnDragOverAt { get; set; }

    /// <summary>The drag left this element.</summary>
    public Action? OnDragLeave { get; set; }

    /// <summary>A drag carrying the payload was released at element-local (x, y).</summary>
    public Action<object?, int, int>? OnDropReleasedAt { get; set; }

    private int _pressLocalX;
    private int _pressLocalY;

    /// <summary>Element-local point of the most recent press. A drag lift carries
    /// no coordinates of its own, so the press point is what it started from.</summary>
    internal (int X, int Y) PressPoint => (_pressLocalX, _pressLocalY);

    public override bool HandlesClick => OnClick is not null || OnClickAt is not null;

    public override bool IsDragSource => DragPayloadAt is not null;

    public override object? GetDragPayload()
        => DragPayloadAt?.Invoke(_pressLocalX, _pressLocalY);

    public override (uint tex, int w, int h)? GetDragGhost()
        => DragGhostAt?.Invoke(_pressLocalX, _pressLocalY);

    private bool TracksPressPoint
        => DragPayloadAt is not null || OnRightClickAt is not null;

    public override bool OnEvent(in UiEvent e)
    {
        switch (e.Type)
        {
            case UiEventType.MouseDown:
            case UiEventType.RightDown:
                // Coordinates are local to the event's own target and are not
                // re-based as the event bubbles, so only the target may read
                // them. Recording the press never consumes it: elements above
                // still act on a bubbled press.
                if (!TracksPressPoint || !ReferenceEquals(e.Target, this)) break;
                _pressLocalX = e.Data1;
                _pressLocalY = e.Data2;
                break;

            case UiEventType.Click:
                if (OnClick is null && OnClickAt is null) break;
                OnClick?.Invoke();
                OnClickAt?.Invoke(e.Data1, e.Data2);
                return true;

            case UiEventType.RightClick:
                if (OnRightClickAt is null || !ReferenceEquals(e.Target, this)) break;
                OnRightClickAt(e.Data1, e.Data2);
                return true;

            case UiEventType.DragBegin:
                return DragPayloadAt is not null;

            case UiEventType.DragEnter:
                if (OnDragOverAt is null) break;
                OnDragOverAt(e.Payload, e.Data1, e.Data2);
                return true;

            case UiEventType.DragOver:          // the root fires this one on LEAVE
                if (OnDragOverAt is null && OnDragLeave is null) break;
                OnDragLeave?.Invoke();
                return true;

            case UiEventType.DropReleased:
                if (OnDropReleasedAt is null) break;
                OnDropReleasedAt(e.Payload, e.Data1, e.Data2);
                return true;
        }
        return false;
    }

    public string? Label { get; set; }
    public UiDatFont? LabelFont { get; set; }
    public Vector4 LabelColor { get; set; } = Vector4.One;

    public Vector4 Tint { get; set; } = Vector4.One;

    public bool Outline { get; set; }

    public Vector4 OutlineColor { get; set; } = UiRenderContext.DefaultOutlineColor;

    public bool MediaVisible { get; set; } = true;

    public uint? RuntimeImageTexture { get; set; }

    protected override void OnDraw(UiRenderContext ctx)
    {
        if (MediaVisible && RuntimeImageTexture is uint runtimeTexture)
        {
            if (runtimeTexture != 0u)
            {
                ctx.DrawSprite(
                    runtimeTexture,
                    0f,
                    0f,
                    Width,
                    Height,
                    0f,
                    0f,
                    1f,
                    1f,
                    Tint);
            }
            DrawLabel(ctx);
            return;
        }

        var (file, _) = ActiveMedia();
        if (MediaVisible && file != 0)
        {
            var (tex, tw, th) = _resolve(file);
            if (tex != 0 && tw != 0 && th != 0)
            {
                ctx.DrawSprite(tex, 0, 0, Width, Height, 0, 0, Width / tw, Height / th, Tint);
            }
        }

        DrawLabel(ctx);
    }

    private void DrawLabel(UiRenderContext ctx)
    {
        if (Label is { Length: > 0 } label && LabelFont is { } lf)
        {
            float tx = (Width - lf.MeasureWidth(label)) * 0.5f;
            float ty = (Height - lf.LineHeight) * 0.5f;
            ctx.DrawStringDat(lf, label, tx, ty, LabelColor, Outline, OutlineColor);
        }
    }
}
