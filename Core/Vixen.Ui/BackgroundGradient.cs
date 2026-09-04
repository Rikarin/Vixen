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
///         ⚠ <b>The reasons are separate on purpose, and that separation has now paid once.</b>
///         <see cref="Stops" /> and <see cref="Position" /> used to mean "a middle stop" and "a stop
///         that is not at its end" — two of the four pieces doc 43 § A11 owed — and keeping them apart
///         is what measured which was which. Both are painted now, and both names survive with
///         narrower meanings: a <i>fourth</i> stop, and a position that cannot be resolved to a
///         fraction. A single "unsupported" would have thrown that measurement away and would be
///         throwing away the next one.
///     </para>
///     <para>
///         ⚠ <b>What is left is not a to-do list.</b> <see cref="Repeating" /> and
///         <see cref="Interpolation" /> are different shaders rather than missing lanes, and
///         <see cref="Extent" /> is a deliberate trade written down on its own member.
///     </para>
/// </remarks>
enum GradientRefusal : byte {
    /// <summary>It is a gradient this engine paints.</summary>
    None,

    /// <summary>There is no <c>background-image</c>, or it is <c>none</c>. Not a failure.</summary>
    Absent,

    /// <summary>A <c>repeating-*-gradient()</c>. The ramp runs once and the record cannot say twice.</summary>
    Repeating,

    /// <summary>A <c>url()</c>, an <c>image-set()</c>, or anything else that is not a gradient.</summary>
    NotAGradient,

    /// <summary>An interpolation hint naming a space this engine does not mix in.</summary>
    /// <remarks>
    ///     ⚠ <b>Narrower than it was, and the difference is the point.</b> This used to mean any
    ///     <c>in …</c> at all; <c>in srgb</c>, <c>in srgb-linear</c> and <c>in oklab</c> are now
    ///     honoured, so what is left is the polar spaces — <c>in hsl</c>, <c>in oklch</c>,
    ///     <c>longer hue</c> — where the interpolation path curves through hue and is a genuinely
    ///     different picture rather than a different lerp.
    /// </remarks>
    Interpolation,

    /// <summary>More than three stops. Three is a start, a middle and an end, which is Tailwind's shape.</summary>
    Stops,

    /// <summary>A stop position this engine cannot resolve — a length, a <c>calc()</c>, a hint.</summary>
    /// <remarks>
    ///     ⚠ A <c>&lt;length&gt;</c> position such as <c>10px</c> is refused rather than converted,
    ///     because converting it needs the gradient line's length and that is a function of the box —
    ///     which is not known here. See <see cref="BackgroundGradient.Axis" /> for the same argument
    ///     about corners, which is resolved later precisely because it can be.
    /// </remarks>
    Position,

    /// <summary>A direction this engine cannot resolve to an axis.</summary>
    Direction,

