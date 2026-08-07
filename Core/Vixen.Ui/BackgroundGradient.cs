// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Mathematics;
using Vixen.Ui.Styling;

namespace Vixen.Ui;

/// <summary>Why a computed <c>background-image</c> is not being painted as a gradient.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A refusal is a value, not a silence.</b> The alternative — parsing what is understood
///         and ignoring the rest — draws a two-stop gradient for a three-stop declaration, and a
///         gradient that is subtly the wrong shape is indistinguishable from one somebody authored
///         badly. Every reading this file cannot honour ends up here, and the box is painted flat, so
///         the failure is a gradient that is visibly *absent* rather than one that is quietly wrong.
///     </para>
///     <para>
///         ⚠ <b>The reasons are separate on purpose.</b> <see cref="Stops" /> and
///         <see cref="Position" /> are both "the stop list is not two plain ends", and telling them
///         apart is what says whether the answer is <c>via-*</c> support or stop positions — which are
///         different pieces of work against different parts of the shader. Collapsing them into one
///         "unsupported" would throw away the only measurement of which is owed.
///     </para>
/// </remarks>
enum GradientRefusal : byte {
    /// <summary>It is a gradient this engine paints.</summary>
    None,

    /// <summary>There is no <c>background-image</c>, or it is <c>none</c>. Not a failure.</summary>
    Absent,

    /// <summary>A <c>radial-gradient()</c>. <see cref="BoxStyle.GradientAxis" /> is linear by construction.</summary>
    Radial,

    /// <summary>A <c>conic-gradient()</c>. Ditto.</summary>
    Conic,

    /// <summary>A <c>url()</c>, an <c>image-set()</c>, or anything else that is not a gradient.</summary>
    NotAGradient,

    /// <summary>An interpolation hint — <c>in oklab</c>, <c>in hsl longer hue</c>.</summary>
    Interpolation,

    /// <summary>More than two stops. A middle stop is <c>via-*</c>, and there is no third colour.</summary>
    Stops,

    /// <summary>A stop is somewhere other than the two ends.</summary>
    Position,

    /// <summary>A direction this engine cannot resolve to an axis.</summary>
    Direction,

    /// <summary>A stop whose colour did not parse.</summary>
    Colour,

    /// <summary>Malformed: unbalanced brackets, an empty stop list, a missing comma.</summary>
    Syntax
}

/// <summary>Which corner a <c>to bottom right</c> style direction names.</summary>
/// <remarks>
///     ⚠ <b>A corner is kept as a corner rather than turned into an angle at parse time, because the
///     angle it means depends on the box.</b> CSS defines <c>to bottom right</c> as the direction whose
///     perpendicular passes through the two *other* corners, which is 45° only when the box is square;
///     on a wide button it is much shallower. The box's size is known where the draw command is built
///     and not where the value is parsed, so the resolution happens there — see
///     <see cref="BackgroundGradient.Axis" />.
/// </remarks>
enum GradientCorner : byte {
    /// <summary>None: the direction is the angle instead.</summary>
    None,

    /// <summary>To top left.</summary>
    TopLeft,

    /// <summary>To top right.</summary>
    TopRight,

    /// <summary>To bottom right.</summary>
    BottomRight,

    /// <summary>To bottom left.</summary>
    BottomLeft
}

