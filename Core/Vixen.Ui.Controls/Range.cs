// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Mathematics;
using Vixen.Input;

namespace Vixen.Ui.Controls;

/// <summary>Anything with a value between two bounds.</summary>
/// <remarks>
///     <para>
///         A slider, a range slider and a progress bar are one arithmetic problem — where does a
///         number sit between two others — and three ways of showing the answer. The problem is
///         solved once here.
///     </para>
///     <para>
///         ⚠ <b>These controls draw themselves rather than being made of parts.</b> Everything else
///         in this set is elements the cascade positions, and a slider cannot be: a thumb at 37%
///         needs a length no stylesheet was given and no flexbox rule produces. The alternative —
///         writing the position through as an offset — settles a frame late on every resize, because
///         the position depends on a width that only exists after layout has run. So the geometry is
///         computed in <see cref="UiElement.OnDraw" />, where the width is known and the answer is
///         used immediately.
///     </para>
///     <para>
///         The cost is that the parts cannot be selected on individually. It is paid back in custom
///         properties: <c>--track-color</c>, <c>--fill-color</c>, <c>--thumb-color</c> and
///         <c>--thumb-size</c> are read from the cascade, so a theme still decides how a slider
///         looks and a <c>:hover</c> rule still changes it.
///     </para>
/// </remarks>
public abstract partial class RangeBase : Control {
    int trackColor;
    int fillColor;
    int thumbColor;
    int thumbBorderColor;
    int thumbSize;

    /// <summary>The bottom of the range.</summary>
    [UiProperty(Changed = nameof(OnBoundsChanged))]
    public partial float Minimum { get; set; }

    /// <summary>The top of it.</summary>
    [UiProperty(Default = 1f, Changed = nameof(OnBoundsChanged))]
    public partial float Maximum { get; set; }

    /// <summary>How far one arrow press moves it. Zero is continuous.</summary>
    [UiProperty]
    public partial float Step { get; set; }

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        trackColor = Document.PropertyId("--track-color");
        fillColor = Document.PropertyId("--fill-color");
        thumbColor = Document.PropertyId("--thumb-color");
        thumbBorderColor = Document.PropertyId("--thumb-border-color");
        thumbSize = Document.PropertyId("--thumb-size");

