// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

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
        thumbSize = Document.PropertyId("--thumb-size");
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

        return Math.Clamp(value, Minimum, Maximum);
    }

    /// <summary>Brings a value inside the bounds and onto a step.</summary>
    protected float Snap(float value) =>
        Step > 0f
            ? Math.Clamp(Minimum + (MathF.Round((value - Minimum) / Step) * Step), Minimum, Maximum)
            : Math.Clamp(value, Minimum, Maximum);

    /// <summary>The strip the fill and the thumbs are drawn on.</summary>
    /// <remarks>
    ///     Inset by half a thumb at each end, so that a thumb at either extreme is inside the
    ///     control rather than half outside it. Every slider that does not do this has a thumb that
    ///     is clipped at zero and at one hundred percent.
    /// </remarks>
    protected Rectangle Rail(Rectangle bounds) {
        var thumb = ThumbSize;
        var height = MathF.Min(bounds.Height, MathF.Max(2f, thumb * 0.3f));

        return new Rectangle(
            bounds.X + (thumb * 0.5f),
            bounds.Y + ((bounds.Height - height) * 0.5f),
            MathF.Max(0f, bounds.Width - thumb),
            height
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

    /// <summary>Draws the unfilled strip.</summary>
    protected void DrawTrack(DrawContext context, Rectangle rail) =>
        context.FillRectangle(rail, TrackColor, rail.Height * 0.5f);

    /// <summary>Draws a thumb centred on a point of the rail.</summary>
    protected void DrawThumb(DrawContext context, Rectangle rail, float fraction) {
        var size = ThumbSize;
        var centre = rail.X + (rail.Width * fraction);

        context.FillRectangle(
            new Rectangle(centre - (size * 0.5f), context.Bounds.Y + ((context.Bounds.Height - size) * 0.5f), size, size),
            ThumbColor,
            size * 0.5f
        );
    }

    void OnBoundsChanged(float previous, float current) => OnBoundsChanged();

    /// <summary>Called when the bounds move, so a subclass can bring its value back inside them.</summary>
    protected virtual void OnBoundsChanged() {
    }
}

/// <summary>A value chosen by dragging a thumb along a track.</summary>
public sealed partial class Slider : RangeBase {
    bool dragging;

    /// <inheritdoc />
    protected override string TagName => "slider";

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
        if (rail.Width <= 0f) {
            return;
        }

        var fraction = Fraction(Value);

        DrawTrack(context, rail);
        context.FillRectangle(
            new Rectangle(rail.X, rail.Y, rail.Width * fraction, rail.Height),
            FillColor,
            rail.Height * 0.5f
        );

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
                Value = ValueAt(FractionAt(args.X));

                args.Handled = true;
                break;

            case PointerAction.Moved when dragging:
                Value = ValueAt(FractionAt(args.X));
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

    /// <summary>Where along the rail a document-space x falls.</summary>
    /// <remarks>
    ///     ⚠ <b>Against the rail rather than against the control.</b> The rail is inset by half a
    ///     thumb at each end, so a press on the extreme left of the control is a fraction below zero
    ///     — which clamps to zero, which is what the user meant. Measuring against the control's own
    ///     width instead would make the thumb lag the cursor by half its width at both ends.
    /// </remarks>
    float FractionAt(float x) {
        var rail = Rail(Bounds);
        return rail.Width <= 0f ? 0f : (x - rail.X) / rail.Width;
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
        if (rail.Width <= 0f) {
            return;
        }

        var low = Fraction(Low);
        var high = Fraction(High);

        DrawTrack(context, rail);
        context.FillRectangle(
            new Rectangle(rail.X + (rail.Width * low), rail.Y, rail.Width * (high - low), rail.Height),
            FillColor,
            rail.Height * 0.5f
        );

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
                var fraction = FractionAt(args.X);

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
                Move(FractionAt(args.X));
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

    float FractionAt(float x) {
        var rail = Rail(Bounds);
        return rail.Width <= 0f ? 0f : (x - rail.X) / rail.Width;
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

    /// <summary>How far along.</summary>
    [UiProperty(Coerce = nameof(CoerceValue))]
    public partial float Value { get; set; }

    /// <summary>Whether the length of the job is unknown.</summary>
    [UiProperty(Changed = nameof(OnIndeterminateChanged))]
    public partial bool IsIndeterminate { get; set; }

    /// <summary>How far round the indeterminate sweep has gone, from zero to one.</summary>
    /// <remarks>
    ///     ⚠ <b>Advanced by the application, because nothing here has a clock.</b> The framework has
    ///     no per-frame animation driver — <c>Vixen.Ui.Styling</c>'s <c>Animator</c> is not wired to
    ///     the document yet — and a control that read <c>DateTime.Now</c> to animate itself would be
    ///     a control whose golden-image test depends on what time it is. A host advances this from
    ///     its frame loop; when transitions arrive, the theme will do it instead.
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

        var radius = bounds.Height * 0.5f;
        context.FillRectangle(bounds, TrackColor, radius);

        if (!IsIndeterminate) {
            context.FillRectangle(
                new Rectangle(bounds.X, bounds.Y, bounds.Width * Fraction(Value), bounds.Height),
                FillColor,
                radius
            );

            return;
        }

        // A third of the bar, sliding from one end to the other and back. The travel is over
        // (1 + width) so that it leaves entirely before it returns, rather than bouncing off a wall
        // it is still touching.
        var span = bounds.Width * 0.3f;
        var travel = (bounds.Width + span) * Math.Clamp(Phase, 0f, 1f);

        var left = MathF.Max(bounds.X, bounds.X + travel - span);
        var right = MathF.Min(bounds.X + bounds.Width, bounds.X + travel);

        if (right > left) {
            context.FillRectangle(new Rectangle(left, bounds.Y, right - left, bounds.Height), FillColor, radius);
        }
    }

    float CoerceValue(float value) => Math.Clamp(value, Minimum, Maximum);

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
