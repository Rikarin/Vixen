// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Mathematics;

namespace Vixen.Ui.Controls;

/// <summary>A reading on a dial: the same capacity a <see cref="LevelIndicator" /> shows, as an arc.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The other half of doc 49 § 7.1's rank 6, and the same control drawn round a
///         circle.</b> A level indicator is a bar because it sits in a row; a gauge is a dial because
///         it sits on its own — a CPU load in a status panel, a frame budget in an overlay, a tank on a
///         HUD. What the reading <i>means</i> is identical, so <see cref="Warning" />,
///         <see cref="Critical" />, <see cref="Direction" /> and <see cref="Level" /> are the level
///         indicator's, computed by the same code, and a bar and a dial over one number cannot
///         disagree about whether it is a problem.
///     </para>
///     <para>
///         ⚠ <b>The dial opens at the bottom and fills clockwise from its lower left.</b> That is
///         what a speedometer, a pressure gauge and every audio meter with a needle does, and the
///         gap is where a caption goes. <see cref="Sweep" /> is how much of the turn it spans.
///     </para>
///     <para>
///         <b>No needle and no text.</b> The filled arc is the needle — a needle is one more thing to
///         antialias at every angle and says nothing the arc's end does not — and the number in the
///         middle is the application's, for <see cref="LevelIndicator" />'s reason: "87 %" is not a
///         sentence this assembly can write without knowing what is being measured. Put a label over
///         it.
///     </para>
///     <para>
///         ⚠ <b><see cref="RangeBase.Orientation" /> means nothing to a dial</b> and is ignored. It is
///         inherited because the bounds, the step and the colours are, and those are the part of
///         <see cref="RangeBase" /> a gauge needs.
///     </para>
/// </remarks>
public sealed partial class Gauge : RangeBase {
    readonly PathBuilder arc = new();
    int arcWidth;

    /// <inheritdoc />
    protected override string TagName => "gauge";

    /// <inheritdoc />
    /// <remarks>A readout is not operated, so it is not in the tab order — <see cref="LevelIndicator" />'s rule.</remarks>
    protected override bool AcceptsFocus => false;

    /// <inheritdoc />
    /// <remarks>ARIA's <c>meter</c>, for the reason <see cref="LevelIndicator" /> gives: a capacity and not a job.</remarks>
    protected override AccessibleRole NativeRole => AccessibleRole.Meter;

    /// <inheritdoc />
    /// <remarks>The reading itself and not a fraction, for <see cref="LevelIndicator" />'s reason.</remarks>
    protected override string? NativeAccessibleValue => Value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>The reading.</summary>
    [UiProperty(Coerce = nameof(CoerceValue), Changed = nameof(OnValueChanged))]
    public partial float Value { get; set; }

    /// <summary>Where the reading stops being ordinary, or <see cref="float.NaN" /> for nowhere.</summary>
    /// <remarks>See <see cref="LevelIndicator.Warning" />; the rule is the same one.</remarks>
    [UiProperty(Default = float.NaN, Changed = nameof(OnThresholdChanged))]
    public partial float Warning { get; set; }

    /// <summary>Where it stops being acceptable, or <see cref="float.NaN" /> for nowhere.</summary>
    /// <remarks>See <see cref="LevelIndicator.Critical" />; the rule is the same one.</remarks>
    [UiProperty(Default = float.NaN, Changed = nameof(OnThresholdChanged))]
    public partial float Critical { get; set; }

    /// <summary>Which way the reading gets worse, or <see cref="LevelDirection.Inferred" /> to read it off the thresholds.</summary>
    /// <remarks>See <see cref="LevelIndicator.Direction" />: needed only when one line is set.</remarks>
    [UiProperty(Changed = nameof(OnDirectionChanged))]
    public partial LevelDirection Direction { get; set; }

    /// <summary>How much of a whole turn the dial spans, from a tenth to all of it.</summary>
    /// <remarks>
    ///     Three quarters by default, which leaves the quarter at the bottom open. A whole turn is a
    ///     ring whose start and end meet at the bottom, which is what a progress ring is.
    /// </remarks>
    [UiProperty(Default = 0.75f, Coerce = nameof(CoerceSweep))]
    public partial float Sweep { get; set; }

    /// <summary>What the reading currently amounts to.</summary>
    /// <remarks>Computed on every read, by the arithmetic <see cref="LevelIndicator.Level" /> uses.</remarks>
    public LevelReading Level => Levels.Of(Value, Warning, Critical, Direction);