        // The starting axis, so that `.horizontal` selects the default rather than only the one
        // somebody set back. `OnOrientationChanged` keeps it in step from here.
        AddClass(Separator.ClassOf(Orientation));
    }

    /// <summary>Where a value sits in the range, as zero to one.</summary>
    /// <remarks>
    ///     ⚠ Zero when the bounds are equal, rather than a division by nothing. A range whose ends
    ///     meet has no inside, and every position in it is equally the start.
    /// </remarks>
    protected float Fraction(float value) {
        var span = Maximum - Minimum;
        return span <= 0f ? 0f : Math.Clamp((value - Minimum) / span, 0f, 1f);
    }

    /// <summary>The value a fraction of the way along the range, snapped to <see cref="Step" />.</summary>
    protected float ValueAt(float fraction) {
        var value = Minimum + (Math.Clamp(fraction, 0f, 1f) * (Maximum - Minimum));

        if (Step > 0f) {
            value = Minimum + (MathF.Round((value - Minimum) / Step) * Step);
        }

        return Clamp(value);
    }

    /// <summary>Brings a value inside the bounds and onto a step.</summary>
    protected float Snap(float value) =>
        Clamp(Step > 0f ? Minimum + (MathF.Round((value - Minimum) / Step) * Step) : value);

    /// <summary>Brings a value inside the bounds, whichever way round they currently are.</summary>
    /// <remarks>
    ///     ⚠ <b>Not <c>Math.Clamp(value, Minimum, Maximum)</c>, and the reason is that the bounds are
    ///     two properties.</b> They are therefore set one at a time, so a caller configuring a
    ///     slider for <c>[Range(8, 4096)]</c> passes through an instant where <see cref="Minimum" />
    ///     is 8 and <see cref="Maximum" /> is still its default 1 — and each setter re-snaps the
    ///     value, so <c>Math.Clamp</c> throws from inside a property assignment. A control that
    ///     cannot be configured in the order its own API offers is the bug; an inverted range for one
    ///     statement is not.
    /// </remarks>
    float Clamp(float value) => Math.Clamp(value, MathF.Min(Minimum, Maximum), MathF.Max(Minimum, Maximum));

    /// <summary>Which way it runs.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Here rather than on <see cref="Slider" />, because every helper below reads
    ///         it.</b> The rail, the thumb and the fraction under a pointer are one arithmetic problem
    ///         with an axis in it, and a subclass that had to re-derive the axis for each of them is
    ///         the version of this that gets one of the three wrong.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Vertical runs bottom-to-top, which is not what the coordinate system does.</b> A
    ///         fader whose maximum is at the top is what every mixer, every volume control and every
    ///         hardware desk has; one that grew downwards would be read backwards by everybody who
    ///         has ever touched one.
    ///     </para>
    /// </remarks>
    [UiProperty(Changed = nameof(OnOrientationChanged))]
    public partial Orientation Orientation { get; set; }

    /// <summary>Whether it runs up the screen rather than across it.</summary>
    protected bool IsVertical => Orientation == Orientation.Vertical;

    /// <summary>The strip the fill and the thumbs are drawn on.</summary>
    /// <remarks>
    ///     Inset by half a thumb at each end, so that a thumb at either extreme is inside the
    ///     control rather than half outside it. Every slider that does not do this has a thumb that
    ///     is clipped at zero and at one hundred percent.
    /// </remarks>
    protected Rectangle Rail(Rectangle bounds) {
        var thumb = ThumbSize;

        if (IsVertical) {
            var width = MathF.Min(bounds.Width, MathF.Max(2f, thumb * 0.3f));

            return new Rectangle(
                bounds.X + ((bounds.Width - width) * 0.5f),
                bounds.Y + (thumb * 0.5f),
                width,
                MathF.Max(0f, bounds.Height - thumb)
            );
        }

        var height = MathF.Min(bounds.Height, MathF.Max(2f, thumb * 0.3f));

        return new Rectangle(
            bounds.X + (thumb * 0.5f),
            bounds.Y + ((bounds.Height - height) * 0.5f),
            MathF.Max(0f, bounds.Width - thumb),
            height
        );
    }

    /// <summary>How long the rail is along the axis it runs on.</summary>
    protected float Extent(Rectangle rail) => IsVertical ? rail.Height : rail.Width;

    /// <summary>And how wide it is across that axis, which is what rounds its ends.</summary>
    protected float Thickness(Rectangle rail) => IsVertical ? rail.Width : rail.Height;

    /// <summary>The part of the rail between two fractions, as a rectangle.</summary>
    /// <remarks>
    ///     ⚠ <b>Measured from the far end when vertical</b>, because fraction zero is at the bottom.
    ///     See <see cref="Orientation" />.
    /// </remarks>
    protected Rectangle Span(Rectangle rail, float from, float to) {
        if (!IsVertical) {
            return new Rectangle(rail.X + (rail.Width * from), rail.Y, rail.Width * (to - from), rail.Height);
        }

        return new Rectangle(
            rail.X,
            rail.Y + (rail.Height * (1f - to)),
            rail.Width,
            rail.Height * (to - from)
        );
    }

    /// <summary>How wide a thumb is.</summary>
    protected float ThumbSize => Document.LengthOf(Style, thumbSize) ?? 14f;

    /// <summary>The track's colour.</summary>
    protected Color4 TrackColor => Document.ColorOf(Style, trackColor) ?? new Color4(0.5f, 0.5f, 0.5f, 0.35f);

    /// <summary>The filled part's colour.</summary>
    protected Color4 FillColor => Document.ColorOf(Style, fillColor) ?? Document.ForegroundOf(this);

    /// <summary>A thumb's colour.</summary>
    protected Color4 ThumbColor => Document.ColorOf(Style, thumbColor) ?? Document.ForegroundOf(this);

    /// <summary>The ring drawn just inside a thumb's edge. Transparent draws none.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A thumb is the one part of a control the user has to <i>find</i> before it can be
    ///         used, and a fill alone cannot promise that.</b> The light palette's
    ///         <c>--thumb-color</c> is <c>#ffffff</c> and its <c>--surface</c> is <c>#ffffff</c>, so
    ///         a slider at its minimum drew a white disc on white paper and had no visible thumb at
    ///         all — the fill was legible only where it happened to overlap
    ///         <see cref="FillColor" />. See <c>ControlTheme.vcss</c>, which is where the ring's
    ///         colour is decided.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Its own token rather than <c>--border</c>, and that is the whole judgement.</b>
    ///         <c>--border</c> separates two surfaces that are already next to each other; at the
    ///         light palette's <c>#d8dbe0</c> it is 1.4:1 against the surface, which is nowhere near
    ///         the 3:1 WCAG asks of the boundary of a control somebody has to grab. A theme that
    ///         wants the old look back sets this to <c>transparent</c> and pays no draw command for
    ///         it.
    ///     </para>
    /// </remarks>
    protected Color4 ThumbBorderColor => Document.ColorOf(Style, thumbBorderColor) ?? default;

    /// <summary>Draws the unfilled strip.</summary>
    protected void DrawTrack(DrawContext context, Rectangle rail) =>
        context.FillRectangle(rail, TrackColor, Thickness(rail) * 0.5f);

    /// <summary>How wide a thumb's ring is. One pixel, which is what every other border here is.</summary>
    const float ThumbBorderWidth = 1f;

    /// <summary>Draws a thumb centred on a point of the rail.</summary>
    /// <remarks>
    ///     Fill then ring, so the ring is the outline of the disc rather than a band under it. See
    ///     <see cref="ThumbBorderColor" /> for why a thumb has one at all.
    /// </remarks>
    protected void DrawThumb(DrawContext context, Rectangle rail, float fraction) {
        var size = ThumbSize;

        var box = IsVertical
            ? new Rectangle(
                context.Bounds.X + ((context.Bounds.Width - size) * 0.5f),
                rail.Y + (rail.Height * (1f - fraction)) - (size * 0.5f),
                size,
                size
            )
            : new Rectangle(
                rail.X + (rail.Width * fraction) - (size * 0.5f),
                context.Bounds.Y + ((context.Bounds.Height - size) * 0.5f),
                size,
                size
            );

        context.FillRectangle(box, ThumbColor, size * 0.5f);

        var ring = ThumbBorderColor;

        // A theme that set nothing, or set `transparent`, costs no command rather than an invisible
        // one — the same test `DrawContext.Styled` applies to a box style nobody filled in.
        if (ring.A > 0f) {
            context.StrokeRectangle(box, ring, ThumbBorderWidth, BoxStyle.Rounded(CornerRadii.Uniform(size * 0.5f)));
        }
    }

    /// <summary>Where along the rail a document-space point falls, as zero to one.</summary>
    /// <remarks>
    ///     ⚠ <b>Against the rail rather than against the control.</b> The rail is inset by half a
    ///     thumb at each end, so a press on the extreme edge of the control is a fraction outside the
    ///     range — which clamps to the end, which is what the user meant. Measuring against the
    ///     control's own size instead would make the thumb lag the cursor by half its width.
    /// </remarks>
    protected float FractionAt(float x, float y) {
        var rail = Rail(Bounds);
        var extent = Extent(rail);

        if (extent <= 0f) {
            return 0f;
        }

        // Inverted for the vertical case, because fraction zero is at the bottom. See `Orientation`.
        return IsVertical ? 1f - ((y - rail.Y) / extent) : (x - rail.X) / extent;
    }

    void OnBoundsChanged(float previous, float current) => OnBoundsChanged();

    /// <summary>Puts the axis on the element as a class, so a theme can size the two differently.</summary>
    /// <remarks>
    ///     A vertical fader wants a width and a horizontal one wants a height, and neither default is
    ///     right for the other — <c>Separator</c>'s classes are the same arrangement for the same
    ///     reason.
    /// </remarks>
    void OnOrientationChanged(Orientation previous, Orientation current) {
        RemoveClass(Separator.ClassOf(previous));
        AddClass(Separator.ClassOf(current));
    }

    /// <summary>Called when the bounds move, so a subclass can bring its value back inside them.</summary>
    protected virtual void OnBoundsChanged() {
    }
}

