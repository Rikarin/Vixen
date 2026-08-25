// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Runtime.CompilerServices;
using Vixen.Core.Mathematics;
using Vixen.Input;
using Vixen.Ui.Styling;

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

    /// <inheritdoc />
    protected override AccessibleRole NativeRole => AccessibleRole.ScrollBar;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The one control in the set whose name is read from the catalogue on every get
    ///     rather than assigned once.</b> A scrollbar has no words on screen and no caption to be
    ///     <see cref="AccessibleRelation.LabelledBy" />, so its only words are the announced ones —
    ///     and because this is a virtual rather than an assignment in <c>OnCreated</c>, it is also
    ///     the one that follows a language change on a bar that is already on screen. Which way
    ///     round it runs is the whole of what distinguishes two bars in the same view.
    /// </remarks>
    protected override string? NativeAccessibleName =>
        IsVertical ? ControlStrings.ScrollBarVertical.Text : ControlStrings.ScrollBarHorizontal.Text;

    /// <inheritdoc />
    /// <remarks>How far down, as a fraction of how far it can go. See <see cref="Slider" /> for why invariant.</remarks>
    protected override string? NativeAccessibleValue =>
        (Range <= 0f ? 0f : Math.Clamp(Value / Range, 0f, 1f)).ToString("0.###", CultureInfo.InvariantCulture);

    bool IsVertical => Orientation == Orientation.Vertical;

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

/// <summary>How a programmatic scroll gets where it is going.</summary>
/// <remarks>
///     CSSOM-View's <c>scroll-behavior</c>, and only its two values. It governs scrolls this control
///     starts — <see cref="ScrollView.ScrollIntoView" />, Page/Home/End — and deliberately not the
///     wheel or a drag on the bar: a smoothed wheel lags the finger by the whole time constant, which
///     reads as a dropped frame rather than as an easing, and browsers exempt direct manipulation for
///     the same reason.
/// </remarks>
public enum ScrollBehavior : byte {
    /// <summary>Jump. The initial value, and what every scroll did before the property was read.</summary>
    Instant,

    /// <summary>Ease, over <see cref="ScrollView.SmoothingConstant" />.</summary>
    Smooth
}

/// <summary>What a scroll that has run out of room does to the wheel.</summary>
/// <remarks>
///     ⚠ <b><c>Contain</c> and <c>None</c> are one behaviour here, and the difference is not
///     implementable rather than unimplemented.</b> In CSS the pair differ only in the
///     <i>local</i> effect at the boundary — <c>contain</c> keeps the browser's rubber-band or
///     pull-to-refresh and <c>none</c> suppresses it — and this engine has neither, so there is
///     nothing for <c>none</c> to additionally turn off. Both stop the chain, which is the half
///     anybody writes the class for. Recorded here rather than left to be measured: a reader
///     comparing the two values and finding identical frames has found the documented answer.
/// </remarks>
public enum OverscrollBehavior : byte {
    /// <summary>The wheel chains to whatever contains this once it can go no further.</summary>
    Auto,

    /// <summary>It does not. The scroll stops at this box's edge.</summary>
    Contain
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
///     <para>
///         ⚠ <b>It reads no <c>overflow</c> of its own, including the per-axis pair, and that is a
///         decision.</b> In CSS <c>overflow</c> on a box is what conjures the scrollbars; here the
///         bars are children this control creates and drives, and the property only says where the
///         clip rectangle's edges are. Wiring the two together would mean this reading its own
///         user-agent rule — <c>scroll-view { overflow: hidden }</c>, the one that makes it clip at
///         all — as an instruction to hide both of its bars. Which bars a view offers is therefore a
///         property of the control, and <c>overflow-x</c> on some other element is a clip and nothing
///         more. The consequence is worth stating plainly: <c>overflow-y: auto</c> on a plain
///         <c>div</c> cuts its content off and offers no way to reach it. Put a <see cref="ScrollView" />
///         there instead.
///     </para>
///     <para>
///         ⚠ <b>It does read four other families, and the distinction is the whole of doc 43 A18.</b>
///         <c>scroll-margin-*</c>, <c>scroll-padding-*</c>, <c>scroll-behavior</c> and
///         <c>overscroll-behavior*</c> say nothing about <i>whether</i> this is a scroll container —
///         which is what <c>overflow</c> would be claiming, and what the paragraph above refuses.
///         They say where a scroll lands, how it gets there, and what happens at the end, which are
///         questions only something that already scrolls can be asked. That is why the utilities were
///         deferred until this control could read them rather than registered as properties on a box:
///         a <c>scroll-mt-4</c> on a <c>div</c> inside nothing is still inert, and correctly so.
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

