// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Mathematics;
using Vixen.Ui.Layout;
using Vixen.Ui.Rendering;
using Vixen.Ui.Styling;

namespace Vixen.Ui;

/// <summary>Reads <c>transform</c>, <c>rotate</c> and <c>scale</c> off a computed style.</summary>
/// <remarks>
///     <para>
///         <b>The second half of the engine's transform stage, and it works nothing like the first.</b>
///         <see cref="TranslationReader" /> resolves <c>translate</c> into two scalars that
///         <c>UiDocument.Accumulate</c> adds to a position, so both the draw list and the hit test read
///         one already-translated rectangle and cannot disagree. A rotation and a scale cannot be
///         folded into a position — they change the box's <i>shape</i> — so they arrive as a
///         <see cref="UiTransform" /> that each consumer applies where it applies things: the geometry
///         builder to a composited group's four composite vertices, the hit test to the pointer on the
///         way down.
///     </para>
///     <para>
///         ⚠ <b>That is two consumers and one matrix, not two copies of the arithmetic.</b> The matrix
///         is composed here, once per element per pass, origin already folded in — see
///         <see cref="UiTransform" /> — and the two consumers apply it and its inverse to a point.
///         There is no second place that knows what <c>rotate</c> means.
///     </para>
///     <para>
///         ⚠ <b>Still not layout.</b> Read in the accumulation pass for
///         <see cref="TranslationReader" />'s reason and with a stronger consequence: a scaled element
///         keeps the space layout gave it, so a <c>scale-150</c> button overflows its row rather than
///         widening it. CSS Transforms 1 §3 requires exactly that, and it is also the only reading
///         that avoids re-shaping glyphs — which is what the refusal this replaced was protecting.
///     </para>
/// </remarks>
sealed class TransformReader {
    readonly int rotate;
    readonly int scale;
    readonly int list;
    readonly int origin;
    readonly int none;
    readonly int left;
    readonly int centre;
    readonly int right;
    readonly int top;
    readonly int bottom;
    readonly NameTable values;
    readonly StyleValueParser parser;

    // ⚠ One list for the life of the document, cleared per element, because this runs once per
    // element per pass and a transformed element is usually a transformed element every frame — a
    // drag, a hover, an animation at rest. `TrackListProperty` in `LayoutStyleBuilder` keeps its
    // scratch for the same reason and states it the same way.
    readonly List<UiTransform> functions = [];

    /// <summary>Interns the four property names, the keyword that means "do not", and the origins.</summary>
    /// <param name="properties">The table property names are interned in.</param>
    /// <param name="values">The table declaration values are interned in.</param>
    /// <param name="keywords">The table identifiers are interned in.</param>
    public TransformReader(NameTable properties, NameTable values, NameTable keywords) {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(keywords);

        rotate = properties.Intern("rotate");
        scale = properties.Intern("scale");
        list = properties.Intern("transform");
        origin = properties.Intern("transform-origin");
        this.values = values;
        none = values.Intern("none");
        left = keywords.Intern("left");
        centre = keywords.Intern("center");
        right = keywords.Intern("right");
        top = keywords.Intern("top");
        bottom = keywords.Intern("bottom");
        parser = new StyleValueParser(values, keywords);
    }