/// <summary>A two-stop linear gradient, as much of one as this engine paints.</summary>
/// <param name="Start">The colour at the near end. Becomes the draw command's own colour.</param>
/// <param name="End">The colour at the far end. Becomes <see cref="BoxStyle.GradientEnd" />.</param>
/// <param name="Angle">
///     Where the far end is, in radians, CSS's convention: zero is <c>to top</c> and it increases
///     clockwise. Ignored when <paramref name="Corner" /> is not <see cref="GradientCorner.None" />.
/// </param>
/// <param name="Corner">The corner the far end sits on, or <see cref="GradientCorner.None" />.</param>
/// <param name="Refusal">Why this is not paintable, or <see cref="GradientRefusal.None" />.</param>
readonly record struct BackgroundGradient(
    Color4 Start,
    Color4 End,
    float Angle,
    GradientCorner Corner,
    GradientRefusal Refusal
) {
    /// <summary>Whether this is a gradient the draw list can carry.</summary>
    public bool IsPaintable => Refusal == GradientRefusal.None;

    /// <summary>A refusal, with no colours.</summary>
    /// <param name="reason">Why.</param>
    /// <returns>The refusal.</returns>
    public static BackgroundGradient Refused(GradientRefusal reason) =>
        new(default, default, 0f, GradientCorner.None, reason);

    /// <summary>Which way the gradient runs across a box of this size, in the box's own space.</summary>
    /// <param name="width">The box's width in pixels.</param>
    /// <param name="height">Its height.</param>
    /// <returns>The axis, pointing at <see cref="End" />. Zero when there is nothing to draw.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The axis points at the <i>end</i> colour, and the shader's <c>t</c> runs from zero
    ///         against it to one along it.</b> Backwards, this paints every gradient in the interface
    ///         upside down — which is a mistake that looks like a design choice and survives review.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Only the direction matters; the length is thrown away.</b> The shader normalises
    ///         this and then divides by the box's own extent along it — <c>abs(ax·w/2) + abs(ay·h/2)</c>,
    ///         which is exactly half CSS's gradient-line length — so the same axis means the same
    ///         picture on boxes of different sizes. That is what lets one style be shared by a column
    ///         of buttons of different heights, and it is also why the corner case below can be
    ///         written as a ratio rather than as a trigonometric special case.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Screen space is y-down</b>, so <c>to top</c> is negative y. The engine's UV
    ///         convention (§ E) is top-left origin, and a gradient written against the maths
    ///         convention would run the wrong way on exactly half the directions — the half that a
    ///         quick look at <c>to bottom</c> would not catch.
    ///     </para>
    /// </remarks>
    public Vector2 Axis(float width, float height) {
        if (!IsPaintable) {
            return Vector2.Zero;
        }

        if (Corner == GradientCorner.None) {
            return new Vector2(MathF.Sin(Angle), -MathF.Cos(Angle));
        }

        // ⚠ <b>The swap is the whole corner rule.</b> The perpendicular to the axis has to pass
        // through the two neighbouring corners, and the vector between those corners is (w, h) — so
        // the axis is (h, w) turned into the right quadrant, and never (w, h) or (1, 1). On a
        // 200×40 button `to bottom right` is a shallow sweep, and using 45° there puts the midpoint
        // colour in visibly the wrong place while still ending on the corner, which is why this is
        // easy to get wrong and hard to see.
        var (signX, signY) = Corner switch {
            GradientCorner.TopLeft => (-1f, -1f),
            GradientCorner.TopRight => (1f, -1f),
            GradientCorner.BottomRight => (1f, 1f),
            _ => (-1f, 1f)
        };

        var axis = new Vector2(signX * height, signY * width);

        // A box with no area has no direction to run along, and a zero axis is the draw list's
        // sentinel for "no gradient" — so a degenerate box falls back to a flat fill rather than
        // dividing by nothing.
        return axis == Vector2.Zero ? Vector2.Zero : axis;
    }
}

/// <summary>Reads a computed <c>background-image</c> into the two-stop gradient the shader draws.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Cached by interned value id, and that is not an optimisation detail.</b> This runs
///         from the draw-list builder, which walks every element every frame; parsing
///         <c>linear-gradient(to right, #4f7cff 0%, #1f1f26 100%)</c> per element per frame would put
///         string work in the one loop that is not allowed to have any. A stylesheet has a handful of
///         distinct gradients and a document has thousands of elements, so the cache hit rate is
///         essentially one. <see cref="StyleValueParser" /> is cached the same way for the same
///         reason.
///     </para>
///     <para>
///         ⚠ <b>Both colour notations, always, and this was found the hard way.</b>
///         <c>background-color: #4f7cff</c> comes back from the cascade as <c>rgb(79, 124, 255)</c>
///         because ExCSS parsed and normalised it — but a value containing a <c>var()</c> is handed
///         back verbatim by design, and the substitution that turns
///         <c>--tw-gradient-stops</c> into colours happens *after* the only step that would have
///         normalised them. So a composed gradient's stops arrive as <c>#4f7cff</c> and a hand-written
///         one's as <c>rgb(…)</c>, in the same property, in the same document. Delegating every stop
///         to <see cref="StyleValueParser" /> is what makes that a non-question rather than a bug
///         waiting for the first hand-written rule.
///     </para>
/// </remarks>
sealed class GradientReader {
    /// <summary>How many stops a paintable gradient has. Two: a start and an end.</summary>
    const int PaintableStops = 2;

    readonly Dictionary<int, BackgroundGradient> cache = [];
    readonly NameTable values;
    readonly StyleValueParser parser;