/// <summary>A value chosen by dragging a thumb along a track.</summary>
public sealed partial class Slider : RangeBase {
    bool dragging;

    /// <inheritdoc />
    protected override string TagName => "slider";

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>A widget role with no words of its own, which is deliberate and is the same
    ///     decision <c>TextField</c> makes.</b> A slider is a number and nothing else; what it is a
    ///     number <i>of</i> is the caption beside it, which is somebody else's element. One
    ///     <c>AddAccessibleRelation(AccessibleRelation.LabelledBy, caption)</c> at the call site is
    ///     the whole of it, and a slider nobody did that to reports <c>null</c> so that
    ///     <c>AccessibilitySnapshot.Unnamed</c> can fail it rather than inventing something
    ///     plausible.
    /// </remarks>
    protected override AccessibleRole NativeRole => AccessibleRole.Slider;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Invariant, and it is a judgement rather than an oversight.</b> What a platform
    ///     bridge wants here is a number it can re-present in the user's own locale and units — and
    ///     what a control this far down the stack knows is neither. A slider whose value should be
    ///     announced as "40 percent" or "1.5 metres" is one whose application overrides
    ///     <c>AccessibleValue</c>; formatting a bare float in the current culture would produce a
    ///     string a bridge has to parse back.
    /// </remarks>
    protected override string? NativeAccessibleValue => Value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Where the thumb is.</summary>
    [UiProperty(Coerce = nameof(CoerceValue), Changed = nameof(OnValueChanged))]
    public partial float Value { get; set; }

