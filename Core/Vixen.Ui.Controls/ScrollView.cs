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

    /// <summary>Raised when a drag on the thumb is let go of.</summary>
    /// <remarks>
    ///     ⚠ <b>The end of a gesture, which is the thing this control had no way to say and the
    ///     reason <c>scroll-snap-type</c> could not be read before it existed.</b>
    ///     <see cref="Scrolled" /> is a stream of positions with no terminator — it says where the
    ///     thumb is, never that the hand has come off it — so a scroll container listening to it
    ///     alone can know everything about where the content went and nothing about when it stopped.
    ///     "Comes to rest" is not derivable from a position stream, and a snap is defined at exactly
    ///     that moment.
    /// </remarks>
    public event Action<ScrollBar>? ScrollEnded;

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

                ScrollEnded?.Invoke(this);

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

/// <summary>Which axes a scroll container snaps on.</summary>
/// <remarks>
///     CSS Scroll Snap's <c>scroll-snap-type</c>, minus the two axes this engine has no writing mode
///     to distinguish — <c>block</c> and <c>inline</c> would mean <c>y</c> and <c>x</c> in every
///     configuration <c>Vixen.Ui.Layout</c> can be in, which is <c>scroll-mbs-*</c>'s argument one
///     property over.
/// </remarks>
public enum ScrollSnapAxis : byte {
    /// <summary>It does not. The initial value, and what every scroll did before this was read.</summary>
    None,

    /// <summary>Horizontally.</summary>
    X,

    /// <summary>Vertically.</summary>
    Y,

    /// <summary>Both, independently — a scroll can be snapped on one axis and loose on the other.</summary>
    Both
}

/// <summary>Where a snap candidate lines up inside the container it snaps in.</summary>
/// <remarks>
///     <c>scroll-snap-align</c>, per axis. The edges are the <i>snapport's</i> — the viewport as
///     <c>scroll-padding</c> leaves it — and the candidate's are its own as <c>scroll-margin</c>
///     leaves it, which is the same pair of elements <see cref="ScrollView.ScrollIntoView" /> reads
///     and for the same reason.
/// </remarks>
public enum ScrollSnapAlign : byte {
    /// <summary>Not a candidate at all. The initial value.</summary>
    None,

    /// <summary>Its near edge meets the snapport's near edge.</summary>
    Start,

    /// <summary>Its middle meets the snapport's middle.</summary>
    Center,

    /// <summary>Its far edge meets the snapport's far edge.</summary>
    End
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

    /// <summary>Whether dragging with a <i>mouse</i> also scrolls the view. A finger always does.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This used to be the whole switch, and the reason it was is no longer true.</b> It
    ///         was opt-in and off because nothing in the engine could tell a finger from a mouse, so
    ///         a view that dragged would have taken text selection, marquees and row drags away from
    ///         every desktop user. <c>DragEvent</c> now carries a
    ///         <see cref="Vixen.Ui.PointerType" />, so the two cases are separable and the touch half
    ///         needs no opt-in at all: a finger scrolls the content, always, and this property is
    ///         what a kiosk or a map view sets to get the mouse to behave like one too.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see cref="Vixen.Ui.PointerType.Unknown" /> does not drag</b>, and is not
    ///         flattened to touch for the same reason it is not flattened to mouse. A producer that
    ///         has not said what it is has not said it is a finger, and guessing here would silently
    ///         re-introduce exactly the desktop regression the property was invented to avoid.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is what momentum is built on, and until it existed there was no gesture to
    ///         attach a velocity to.</b> The view scrolled from the wheel, the keyboard and its bars,
    ///         none of which has a finger leaving it — so a fling had nothing to continue. That is
    ///         the premise the momentum work was blocked on rather than the deceleration curve, which
    ///         is four lines.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The wheel is deliberately <i>not</i> given a fling.</b> On macOS AppKit generates
    ///         the trackpad's own momentum phase and SDL forwards it as ordinary wheel deltas, so a
    ///         curve added on that path would run on top of the operating system's and the two would
    ///         compound. A drag is the platform-neutral gesture that carries no momentum of its own.
    ///     </para>
    /// </remarks>
    [UiProperty]
    public partial bool DragToScroll { get; set; }

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
        VerticalBar.Scrolled += (_, value) => { Began(); Settle(); ScrollTop = value; };
        VerticalBar.ScrollEnded += _ => Ended();