    /// <summary>Creates a reader over a style engine's value table.</summary>
    /// <param name="values">The table declaration values are interned in.</param>
    /// <param name="parser">The parser stop colours are read with.</param>
    public GradientReader(NameTable values, StyleValueParser parser) {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(parser);

        this.values = values;
        this.parser = parser;
    }

    /// <summary>Reads an interned <c>background-image</c> value.</summary>
    /// <param name="value">Its id.</param>
    /// <returns>The gradient, or a refusal saying why it is not one.</returns>
    public BackgroundGradient Read(int value) {
        if (cache.TryGetValue(value, out var cached)) {
            return cached;
        }

        var parsed = Parse(values.NameOf(value).AsSpan());
        cache[value] = parsed;

        return parsed;
    }

    /// <summary>Reads <c>background-image</c> text.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The gradient, or a refusal.</returns>
    public BackgroundGradient Parse(ReadOnlySpan<char> text) {
        text = text.Trim();

        if (text.IsEmpty || text.Equals("none", StringComparison.OrdinalIgnoreCase)) {
            return BackgroundGradient.Refused(GradientRefusal.Absent);
        }

        var open = text.IndexOf('(');
        if (open <= 0 || text[^1] != ')') {
            return BackgroundGradient.Refused(GradientRefusal.NotAGradient);
        }

        var name = text[..open].Trim();
        var arguments = text[(open + 1)..^1];

        // ⚠ Named one at a time rather than by a "contains gradient" test, so that a
        // `repeating-linear-gradient` is refused as what it is instead of being mistaken for the
        // plain one and drawn with its repeats silently dropped.
        if (name.Equals("radial-gradient", StringComparison.OrdinalIgnoreCase)
            || name.Equals("repeating-radial-gradient", StringComparison.OrdinalIgnoreCase)) {
            return BackgroundGradient.Refused(GradientRefusal.Radial);
        }

        if (name.Equals("conic-gradient", StringComparison.OrdinalIgnoreCase)
            || name.Equals("repeating-conic-gradient", StringComparison.OrdinalIgnoreCase)) {
            return BackgroundGradient.Refused(GradientRefusal.Conic);
        }

        if (!name.Equals("linear-gradient", StringComparison.OrdinalIgnoreCase)) {
            return BackgroundGradient.Refused(GradientRefusal.NotAGradient);
        }

        Span<Range> parts = stackalloc Range[8];
        var count = SplitCommas(arguments, parts);

        if (count == 0) {
            return BackgroundGradient.Refused(GradientRefusal.Syntax);
        }

        // CSS's default direction is `to bottom`, which is 180° and not zero — a gradient with no
        // direction at all runs down the box, and defaulting to zero would run every one of them up.
        var angle = MathF.PI;
        var corner = GradientCorner.None;
        var first = arguments[parts[0]].Trim();
        var stopsFrom = 0;

        if (!LooksLikeStop(first)) {
            stopsFrom = 1;

            var direction = ReadDirection(first, ref angle, ref corner);
            if (direction != GradientRefusal.None) {
                return BackgroundGradient.Refused(direction);
            }
        }

        var stops = count - stopsFrom;

        if (stops < PaintableStops) {
            return BackgroundGradient.Refused(GradientRefusal.Syntax);
        }

        // ⚠ <b>Refused rather than approximated by its two ends.</b> `via-*` composes a real middle
        // stop, and dropping it draws a gradient that is the right two colours and the wrong shape
        // everywhere between them — which reads as a rendering bug in the theme rather than as a
        // missing feature. See docs/plan/43 § A11 for what a third stop costs.
        if (stops > PaintableStops) {
            return BackgroundGradient.Refused(GradientRefusal.Stops);
        }

        if (!ReadStop(arguments[parts[stopsFrom]], 0f, out var start, out var startRefusal)) {
            return BackgroundGradient.Refused(startRefusal);
        }

        if (!ReadStop(arguments[parts[stopsFrom + 1]], 1f, out var end, out var endRefusal)) {
            return BackgroundGradient.Refused(endRefusal);
        }

        return new BackgroundGradient(start, end, angle, corner, GradientRefusal.None);
    }