    /// <summary>Raised when the value changes, however it changed.</summary>
    public event Action<Slider, float>? ValueChanged;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        AddHandler<PointerEvent>(static (element, args) => ((Slider) element).Pointed(args));
        AddHandler<KeyEvent>(static (element, args) => ((Slider) element).Keyed(args));
    }

    /// <inheritdoc />
    protected override void OnDraw(DrawContext context) {
        base.OnDraw(context);

        var rail = Rail(context.Bounds);
        if (Extent(rail) <= 0f) {
            return;
        }

        var fraction = Fraction(Value);

        DrawTrack(context, rail);
        context.FillRectangle(Span(rail, 0f, fraction), FillColor, Thickness(rail) * 0.5f);

        DrawThumb(context, rail, fraction);
    }

    /// <inheritdoc />
    protected override void OnBoundsChanged() => Value = Snap(Value);

    float CoerceValue(float value) => Snap(value);

    void OnValueChanged(float previous, float current) {
        Raise(new ValueChangedEvent<float> { Previous = previous, Value = current });
        ValueChanged?.Invoke(this, current);
    }

    void Pointed(PointerEvent args) {
        switch (args.Action) {
            case PointerAction.Pressed when args.Button == PointerButton.Primary:
                dragging = true;

                Document.Focus(this);
                Document.CapturePointer(this);
                Value = ValueAt(FractionAt(args.X, args.Y));

                args.Handled = true;
                break;

            case PointerAction.Moved when dragging:
                Value = ValueAt(FractionAt(args.X, args.Y));
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

    void Keyed(KeyEvent args) {
        if (args.Action != KeyAction.Pressed) {
            return;
        }

        // A step of zero means continuous, and a continuous slider still has to be movable by
        // keyboard — so the arrows fall back to a hundredth of the range, which is what a slider
        // with no declared step is asking for.
        var step = Step > 0f ? Step : (Maximum - Minimum) * 0.01f;

        var moved = args.Key switch {
            InputKey.Left or InputKey.Down => Value - step,
            InputKey.Right or InputKey.Up => Value + step,
            InputKey.PageDown => Value - (step * 10f),
            InputKey.PageUp => Value + (step * 10f),
            InputKey.Home => Minimum,
            InputKey.End => Maximum,
            _ => float.NaN
        };

        if (float.IsNaN(moved)) {
            return;
        }

        Value = moved;
        args.Handled = true;
    }
}

/// <summary>Two values, and the span between them.</summary>
/// <remarks>
///     ⚠ <b>The thumbs may meet but never cross.</b> Dragging the low thumb past the high one stops
///     it rather than swapping them, because swapping means the thumb under the cursor is suddenly a
///     different thumb — and the drag that was raising the ceiling starts lowering the floor without
///     the user having done anything.
/// </remarks>
public sealed partial class RangeSlider : RangeBase {
    bool draggingHigh;
    bool dragging;

    /// <inheritdoc />
    protected override string TagName => "range-slider";

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b><c>group</c> and not <c>slider</c>, and the difference is a limitation this states
    ///     rather than papers over.</b> WAI-ARIA's answer for a two-thumb range is a <c>group</c>
    ///     containing one <c>slider</c> per thumb, because each thumb is separately focusable and
    ///     separately announced. The thumbs here are drawn rather than elements — see the class's
    ///     own remarks about why every control in this set that draws a thumb draws it — so there is
    ///     nothing to put the two roles on. A single <c>slider</c> role would be worse than this:
    ///     <c>aria-valuenow</c> is one number, and a screen reader told the low end is the value
    ///     would announce a control that never changes when the high end moves.
    /// </remarks>
    protected override AccessibleRole NativeRole => AccessibleRole.Group;

    /// <inheritdoc />
    /// <remarks>Both ends, because either alone is a different control's value.</remarks>
    protected override string? NativeAccessibleValue =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Low:0.###} \u2013 {High:0.###}"
        );

    /// <summary>The bottom of the chosen span.</summary>
    [UiProperty(Coerce = nameof(CoerceLow), Changed = nameof(OnSpanChanged))]
    public partial float Low { get; set; }

    /// <summary>The top of it.</summary>
    [UiProperty(Default = 1f, Coerce = nameof(CoerceHigh), Changed = nameof(OnSpanChanged))]
    public partial float High { get; set; }

    /// <summary>Raised when either end moves.</summary>
    public event Action<RangeSlider, float, float>? SpanChanged;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        AddHandler<PointerEvent>(static (element, args) => ((RangeSlider) element).Pointed(args));
        AddHandler<KeyEvent>(static (element, args) => ((RangeSlider) element).Keyed(args));
    }

    /// <inheritdoc />
    protected override void OnDraw(DrawContext context) {
        base.OnDraw(context);

        var rail = Rail(context.Bounds);
        if (Extent(rail) <= 0f) {
            return;
        }

        var low = Fraction(Low);
        var high = Fraction(High);

        DrawTrack(context, rail);
        context.FillRectangle(Span(rail, low, high), FillColor, Thickness(rail) * 0.5f);

        DrawThumb(context, rail, low);
        DrawThumb(context, rail, high);
    }

    /// <inheritdoc />
    protected override void OnBoundsChanged() {
        Low = Snap(Low);
        High = Snap(High);
    }

    float CoerceLow(float value) => MathF.Min(Snap(value), High);

    float CoerceHigh(float value) => MathF.Max(Snap(value), Low);

    void OnSpanChanged(float previous, float current) => SpanChanged?.Invoke(this, Low, High);

    void Pointed(PointerEvent args) {
        switch (args.Action) {
            case PointerAction.Pressed when args.Button == PointerButton.Primary:
                var fraction = FractionAt(args.X, args.Y);

                // Whichever thumb is nearer, which is the only rule that does not need the user to
                // aim: a press exactly between them takes the high one, arbitrarily and
                // consistently.
                draggingHigh = MathF.Abs(fraction - Fraction(High)) <= MathF.Abs(fraction - Fraction(Low));
                dragging = true;

                Document.Focus(this);
                Document.CapturePointer(this);
                Move(fraction);

                args.Handled = true;
                break;

            case PointerAction.Moved when dragging:
                Move(FractionAt(args.X, args.Y));
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

    void Move(float fraction) {
        if (draggingHigh) {
            High = ValueAt(fraction);
        } else {
            Low = ValueAt(fraction);
        }
    }

    void Keyed(KeyEvent args) {
        if (args.Action != KeyAction.Pressed) {
            return;
        }

        // Tab moves between controls, so it cannot also move between the two thumbs of one. The
        // bracket keys are what a keyboard user gets instead, and the thumb the pointer touched
        // last is the one the arrows move — which is the least surprising rule available, since it
        // is the one they were just looking at.
        if (args.Key is InputKey.LeftBracket or InputKey.RightBracket) {
            draggingHigh = args.Key == InputKey.RightBracket;
            args.Handled = true;

            return;
        }

        var step = Step > 0f ? Step : (Maximum - Minimum) * 0.01f;
        var from = draggingHigh ? High : Low;

        var moved = args.Key switch {
            InputKey.Left or InputKey.Down => from - step,
            InputKey.Right or InputKey.Up => from + step,
            InputKey.Home => Minimum,
            InputKey.End => Maximum,
            _ => float.NaN
        };

        if (float.IsNaN(moved)) {
            return;
        }

        if (draggingHigh) {
            High = moved;
        } else {
            Low = moved;
        }

        args.Handled = true;
    }
}

/// <summary>How far along something is.</summary>
/// <remarks>
///     ⚠ <b>Indeterminate is a separate flag rather than a magic value.</b> A progress bar told to
///     show <c>-1</c> or <c>NaN</c> for "I do not know" is a progress bar that shows a full one or
///     an empty one the day somebody's arithmetic produces that number by accident.
/// </remarks>
public sealed partial class ProgressBar : RangeBase {
    /// <inheritdoc />
    protected override string TagName => "progress-bar";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <inheritdoc />
    protected override AccessibleRole NativeRole => AccessibleRole.ProgressBar;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b><c>null</c> while <see cref="IsIndeterminate" />, which is the whole point of the
    ///     flag.</b> ARIA says an indeterminate progress bar omits <c>aria-valuenow</c>, and a
    ///     screen reader reading "nought per cent" for a job whose length is unknown is the failure
    ///     that omission exists to prevent. <see cref="AccessibleStates.Busy" /> is what is said
    ///     instead.
    /// </remarks>
    protected override string? NativeAccessibleValue =>
        IsIndeterminate ? null : Fraction(Value).ToString("0.###", CultureInfo.InvariantCulture);

    /// <inheritdoc />
    protected override AccessibleStates NativeAccessibleState =>
        IsIndeterminate ? AccessibleStates.Busy : AccessibleStates.None;

    /// <summary>How far along.</summary>
    [UiProperty(Coerce = nameof(CoerceValue))]
    public partial float Value { get; set; }

    /// <summary>Whether the length of the job is unknown.</summary>
    [UiProperty(Changed = nameof(OnIndeterminateChanged))]
    public partial bool IsIndeterminate { get; set; }

    /// <summary>How far round the indeterminate sweep has gone, from zero to one.</summary>
    /// <remarks>
    ///     ⚠ <b>Advanced by the application — and no longer because nothing here has a clock, which
    ///     is what this remark said for as long as it was false.</b> The framework has a per-frame
    ///     animation driver: <c>UiDocument.Tick</c> advances <c>Vixen.Ui.Styling</c>'s
    ///     <c>Animator</c>, <c>StyleUpdater.Announce</c> starts transitions from the cascade,
    ///     <c>UiDocument.Apply</c> overlays what is in flight, and both hosts drive it —
    ///     <c>UiApplication</c> and <c>EditorShell</c>. <c>TransitionTests</c> reads a value
    ///     mid-flight. All four legs landed together and nothing came back to this sentence.
    ///     <para>
    ///         What survives is the other half, which was always the real reason: a control that read
    ///         <c>DateTime.Now</c> to animate itself is a control whose golden image depends on what
    ///         time it is. So the phase stays a property a host writes from its frame loop, and time
    ///         still arrives from outside.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The theme cannot take it over yet, and the blocker is not the animator.</b> An
    ///         <c>animate-spin</c> would name a <c>@keyframes</c> block that does not exist — there
    ///         is not one <c>@keyframes</c> in any <c>.vcss</c> in the tree,
    ///         <c>Theme/vixen.default.vcss</c> included — and <c>Animator.Apply</c> walks the
    ///         properties an element has already cascaded, so a keyframe cannot introduce one the
    ///         element never declared. This is still the place the theme takes it over; it is waiting
    ///         on those two and not on a clock.
    ///     </para>
    /// </remarks>
    [UiProperty]
    public partial float Phase { get; set; }

    /// <inheritdoc />
    protected override void OnDraw(DrawContext context) {
        base.OnDraw(context);

        var bounds = context.Bounds;
        if (bounds.Width <= 0f || bounds.Height <= 0f) {
            return;
        }

        var radius = Thickness(bounds) * 0.5f;
        context.FillRectangle(bounds, TrackColor, radius);

        if (!IsIndeterminate) {
            context.FillRectangle(Span(bounds, 0f, Fraction(Value)), FillColor, radius);
            return;
        }

        // A third of the bar, sliding from one end to the other and back. The travel is over
        // (1 + length) so that it leaves entirely before it returns, rather than bouncing off a wall
        // it is still touching.
        const float Sweep = 0.3f;

        var travel = (1f + Sweep) * Math.Clamp(Phase, 0f, 1f);
        var from = MathF.Max(0f, travel - Sweep);
        var to = MathF.Min(1f, travel);

        if (to > from) {
            context.FillRectangle(Span(bounds, from, to), FillColor, radius);
        }
    }

    /// <inheritdoc cref="RangeBase.Snap" />
    float CoerceValue(float value) => Snap(value);

    void OnIndeterminateChanged(bool previous, bool current) {
        if (current) {
            AddClass("indeterminate");
        } else {
            RemoveClass("indeterminate");
        }
    }
}

/// <summary>A turning arc, for a wait with no length.</summary>
/// <remarks>
///     Its <see cref="Phase" /> is advanced by the application, for the reason
///     <see cref="ProgressBar.Phase" /> is: nothing in this assembly knows what time it is, and a
///     control that found out would be one nothing could take a stable picture of.
/// </remarks>
public sealed partial class Spinner : Control {
    readonly PathBuilder arc = new();

    /// <inheritdoc />
    protected override string TagName => "spinner";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The same role a <see cref="ProgressBar" /> has, because to anything listening it is
    ///     the same thing.</b> A spinner is an indeterminate progress bar drawn as a circle; the
    ///     shape is a fact about the pixels. <see cref="Phase" /> is an angle rather than a
    ///     fraction of a job, so there is no value to announce and there is never one to invent.
    /// </remarks>
    protected override AccessibleRole NativeRole => AccessibleRole.ProgressBar;

    /// <inheritdoc />
    protected override AccessibleStates NativeAccessibleState => AccessibleStates.Busy;

    /// <summary>How far round it has turned, from zero to one.</summary>
    [UiProperty]
    public partial float Phase { get; set; }

    /// <summary>How much of the circle the arc covers, from zero to one.</summary>
    [UiProperty(Default = 0.75f)]
    public partial float Sweep { get; set; }

    /// <summary>How thick the arc is, as a fraction of the radius.</summary>
    [UiProperty(Default = 0.2f)]
    public partial float Thickness { get; set; }

    /// <inheritdoc />
    /// <remarks>
    ///     Built as a filled annulus sector rather than stroked, so that its width scales with the
    ///     control — the same reason <see cref="ControlIcons" /> fills its outlines. The segment
    ///     count is fixed at thirty-two for a whole turn, which is smooth at every size a spinner is
    ///     ever drawn at and cheap enough to rebuild each frame, which it must be: the arc moves.
    /// </remarks>
    protected override void OnDraw(DrawContext context) {
        base.OnDraw(context);

        var bounds = context.Bounds;
        var radius = MathF.Min(bounds.Width, bounds.Height) * 0.5f;

        if (radius <= 0f) {
            return;
        }

        var centreX = bounds.X + (bounds.Width * 0.5f);
        var centreY = bounds.Y + (bounds.Height * 0.5f);

        var inner = radius * (1f - Math.Clamp(Thickness, 0.05f, 1f));
        var sweep = Math.Clamp(Sweep, 0f, 1f) * MathF.Tau;
        var start = Phase * MathF.Tau;

        var steps = Math.Max(2, (int) MathF.Ceiling(32f * Math.Clamp(Sweep, 0f, 1f)));

        arc.Clear();

        for (var i = 0; i <= steps; i++) {
            var angle = start + (sweep * i / steps);
            arc.Add(centreX + (MathF.Cos(angle) * radius), centreY + (MathF.Sin(angle) * radius), i == 0);
        }

        for (var i = steps; i >= 0; i--) {
            var angle = start + (sweep * i / steps);
            arc.LineTo(new Vector2(centreX + (MathF.Cos(angle) * inner), centreY + (MathF.Sin(angle) * inner)));
        }

        arc.Close();
        context.Fill(arc, context.Foreground);
    }
}

/// <summary>A one-line convenience so the spinner's arc reads as an arc.</summary>
static class PathExtensions {
    /// <summary>Starts a contour or continues it.</summary>
    public static void Add(this PathBuilder path, float x, float y, bool first) {
        if (first) {
            path.MoveTo(new Vector2(x, y));
        } else {
            path.LineTo(new Vector2(x, y));
        }
    }
}