    /// <summary>The affine an element's style places it under, or null where there is none.</summary>
    /// <param name="element">The element, whose border box the origin and any percentage resolve against.</param>
    /// <param name="metrics">The lengths <c>em</c>, <c>rem</c> and the viewport units resolve against.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Null rather than the identity, and the two misses are checked before anything is
    ///         read.</b> This runs once per element per pass and almost no element carries either
    ///         property, so the pair of <see cref="ComputedStyle.TryGet" /> failures is the whole cost
    ///         for the overwhelming majority of a document —
    ///         <see cref="TranslationReader.Of" />'s argument, doubled because there are two
    ///         properties. Returning an identity instead would be correct and would cost every element
    ///         in the tree a matrix nobody looks at.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Scale first and rotate second, per Transforms 2 §3, which orders the three
    ///         independent properties <c>translate</c>, then <c>rotate</c>, then <c>scale</c> as
    ///         <i>matrix</i> multiplications — so the scale is the innermost and applies to the point
    ///         first.</b> The two commute only when the scale is uniform, which is exactly the case a
    ///         test written casually would use, so the order is asserted in
    ///         <c>Vixen.Ui.Tests.TransformTests</c> against a non-uniform one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An identity composition returns null too.</b> <c>rotate: 0deg</c> and
    ///         <c>scale: 1</c> are the initial values and are written constantly — every
    ///         <c>rotate-0</c>, every animation at rest. Each one that reached
    ///         <c>DrawListBuilder</c> as a transform would open a group and spend a viewport-sized
    ///         surface and a render pass on the identical picture. See <see cref="UiTransform.IsIdentity" />.
    ///     </para>
    /// </remarks>
    public UiTransform? Of(UiElement element, LengthContext metrics) {
        ArgumentNullException.ThrowIfNull(element);

        var hasRotation = element.Style.TryGet(rotate, out var rotation) && rotation != none;
        var hasScale = element.Style.TryGet(scale, out var scaling) && scaling != none;
        var hasList = element.Style.TryGet(list, out var written) && written != none;

        if (!hasRotation && !hasScale && !hasList) {
            return null;
        }

        var about = Origin(element, metrics);
        var composed = UiTransform.Identity;

        if (hasList) {
            // ⚠ <b>The list is innermost, and that is the specification's order rather than the
            // written one.</b> Transforms 2 §3 builds the matrix as translate, then rotate, then
            // scale, then <c>transform</c> — as matrix multiplications, so <c>transform</c> is the
            // last factor and applies to a point <i>first</i>. Reading the four in the order they are
            // listed gives the transpose of the right answer on any element that sets more than one,
            // which is a picture that is right for a uniform scale and wrong for everything else.
            // ⚠ <b>A refused list drops itself and leaves the other two standing</b>, which is CSS's
            // rule for an invalid declaration rather than a convenience: `rotate` and `scale` are
            // separate properties and are not made invalid by their neighbour. Returning nothing at
            // all here would let one `perspective()` somebody pasted in cancel a rotation two lines
            // above it.
            if (Functions(values.NameOf(written), element, metrics, out var read)) {
                composed = read.About(about);
            }
        }

        if (hasScale) {
            Scaling(parser.Parse(scaling), out var x, out var y);
            composed = composed.Then(UiTransform.Scale(x, y, about));
        }

        if (hasRotation) {
            composed = composed.Then(UiTransform.Rotation(Degrees(parser.Parse(rotation)), about));
        }

        return composed.IsIdentity ? null : composed;
    }