    /// <summary>Whether an argument is a colour stop rather than a direction.</summary>
    /// <remarks>
    ///     A direction is <c>to …</c> or an angle, and everything else in the first slot is a colour.
    ///     Tested by what it starts with rather than by trying to parse it both ways, because
    ///     <c>to</c> is not a colour and a colour never begins with a digit followed by an angle unit
    ///     — and because a failed colour parse has to stay distinguishable from a direction, so that
    ///     a typo in the first stop is reported as <see cref="GradientRefusal.Colour" />.
    /// </remarks>
    static bool LooksLikeStop(ReadOnlySpan<char> text) =>
        !text.StartsWith("to ", StringComparison.OrdinalIgnoreCase)
        && !text.StartsWith("in ", StringComparison.OrdinalIgnoreCase)
        && !(text.Length > 0 && (char.IsAsciiDigit(text[0]) || text[0] is '-' or '+' or '.'));

    /// <summary>Reads the first argument as a direction.</summary>
    static GradientRefusal ReadDirection(ReadOnlySpan<char> text, ref float angle, ref GradientCorner corner) {
        // ⚠ An interpolation hint is refused and never ignored. `linear-gradient(in oklab, …)` and
        // the same gradient without the hint are *different pictures* — that is the entire reason
        // CSS Color 4 added the syntax — so honouring the stops and dropping the space would draw
        // the one thing the author explicitly asked not to have.
        if (text.StartsWith("in ", StringComparison.OrdinalIgnoreCase) || ContainsWord(text, "in")) {
            return GradientRefusal.Interpolation;
        }

        if (text.StartsWith("to ", StringComparison.OrdinalIgnoreCase)) {
            return ReadSide(text[3..].Trim(), ref angle, ref corner);
        }

        return ReadAngle(text, ref angle);
    }

    /// <summary>Reads <c>right</c>, <c>bottom left</c> and the rest of the eight.</summary>
    static GradientRefusal ReadSide(ReadOnlySpan<char> text, ref float angle, ref GradientCorner corner) {
        var vertical = ReadOnlySpan<char>.Empty;
        var horizontal = ReadOnlySpan<char>.Empty;

        foreach (var range in text.Split(' ')) {
            var word = text[range].Trim();

            if (word.IsEmpty) {
                continue;
            }

            if (word.Equals("top", StringComparison.OrdinalIgnoreCase)
                || word.Equals("bottom", StringComparison.OrdinalIgnoreCase)) {
                if (!vertical.IsEmpty) {
                    return GradientRefusal.Direction;
                }

                vertical = word;
                continue;
            }

            if (word.Equals("left", StringComparison.OrdinalIgnoreCase)
                || word.Equals("right", StringComparison.OrdinalIgnoreCase)) {
                if (!horizontal.IsEmpty) {
                    return GradientRefusal.Direction;
                }

                horizontal = word;
                continue;
            }

            return GradientRefusal.Direction;
        }

        var top = vertical.Equals("top", StringComparison.OrdinalIgnoreCase);
        var left = horizontal.Equals("left", StringComparison.OrdinalIgnoreCase);

        if (vertical.IsEmpty && horizontal.IsEmpty) {
            return GradientRefusal.Direction;
        }

        if (vertical.IsEmpty) {
            angle = left ? -MathF.PI / 2f : MathF.PI / 2f;
            corner = GradientCorner.None;

            return GradientRefusal.None;
        }

        if (horizontal.IsEmpty) {
            angle = top ? 0f : MathF.PI;
            corner = GradientCorner.None;

            return GradientRefusal.None;
        }

        corner = top
            ? left ? GradientCorner.TopLeft : GradientCorner.TopRight
            : left ? GradientCorner.BottomLeft : GradientCorner.BottomRight;

        return GradientRefusal.None;
    }

    /// <summary>Reads an angle in any of CSS's four units.</summary>
    /// <remarks>
    ///     ⚠ All four, because <c>turn</c> is what a generated stylesheet tends to emit and <c>grad</c>
    ///     is rare enough that supporting three of four would look complete right up until it silently
    ///     drew a 100-gradian sweep as a 100-radian one.
    /// </remarks>
    static GradientRefusal ReadAngle(ReadOnlySpan<char> text, ref float angle) {
        var (unit, scale) = text.EndsWith("deg", StringComparison.OrdinalIgnoreCase)
            ? (3, MathF.PI / 180f)
            : text.EndsWith("grad", StringComparison.OrdinalIgnoreCase)
                ? (4, MathF.PI / 200f)
                : text.EndsWith("turn", StringComparison.OrdinalIgnoreCase)
                    ? (4, MathF.PI * 2f)
                    : text.EndsWith("rad", StringComparison.OrdinalIgnoreCase)
                        ? (3, 1f)
                        : (0, 0f);

        if (unit == 0) {
            return GradientRefusal.Direction;
        }

        var number = text[..^unit].Trim();

        if (!float.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var magnitude)) {
            return GradientRefusal.Direction;
        }

