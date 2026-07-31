// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Input;

namespace Vixen.Ui.Controls;

/// <summary>The bar down the side of a <see cref="ScrollView" />.</summary>
/// <remarks>
///     <para>
///         A real element rather than something the scroll view draws, for one reason: it has to be
///         drawn <i>over</i> the content, and <see cref="UiElement.OnDraw" /> runs before an
///         element's children. A later sibling is painted last, which is exactly where a scrollbar
///         belongs.
///     </para>
///     <para>
///         It draws its own thumb rather than holding one, for the reason every other control in
///         this set that draws itself does: the thumb's length and position are fractions of a
///         viewport that only exists after layout, and writing them back as offsets would settle a
///         frame late on every scroll — which is the one frame anybody is looking at.
///     </para>
/// </remarks>
public sealed partial class ScrollBar : Control {
    int trackColor;
    int thumbColor;
    bool dragging;
    float grabbed;

    /// <inheritdoc />
    protected override string TagName => "scrollbar";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>Which way it runs.</summary>
    /// <remarks>
    ///     ⚠ <b>The class follows the property, and it has to.</b> Everything about where a scrollbar
    ///     sits is a theme rule keyed on <c>.vertical</c> or <c>.horizontal</c> — a bar whose class
    ///     said one thing while its drawing and hit testing said the other is laid out down the
    ///     bottom edge and drawn as though it ran down the side. That is not hypothetical:
    ///     <see cref="ScrollView" /> creates both bars and assigns this <i>after</i> construction, so
    ///     a class fixed at creation described the default rather than the answer, and every vertical
    ///     scrollbar in the set was styled as a horizontal one.
    /// </remarks>
    [UiProperty(Changed = nameof(OnOrientationChanged))]
    public partial Orientation Orientation { get; set; }

    /// <summary>How far down the content the viewport currently is.</summary>
    [UiProperty]
    public partial float Value { get; set; }

    /// <summary>How much of the content is visible at once.</summary>
    [UiProperty(Default = 1f)]
    public partial float ViewportSize { get; set; }

    /// <summary>How much content there is.</summary>
    [UiProperty(Default = 1f)]
    public partial float ContentSize { get; set; }

    /// <summary>Raised when a drag on the thumb moves it.</summary>
    public event Action<ScrollBar, float>? Scrolled;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        trackColor = Document.PropertyId("--track-color");
        thumbColor = Document.PropertyId("--thumb-color");

        AddClass(Separator.ClassOf(Orientation));
        AddHandler<PointerEvent>(static (element, args) => ((ScrollBar) element).Pointed(args));
    }

    /// <summary>How far it can travel.</summary>
    public float Range => MathF.Max(0f, ContentSize - ViewportSize);

    void OnOrientationChanged(Orientation previous, Orientation current) {
        RemoveClass(Separator.ClassOf(previous));
        AddClass(Separator.ClassOf(current));
    }

    /// <inheritdoc />
    protected override void OnDraw(DrawContext context) {
        base.OnDraw(context);

        var bounds = context.Bounds;
        if (bounds.Width <= 0f || bounds.Height <= 0f || Range <= 0f) {
            return;
        }

        context.FillRectangle(bounds, Document.ColorOf(Style, trackColor) ?? new Color4(0f, 0f, 0f, 0.08f));

        var (offset, length) = Thumb(Orientation == Orientation.Vertical ? bounds.Height : bounds.Width);
        var colour = Document.ColorOf(Style, thumbColor) ?? new Color4(0.5f, 0.5f, 0.5f, 0.8f);

        var thumb = Orientation == Orientation.Vertical
            ? new Rectangle(bounds.X, bounds.Y + offset, bounds.Width, length)
            : new Rectangle(bounds.X + offset, bounds.Y, length, bounds.Height);

        context.FillRectangle(thumb, colour, MathF.Min(thumb.Width, thumb.Height) * 0.5f);
    }

    /// <summary>Where the thumb sits along the bar, and how long it is.</summary>
    /// <remarks>
    ///     ⚠ <b>The thumb has a minimum length.</b> Proportional length alone gives a thumb of two
    ///     pixels in a hundred-thousand-row list, which nobody can grab — so it stops shrinking, and
    ///     the travel is measured against what is left rather than against the whole bar. Getting
    ///     the second half wrong is the classic scrollbar bug: the thumb reaches the bottom before
    ///     the content does.
    /// </remarks>
    (float Offset, float Length) Thumb(float bar) {
        var proportion = ContentSize <= 0f ? 1f : Math.Clamp(ViewportSize / ContentSize, 0f, 1f);
        var length = MathF.Max(MathF.Min(bar, 24f), bar * proportion);
        var travel = MathF.Max(0f, bar - length);

        return (travel * (Range <= 0f ? 0f : Math.Clamp(Value / Range, 0f, 1f)), length);
    }

    void Pointed(PointerEvent args) {
        var bounds = Bounds;
        var vertical = Orientation == Orientation.Vertical;

        var bar = vertical ? bounds.Height : bounds.Width;
        var along = (vertical ? args.Y - bounds.Y : args.X - bounds.X);

        switch (args.Action) {
            case PointerAction.Pressed when args.Button == PointerButton.Primary && Range > 0f:
                var (offset, length) = Thumb(bar);

                // A press on the thumb grabs it where it was touched; a press on the track jumps the
                // thumb's centre to the cursor and then drags from there. Jumping to the top of the
                // thumb instead makes a track click feel like it overshoots by half a thumb.
                grabbed = along >= offset && along < offset + length ? along - offset : length * 0.5f;
                dragging = true;

                Document.CapturePointer(this);
                Drag(along - grabbed, bar, length);

                args.Handled = true;
                break;

            case PointerAction.Moved when dragging:
                Drag(along - grabbed, bar, Thumb(bar).Length);
                args.Handled = true;
                break;

            case PointerAction.Released when dragging:
                dragging = false;
                Document.ReleasePointer();

                args.Handled = true;
                break;

            default:
                break;
        }
    }

    void Drag(float offset, float bar, float length) {
        var travel = MathF.Max(0f, bar - length);
        var value = travel <= 0f ? 0f : Math.Clamp(offset / travel, 0f, 1f) * Range;

        Value = value;
        Scrolled?.Invoke(this, value);
    }
}

