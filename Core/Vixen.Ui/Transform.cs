// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui.Layout;
using Vixen.Ui.Rendering;
using Vixen.Ui.Styling;

namespace Vixen.Ui;

/// <summary>Reads CSS Transforms 2's <c>rotate</c> and <c>scale</c> off a computed style.</summary>
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
    readonly int origin;
    readonly int none;
    readonly int left;
    readonly int centre;
    readonly int right;
    readonly int top;
    readonly int bottom;
    readonly StyleValueParser parser;

    /// <summary>Interns the three property names, the keyword that means "do not", and the origins.</summary>
    /// <param name="properties">The table property names are interned in.</param>
    /// <param name="values">The table declaration values are interned in.</param>
    /// <param name="keywords">The table identifiers are interned in.</param>
    public TransformReader(NameTable properties, NameTable values, NameTable keywords) {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(keywords);

        rotate = properties.Intern("rotate");
        scale = properties.Intern("scale");
        origin = properties.Intern("transform-origin");
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

        if (!hasRotation && !hasScale) {
            return null;
        }

        var about = Origin(element, metrics);
        var composed = UiTransform.Identity;

        if (hasScale) {
            Scaling(parser.Parse(scaling), out var x, out var y);
            composed = composed.Then(UiTransform.Scale(x, y, about));
        }

        if (hasRotation) {
            composed = composed.Then(UiTransform.Rotation(Degrees(parser.Parse(rotation)), about));
        }

        return composed.IsIdentity ? null : composed;
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