    /// <summary>The angle the dial starts at, in radians clockwise from pointing right.</summary>
    /// <remarks>
    ///     ⚠ <b>Clockwise because y points down.</b> An angle growing from zero turns from the right
    ///     edge towards the bottom on the screen, so the open gap is centred on a quarter turn and the
    ///     dial begins half the unswept part beyond it.
    /// </remarks>
    float StartAngle => (MathF.PI * 0.5f) + ((1f - Sweep) * MathF.PI);

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        arcWidth = Document.PropertyId("--arc-width");
        Levels.Apply(this, Level);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         Two filled annulus sectors that meet at the reading, the fill before it and the track
    ///         after, rather than two strokes — for <see cref="Spinner" />'s reason, which is that a
    ///         filled shape scales with the control. The ends are square: a round cap would reach
    ///         past the reading by half the arc's width, so a gauge at nought would still show a
    ///         dot.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The segment count follows the angle rather than being fixed</b>, at one per
    ///         twelfth of a radian. A fixed count for the whole dial makes a nearly-empty reading a
    ///         straight chord across two points, which draws as a sliver in the wrong place.
    ///     </para>
    /// </remarks>
    protected override void OnDraw(DrawContext context) {
        base.OnDraw(context);

        var bounds = context.Bounds;
        var outer = MathF.Min(bounds.Width, bounds.Height) * 0.5f;

        if (outer <= 0f) {
            return;
        }

        // ⚠ The floor is the smaller of half a pixel and the radius, since `Math.Clamp` throws when
        // its minimum passes its maximum and a gauge laid out at a third of a pixel is still a gauge.
        var width = Math.Clamp(Document.LengthOf(Style, arcWidth) ?? outer * 0.2f, MathF.Min(0.5f, outer), outer);
        var inner = outer - width;
        var centre = new Vector2(bounds.X + (bounds.Width * 0.5f), bounds.Y + (bounds.Height * 0.5f));

        var start = StartAngle;
        var sweep = Sweep * MathF.Tau;
        var lit = sweep * Fraction(Value);

        // ⚠ The track is drawn only where the fill is not, rather than whole and underneath it. Two
        // antialiased shapes sharing an edge leave the one below showing through the partial
        // coverage of the one above, and a dial whose fill and track both begin at the same angle
        // drew a light pixel into the fill's first edge — seen in the first picture of this control,
        // not in any count.
        if (lit > 0f) {
            Sector(context, centre, inner, outer, start, lit, FillColor);
        }

        if (lit < sweep) {
            Sector(context, centre, inner, outer, start + lit, sweep - lit, TrackColor);
        }
    }

    /// <summary>Fills the part of the ring between two angles.</summary>
    void Sector(DrawContext context, Vector2 centre, float inner, float outer, float start, float sweep, Color4 colour) {
        var steps = Math.Max(2, (int)MathF.Ceiling(sweep * 12f));

        arc.Clear();

        for (var i = 0; i <= steps; i++) {
            var angle = start + (sweep * i / steps);
            arc.Add(centre.X + (MathF.Cos(angle) * outer), centre.Y + (MathF.Sin(angle) * outer), i == 0);
        }

        for (var i = steps; i >= 0; i--) {
            var angle = start + (sweep * i / steps);
            arc.LineTo(new Vector2(centre.X + (MathF.Cos(angle) * inner), centre.Y + (MathF.Sin(angle) * inner)));
        }

        arc.Close();
        context.Fill(arc, colour);
    }

    /// <inheritdoc cref="RangeBase.Snap" />
    float CoerceValue(float value) => Snap(value);

    /// <summary>A tenth of a turn at least, and never more than one.</summary>
    /// <remarks>
    ///     Not zero, because a dial spanning nothing draws nothing at every reading and looks exactly
    ///     like a gauge that failed to load; <see cref="float.NaN" /> takes the default.
    /// </remarks>
    static float CoerceSweep(float value) => float.IsNaN(value) ? 0.75f : Math.Clamp(value, 0.1f, 1f);

    void OnValueChanged(float previous, float current) {
        Levels.Apply(this, Level);
        InvalidateAccessibility();
    }

    void OnThresholdChanged(float previous, float current) => Levels.Apply(this, Level);

    void OnDirectionChanged(LevelDirection previous, LevelDirection current) => Levels.Apply(this, Level);
}