/// <summary>A window onto content that is bigger than it is.</summary>
/// <remarks>
///     <para>
///         <b>Scrolling is an offset, not a layout.</b> <see cref="ScrollTop" /> writes
///         <see cref="UiElement.OffsetY" /> on the content, which is applied when absolute positions
///         are accumulated — so a scroll costs one walk of the tree and no cascade, no flexbox and
///         no measurement. A scroll view that moved its content by changing a layout property would
///         relayout its entire subtree sixty times a second.
///     </para>
///     <para>
///         ⚠ <b>Everything stays in the tree, including what has scrolled out of sight.</b> This is
///         the plain scroll view; a list of a million rows wants <c>VirtualizingPanel</c>, which doc
///         09 makes a first-class primitive and which is owed. Said here rather than discovered:
///         putting a hundred thousand elements in one of these will work and will not be fast.
///     </para>
///     <para>
///         The clipping is <c>overflow: hidden</c> in the theme rather than anything here, so it is
///         the draw list's clip stack that does it — the same mechanism any other clipped element
///         uses.
///     </para>
/// </remarks>
public sealed partial class ScrollView : Control {
    /// <inheritdoc />
    protected override string TagName => "scroll-view";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>Where the content goes.</summary>
    public UiElement Content { get; private set; } = null!;

    /// <inheritdoc />
    /// <remarks>So that markup written inside a <c>&lt;ScrollView&gt;</c> is what scrolls.</remarks>
    protected override UiElement ContentHost => Content;

    /// <summary>The bar down the right.</summary>
    public ScrollBar VerticalBar { get; private set; } = null!;

    /// <summary>The bar along the bottom.</summary>
    public ScrollBar HorizontalBar { get; private set; } = null!;

    /// <summary>How far down the content the viewport is.</summary>
    [UiProperty(Coerce = nameof(CoerceTop), Changed = nameof(OnScrolled))]
    public partial float ScrollTop { get; set; }

    /// <summary>How far across it is.</summary>
    [UiProperty(Coerce = nameof(CoerceLeft), Changed = nameof(OnScrolled))]
    public partial float ScrollLeft { get; set; }

    /// <summary>Raised when either offset changes.</summary>
    public event Action<ScrollView>? Scrolled;

    /// <summary>How far down it can go.</summary>
    public float MaximumTop => MathF.Max(0f, Content.Height - Height);

    /// <summary>How far across it can go.</summary>
    public float MaximumLeft => MathF.Max(0f, Content.Width - Width);

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Content = Part("scroll-content");

        VerticalBar = Part<ScrollBar>();
        VerticalBar.Orientation = Orientation.Vertical;
        VerticalBar.Scrolled += (_, value) => ScrollTop = value;

        HorizontalBar = Part<ScrollBar>();
        HorizontalBar.Orientation = Orientation.Horizontal;
        HorizontalBar.Scrolled += (_, value) => ScrollLeft = value;

        AddHandler<WheelEvent>(static (element, args) => ((ScrollView) element).Wheeled(args));
        AddHandler<KeyEvent>(static (element, args) => ((ScrollView) element).Keyed(args));
        AddHandler<FocusEvent>(static (element, args) => ((ScrollView) element).Refocused(args));