        HorizontalBar = Part<ScrollBar>();
        HorizontalBar.Orientation = Orientation.Horizontal;
        HorizontalBar.Scrolled += (_, value) => { Began(); Settle(); ScrollLeft = value; };
        HorizontalBar.ScrollEnded += _ => Ended();

        AddHandler<WheelEvent>(static (element, args) => ((ScrollView) element).Wheeled(args));
        AddHandler<DragEvent>(static (element, args) => ((ScrollView) element).Dragged(args));
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
        var (snappedTop, snappedLeft) = SnapPositions(
            Math.Clamp(top, 0f, MaximumTop),
            Math.Clamp(left, 0f, MaximumLeft),
            ScrollTop,
            ScrollLeft
        );

        Move(snappedTop, snappedLeft);
    }

    /// <summary>Goes to an offset the way <c>scroll-behavior</c> says to, snapping nothing.</summary>
    /// <remarks>
    ///     ⚠ <b>The half of <see cref="ScrollTo" /> that is not the snap, split out because the snap
    ///     would otherwise recurse.</b> A gesture ending computes where it should come to rest and
    ///     then has to <i>get</i> there, and getting there through <see cref="ScrollTo" /> would ask
    ///     the snap where a snap position snaps to. That is idempotent for the nearest-candidate rule
    ///     and is <i>not</i> for <c>scroll-snap-stop: always</c>, which is a claim about what a scroll
    ///     passed over — so a second pass would see the trip to the snap position as its own gesture.
    /// </remarks>
    void Move(float top, float left) {
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

        // ⚠ Before the easing rather than after it, and outside the `IsScrolling` guard: the whole
        // point of the terminator is that it fires on a view that is doing nothing at all.
        //
        // ⚠ Except while a finger is down or a fling is running, and that exception is what a
        // gesture with a real terminator earns. `SnapIdleSeconds` is an eighth of a second — shorter
        // than any drag worth making and far shorter than a fling — so without this a drag would be
        // declared over, and snapped, while it was still happening. The wheel needs the timer
        // precisely because it has no end in it; a drag ends when the pointer comes up and a fling
        // ends when it runs out of speed.
        if (gesturing
            && !dragging
            && !IsFlinging
            && (float) (now - gestureAt).TotalSeconds >= SnapIdleSeconds) {
            Ended();
        }

        if (elapsed > 0f) {
            if (dragging) {
                Track(elapsed);
            } else if (IsFlinging) {
                Fling(elapsed);
            }
        }

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

    // ── Dragging the content, and the fling at the end of it ────────────────────────────────

    bool dragging;
    float velocityTop;
    float velocityLeft;
    float sampledTop;
    float sampledLeft;

    /// <summary>How long a fling takes to lose about two thirds of its speed.</summary>
    /// <remarks>
    ///     ⚠ <b>A time constant against real elapsed seconds, not a per-frame multiplier.</b> A
    ///     factor applied once a frame decelerates twice as fast at 120 fps as at 60, which is the
    ///     commonest way an inertial scroll is written wrong and is invisible on the machine it was
    ///     tuned on. <see cref="SmoothingConstant" /> one screen up makes the same choice for the
    ///     same reason; this constant is far larger because a fling is meant to be watched travelling
    ///     and an eased jump is meant to be over.
    /// </remarks>
    public const float FlingDecayConstant = 0.325f;

    /// <summary>How slow a fling has to get before it is over, in pixels per second.</summary>
    /// <remarks>
    ///     ⚠ <b>An exponential decay never reaches zero</b>, so without a floor the offset would go
    ///     on changing by a millionth of a pixel every frame for the life of the document — and every
    ///     one of those frames invalidates positions and rebuilds the draw list. It is the same
    ///     argument the half-pixel terminator in <see cref="Advance" /> makes, in the units this one
    ///     is measured in.
    /// </remarks>
    public const float FlingStopSpeed = 8f;

    /// <summary>How much of a new velocity reading is believed, against what was already known.</summary>
    /// <remarks>
    ///     ⚠ <b>Smoothed, because the last sample before a finger lifts is the worst one.</b> A hand
    ///     slows fractionally as it releases, so a fling taken from the final frame alone is
    ///     consistently slower than the gesture felt — and one stalled frame mid-drag would otherwise
    ///     report a velocity of nothing at all. A running average over the tail of the drag is what
    ///     every platform's velocity tracker computes; this is the cheapest honest form of one.
    /// </remarks>
    public const float VelocityBlend = 0.35f;

    /// <summary>Whether a fling is still carrying the content.</summary>
    public bool IsFlinging { get; private set; }

    /// <summary>Drops any fling in flight, leaving the offset where it got to.</summary>
    public void StopFling() {
        IsFlinging = false;
        velocityTop = 0f;
        velocityLeft = 0f;
    }

    /// <summary>Scrolls the content under the finger, and lets go of it with whatever speed it had.</summary>
    /// <remarks>
    ///     ⚠ <b>The delta is subtracted.</b> A drag moves the <i>content</i>, and the offset is how
    ///     far down the content the viewport is — so dragging downwards moves the viewport up. The
    ///     bars and the wheel both write the offset directly and do not invert; this is the one path
    ///     where the number the user is moving is not the number being stored.
    /// </remarks>
    void Dragged(DragEvent args) {
        // ⚠ The device, not only the property. A finger — or a pen, which is a finger for this
        // purpose because neither has a cursor to select with — drags the content whatever the
        // application asked for; a mouse does it only when asked. See `DragToScroll`.
        if (!DragToScroll && args.PointerType is not (PointerType.Touch or PointerType.Pen)) {
            return;
        }

        switch (args.Stage) {
            case DragStage.Started:
                // Direct manipulation, so it takes the content away from anything easing it — and
                // from its own previous fling, which is what makes a second flick continue the first
                // rather than fight it.
                Began();
                Settle();
                StopFling();

                dragging = true;
                sampledTop = ScrollTop;
                sampledLeft = ScrollLeft;

                args.Handled = true;
                break;

            case DragStage.Moved when dragging:
                ScrollTop -= args.DeltaY;
                ScrollLeft -= args.DeltaX;

                args.Handled = true;
                break;

            case DragStage.Completed when dragging:
                dragging = false;

                // ⚠ The snap runs only when there is no fling to run first. A fling that comes to
                // rest somewhere a snap point does not want is snapped when it stops — see
                // <see cref="Fling" /> — and snapping now would take the content away from the
                // finger's speed at the moment the user expects it to keep going.
                if (MathF.Abs(velocityTop) >= FlingStopSpeed || MathF.Abs(velocityLeft) >= FlingStopSpeed) {
                    IsFlinging = true;
                } else {
                    StopFling();
                    Ended();
                }

                args.Handled = true;
                break;

            case DragStage.Cancelled when dragging:
                dragging = false;
                StopFling();
                Ended();

                args.Handled = true;
                break;

            default:
                break;
        }
    }

    /// <summary>Measures how fast the content is being dragged, on the clock that will carry it on.</summary>
    /// <remarks>
    ///     ⚠ <b>Sampled per tick rather than per drag event, and that is what makes it honest.</b>
    ///     <see cref="DragEvent" /> carries no timestamp, and several of them can arrive between two
    ///     frames — so a velocity computed per event would divide by a zero interval or invent one.
    ///     Measuring the offset's change over the document's own tick means a fling can never be
    ///     faster than the frames that produced it, and means a test that steps the clock gets the
    ///     same number on every machine.
    /// </remarks>
    void Track(float elapsed) {
        var top = (ScrollTop - sampledTop) / elapsed;
        var left = (ScrollLeft - sampledLeft) / elapsed;

        sampledTop = ScrollTop;
        sampledLeft = ScrollLeft;

        velocityTop += (top - velocityTop) * VelocityBlend;
        velocityLeft += (left - velocityLeft) * VelocityBlend;
    }

    /// <summary>Carries the content on after the finger has gone, and decides where it stops.</summary>
    void Fling(float elapsed) {
        var decay = MathF.Exp(-elapsed / FlingDecayConstant);

        var top = ScrollTop;
        var left = ScrollLeft;

        // The integral of a decaying velocity over the interval, rather than speed × time: at a
        // frame long enough to matter the two differ by the whole of the deceleration, and a fling
        // stepped in one large tick would travel further than the same fling stepped in twenty.
        ScrollTop = top + (velocityTop * FlingDecayConstant * (1f - decay));
        ScrollLeft = left + (velocityLeft * FlingDecayConstant * (1f - decay));

        velocityTop *= decay;
        velocityLeft *= decay;

        // ⚠ An axis that did not move is an axis that has reached an end, and its speed is spent.
        // Without this the fling goes on decaying against a clamp for a second after it visibly
        // stopped, and a flick back the other way inside that second starts from a velocity the
        // content has not had since it hit the edge. There is no bounce: elastic overscroll needs an
        // offset that may leave its range, and `ScrollTop` coerces.
        if (ScrollTop.Equals(top)) {
            velocityTop = 0f;
        }

        if (ScrollLeft.Equals(left)) {
            velocityLeft = 0f;
        }

        if (MathF.Abs(velocityTop) >= FlingStopSpeed || MathF.Abs(velocityLeft) >= FlingStopSpeed) {
            return;
        }

        StopFling();

        // Where a fling comes to rest is where the gesture came to rest, so this is the moment the
        // snap is defined at — the same moment `ScrollBar.ScrollEnded` and the wheel's idle
        // terminator name for the gestures that have one.
        Ended();
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

    // ── Scroll snapping ─────────────────────────────────────────────────────────────────────
    //
    // ⚠ <b>This is the one family in doc 43 § Part 8 § 3 whose deferral premise was true.</b> The
    // other twenty-two were four property reads inside a control that already scrolled; this one is
    // an algorithm and a gesture. The algorithm is the easy half — a snap position is one
    // subtraction per candidate per axis, below — and the gesture is the half that had nothing to
    // build on: neither the wheel nor a drag on the bar had an *end*, and "comes to rest" is the
    // only moment at which a snap is defined. `ScrollBar.ScrollEnded` is the drag's terminator and
    // `SnapIdleSeconds` is the wheel's.

    /// <summary>How long the wheel has to be still before the scroll is deemed to have come to rest.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Wall-clock, and it has to be — a wheel is a stream of deltas with no terminator
    ///         in it.</b> A finger leaving a trackpad, a wheel that stops turning and a hand that
    ///         paused mid-flick are the same silence, and no amount of counting frames or deltas
    ///         tells them apart; every browser answers this with an idle timeout for the same reason.
    ///         What is <i>not</i> wall-clock is anything that asserts on it: the clock is
    ///         <see cref="UiDocument.Ticked" />'s, which a test drives by hand, so "after 125 ms of
    ///         stillness" is a statement about frames the test delivered rather than about how busy
    ///         the machine was.
    ///     </para>
    ///     <para>
    ///         Measured against the last <i>tick</i> rather than against the wheel event's own
    ///         timestamp, because the two need not be the same clock — a platform head is free to
    ///         stamp input from a monotonic source the frame loop never reads, and a mismatch there
    ///         would either snap during a gesture or never snap at all.
    ///     </para>
    /// </remarks>
    public const float SnapIdleSeconds = 0.125f;

    /// <summary>How near a candidate has to be for <c>proximity</c> to snap to it, as a fraction of the viewport.</summary>
    /// <remarks>
    ///     ⚠ <b>CSS leaves this to the implementation, so it is a number rather than a reading.</b>
    ///     The whole difference between the two strictnesses is that <c>mandatory</c> always lands on
    ///     a candidate and <c>proximity</c> only does so when one is close enough to be what the
    ///     reader plainly meant — a threshold of one makes the two identical, and a threshold of zero
    ///     makes <c>proximity</c> the inert half of a family this table is not allowed to register.
    /// </remarks>
    public const float SnapProximity = 0.25f;

    bool gesturing;
    TimeSpan gestureAt;
    float gestureTop;
    float gestureLeft;

    readonly List<UiElement> candidates = [];

    /// <summary>Notes that a direct scroll has started, and where from.</summary>
    /// <remarks>
    ///     ⚠ <b>Where from is the part <c>scroll-snap-stop: always</c> cannot be read without.</b>
    ///     <c>always</c> is not a claim about where a scroll ended; it is a claim about what it went
    ///     <i>past</i> on the way, so the origin has to survive the whole gesture. Re-entrant on
    ///     purpose: every wheel notch and every move of the thumb calls this, and only the first of
    ///     them is the start.
    /// </remarks>
    void Began() {
        if (gesturing) {
            return;
        }

        gesturing = true;
        gestureTop = ScrollTop;
        gestureLeft = ScrollLeft;
    }

    /// <summary>The gesture is over: come to rest wherever <c>scroll-snap-type</c> says to.</summary>
    void Ended() {
        if (!gesturing) {
            return;
        }

        gesturing = false;

        var (top, left) = SnapPositions(ScrollTop, ScrollLeft, gestureTop, gestureLeft);
        if (!top.Equals(ScrollTop) || !left.Equals(ScrollLeft)) {
            Move(top, left);
        }
    }

    /// <summary>Where a scroll that wanted to end at an offset actually ends.</summary>
    /// <param name="top">The offset wanted, already clamped.</param>
    /// <param name="left">Ditto, horizontally.</param>
    /// <param name="fromTop">Where the scroll started, for <c>scroll-snap-stop</c>.</param>
    /// <param name="fromLeft">Ditto.</param>
    /// <returns>The offsets to come to rest at, which are the arguments when nothing snaps.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The candidates' bounds are read at the offset the view is at <i>now</i>, not at
    ///         <paramref name="top" />.</b> A snap position is therefore
    ///         <c>ScrollTop + (candidate edge − snapport edge)</c> — an absolute offset that does not
    ///         depend on where the scroll wanted to go, which is what lets one walk answer both "the
    ///         nearest one to here" and "the first one between here and there".
    ///     </para>
    ///     <para>
    ///         Pure but for the walk: it writes nothing, so <see cref="ScrollTo" /> can ask it where
    ///         a destination would land before deciding how to get there.
    ///     </para>
    /// </remarks>
    (float Top, float Left) SnapPositions(float top, float left, float fromTop, float fromLeft) {
        var (axis, mandatory) = SnapType();
        if (axis == ScrollSnapAxis.None) {
            return (top, left);
        }

        var vertical = axis is ScrollSnapAxis.Y or ScrollSnapAxis.Both;
        var horizontal = axis is ScrollSnapAxis.X or ScrollSnapAxis.Both;

        candidates.Clear();
        Gather(Content);

        if (candidates.Count == 0) {
            return (top, left);
        }

        var padding = InsetOf(this);
        var viewport = Bounds;

        var nearTop = viewport.Top + padding.Top;
        var farBottom = viewport.Bottom - padding.Bottom;
        var nearLeft = viewport.Left + padding.Left;
        var farRight = viewport.Right - padding.Right;

        var choiceY = default(SnapChoice);
        var choiceX = default(SnapChoice);

        foreach (var candidate in candidates) {
            var (block, inline) = AlignOf(candidate);

            var margin = InsetOf(candidate);
            var area = candidate.Bounds;
            var always = candidate.Style.TryGet(names.SnapStop, out var stop) && stop == names.Always;

            if (vertical && block != ScrollSnapAlign.None) {
                var position = block switch {
                    ScrollSnapAlign.Start => ScrollTop + (area.Top - margin.Top) - nearTop,
                    ScrollSnapAlign.End => ScrollTop + area.Bottom + margin.Bottom - farBottom,
                    _ => ScrollTop
                        + (((area.Top - margin.Top) + area.Bottom + margin.Bottom) * 0.5f)
                        - ((nearTop + farBottom) * 0.5f)
                };

                Consider(ref choiceY, Math.Clamp(position, 0f, MaximumTop), fromTop, top, always);
            }

            if (horizontal && inline != ScrollSnapAlign.None) {
                var position = inline switch {
                    ScrollSnapAlign.Start => ScrollLeft + (area.Left - margin.Left) - nearLeft,
                    ScrollSnapAlign.End => ScrollLeft + area.Right + margin.Right - farRight,
                    _ => ScrollLeft
                        + (((area.Left - margin.Left) + area.Right + margin.Right) * 0.5f)
                        - ((nearLeft + farRight) * 0.5f)
                };

                Consider(ref choiceX, Math.Clamp(position, 0f, MaximumLeft), fromLeft, left, always);
            }
        }

        candidates.Clear();

        return (
            Resolve(choiceY, top, mandatory, viewport.Height * SnapProximity),
            Resolve(choiceX, left, mandatory, viewport.Width * SnapProximity)
        );
    }

    /// <summary>What one candidate is worth on one axis: the nearest, and whether it may be skipped.</summary>
    struct SnapChoice {
        public bool Any;
        public float Nearest;
        public float Distance;

        public bool Blocked;
        public float Stop;
        public float Travelled;
    }

    /// <summary>Folds one candidate's snap position into an axis's answer.</summary>
    /// <remarks>
    ///     ⚠ <b>The two halves answer different questions and the second is not a tie-break of the
    ///     first.</b> "Nearest" is measured from where the scroll <i>ended</i>; a
    ///     <c>scroll-snap-stop: always</c> candidate is chosen by how little was travelled to reach
    ///     it, because the rule is that a scroll may not pass one — so of two that were both passed
    ///     over, the one that stops the scroll is the earlier, which is very often the further from
    ///     where it wanted to end up.
    /// </remarks>
    static void Consider(ref SnapChoice choice, float position, float from, float to, bool always) {
        var distance = MathF.Abs(position - to);

        if (!choice.Any || distance < choice.Distance) {
            choice.Any = true;
            choice.Nearest = position;
            choice.Distance = distance;
        }

        if (!always) {
            return;
        }

        // Strictly past the origin and no further than the destination, which is what "passed over"
        // means — and which makes a scroll that went nowhere (`from == to`, every programmatic call
        // that is not a page or a key) pass over nothing at all rather than everything.
        var low = MathF.Min(from, to);
        var high = MathF.Max(from, to);

        if (position <= low || position > high) {
            return;
        }

        var travelled = MathF.Abs(position - from);
        if (!choice.Blocked || travelled < choice.Travelled) {
            choice.Blocked = true;
            choice.Stop = position;
            choice.Travelled = travelled;
        }
    }

    /// <summary>What an axis comes to rest at, given everything considered on it.</summary>
    static float Resolve(SnapChoice choice, float wanted, bool mandatory, float proximity) {
        if (choice.Blocked) {
            return choice.Stop;
        }

        if (!choice.Any) {
            return wanted;
        }

        return mandatory || choice.Distance <= proximity ? choice.Nearest : wanted;
    }

    /// <summary>Collects every element under this view that declares itself a snap candidate.</summary>
    /// <remarks>
    ///     ⚠ <b>It does not descend into another <see cref="ScrollView" />, and that is the rule
    ///     rather than an optimisation.</b> A snap area belongs to its <i>nearest</i> scroll
    ///     container, so a row inside an inner list is the inner list's candidate and not this one's
    ///     — an outer view that gathered them would snap to a position the inner view is about to
    ///     move out from under it. The inner view itself may still carry a
    ///     <c>scroll-snap-align</c> of its own, which is why the test comes after the check and not
    ///     before it.
    /// </remarks>
    void Gather(UiElement element) {
        foreach (var child in element.Children) {
            if (AlignOf(child) is not (ScrollSnapAlign.None, ScrollSnapAlign.None)) {
                candidates.Add(child);
            }

            if (child is not ScrollView) {
                Gather(child);
            }
        }
    }

    /// <summary>What <c>scroll-snap-type</c> says on this view.</summary>
    /// <remarks>
    ///     ⚠ <b>Read as text rather than through <see cref="UiDocument.KeywordOf" />, because the
    ///     value is legally two words.</b> <c>scroll-snap-type: y mandatory</c> is one declaration
    ///     carrying an axis and a strictness, and every accessor on <c>StyleAccess</c> answers
    ///     <c>null</c> to a two-word value by design. The two keywords are order-independent in this
    ///     reading, which is laxer than the grammar and is the right way round: the strictness
    ///     reaches this through a <c>var()</c> substitution — <c>snap-y</c> and <c>snap-mandatory</c>
    ///     are two classes and the fragment is what joins them — so what arrives here is a string
    ///     assembled by the cascade rather than one a person typed.
    /// </remarks>
    (ScrollSnapAxis Axis, bool Mandatory) SnapType() {
        if (!Style.TryGet(names.SnapType, out var id)) {
            return (ScrollSnapAxis.None, false);
        }

        var text = Document.Styles.Values.NameOf(id);

        var axis = Mentions(text, "both") ? ScrollSnapAxis.Both
            : Mentions(text, "x") ? ScrollSnapAxis.X
            : Mentions(text, "y") ? ScrollSnapAxis.Y
            : ScrollSnapAxis.None;

        return (axis, Mentions(text, "mandatory"));
    }

    /// <summary>What <c>scroll-snap-align</c> says on one element, per axis.</summary>
    /// <remarks>
    ///     Block first and inline second, which is the CSS order, and one word means both — so
    ///     <c>scroll-snap-align: start</c> is a candidate on whichever axis the container snaps and
    ///     <c>start none</c> is one vertically only.
    /// </remarks>
    (ScrollSnapAlign Block, ScrollSnapAlign Inline) AlignOf(UiElement element) {
        if (!element.Style.TryGet(names.SnapAlign, out var id)) {
            return (ScrollSnapAlign.None, ScrollSnapAlign.None);
        }

        var text = Document.Styles.Values.NameOf(id);
        var space = text.IndexOf(' ');

        if (space < 0) {
            var both = Alignment(text.AsSpan());
            return (both, both);
        }

        return (Alignment(text.AsSpan(0, space)), Alignment(text.AsSpan(space + 1).Trim()));
    }

    static ScrollSnapAlign Alignment(ReadOnlySpan<char> word) =>
        word switch {
            "start" => ScrollSnapAlign.Start,
            "center" => ScrollSnapAlign.Center,
            "end" => ScrollSnapAlign.End,
            _ => ScrollSnapAlign.None
        };

    /// <summary>Whether a value's word list contains one whole word.</summary>
    /// <remarks>
    ///     ⚠ Whole words, or <c>x</c> matches inside <c>both</c> and every container that snaps on
    ///     both axes snaps on one.
    /// </remarks>
    static bool Mentions(string text, string word) {
        for (var index = text.IndexOf(word, StringComparison.Ordinal); index >= 0;) {
            var opens = index == 0 || text[index - 1] == ' ';
            var closes = index + word.Length == text.Length || text[index + word.Length] == ' ';

            if (opens && closes) {
                return true;
            }

            index = text.IndexOf(word, index + word.Length, StringComparison.Ordinal);
        }

        return false;
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

            SnapType = properties.Intern("scroll-snap-type");
            SnapAlign = properties.Intern("scroll-snap-align");
            SnapStop = properties.Intern("scroll-snap-stop");

            Behavior = properties.Intern("scroll-behavior");
            Overscroll = properties.Intern("overscroll-behavior");
            OverscrollX = properties.Intern("overscroll-behavior-x");
            OverscrollY = properties.Intern("overscroll-behavior-y");
            Direction = properties.Intern("direction");

            Smooth = values.Intern("smooth");
            Auto = values.Intern("auto");
            Rtl = values.Intern("rtl");
            Always = values.Intern("always");
        }

        public static ScrollNames Of(UiDocument document) => Cache.GetValue(document, static key => new ScrollNames(key));

        public EdgeIds Margin { get; }
        public EdgeIds Padding { get; }
        public int SnapType { get; }
        public int SnapAlign { get; }
        public int SnapStop { get; }
        public int Behavior { get; }
        public int Overscroll { get; }
        public int OverscrollX { get; }
        public int OverscrollY { get; }
        public int Direction { get; }
        public int Smooth { get; }
        public int Auto { get; }
        public int Rtl { get; }
        public int Always { get; }
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

        // ⚠ A `mandatory` container is snapped at rest and not merely on the way to rest, which is
        // the half of CSS Scroll Snap that is not about gestures at all: content inserted above the
        // viewport, or a resize, moves every candidate out from under an offset nobody touched. Only
        // `mandatory`, because `proximity` explicitly permits resting between candidates; and never
        // during a gesture or an easing, which own the offset while they run.
        if (gesturing || IsScrolling) {
            return;
        }

        var (axis, mandatory) = SnapType();
        if (!mandatory || axis == ScrollSnapAxis.None) {
            return;
        }

        // Assigned rather than routed through `Move`: a smooth re-snap would set `IsScrolling` from
        // inside the layout pass that will run again next frame, and the two would take turns.
        var (top, left) = SnapPositions(ScrollTop, ScrollLeft, ScrollTop, ScrollLeft);

        ScrollTop = top;
        ScrollLeft = left;
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
        Began();
        Settle();

        // ⚠ The tick clock, not `args.Timestamp` — see `SnapIdleSeconds`. Stamped before the scroll
        // rather than after it so that a wheel a fully-scrolled view chains outwards still counts as
        // this view's gesture continuing: an idle timer that only advanced when the offset moved
        // would fire mid-flick at every stop.
        gestureAt = last;

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