        angle = magnitude * scale;
        return GradientRefusal.None;
    }

    /// <summary>Reads one <c>&lt;colour&gt; [&lt;position&gt;]</c> stop.</summary>
    /// <param name="text">The stop.</param>
    /// <param name="expected">Where it has to be — zero for the start, one for the end.</param>
    /// <param name="colour">Its colour.</param>
    /// <param name="refusal">Why it was refused, when it was.</param>
    /// <returns>Whether it is a stop this engine can paint.</returns>
    /// <remarks>
    ///     ⚠ <b>A position that is not the end it belongs to is a refusal and not a rounding.</b> The
    ///     shader's parameter runs zero to one across the box with no scale and no bias to spare, so
    ///     <c>from-10%</c> cannot be honoured by moving a colour — it needs the ramp remapped, which
    ///     is a field in <c>UiShape</c> and a line in the shader. Painting it at 0% instead puts the
    ///     transition in the wrong place over the whole box, which is precisely the silent wrongness
    ///     this type exists to refuse. The composed utilities always write <c>0%</c> and <c>100%</c>,
    ///     so the common path states its positions rather than omitting them, and both spellings have
    ///     to be accepted.
    /// </remarks>
    bool ReadStop(ReadOnlySpan<char> text, float expected, out Color4 colour, out GradientRefusal refusal) {
        colour = default;
        text = text.Trim();

        var split = LastSpace(text);
        var position = split < 0 ? ReadOnlySpan<char>.Empty : text[(split + 1)..];
        var body = split < 0 ? text : text[..split].Trim();

        if (!position.IsEmpty) {
            if (!ReadPosition(position, out var where)) {
                // Not a position after all — a multi-word colour like `rgb(1 2 3)` already came back
                // as one token, so anything left here that is not a number is part of the colour.
                body = text;
            } else if (MathF.Abs(where - expected) > 1e-4f) {
                refusal = GradientRefusal.Position;
                return false;
            }
        }

        var value = parser.Parse(body);

        if (value.Kind != StyleValueKind.Color) {
            refusal = GradientRefusal.Colour;
            return false;
        }

        colour = value.Color;
        refusal = GradientRefusal.None;

        return true;
    }

    /// <summary>Reads a stop position as a fraction, from a percentage or a bare zero.</summary>
    static bool ReadPosition(ReadOnlySpan<char> text, out float position) {
        position = 0f;

        var percent = text.EndsWith("%", StringComparison.Ordinal);
        var number = percent ? text[..^1] : text;

        if (!float.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var magnitude)) {
            return false;
        }

        position = percent ? magnitude / 100f : magnitude;
        return true;
    }

    /// <summary>The last top-level space in a stop, which is what separates it from its position.</summary>
    static int LastSpace(ReadOnlySpan<char> text) {
        var depth = 0;

        for (var i = text.Length - 1; i >= 0; i--) {
            switch (text[i]) {
                case ')':
                    depth++;
                    break;

                case '(':
                    depth--;
                    break;

                case ' ' when depth == 0:
                    return i;
            }
        }

        return -1;
    }

    /// <summary>Whether a space-separated word appears in a span.</summary>
    static bool ContainsWord(ReadOnlySpan<char> text, ReadOnlySpan<char> word) {
        foreach (var range in text.Split(' ')) {
            if (text[range].Trim().Equals(word, StringComparison.OrdinalIgnoreCase)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Splits on the commas that are not inside a function.</summary>
    /// <remarks>
    ///     ⚠ Depth-aware, because <c>rgb(79, 124, 255)</c> is one stop containing two commas — and a
    ///     naive split turns a perfectly ordinary hand-written gradient into five arguments and a
    ///     refusal. That is the notation half the values arrive in.
    /// </remarks>
    static int SplitCommas(ReadOnlySpan<char> text, Span<Range> ranges) {
        var count = 0;
        var start = 0;
        var depth = 0;

        for (var i = 0; i < text.Length && count < ranges.Length; i++) {
            switch (text[i]) {
                case '(':
                    depth++;
                    break;

                case ')':
                    depth--;
                    break;

                case ',' when depth == 0:
                    ranges[count++] = new Range(start, i);
                    start = i + 1;
                    break;
            }
        }

        if (start < text.Length && count < ranges.Length) {
            ranges[count++] = new Range(start, text.Length);
        }

        return count;
    }
}