    /// <summary>An ending shape or size on a round gradient that is not <c>farthest-corner</c>.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Narrower than it was, and the half that closed is the half anybody writes.</b> This
    ///         used to cover <c>at &lt;position&gt;</c> as well, on the argument that the record had no
    ///         centre and CSS's defaults are functions of the box. The centre is a lane now — see
    ///         <see cref="BackgroundGradient.Centre" /> and <c>UiShape.Paint</c> — so
    ///         <c>radial-gradient(at 25% 75%, …)</c> and <c>conic-gradient(from 45deg at top left, …)</c>
    ///         paint, which is what <c>bg-radial-[at_*]</c> and <c>mask-radial-at-*</c> are spelled in.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What is still refused is the ending <i>shape</i>, and it is refused rather than
    ///         approximated for the reason the whole enum exists.</b> <c>circle</c> forces the two radii
    ///         equal, <c>closest-side</c> and its three siblings pick a different pair of radii
    ///         entirely, and an explicit <c>80px 40px</c> states them outright — four different
    ///         ellipses, none of which is the <c>farthest-corner</c> one this engine computes. Drawing
    ///         any of them as farthest-corner is a ramp that finishes in the wrong place, which is the
    ///         failure that looks like a design choice. <b>The centre could land without them because
    ///         moving a farthest-corner ellipse's centre is still a farthest-corner ellipse</b> — the
    ///         radii change, and <c>DrawListBuilder.RampFrame</c> is the closed form for how.
    ///     </para>
    /// </remarks>
    Extent,

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

/// <summary>One component of a CSS <c>&lt;position&gt;</c>: a fraction of the box, plus a pixel offset.</summary>
/// <param name="Fraction">How far across the box, from zero at the near edge to one at the far one.</param>
/// <param name="Pixels">A length added to it.</param>
/// <remarks>
///     ⚠ <b>Both halves, because CSS's <c>&lt;position&gt;</c> is a sum and not a choice.</b>
///     <c>at 25%</c> is a fraction, <c>at 10px</c> is a length, and <c>calc(50% + 10px)</c> is both —
///     but so is the two-value form <c>at right 10px top 20px</c>, which is a keyword edge plus an
///     inset. Carrying only whichever one the author wrote would make the pair unrepresentable, and a
///     percentage resolved at parse time would need a box the parser does not have.
/// </remarks>
readonly record struct GradientOffset(float Fraction, float Pixels) {
    /// <summary>Where this sits along an extent, in pixels from its near edge.</summary>
    /// <param name="extent">The extent, in pixels.</param>
    /// <returns>The position.</returns>
    public float Resolve(float extent) => (Fraction * extent) + Pixels;
}

/// <summary>Where a round gradient's <c>at</c> puts its centre.</summary>
/// <param name="X">Across.</param>
/// <param name="Y">Down.</param>
readonly record struct GradientPoint(GradientOffset X, GradientOffset Y) {
    /// <summary>The middle of the box, which is CSS's default for both round shapes.</summary>
    public static GradientPoint Middle => new(new GradientOffset(0.5f, 0f), new GradientOffset(0.5f, 0f));

    /// <summary>Where this sits in a box, in pixels from its top left corner.</summary>
    /// <param name="width">The box's width.</param>
    /// <param name="height">Its height.</param>
    /// <returns>The point.</returns>
    public Vector2 Resolve(float width, float height) => new(X.Resolve(width), Y.Resolve(height));
}

/// <summary>A gradient, as much of one as this engine paints.</summary>
/// <param name="Start">The colour at the first stop. Becomes the draw command's own colour.</param>
/// <param name="Via">The colour at the middle stop, when <paramref name="HasVia" />.</param>
/// <param name="End">The colour at the last stop. Becomes <see cref="BoxStyle.GradientEnd" />.</param>
/// <param name="HasVia">Whether there is a middle stop.</param>
/// <param name="Stops">Where the three stops sit, already sorted and defaulted.</param>
/// <param name="Shape">Linear, radial or conic.</param>
/// <param name="Space">Which space the stops interpolate in.</param>
/// <param name="Angle">
///     Where the far end is, in radians, CSS's convention: zero is <c>to top</c> and it increases
///     clockwise. Ignored when <paramref name="Corner" /> is not <see cref="GradientCorner.None" />.
///     A conic gradient's <c>from</c> angle is carried here too, for the reason
///     <see cref="Axis" /> gives.
/// </param>
/// <param name="Corner">The corner the far end sits on, or <see cref="GradientCorner.None" />.</param>
/// <param name="Centre">
///     Where an <c>at &lt;position&gt;</c> puts a round gradient's centre, or null for CSS's default.
///     <para>
///         ⚠ <b>Null rather than <see cref="GradientPoint.Middle" />, and the difference is a lane in
///         <c>UiShape</c> rather than a nicety.</b> An unstated centre is the one case the record can
///         say nothing about and let the shader keep the arrangement it had — see <c>UiShape.Paint</c>,
///         whose zero means "the ramp is the box". Defaulting to the middle here would make every
///         gradient in the interface write two more lanes to describe the arrangement they already
///         had, and a stale shader reading them would be the one that noticed.
///     </para>
///     <para>
///         ⚠ Meaningless on a linear gradient, which has no <c>at</c> in CSS's grammar at all;
///         <c>ReadPrelude</c> refuses one rather than storing it.
///     </para>
/// </param>
/// <param name="Refusal">Why this is not paintable, or <see cref="GradientRefusal.None" />.</param>
readonly record struct BackgroundGradient(
    Color4 Start,
    Color4 Via,
    Color4 End,
    bool HasVia,
    GradientStops Stops,
    GradientShape Shape,
    GradientSpace Space,
    float Angle,
    GradientCorner Corner,
    GradientPoint? Centre,
    GradientRefusal Refusal
) {
    /// <summary>Whether this is a gradient the draw list can carry.</summary>
    public bool IsPaintable => Refusal == GradientRefusal.None;

    /// <summary>A refusal, with no colours.</summary>
    /// <param name="reason">Why.</param>
    /// <returns>The refusal.</returns>
    public static BackgroundGradient Refused(GradientRefusal reason) => new(
        default,
        default,
        default,
        false,
        GradientStops.Default,
        GradientShape.None,
        GradientSpace.Srgb,
        0f,
        GradientCorner.None,
        null,
        reason
    );

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
        if (!IsPaintable || Shape == GradientShape.Radial) {
            // ⚠ A radial gradient has no direction, and this returning zero is *not* the sentinel it
            // used to be. `BoxStyle.Shape` is what says whether there is a gradient now, precisely
            // because a round one could not be told from a flat fill by an axis that has no meaning.
            return Vector2.Zero;
        }

        if (Corner == GradientCorner.None) {
            // ⚠ A conic gradient's axis is not an axis; it is the direction its zero angle points,
            // and the same formula produces it. That is deliberate rather than reuse for its own
            // sake: the shader recovers the angle with `atan2(x, -y)`, which inverts exactly this,
            // so `from 45deg` survives the round trip through a lane that already existed.
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

/// <summary>Reads a computed <c>background-image</c> into the gradient the shader draws.</summary>
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
///     <para>
///         ⚠ <b>And the same is true of the <i>positions</i>, which is the trap that catches the
///         second person here.</b> Nothing normalises them either, so <c>from-10%</c> composes into a
///         stop reading <c>10%</c> while a value that went through ExCSS may have been rewritten —
///         and a <c>--tw-gradient-from-position</c> that nobody set arrives as the fragment's own
///         initial text. Every position is therefore read from text with the same code path whatever
///         wrote it, and a position this file cannot resolve is <see cref="GradientRefusal.Position" />
///         rather than a silent zero.
///     </para>
/// </remarks>
sealed class GradientReader {
    /// <summary>The fewest stops a gradient can have, and the most this record carries.</summary>
    const int LeastStops = 2;

    /// <summary>Three: a start, a middle and an end, which is exactly Tailwind's shape.</summary>
    /// <remarks>
    ///     ⚠ <b>A fourth is refused rather than resampled.</b> The record has one middle colour and a
    ///     shader with one branch; approximating four stops with three draws a gradient that is right
    ///     at both ends and wrong in the interior, which is <see cref="GradientRefusal" />'s whole
    ///     argument. A stop list long enough to need more is a 1D ramp texture, not this.
    /// </remarks>
    const int MostStops = 3;

    /// <summary>The most layers a <c>mask-image</c> list may have.</summary>
    /// <remarks>
    ///     ⚠ <b>Six is what the utility layer can generate and eight is what is allowed, and the gap
    ///     is deliberate.</b> Tailwind's widest mask is four edge ramps under <c>--tw-mask-linear</c>
    ///     plus a radial and a conic, which is six; a hand-written <c>.vcss</c> may reasonably want
    ///     one or two more. Past that the list is refused outright rather than truncated, because
    ///     truncation would silently drop the layers at one end and the picture would be a mask that
    ///     nearly works — the failure mode <see cref="GradientRefusal" /> exists to avoid.
    /// </remarks>
    public const int MostLayers = 8;

    readonly Dictionary<int, BackgroundGradient> cache = [];
    readonly Dictionary<int, BackgroundGradient[]> layerCache = [];
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

    /// <summary>Reads an interned <c>mask-image</c> value as the list of layers it is.</summary>
    /// <param name="value">Its id.</param>
    /// <returns>
    ///     One entry per layer, topmost first, each of which may be a refusal. Empty when the value
    ///     is <c>none</c>, when it is blank, or when it has more than <see cref="MostLayers" />
    ///     layers.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The same <see cref="Parse" /> per layer, which is what keeps a one-layer list and
    ///         a bare <c>mask-image</c> the same picture.</b> A second parser tuned for lists would be
    ///         a second set of refusals to keep in step with this one, and the layer syntax is not a
    ///         different production — it is this production, several times, separated by commas.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The split is at depth zero, so the commas <i>inside</i> a gradient's own argument
    ///         list are not layer separators.</b> <c>linear-gradient(black, transparent)</c> is one
    ///         layer with two stops and not two layers of nonsense, and a naive
    ///         <c>text.Split(',')</c> here would turn every existing single-layer mask in the engine
    ///         into a list of unparseable fragments.
    ///     </para>
    /// </remarks>
    public IReadOnlyList<BackgroundGradient> ReadLayers(int value) {
        if (layerCache.TryGetValue(value, out var cached)) {
            return cached;
        }

        var parsed = ParseLayers(values.NameOf(value).AsSpan());
        layerCache[value] = parsed;

        return parsed;
    }

    /// <summary>Reads <c>mask-image</c> text as the list of layers it is.</summary>
    /// <param name="text">The text.</param>
    /// <returns>One entry per layer, topmost first.</returns>
    public BackgroundGradient[] ParseLayers(ReadOnlySpan<char> text) {
        text = text.Trim();

        if (text.IsEmpty || text.Equals("none", StringComparison.OrdinalIgnoreCase)) {
            return [];
        }

        // ⚠ One slot more than the ceiling, so that a list *at* the ceiling and a list past it can be
        // told apart. `SplitCommas` stops when it runs out of room and says nothing about what it
        // dropped, so a span sized exactly to the ceiling would report a nine-layer value as a legal
        // eight-layer one and mask by the wrong eight.
        Span<Range> parts = stackalloc Range[MostLayers + 1];
        var count = SplitCommas(text, parts);

        if (count == 0 || count > MostLayers) {
            return [];
        }

        var layers = new BackgroundGradient[count];

        for (var i = 0; i < count; i++) {
            layers[i] = Parse(text[parts[i]]);
        }

        return layers;
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
        // plain one and drawn with its repeats silently dropped. The record's parameter is clamped
        // at both ends, so repeating is not a lane away — it is a different shader.
        if (name.StartsWith("repeating-", StringComparison.OrdinalIgnoreCase)) {
            return BackgroundGradient.Refused(GradientRefusal.Repeating);
        }

        var shape = name switch {
            _ when name.Equals("linear-gradient", StringComparison.OrdinalIgnoreCase) => GradientShape.Linear,
            _ when name.Equals("radial-gradient", StringComparison.OrdinalIgnoreCase) => GradientShape.Radial,
            _ when name.Equals("conic-gradient", StringComparison.OrdinalIgnoreCase) => GradientShape.Conic,
            _ => GradientShape.None
        };

        if (shape == GradientShape.None) {
            return BackgroundGradient.Refused(GradientRefusal.NotAGradient);
        }

        Span<Range> parts = stackalloc Range[8];
        var count = SplitCommas(arguments, parts);

        if (count == 0) {
            return BackgroundGradient.Refused(GradientRefusal.Syntax);
        }

        // CSS's default direction for a linear gradient is `to bottom`, which is 180° and not zero —
        // one with no direction at all runs down the box, and defaulting to zero would run every one
        // of them up. A conic gradient's default `from` is 0deg, which in the same convention is up,
        // so the two defaults are genuinely different numbers rather than one shared constant.
        var angle = shape == GradientShape.Linear ? MathF.PI : 0f;
        var corner = GradientCorner.None;
        GradientPoint? centre = null;

        // ⚠ <b>sRGB, because that is what CSS says an unhinted gradient means</b> — not linear RGB,
        // which is what this engine paints in and what the shader lerped in before there was a
        // choice. The two disagree most exactly at the midpoint. Tailwind writes `in oklab` on every
        // gradient it generates, so the composed path never reaches this default; a hand-written
        // `.vcss` rule does, and it should match a browser.
        var space = GradientSpace.Srgb;

        var first = arguments[parts[0]].Trim();
        var stopsFrom = 0;

        if (!LooksLikeStop(first)) {
            stopsFrom = 1;

            var prelude = ReadPrelude(first, shape, ref angle, ref corner, ref space, ref centre);
            if (prelude != GradientRefusal.None) {
                return BackgroundGradient.Refused(prelude);
            }
        }

        var stops = count - stopsFrom;

        if (stops < LeastStops) {
            return BackgroundGradient.Refused(GradientRefusal.Syntax);
        }

        if (stops > MostStops) {
            return BackgroundGradient.Refused(GradientRefusal.Stops);
        }

        Span<Color4> colours = stackalloc Color4[MostStops];
        Span<float> positions = stackalloc float[MostStops];
        Span<bool> stated = stackalloc bool[MostStops];

        for (var i = 0; i < stops; i++) {
            if (!ReadStop(arguments[parts[stopsFrom + i]], out colours[i], out positions[i], out stated[i], out var why)) {
                return BackgroundGradient.Refused(why);
            }
        }

        Resolve(positions[..stops], stated[..stops]);

        var hasVia = stops == MostStops;

        return new BackgroundGradient(
            colours[0],
            hasVia ? colours[1] : default,
            colours[stops - 1],
            hasVia,
            new GradientStops(positions[0], hasVia ? positions[1] : 0.5f, positions[stops - 1]),
            shape,
            space,
            angle,
            corner,
            centre,
            GradientRefusal.None
        );
    }

    /// <summary>Fills in the positions nobody stated, then makes the list non-decreasing.</summary>
    /// <param name="positions">The positions, stated or not, rewritten in place.</param>
    /// <param name="stated">Which of them the author actually wrote.</param>
    /// <remarks>
    ///     <para>
    ///         CSS's rule, in the two cases three stops can produce. An unstated first stop is at
    ///         zero and an unstated last is at one; an unstated middle is halfway between its
    ///         neighbours, which for three stops is the average of the ends and is <i>not</i>
    ///         necessarily 50% — <c>from-20% to-100%</c> with a bare <c>via-red</c> puts the middle
    ///         at 60%.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The non-decreasing pass is last and is not tidying.</b> CSS says a stop earlier
    ///         in the list than its predecessor is clamped up to it, which turns <c>from-60% to-20%</c>
    ///         into a hard edge at 60% rather than into a backwards ramp. Without it the shader's
    ///         <c>Span</c> sees a negative width, takes its zero-width branch, and draws the step in
    ///         the right place by accident — which is the kind of agreement that stops holding the
    ///         moment either side is touched.
    ///     </para>
    /// </remarks>
    static void Resolve(Span<float> positions, ReadOnlySpan<bool> stated) {
        if (!stated[0]) {
            positions[0] = 0f;
        }

        var last = positions.Length - 1;

        if (!stated[last]) {
            positions[last] = 1f;
        }

        for (var i = 1; i < last; i++) {
            if (!stated[i]) {
                positions[i] = (positions[i - 1] + positions[last]) / 2f;
            }
        }

        for (var i = 1; i < positions.Length; i++) {
            positions[i] = MathF.Max(positions[i], positions[i - 1]);
        }
    }

    /// <summary>Whether an argument is a colour stop rather than a prelude.</summary>
    /// <remarks>
    ///     A prelude opens with one of CSS's own keywords or with an angle, and everything else in the
    ///     first slot is a colour. Tested by what it starts with rather than by trying to parse it
    ///     both ways, because none of these words is a colour and a colour never begins with a digit —
    ///     and because a failed colour parse has to stay distinguishable from a direction, so that a
    ///     typo in the first stop is reported as <see cref="GradientRefusal.Colour" />.
    /// </remarks>
    static bool LooksLikeStop(ReadOnlySpan<char> text) {
        if (text.Length > 0 && (char.IsAsciiDigit(text[0]) || text[0] is '-' or '+' or '.')) {
            return false;
        }

        foreach (var keyword in (ReadOnlySpan<string>) [
            "to ", "in ", "from ", "at ", "circle", "ellipse", "closest-", "farthest-"
        ]) {
            if (text.StartsWith(keyword, StringComparison.OrdinalIgnoreCase)) {
                return false;
            }
        }

        return true;
    }

    /// <summary>Reads the first argument: a direction, a round gradient's geometry, and a space.</summary>
    /// <param name="text">The argument.</param>
    /// <param name="shape">Which gradient function this is.</param>
    /// <param name="angle">The direction, or a conic's <c>from</c>.</param>
    /// <param name="corner">The corner a <c>to bottom right</c> names.</param>
    /// <param name="space">Which space to interpolate in.</param>
    /// <param name="centre">Where an <c>at &lt;position&gt;</c> puts a round gradient's centre.</param>
    /// <returns>Why it was refused, or <see cref="GradientRefusal.None" />.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>One scanner over three grammars, because CSS's own grammar is a <c>||</c>.</b> The
    ///         geometry and the interpolation method may appear in either order in the same
    ///         comma-separated argument — <c>to right in oklab</c> and <c>in oklab to right</c> are the
    ///         same declaration — so reading a prefix and then the rest cannot be right. Tailwind
    ///         happens to emit the first order every time, which is exactly why the second one would
    ///         have gone unnoticed.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An unknown word is a refusal and never a skip.</b> The words this does not
    ///         understand are the ones that change the picture — <c>at 20% 80%</c>, <c>circle</c>,
    ///         <c>longer hue</c> — so quietly ignoring one draws a centred farthest-corner ellipse for
    ///         a declaration that asked for something else and looks like a gradient that is merely
    ///         positioned oddly.
    ///     </para>
    /// </remarks>
    static GradientRefusal ReadPrelude(
        ReadOnlySpan<char> text,
        GradientShape shape,
        ref float angle,
        ref GradientCorner corner,
        ref GradientSpace space,
        ref GradientPoint? centre
    ) {
        Span<Range> words = stackalloc Range[12];
        var count = SplitWords(text, words);

        if (count == 0) {
            return GradientRefusal.Direction;
        }

        for (var i = 0; i < count; i++) {
            var word = text[words[i]];

            if (word.Equals("in", StringComparison.OrdinalIgnoreCase)) {
                if (++i >= count) {
                    return GradientRefusal.Interpolation;
                }

                if (!ReadSpace(text[words[i]], ref space)) {
                    return GradientRefusal.Interpolation;
                }

                // A hue method — `shorter hue`, `longer hue` — is two more words and a different
                // interpolation path entirely, so its presence is a refusal even after the space
                // itself was understood.
                if (i + 1 < count && text[words[i + 1]].Equals("hue", StringComparison.OrdinalIgnoreCase)) {
                    return GradientRefusal.Interpolation;
                }

                continue;
            }

            if (word.Equals("to", StringComparison.OrdinalIgnoreCase)) {
                if (shape != GradientShape.Linear) {
                    return GradientRefusal.Direction;
                }

                var from = i + 1;

                while (i + 1 < count && IsSide(text[words[i + 1]])) {
                    i++;
                }

                if (i < from) {
                    return GradientRefusal.Direction;
                }

                var side = text[words[from].Start..words[i].End];

                var refusal = ReadSide(side, ref angle, ref corner);
                if (refusal != GradientRefusal.None) {
                    return refusal;
                }

                continue;
            }

            if (word.Equals("from", StringComparison.OrdinalIgnoreCase)) {
                if (shape != GradientShape.Conic || ++i >= count) {
                    return GradientRefusal.Direction;
                }

                var refusal = ReadAngle(text[words[i]], ref angle);
                if (refusal != GradientRefusal.None) {
                    return refusal;
                }

                continue;
            }

            // ⚠ `ellipse farthest-corner at center` is CSS's default and is accepted for saying so;
            // everything else about a round gradient's geometry is a centre or an extent the record
            // has no lanes for. See `GradientRefusal.Extent`.
            if (shape != GradientShape.Linear
                && (word.Equals("ellipse", StringComparison.OrdinalIgnoreCase)
                    || word.Equals("farthest-corner", StringComparison.OrdinalIgnoreCase))) {
                continue;
            }

            // ⚠ <b>`at` runs to the end of the prelude or to the next `in`, and stopping at `in` is
            // the whole reason this is a scan rather than a suffix.</b> CSS's grammar is a `||`, so
            // `radial-gradient(at 25% 75% in oklab, …)` and `radial-gradient(in oklab at 25% 75%, …)`
            // are the same declaration — and a position parser that swallowed `in oklab` as two more
            // components would refuse the first and honour the second.
            if (word.Equals("at", StringComparison.OrdinalIgnoreCase)) {
                if (shape == GradientShape.Linear) {
                    // A linear gradient has no `at` in CSS's grammar at all, so this is a typo rather
                    // than a form this engine declines to draw.
                    return GradientRefusal.Direction;
                }

                var start = i + 1;
                var stop = start;

                while (stop < count && !text[words[stop]].Equals("in", StringComparison.OrdinalIgnoreCase)) {
                    stop++;
                }

                var placed = ReadPosition(text, words[start..stop], out var at);
                if (placed != GradientRefusal.None) {
                    return placed;
                }

                centre = at;
                i = stop - 1;

                continue;
            }

            if (word.Equals("circle", StringComparison.OrdinalIgnoreCase)
                || word.StartsWith("closest-", StringComparison.OrdinalIgnoreCase)
                || word.StartsWith("farthest-", StringComparison.OrdinalIgnoreCase)) {
                return GradientRefusal.Extent;
            }

            if (shape == GradientShape.Radial) {
                // ⚠ <c>Extent</c> and not <c>Direction</c>. A bare length or percentage in a radial
                // prelude is its *size* — <c>radial-gradient(80px, …)</c> — and reporting it as an
                // unreadable direction would send the next reader to the angle parser, which is the
                // one part of this file that has nothing to do with it. The refusal reason is the
                // measurement, which is the whole argument this enum is built on.
                return GradientRefusal.Extent;
            }

            if (shape == GradientShape.Conic) {
                // A conic's angle has to be spelled `from <angle>`; a bare one is not CSS.
                return GradientRefusal.Direction;
            }

            var read = ReadAngle(word, ref angle);
            if (read != GradientRefusal.None) {
                return read;
            }
        }

        return GradientRefusal.None;
    }

    /// <summary>Reads a <c>background-position</c>, which is the same production an <c>at</c> takes.</summary>
    /// <param name="text">The computed value.</param>
    /// <returns>The position, or null where it is not one this engine resolves.</returns>
    /// <remarks>
    ///     ⚠ <b>The same parser as <c>at &lt;position&gt;</c>, deliberately and for the reason the mask
    ///     reader shares this whole class.</b> <c>background-position: 25% 75%</c> and
    ///     <c>radial-gradient(at 25% 75%, …)</c> are the same grammar in CSS's own specification, and
    ///     two readers of it would be two sets of keyword handling to keep in step — where the failure
    ///     is a centre that lands in a different place depending on which of the two an author reached
    ///     for.
    /// </remarks>
    public static GradientPoint? ReadPlacement(string text) {
        var span = text.AsSpan().Trim();
        Span<Range> words = stackalloc Range[6];
        var count = SplitWords(span, words);

        return ReadPosition(span, words[..count], out var point) == GradientRefusal.None ? point : null;
    }

    /// <summary>Reads a <c>background-size</c>, as a fraction of the positioning area plus pixels.</summary>
    /// <param name="text">The computed value.</param>
    /// <returns>The tile's size, or null where it is the whole positioning area.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>auto</c>, <c>cover</c> and <c>contain</c> all return null, and that is CSS
    ///         rather than a gap.</b> Backgrounds 3 § 3.9 resolves all three against the image's
    ///         intrinsic dimensions and ratio; a gradient has neither, so <c>auto</c> is 100%,
    ///         <c>contain</c> is the area and <c>cover</c> is the area. For the only kind of
    ///         <c>background-image</c> this engine paints the three are one picture — which is also
    ///         why <c>bg-auto</c>, <c>bg-cover</c> and <c>bg-contain</c> are not registered.
    ///     </para>
    ///     <para>
    ///         ⚠ A one-value form leaves the second axis <c>auto</c>, which for the same reason is the
    ///         whole area and not the first value repeated.
    ///     </para>
    /// </remarks>
    public static GradientPoint? ReadSize(string text) {
        var span = text.AsSpan().Trim();
        Span<Range> words = stackalloc Range[4];
        var count = SplitWords(span, words);

        if (count is 0 or > 2) {
            return null;
        }

        var whole = new GradientOffset(1f, 0f);
        var y = whole;

        if (!ReadAxis(span[words[0]], out var x) || (count == 2 && !ReadAxis(span[words[1]], out y))) {
            return null;
        }

        // The whole area in both axes is what the record's zero already says, so saying it again would
        // write two lanes to describe the arrangement the shader has without them.
        return x == whole && y == whole ? null : new GradientPoint(x, y);

        static bool ReadAxis(ReadOnlySpan<char> word, out GradientOffset offset) {
            if (word.Equals("auto", StringComparison.OrdinalIgnoreCase)
                || word.Equals("cover", StringComparison.OrdinalIgnoreCase)
                || word.Equals("contain", StringComparison.OrdinalIgnoreCase)) {
                offset = new GradientOffset(1f, 0f);

                return true;
            }

            return ReadLength(word, out offset);
        }
    }

    /// <summary>Reads a CSS <c>&lt;position&gt;</c>: the words after an <c>at</c>.</summary>
    /// <param name="text">The prelude the words index into.</param>
    /// <param name="words">The components, already split.</param>
    /// <param name="point">Receives the position.</param>
    /// <returns>Why it was refused, or <see cref="GradientRefusal.None" />.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>One and two components are the forms anybody writes and four is the edge-offset
    ///         form; three is refused rather than guessed.</b> CSS Values 4 § 8.2 keeps a three-value
    ///         syntax only for <c>background-position</c>'s legacy grammar, where it means an edge, an
    ///         offset and a second edge — and the value it leaves out is the one whose absence changes
    ///         which axis the offset belongs to. Guessing that is how a centre lands on the wrong side.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Two keywords may be written in either order and two <i>values</i> may not.</b>
    ///         <c>at top left</c> and <c>at left top</c> are the same point because each keyword names
    ///         its own axis; <c>at 25% 75%</c> is across-then-down by position and nothing else. So the
    ///         two-component case is decided by whether both halves are keywords, which is CSS's own
    ///         rule and not a convenience.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Percentages and pixels, and every other unit is a refusal.</b> A <c>rem</c> or an
    ///         <c>em</c> here would need the element's font size, which is resolved a long way from
    ///         this parser — the same argument <see cref="GradientRefusal.Position" /> already makes
    ///         about a stop written as a length. Refusing is what keeps the gradient visibly absent
    ///         instead of centred on a number nobody meant.
    ///     </para>
    /// </remarks>
    static GradientRefusal ReadPosition(ReadOnlySpan<char> text, ReadOnlySpan<Range> words, out GradientPoint point) {
        point = GradientPoint.Middle;

        switch (words.Length) {
            case 0:
                return GradientRefusal.Extent;

            case 1: {
                if (!ReadComponent(text[words[0]], out var only, out var axis)) {
                    return GradientRefusal.Extent;
                }

                // A lone `top` or `bottom` is a vertical keyword and leaves the horizontal centred,
                // which is the one place a single component is not the horizontal one.
                point = axis == PositionAxis.Vertical
                    ? new GradientPoint(GradientPoint.Middle.X, only)
                    : new GradientPoint(only, GradientPoint.Middle.Y);

                return GradientRefusal.None;
            }

            case 2: {
                if (!ReadComponent(text[words[0]], out var first, out var firstAxis)
                    || !ReadComponent(text[words[1]], out var second, out var secondAxis)) {
                    return GradientRefusal.Extent;
                }

                if (firstAxis == PositionAxis.Vertical && secondAxis == PositionAxis.Horizontal) {
                    point = new GradientPoint(second, first);

                    return GradientRefusal.None;
                }

                if (firstAxis == PositionAxis.Vertical || secondAxis == PositionAxis.Horizontal) {
                    // `at 25% left` and `at top bottom` name one axis twice, which is not a position.
                    return GradientRefusal.Extent;
                }

                point = new GradientPoint(first, second);

                return GradientRefusal.None;
            }

            case 4: {
                if (!ReadEdgePair(text, words[0], words[1], PositionAxis.Horizontal, out var x)
                    || !ReadEdgePair(text, words[2], words[3], PositionAxis.Vertical, out var y)) {
                    return GradientRefusal.Extent;
                }

                point = new GradientPoint(x, y);

                return GradientRefusal.None;
            }

            default:
                return GradientRefusal.Extent;
        }
    }

    /// <summary>Which axis a position keyword commits to.</summary>
    enum PositionAxis : byte {
        /// <summary>A number, or <c>center</c>: whichever axis it is written in.</summary>
        Either,

        /// <summary><c>left</c> or <c>right</c>.</summary>
        Horizontal,

        /// <summary><c>top</c> or <c>bottom</c>.</summary>
        Vertical
    }

    /// <summary>One component of a position: a keyword, a percentage, or a length in pixels.</summary>
    static bool ReadComponent(ReadOnlySpan<char> word, out GradientOffset offset, out PositionAxis axis) {
        axis = PositionAxis.Either;

        if (word.Equals("center", StringComparison.OrdinalIgnoreCase)) {
            offset = new GradientOffset(0.5f, 0f);

            return true;
        }

        if (word.Equals("left", StringComparison.OrdinalIgnoreCase)) {
            (offset, axis) = (new GradientOffset(0f, 0f), PositionAxis.Horizontal);

            return true;
        }

        if (word.Equals("right", StringComparison.OrdinalIgnoreCase)) {
            (offset, axis) = (new GradientOffset(1f, 0f), PositionAxis.Horizontal);

            return true;
        }

        if (word.Equals("top", StringComparison.OrdinalIgnoreCase)) {
            (offset, axis) = (new GradientOffset(0f, 0f), PositionAxis.Vertical);

            return true;
        }

        if (word.Equals("bottom", StringComparison.OrdinalIgnoreCase)) {
            (offset, axis) = (new GradientOffset(1f, 0f), PositionAxis.Vertical);

            return true;
        }

        return ReadLength(word, out offset);
    }

    /// <summary>An edge keyword followed by an inset from it: <c>right 10px</c>.</summary>
    /// <remarks>
    ///     ⚠ The inset runs <i>inwards</i> from the named edge, so <c>right 10px</c> is ten pixels from
    ///     the right and not ten past the left — which is why the far edge negates rather than adds.
    /// </remarks>
    static bool ReadEdgePair(
        ReadOnlySpan<char> text,
        Range edge,
        Range inset,
        PositionAxis expected,
        out GradientOffset offset
    ) {
        offset = default;

        if (!ReadComponent(text[edge], out var anchor, out var axis)
            || axis != expected
            || !ReadLength(text[inset], out var away)) {
            return false;
        }

        offset = anchor.Fraction > 0.5f
            ? new GradientOffset(1f - away.Fraction, -away.Pixels)
            : new GradientOffset(away.Fraction, away.Pixels);

        return true;
    }

    /// <summary>A percentage or a length this parser can resolve without a font.</summary>
    static bool ReadLength(ReadOnlySpan<char> word, out GradientOffset offset) {
        offset = default;

        if (word.EndsWith("%", StringComparison.Ordinal)) {
            if (!float.TryParse(word[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)) {
                return false;
            }

            offset = new GradientOffset(percent / 100f, 0f);

            return true;
        }

        // ⚠ `px` or a bare zero, and nothing else. A unitless non-zero length is not CSS at all, and
        // `rem` and `em` need a font size resolved a long way from here — see this method's remark.
        if (word.EndsWith("px", StringComparison.OrdinalIgnoreCase)) {
            if (!float.TryParse(word[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var pixels)) {
                return false;
            }

            offset = new GradientOffset(0f, pixels);

            return true;
        }

        if (word.Equals("0", StringComparison.Ordinal)) {
            offset = new GradientOffset(0f, 0f);

            return true;
        }

        return false;
    }

    /// <summary>Maps a CSS colour space name onto one this shader mixes in.</summary>
    /// <remarks>
    ///     ⚠ <b>Only the rectangular spaces, and the omissions are deliberate rather than pending.</b>
    ///     <c>hsl</c>, <c>hwb</c>, <c>lch</c> and <c>oklch</c> interpolate along a hue *arc*, which is
    ///     not a lerp in any three lanes — two colours plus a direction round the wheel is a different
    ///     shader, not a different constant. <c>lab</c> and <c>display-p3</c> are rectangular and could
    ///     be added as two more transfer functions; they are refused because nothing asks for them and
    ///     an untested space is worse than an honest gap.
    /// </remarks>
    static bool ReadSpace(ReadOnlySpan<char> text, ref GradientSpace space) {
        if (text.Equals("srgb", StringComparison.OrdinalIgnoreCase)) {
            space = GradientSpace.Srgb;
            return true;
        }

        if (text.Equals("srgb-linear", StringComparison.OrdinalIgnoreCase)) {
            space = GradientSpace.Linear;
            return true;
        }

        if (text.Equals("oklab", StringComparison.OrdinalIgnoreCase)) {
            space = GradientSpace.Oklab;
            return true;
        }

        return false;
    }

    /// <summary>Whether a word is one of the four sides a <c>to …</c> can name.</summary>
    static bool IsSide(ReadOnlySpan<char> word) =>
        word.Equals("top", StringComparison.OrdinalIgnoreCase)
        || word.Equals("bottom", StringComparison.OrdinalIgnoreCase)
        || word.Equals("left", StringComparison.OrdinalIgnoreCase)
        || word.Equals("right", StringComparison.OrdinalIgnoreCase);

    /// <summary>Splits on runs of whitespace that are not inside a function.</summary>
    static int SplitWords(ReadOnlySpan<char> text, Span<Range> ranges) {
        var count = 0;
        var depth = 0;
        var start = -1;

        for (var i = 0; i <= text.Length && count < ranges.Length; i++) {
            var boundary = i == text.Length || (depth == 0 && char.IsWhiteSpace(text[i]));

            if (i < text.Length) {
                switch (text[i]) {
                    case '(':
                        depth++;
                        break;

                    case ')':
                        depth--;
                        break;
                }
            }

            if (boundary) {
                if (start >= 0) {
                    ranges[count++] = new Range(start, i);
                    start = -1;
                }
            } else if (start < 0) {
                start = i;
            }
        }

        return count;
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
    /// <param name="colour">Its colour.</param>
    /// <param name="position">Where it sits, when it said.</param>
    /// <param name="stated">Whether it said.</param>
    /// <param name="refusal">Why it was refused, when it was.</param>
    /// <returns>Whether it is a stop this engine can paint.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Whether a position was stated is carried out separately from what it is, because
    ///         the two mean different things and zero is a legal answer to both.</b> An omitted first
    ///         position and <c>from-0%</c> agree here and stop agreeing the moment a middle stop
    ///         exists: an unstated middle sits halfway between its neighbours, and a stated
    ///         <c>via-0%</c> is a hard edge at the start. Folding them together loses the only thing
    ///         <see cref="Resolve" /> needs to know.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A trailing token that looks numeric and does not parse is a refusal, not part of
    ///         the colour.</b> <c>#fff 10px</c> and <c>#fff calc(10%)</c> are positions this file
    ///         cannot resolve; folding them back into the colour makes the whole stop fail as
    ///         <see cref="GradientRefusal.Colour" />, which sends the next reader looking at the
    ///         palette. Anything not starting with a digit really is part of a multi-word colour.
    ///     </para>
    /// </remarks>
    bool ReadStop(
        ReadOnlySpan<char> text,
        out Color4 colour,
        out float position,
        out bool stated,
        out GradientRefusal refusal
    ) {
        colour = default;
        position = 0f;
        stated = false;
        text = text.Trim();

        var body = text;
        var split = LastSpace(body);

        if (split >= 0) {
            var trailing = body[(split + 1)..];

            if (IsNumeric(trailing)) {
                if (!ReadPosition(trailing, out position)) {
                    refusal = GradientRefusal.Position;
                    return false;
                }

                stated = true;
                body = body[..split].Trim();

                // ⚠ CSS lets one stop carry *two* positions — `red 0% 40%` is shorthand for the same
                // colour twice — and the record has one lane per stop. Caught by looking again rather
                // than by trusting the count, because the second one is what turns a three-stop
                // declaration into a four-stop ramp.
                var again = LastSpace(body);

                if (again >= 0 && IsNumeric(body[(again + 1)..])) {
                    refusal = GradientRefusal.Position;
                    return false;
                }
            }
        }

        if (body.IsEmpty) {
            // A bare position with no colour is CSS's interpolation *hint*, which moves the midpoint
            // of a ramp rather than adding a stop to it. A different feature wearing the same syntax.
            refusal = GradientRefusal.Position;
            return false;
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

    /// <summary>Whether a token is meant to be a number, whatever it turns out to be.</summary>
    static bool IsNumeric(ReadOnlySpan<char> text) =>
        text.Length > 0 && (char.IsAsciiDigit(text[0]) || text[0] is '-' or '+' or '.');

    /// <summary>Reads a stop position as a fraction, from a percentage or a bare zero.</summary>
    /// <remarks>
    ///     ⚠ <b>Not clamped to <c>[0, 1]</c>.</b> CSS lets a stop sit outside the box —
    ///     <c>red -20%, blue 120%</c> is a ramp whose ends are off either edge, so what shows is its
    ///     middle — and the shader's <c>Span</c> divides by the stated width, which reproduces that
    ///     exactly. Clamping here would flatten it into a full-width ramp, brighter at both edges than
    ///     the author asked for.
    /// </remarks>
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