    /// <summary>Reads a <c>&lt;transform-list&gt;</c> into one matrix, in the element's own space.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Composed right to left.</b> <c>transform: rotate(45deg) translate(20px)</c> is the
    ///         matrix product <c>R · T</c>, so the <i>last</i> function is applied to a point first —
    ///         the element is moved twenty points along its own rotated x axis rather than twenty
    ///         points across the screen and then spun. The two differ by exactly the rotation, which
    ///         is invisible whenever only one function is written and is the first thing a two-function
    ///         declaration shows.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The whole list is refused if any one function is</b>, and refused means <i>no
    ///         transform</i> rather than a partial one. The three-dimensional functions are the reason
    ///         this matters: <c>rotateX</c>, <c>translate3d</c> and <c>perspective</c> are legal CSS
    ///         and there is no third axis here, so reading the ones that happen to be flat and
    ///         dropping the rest turns a card flip into a card that never moves — which is a picture,
    ///         and a wrong one. Nothing is the honest answer, and it is also what CSS does with a
    ///         declaration it cannot parse.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Percentages are of the element's own border box</b>, per Transforms 1 §8 — x
    ///         against its width, y against its height. That is the same rule <c>translate</c> the
    ///         property follows and the opposite of every percentage in the box model, which resolve
    ///         against the <i>containing block</i>. See <see cref="TranslationReader" />.
    ///     </para>
    /// </remarks>
    bool Functions(string text, UiElement element, LengthContext metrics, out UiTransform result) {
        result = UiTransform.Identity;
        functions.Clear();

        var span = text.AsSpan().Trim();

        if (span.IsEmpty || span.Equals("none", StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        var at = 0;

        while (at < span.Length) {
            if (char.IsWhiteSpace(span[at]) || span[at] == ',') {
                at++;
                continue;
            }

            var open = span[at..].IndexOf('(');

            if (open <= 0) {
                return false;
            }

            var name = span.Slice(at, open).Trim();
            at += open + 1;

            var close = span[at..].IndexOf(')');

            if (close < 0) {
                return false;
            }

            var arguments = span.Slice(at, close);
            at += close + 1;

            // ⚠ A nested parenthesis is `calc()`, `min()` or `var()`, none of which this reads. Caught
            // by looking for one inside the arguments rather than by matching depth, because the
            // answer either way is a refusal and a depth counter would only reach it later.
            if (arguments.Contains('(')) {
                return false;
            }

            if (!Function(name, arguments, element, metrics, out var matrix)) {
                return false;
            }

            functions.Add(matrix);
        }

        if (functions.Count == 0) {
            return false;
        }

        for (var index = functions.Count - 1; index >= 0; index--) {
            result = result.Then(functions[index]);
        }

        return true;
    }

    /// <summary>One <c>&lt;transform-function&gt;</c>, in the element's own space.</summary>
    /// <remarks>
    ///     ⚠ <b><c>skew</c>'s two angles are crossed, and the crossing is the whole of it.</b>
    ///     <c>skewX(a)</c> shifts a point's <i>x</i> by its <i>y</i>, which is the <c>M21</c> cell —
    ///     the y contribution to x — so the first argument writes the second row and the second
    ///     argument writes the first. Written the obvious way round, <c>skewX</c> slants the box the
    ///     other way and only a test with a non-zero y can tell.
    /// </remarks>
    static bool Function(
        ReadOnlySpan<char> name,
        ReadOnlySpan<char> arguments,
        UiElement element,
        LengthContext metrics,
        out UiTransform result
    ) {
        result = UiTransform.Identity;

        Span<Range> parts = stackalloc Range[7];
        var count = Split(arguments, parts);

        if (count <= 0) {
            return false;
        }

        if (Is(name, "matrix")) {
            Span<float> cells = stackalloc float[6];

            if (count != 6) {
                return false;
            }

            for (var index = 0; index < 6; index++) {
                if (!Number(arguments[parts[index]], out cells[index])) {
                    return false;
                }
            }

            result = new UiTransform(cells[0], cells[1], cells[2], cells[3], cells[4], cells[5]);
            return true;
        }

        if (Is(name, "translate") || Is(name, "translateX") || Is(name, "translateY")) {
            var horizontal = !Is(name, "translateY");
            var vertical = Is(name, "translateY");

            if (count > (Is(name, "translate") ? 2 : 1)) {
                return false;
            }

            if (!Distance(arguments[parts[0]], element, metrics, vertical, out var first)) {
                return false;
            }

            var x = horizontal ? first : 0f;
            var y = vertical ? first : 0f;

            if (count == 2) {
                if (!Distance(arguments[parts[1]], element, metrics, vertical: true, out y)) {
                    return false;
                }
            }

            result = new UiTransform(1f, 0f, 0f, 1f, x, y);
            return true;
        }

        if (Is(name, "scale") || Is(name, "scaleX") || Is(name, "scaleY")) {
            if (count > (Is(name, "scale") ? 2 : 1)) {
                return false;
            }

            if (!Number(arguments[parts[0]], out var first)) {
                return false;
            }

            // ⚠ A one-argument `scale()` is uniform, and the two axis forms leave the other axis at
            // one. Same asymmetry `Scaling` documents for the `scale` property, for the same reason:
            // the identity for a scale is one rather than zero.
            var x = Is(name, "scaleY") ? 1f : first;
            var y = Is(name, "scaleX") ? 1f : first;

            if (count == 2 && !Number(arguments[parts[1]], out y)) {
                return false;
            }

            result = new UiTransform(x, 0f, 0f, y, 0f, 0f);
            return true;
        }

        if (Is(name, "rotate") || Is(name, "rotateZ")) {
            if (count != 1 || !Angle(arguments[parts[0]], out var degrees)) {
                return false;
            }

            result = UiTransform.Rotation(degrees, Vector2.Zero);
            return true;
        }

        if (Is(name, "skew") || Is(name, "skewX") || Is(name, "skewY")) {
            if (count > (Is(name, "skew") ? 2 : 1)) {
                return false;
            }

            if (!Angle(arguments[parts[0]], out var first)) {
                return false;
            }

            var second = 0f;

            if (count == 2 && !Angle(arguments[parts[1]], out second)) {
                return false;
            }

            var horizontal = Is(name, "skewY") ? 0f : first;
            var vertical = Is(name, "skewY") ? first : second;

            result = new UiTransform(1f, Tangent(vertical), Tangent(horizontal), 1f, 0f, 0f);
            return true;
        }

        return false;
    }

    static float Tangent(float degrees) => MathF.Tan(degrees * (MathF.PI / 180f));

    static bool Is(ReadOnlySpan<char> name, string expected) =>
        name.Equals(expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>Cuts an argument list on its commas, or on whitespace where it has none.</summary>
    /// <remarks>
    ///     ⚠ Returns −1 for more arguments than the caller has room for, so that <c>matrix(…)</c> with
    ///     seven cells is refused rather than silently read as six.
    /// </remarks>
    static int Split(ReadOnlySpan<char> arguments, Span<Range> parts) {
        var count = 0;
        var at = 0;

        while (at < arguments.Length) {
            if (char.IsWhiteSpace(arguments[at]) || arguments[at] == ',') {
                at++;
                continue;
            }

            var start = at;

            while (at < arguments.Length && !char.IsWhiteSpace(arguments[at]) && arguments[at] != ',') {
                at++;
            }

            if (count == parts.Length) {
                return -1;
            }

            parts[count++] = new Range(start, at);
        }

        return count;
    }

    /// <summary>A bare number, or a percentage read as a fraction.</summary>
    static bool Number(ReadOnlySpan<char> text, out float value) {
        if (text.EndsWith("%", StringComparison.Ordinal)) {
            if (!float.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out value)) {
                return false;
            }

            value /= 100f;
            return true;
        }

        return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>An angle, in degrees, in any of the four units CSS spells one with.</summary>
    static bool Angle(ReadOnlySpan<char> text, out float degrees) {
        degrees = 0f;

        var (suffix, per) = Suffix(text);

        if (float.IsNaN(per) || suffix >= text.Length) {
            return false;
        }

        if (!float.TryParse(text[..^suffix], NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) {
            return false;
        }

        degrees = number * per;
        return true;

        static (int Length, float Degrees) Suffix(ReadOnlySpan<char> value) {
            if (value.EndsWith("deg", StringComparison.OrdinalIgnoreCase)) {
                return (3, 1f);
            }

            if (value.EndsWith("grad", StringComparison.OrdinalIgnoreCase)) {
                return (4, 0.9f);
            }

            if (value.EndsWith("turn", StringComparison.OrdinalIgnoreCase)) {
                return (4, 360f);
            }

            if (value.EndsWith("rad", StringComparison.OrdinalIgnoreCase)) {
                return (3, 180f / MathF.PI);
            }

            // ⚠ A unitless angle is not zero degrees, it is invalid — CSS admits a bare `0` for a
            // length and not for an angle. Reading it as zero would make `rotate(45)` a typo that
            // silently does nothing rather than one the whole declaration is dropped for.
            return (0, float.NaN);
        }
    }

    /// <summary>A length or a percentage of the element's own border box, in points.</summary>
    static bool Distance(
        ReadOnlySpan<char> text,
        UiElement element,
        LengthContext metrics,
        bool vertical,
        out float points
    ) {
        points = 0f;

        if (text.EndsWith("%", StringComparison.Ordinal)) {
            if (!float.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)) {
                return false;
            }

            points = percent / 100f * (vertical ? element.Height : element.Width);
            return true;
        }

        if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var bare)) {
            // A unitless length is only legal as zero, and anything else is a declaration to drop.
            points = 0f;
            return bare == 0f;
        }

        var digits = 0;

        while (digits < text.Length && (char.IsAsciiDigit(text[digits]) || text[digits] is '.' or '-' or '+' or 'e' or 'E')) {
            digits++;
        }

        if (!float.TryParse(text[..digits], NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) {
            return false;
        }

        var unit = text[digits..] switch {
            var u when u.Equals("px", StringComparison.OrdinalIgnoreCase) => StyleUnit.Pixels,
            var u when u.Equals("em", StringComparison.OrdinalIgnoreCase) => StyleUnit.Em,
            var u when u.Equals("rem", StringComparison.OrdinalIgnoreCase) => StyleUnit.Rem,
            var u when u.Equals("vw", StringComparison.OrdinalIgnoreCase) => StyleUnit.ViewportWidth,
            var u when u.Equals("vh", StringComparison.OrdinalIgnoreCase) => StyleUnit.ViewportHeight,
            var u when u.Equals("vmin", StringComparison.OrdinalIgnoreCase) => StyleUnit.ViewportMin,
            var u when u.Equals("vmax", StringComparison.OrdinalIgnoreCase) => StyleUnit.ViewportMax,
            _ => StyleUnit.None
        };

        if (unit == StyleUnit.None) {
            return false;
        }

        var length = metrics.ToLength(StyleValue.FromLength(number, unit));

        if (length.Unit != LayoutUnit.Point) {
            return false;
        }

        points = length.Value;
        return true;
    }

    /// <summary>The point a transform turns about, in absolute document coordinates.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The border box's centre by default, which is the initial value and is <i>not</i>
    ///         the box's origin.</b> Transforms 1 §6 makes <c>transform-origin</c>
    ///         <c>50% 50%</c>, so <c>rotate-45</c> on a button spins it in place. Defaulting to the top
    ///         left instead would swing every rotated element down and to the right by a distance that
    ///         depends on its size, which reads as a layout bug rather than as a wrong origin.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Absolute rather than relative to the element, because the matrix it goes into is
    ///         absolute.</b> Both consumers work in document space — the geometry builder emits vertices
    ///         there and the hit test receives the pointer there — so the origin is added in here, once,
    ///         rather than at each of them.
    ///     </para>
    ///     <para>
    ///         A percentage is of the element's own border box, like <c>translate</c>'s and unlike
    ///         every percentage in the box model. A length is from the box's top left. The five
    ///         keywords are the positions CSS names, and a component this engine cannot read falls back
    ///         to the centre rather than to zero — the difference between "the author wrote something
    ///         odd" and "the element jumps to its corner".
    ///     </para>
    /// </remarks>
    Vector2 Origin(UiElement element, LengthContext metrics) {
        var x = element.Width / 2f;
        var y = element.Height / 2f;

        if (element.Style.TryGet(origin, out var id)) {
            var value = parser.Parse(id);

            if (value.Kind == StyleValueKind.List) {
                var parts = value.Items;

                // ⚠ <b>Two keywords are assigned by <i>axis</i> and not by position, which is the one
                // place this grammar is not positional.</b> Transforms 1 §6 allows
                // <c>transform-origin: top right</c> and <c>right top</c> to mean the same point,
                // because <c>top</c> can only be a y and <c>right</c> can only be an x. Read
                // positionally, <c>top right</c> would ask <c>top</c> for an x and <c>right</c> for a
                // y, get neither, and fall back to the centre on both axes — so <c>origin-top-right</c>
                // and five of its eight siblings would silently be <c>origin-center</c>. A length is
                // always positional, and mixing the two is only legal in that order.
                if (parts.Length > 1 && Axial(parts[0]) && Axial(parts[1])) {
                    x = Keyword(parts[0], element.Width, horizontal: true, x);
                    x = Keyword(parts[1], element.Width, horizontal: true, x);
                    y = Keyword(parts[0], element.Height, horizontal: false, y);
                    y = Keyword(parts[1], element.Height, horizontal: false, y);
                } else {
                    if (parts.Length > 0) {
                        x = Component(parts[0], element.Width, horizontal: true, metrics, x);
                    }

                    if (parts.Length > 1) {
                        y = Component(parts[1], element.Height, horizontal: false, metrics, y);
                    }
                }
            } else {
                // ⚠ One component sets x and leaves y centred, per Transforms 1 §6 — except for the
                // two vertical keywords, which name a y and leave x centred. `Component` returns the
                // fallback unchanged for a keyword of the other axis, which is what makes
                // `transform-origin: top` land at the top *middle* rather than at the top left.
                x = Component(value, element.Width, horizontal: true, metrics, x);
                y = Component(value, element.Height, horizontal: false, metrics, y);
            }
        }

        return new Vector2(element.AbsoluteLeft + x, element.AbsoluteTop + y);
    }

    /// <summary>Whether a component is one of the five keywords an origin can be written with.</summary>
    bool Axial(StyleValue value) =>
        value.Kind == StyleValueKind.Keyword
        && (value.Keyword == left
            || value.Keyword == right
            || value.Keyword == top
            || value.Keyword == bottom
            || value.Keyword == centre);

    /// <summary>One component of an origin, in points from the box's top left.</summary>
    float Component(StyleValue value, float against, bool horizontal, LengthContext metrics, float fallback) {
        if (value.Kind == StyleValueKind.Keyword) {
            return Keyword(value, against, horizontal, fallback);
        }

        var length = metrics.ToLength(value);

        return length.Unit switch {
            LayoutUnit.Point => length.Value,
            LayoutUnit.Percent => length.Value / 100f * against,
            _ => fallback
        };
    }

    /// <summary>One keyword, on one axis, leaving the fallback alone where it names the other.</summary>
    /// <remarks>
    ///     ⚠ <c>center</c> answers on <i>both</i> axes, which is what makes it the value that can be
    ///     written beside any other — <c>center right</c> and <c>right center</c> are both the middle
    ///     of the right edge. The four directional keywords answer on one axis and return the fallback
    ///     on the other, which is what lets the caller ask each of a pair about each axis and take
    ///     whichever answered.
    /// </remarks>
    float Keyword(StyleValue value, float against, bool horizontal, float fallback) {
        var keyword = value.Keyword;

        if (keyword == centre) {
            return against / 2f;
        }

        if (horizontal) {
            return keyword == left ? 0f : keyword == right ? against : fallback;
        }

        return keyword == top ? 0f : keyword == bottom ? against : fallback;
    }

    /// <summary>An angle in degrees, or zero for anything unreadable.</summary>
    /// <remarks>
    ///     ⚠ <b>A list is refused whole rather than having its angle picked out of it.</b> Transforms 2
    ///     §3 also spells <c>rotate</c> as an axis and an angle — <c>rotate: x 45deg</c> — which is a
    ///     rotation out of the plane this engine has no depth for. Reading the angle and ignoring the
    ///     axis would turn every <c>rotate: x 45deg</c> into a <i>z</i> rotation of forty-five degrees,
    ///     which is not a degraded picture but a different one. Zero leaves the element alone, which
    ///     is what an engine with no third axis can honestly do.
    /// </remarks>
    static float Degrees(StyleValue value) =>
        value.Kind == StyleValueKind.Length && value.Unit == StyleUnit.Degrees ? value.Number : 0f;

    /// <summary>The two scale factors, defaulting to one.</summary>
    /// <remarks>
    ///     ⚠ <b>One component scales <i>both</i> axes, which is <c>translate</c>'s rule inverted and is
    ///     the spec's.</b> <c>translate: 8px</c> leaves y alone because the identity for a translation
    ///     is zero; <c>scale: 1.5</c> scales y too because the identity for a scale is one and a
    ///     one-component scale is defined as uniform. Copying the translation reader's shape here would
    ///     make <c>scale-150</c> a horizontal stretch.
    ///     <para>
    ///         Both a bare number and a percentage are accepted, because Transforms 2 §3 allows both
    ///         and Tailwind's <c>scale-*</c> emits the percentage.
    ///     </para>
    /// </remarks>
    static void Scaling(StyleValue value, out float x, out float y) {
        if (value.Kind != StyleValueKind.List) {
            x = y = Factor(value, 1f);
            return;
        }

        var parts = value.Items;
        x = parts.Length > 0 ? Factor(parts[0], 1f) : 1f;
        y = parts.Length > 1 ? Factor(parts[1], 1f) : x;
    }

    /// <summary>One scale factor.</summary>
    static float Factor(StyleValue value, float fallback) => value.Kind switch {
        StyleValueKind.Number => value.Number,
        StyleValueKind.Length when value.Unit == StyleUnit.Percent => value.Number / 100f,
        _ => fallback
    };
}