        // A drag on a bar is direct manipulation, so it settles any easing and is never smoothed
        // itself — the thumb is under the finger and must stay there.
        VerticalBar = Part<ScrollBar>();
        VerticalBar.Orientation = Orientation.Vertical;
        VerticalBar.Scrolled += (_, value) => { Settle(); ScrollTop = value; };

        HorizontalBar = Part<ScrollBar>();
        HorizontalBar.Orientation = Orientation.Horizontal;
        HorizontalBar.Scrolled += (_, value) => { Settle(); ScrollLeft = value; };

        AddHandler<WheelEvent>(static (element, args) => ((ScrollView) element).Wheeled(args));
        AddHandler<KeyEvent>(static (element, args) => ((ScrollView) element).Keyed(args));
        AddHandler<FocusEvent>(static (element, args) => ((ScrollView) element).Refocused(args));

        names = ScrollNames.Of(Document);

        settle = _ => Refresh();
        Document.LayoutFinished += settle;

        step = (_, now) => Advance(now);
        Document.Ticked += step;
    }

    Action<UiDocument>? settle;
    Action<UiDocument, TimeSpan>? step;

    /// <inheritdoc />
    protected override void OnRemoved() {
        if (settle is not null) {
            Document.LayoutFinished -= settle;
            settle = null;
        }

        if (step is not null) {
            Document.Ticked -= step;
            step = null;
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

        // ⚠ The two insets come off two *different* elements, and swapping them is the mistake this
        // reads as. CSS Scroll Snap §6 puts `scroll-margin` on the target — it is that element saying
        // "leave this much of me showing" — and `scroll-padding` on the scroll container, which is
        // this saying "do not land anything under my sticky header". A version that read both off one
        // element would work for every test where they are equal and for nothing else.
        var margin = InsetOf(element);
        var padding = InsetOf(this);

        var top = ScrollTop;
        var left = ScrollLeft;

        if (target.Top - margin.Top < viewport.Top + padding.Top) {
            top -= viewport.Top + padding.Top - (target.Top - margin.Top);
        } else if (target.Bottom + margin.Bottom > viewport.Bottom - padding.Bottom) {
            top += target.Bottom + margin.Bottom - (viewport.Bottom - padding.Bottom);
        }

        if (target.Left - margin.Left < viewport.Left + padding.Left) {
            left -= viewport.Left + padding.Left - (target.Left - margin.Left);
        } else if (target.Right + margin.Right > viewport.Right - padding.Right) {
            left += target.Right + margin.Right - (viewport.Right - padding.Right);
        }

        ScrollTo(top, left);
    }

    /// <summary>Scrolls to an offset, the way <c>scroll-behavior</c> says to.</summary>
    /// <param name="top">The vertical offset wanted. Clamped, as <see cref="ScrollTop" /> is.</param>
    /// <param name="left">Ditto, horizontally.</param>
    /// <remarks>
    ///     ⚠ <b>The smooth path writes a <i>destination</i> and returns; it does not scroll.</b> That
    ///     is what makes a second call mid-flight retarget rather than restart — a list being arrowed
    ///     down eases towards wherever the last key asked for, at the speed it already had, instead of
    ///     decelerating into every row on the way. It is also why the destination is clamped here and
    ///     not only when it is applied: a target beyond the end would otherwise leave
    ///     <see cref="IsScrolling" /> true for ever, easing towards somewhere the clamp will never let
    ///     it reach.
    /// </remarks>
    public void ScrollTo(float top, float left) {
        if (Behaviour() != ScrollBehavior.Smooth) {
            Settle();

            ScrollTop = top;
            ScrollLeft = left;
            return;
        }

        wantedTop = Math.Clamp(top, 0f, MaximumTop);
        wantedLeft = Math.Clamp(left, 0f, MaximumLeft);
        IsScrolling = true;
    }

    /// <summary>Whether a smooth scroll is still on its way somewhere.</summary>
    public bool IsScrolling { get; private set; }

    /// <summary>How long a smooth scroll takes to cover about two thirds of the distance.</summary>
    /// <remarks>
    ///     An exponential ease rather than a fixed duration, because the distance is not known when
    ///     the scroll starts and a fixed duration makes a one-row move as slow as a whole-page one.
    ///     The constant is applied against real elapsed time, so the curve is the same at 30 fps and
    ///     at 240 — a per-frame fraction would make the animation twice as fast on a faster machine,
    ///     which is the commonest way an easing like this is written wrong.
    /// </remarks>
    public const float SmoothingConstant = 0.075f;

    float wantedTop;
    float wantedLeft;
    TimeSpan last;

    void Advance(TimeSpan now) {
        var elapsed = (float) (now - last).TotalSeconds;
        last = now;

        if (!IsScrolling || elapsed <= 0f) {
            return;
        }

        var fraction = 1f - MathF.Exp(-elapsed / SmoothingConstant);

        var top = ScrollTop + (wantedTop - ScrollTop) * fraction;
        var left = ScrollLeft + (wantedLeft - ScrollLeft) * fraction;

        // ⚠ Snap and stop inside half a pixel, or this never finishes: an exponential approach never
        // arrives, so the offset would go on changing by a millionth of a pixel every frame for the
        // life of the document — and every one of those frames invalidates positions and rebuilds the
        // draw list. A scroll that never ends is a frame budget that never recovers.
        if (MathF.Abs(wantedTop - top) < 0.5f && MathF.Abs(wantedLeft - left) < 0.5f) {
            top = wantedTop;
            left = wantedLeft;
            IsScrolling = false;
        }

        ScrollTop = top;
        ScrollLeft = left;
    }

    /// <summary>Abandons any smooth scroll in flight, leaving the offset where it got to.</summary>
    /// <remarks>
    ///     ⚠ Called by everything that is a <i>direct</i> scroll — the wheel, a drag on the bar — so
    ///     that a hand on the wheel takes the content away from an easing that was still running.
    ///     Without it the two fight for one offset and the animation wins, because it writes every
    ///     frame and the wheel writes only when it is turned.
    /// </remarks>
    public void Settle() {
        IsScrolling = false;
        wantedTop = ScrollTop;
        wantedLeft = ScrollLeft;
    }

    /// <summary>The four scroll insets an element declares, resolved to physical edges.</summary>
    /// <param name="element">
    ///     The target, for <c>scroll-margin</c>; this view, for <c>scroll-padding</c>. Which family is
    ///     read follows from which of the two it is, because the same element is never both.
    /// </param>
    /// <remarks>
    ///     ⚠ <b>The logical pair is resolved here rather than by the layout, which is the one place
    ///     this differs from <c>margin-inline-start</c>.</b> `LayoutStyleBuilder` reads that longhand
    ///     into an edge slot and `StyleResolution` folds it against `direction` during the pass; no
    ///     pass ever sees these, because a scroll offset is not layout. So the fold is done here,
    ///     against the same `direction` keyword and with the same precedence — the physical edge wins
    ///     where both are written, which is what CSS Logical §4 says and what the layout already does.
    /// </remarks>
    ScrollInset InsetOf(UiElement element) {
        var style = element.Style;
        var scrolling = ReferenceEquals(element, this);
        var edges = scrolling ? names.Padding : names.Margin;

        var top = Document.LengthOf(style, edges.Top) ?? 0f;
        var bottom = Document.LengthOf(style, edges.Bottom) ?? 0f;

        var start = Document.LengthOf(style, edges.Start);
        var end = Document.LengthOf(style, edges.End);

        var rtl = style.TryGet(names.Direction, out var direction) && direction == names.Rtl;

        var left = Document.LengthOf(style, edges.Left) ?? (rtl ? end : start) ?? 0f;
        var right = Document.LengthOf(style, edges.Right) ?? (rtl ? start : end) ?? 0f;

        return new ScrollInset(top, right, bottom, left);
    }

    /// <summary>What <c>scroll-behavior</c> says on this view.</summary>
    ScrollBehavior Behaviour() =>
        Style.TryGet(names.Behavior, out var value) && value == names.Smooth
            ? ScrollBehavior.Smooth
            : ScrollBehavior.Instant;

    /// <summary>What <c>overscroll-behavior</c> says on this view, for one axis.</summary>
    /// <param name="axis">The axis's own longhand.</param>
    /// <remarks>
    ///     The shorthand first and the longhand over it, which is exactly what
    ///     <c>LayoutStyleBuilder</c> does for <c>overflow</c> and for the same reason: nothing expands
    ///     <c>overscroll-behavior</c> into its two longhands on the way in, so by the time there is a
    ///     computed style "which was written last" no longer has an answer, and a named axis winning
    ///     is both what every sheet here is written against and what CSS agrees with whenever the
    ///     longhand really did come last.
    /// </remarks>
    OverscrollBehavior Chaining(int axis) {
        if (Style.TryGet(axis, out var value)) {
            return value == names.Auto ? OverscrollBehavior.Auto : OverscrollBehavior.Contain;
        }

        if (Style.TryGet(names.Overscroll, out var both)) {
            return both == names.Auto ? OverscrollBehavior.Auto : OverscrollBehavior.Contain;
        }

        return OverscrollBehavior.Auto;
    }

    ScrollNames names = null!;

    /// <summary>An inset in physical edges, which is what the offsets are measured in.</summary>
    readonly record struct ScrollInset(float Top, float Right, float Bottom, float Left);

    /// <summary>The property and keyword ids one document interns for scrolling.</summary>
    /// <remarks>
    ///     ⚠ <b>One instance per document rather than a field per view, and the reason is the count.</b>
    ///     Sixteen ids interned per <see cref="ScrollView" /> would be sixteen dictionary probes for
    ///     every tree, grid, code editor and panel in an editor frame — and every one of them would
    ///     answer the same number, because <c>Intern</c> is idempotent within a document. Cached
    ///     against the document, as <see cref="UiDocument.PropertyId" />'s own remark requires.
    /// </remarks>
    sealed class ScrollNames {
        static readonly ConditionalWeakTable<UiDocument, ScrollNames> Cache = [];

        ScrollNames(UiDocument document) {
            var properties = document.Styles.Properties;
            var values = document.Styles.Values;

            Margin = EdgeIds.For(properties, "scroll-margin");
            Padding = EdgeIds.For(properties, "scroll-padding");

            Behavior = properties.Intern("scroll-behavior");
            Overscroll = properties.Intern("overscroll-behavior");
            OverscrollX = properties.Intern("overscroll-behavior-x");
            OverscrollY = properties.Intern("overscroll-behavior-y");
            Direction = properties.Intern("direction");

            Smooth = values.Intern("smooth");
            Auto = values.Intern("auto");
            Rtl = values.Intern("rtl");
        }

        public static ScrollNames Of(UiDocument document) => Cache.GetValue(document, static key => new ScrollNames(key));

        public EdgeIds Margin { get; }
        public EdgeIds Padding { get; }
        public int Behavior { get; }
        public int Overscroll { get; }
        public int OverscrollX { get; }
        public int OverscrollY { get; }
        public int Direction { get; }
        public int Smooth { get; }
        public int Auto { get; }
        public int Rtl { get; }
    }

    /// <summary>The six longhands of one scroll-inset family, interned.</summary>
    /// <remarks>
    ///     ⚠ <b>No shorthand slot, unlike <c>LayoutStyleBuilder.EdgeNames</c>, and that is deliberate
    ///     rather than an omission.</b> ExCSS expands <c>margin</c> and <c>padding</c> while parsing
    ///     and has never heard of <c>scroll-margin</c>, so a <c>scroll-margin: 4px</c> would reach the
    ///     cascade as one unexpanded declaration that nothing here could read — the <c>inset</c> hole
    ///     `ShorthandExpansion` already records. The families emit the four longhands instead, so
    ///     there is no shorthand to read and no chance of reading one and the longhands in the wrong
    ///     order.
    /// </remarks>
    readonly record struct EdgeIds(int Top, int Right, int Bottom, int Left, int Start, int End) {
        public static EdgeIds For(NameTable properties, string prefix) =>
            new(
                properties.Intern($"{prefix}-top"),
                properties.Intern($"{prefix}-right"),
                properties.Intern($"{prefix}-bottom"),
                properties.Intern($"{prefix}-left"),
                properties.Intern($"{prefix}-inline-start"),
                properties.Intern($"{prefix}-inline-end")
            );
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
        // A hand on the wheel takes the content off any easing still running, and the wheel itself is
        // never smoothed — see `ScrollBehavior`.
        Settle();

        var top = ScrollTop;
        var left = ScrollLeft;

        ScrollTop += args.DeltaY;
        ScrollLeft += args.DeltaX;

        // ⚠ Handled if it actually scrolled. A view already at the bottom must let the wheel through
        // to whatever contains it, or a page with a fully-scrolled list in the middle of it becomes a
        // page that cannot be scrolled past the list.
        if (!ScrollTop.Equals(top) || !ScrollLeft.Equals(left)) {
            args.Handled = true;
            return;
        }

        // ⚠ And it went nowhere — so whether the wheel chains outwards is `overscroll-behavior`'s
        // decision and nothing else's. Per axis, and asked only of an axis the wheel actually turned:
        // a purely vertical wheel over a view with `overscroll-x: contain` must still chain, or a
        // horizontal-only opt-out would silently trap every vertical scroll in the region.
        var contained = (args.DeltaY != 0f && Chaining(names.OverscrollY) == OverscrollBehavior.Contain)
            || (args.DeltaX != 0f && Chaining(names.OverscrollX) == OverscrollBehavior.Contain);

        if (contained) {
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

        // Smoothed where `scroll-behavior` asks for it, unlike the wheel: a key press is a discrete
        // request for a destination rather than a continuous one for a delta, so there is nothing for
        // the easing to lag behind.
        ScrollTo(moved, ScrollLeft);
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