        settle = _ => Refresh();
        Document.LayoutFinished += settle;
    }

    Action<UiDocument>? settle;

    /// <inheritdoc />
    protected override void OnRemoved() {
        if (settle is not null) {
            Document.LayoutFinished -= settle;
            settle = null;
        }

        base.OnRemoved();
    }

    /// <summary>Scrolls until an element inside is visible.</summary>
    /// <param name="element">The element. Must be inside this view.</param>
    /// <remarks>
    ///     ⚠ <b>The minimum movement that works</b>, rather than centring. Centring on every focus
    ///     change makes a list jump under a keyboard user who is arrowing down it one row at a time,
    ///     because every row that was already perfectly visible still moves.
    /// </remarks>
    public void ScrollIntoView(UiElement element) {
        ArgumentNullException.ThrowIfNull(element);

        var target = element.Bounds;
        var viewport = Bounds;

        if (target.Height <= 0f && target.Width <= 0f) {
            return;
        }

        if (target.Top < viewport.Top) {
            ScrollTop -= viewport.Top - target.Top;
        } else if (target.Bottom > viewport.Bottom) {
            ScrollTop += target.Bottom - viewport.Bottom;
        }

        if (target.Left < viewport.Left) {
            ScrollLeft -= viewport.Left - target.Left;
        } else if (target.Right > viewport.Right) {
            ScrollLeft += target.Right - viewport.Right;
        }
    }

    /// <summary>Brings the bars up to date with the content's size.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Hung on <see cref="UiDocument.LayoutFinished" />, which is where it belongs.</b> A
    ///         scroll bar's range is a fact about the content's laid-out height — a result of the
    ///         pass, not an input to it — so a version that ran on scrolls alone was one frame stale
    ///         for every content that changed without one, which is most of them.
    ///     </para>
    ///     <para>
    ///         ⚠ It stays public. A caller that has just filled the content and wants to read the
    ///         range before the next pass still has a way to ask, and it is idempotent.
    ///     </para>
    /// </remarks>
    public void Refresh() {
        VerticalBar.ViewportSize = Height;
        VerticalBar.ContentSize = Content.Height;
        VerticalBar.Value = ScrollTop;

        HorizontalBar.ViewportSize = Width;
        HorizontalBar.ContentSize = Content.Width;
        HorizontalBar.Value = ScrollLeft;

        // The clamp has to run again here rather than only in the coercion, because the thing it
        // clamps against is the content's size — and that changes without anybody assigning to the
        // scroll offset at all.
        ScrollTop = CoerceTop(ScrollTop);
        ScrollLeft = CoerceLeft(ScrollLeft);
    }

    float CoerceTop(float value) => Math.Clamp(value, 0f, MaximumTop);

    float CoerceLeft(float value) => Math.Clamp(value, 0f, MaximumLeft);

    void OnScrolled(float previous, float current) {
        Content.OffsetY = -ScrollTop;
        Content.OffsetX = -ScrollLeft;

        VerticalBar.Value = ScrollTop;
        VerticalBar.ViewportSize = Height;
        VerticalBar.ContentSize = Content.Height;

        HorizontalBar.Value = ScrollLeft;
        HorizontalBar.ViewportSize = Width;
        HorizontalBar.ContentSize = Content.Width;

        Scrolled?.Invoke(this);
    }

    void Wheeled(WheelEvent args) {
        var top = ScrollTop;
        var left = ScrollLeft;

        ScrollTop += args.DeltaY;
        ScrollLeft += args.DeltaX;

        // ⚠ Handled only if it actually scrolled. A view already at the bottom must let the wheel
        // through to whatever contains it, or a page with a fully-scrolled list in the middle of it
        // becomes a page that cannot be scrolled past the list.
        if (!ScrollTop.Equals(top) || !ScrollLeft.Equals(left)) {
            args.Handled = true;
        }
    }

    void Keyed(KeyEvent args) {
        if (args.Action != KeyAction.Pressed || !args.Has(ModifierKeys.None)) {
            return;
        }

        var page = MathF.Max(1f, Height - 24f);

        var moved = args.Key switch {
            InputKey.PageDown => ScrollTop + page,
            InputKey.PageUp => ScrollTop - page,
            InputKey.Home => 0f,
            InputKey.End => MaximumTop,
            _ => float.NaN
        };

        if (float.IsNaN(moved)) {
            return;
        }

        ScrollTop = moved;
        args.Handled = true;
    }

    void Refocused(FocusEvent args) {
        // The focus arriving anywhere inside brings it into view. This is why FocusEvent is routed
        // rather than a callback on the focused element: a scroll view can hear about a focus it
        // knows nothing about, which is the only way a field five levels down gets scrolled to.
        if (args.Gained && args.Next is { } focused && !ReferenceEquals(focused, this)) {
            ScrollIntoView(focused);
        }
    }
}
