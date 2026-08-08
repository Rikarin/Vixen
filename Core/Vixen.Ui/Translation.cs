// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Layout;
using Vixen.Ui.Styling;

namespace Vixen.Ui;

/// <summary>Reads CSS Transforms 2's <c>translate</c> property off a computed style.</summary>
/// <remarks>
///     <para>
///         <b>The whole of the engine's transform stage, and it is deliberately one property wide.</b>
///         CSS Transforms 2 §3 splits <c>transform</c> into three independent properties —
///         <c>translate</c>, <c>rotate</c> and <c>scale</c> — and Tailwind v4 emits them separately for
///         the same reason: they compose in a fixed order and each is useful alone. Vixen reads the
///         first and refuses the other two, and <i>which</i> one it reads is not an arbitrary place to
///         stop. See the class remarks on <see cref="Of" />.
///     </para>
///     <para>
///         ⚠ <b>A translation is not layout, and this is what keeps that true.</b> Per §3 a transform
///         is applied after layout and does not affect it: a translated element's siblings do not
///         move, its parent does not resize around it, and it can overflow anything. Reading the
///         property here — in <see cref="UiDocument" />'s accumulation pass, downstream of flexbox and
///         upstream of every consumer — is what buys that for nothing. Resolving it into the layout
///         style instead would make a nudged element push its neighbours along, which is the one thing
///         a transform is specified never to do.
///     </para>
///     <para>
///         ⚠ <b>Shared with nobody, and that is the point of it being this small.</b>
///         <see cref="OverflowReader" /> exists because painting and hit testing each interned
///         <c>overflow</c> for themselves and drifted. A translation cannot drift the same way,
///         because there is exactly one place it is applied and both consumers read the result rather
///         than the property — see <c>UiDocument.Accumulate</c>. That is the strongest form of the
///         same guarantee, and it is why this class has one caller.
///     </para>
/// </remarks>
sealed class TranslationReader {
    readonly int property;
    readonly int none;
    readonly StyleValueParser parser;

    /// <summary>Interns the property name and the one keyword that means "do not move".</summary>
    /// <param name="properties">The table property names are interned in.</param>
    /// <param name="values">The table declaration values are interned in.</param>
    /// <param name="keywords">The table identifiers are interned in.</param>
    public TranslationReader(NameTable properties, NameTable values, NameTable keywords) {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(keywords);

        property = properties.Intern("translate");
        none = values.Intern("none");
        parser = new StyleValueParser(values, keywords);
    }

    /// <summary>How far an element's style moves it, in points.</summary>
    /// <param name="element">The element, which a percentage is resolved against.</param>
    /// <param name="metrics">The lengths <c>em</c>, <c>rem</c> and the viewport units resolve against.</param>
    /// <param name="x">Receives the horizontal movement, zero when there is none.</param>
    /// <param name="y">Receives the vertical movement.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A percentage is of the element's <i>own</i> border box, not of its container.</b>
    ///         CSS Transforms 1 §8 is explicit, and it is the opposite of every other percentage in
    ///         the box model — <c>left: 50%</c> is half the containing block and
    ///         <c>translate: 50%</c> is half the element. That difference is the whole reason
    ///         <c>-translate-x-full</c> is the idiom for sliding a drawer exactly its own width off
    ///         screen: no author has to know how wide it turned out to be. Resolving it against the
    ///         parent instead would be right often enough to look like a rounding error.
    ///     </para>
    ///     <para>
    ///         <b>One component means the second is zero</b>, per §3 — <c>translate: 8px</c> moves
    ///         along x only. A third is <c>translate-z</c>, which this engine has no depth for and
    ///         ignores rather than mistaking for y.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Anything unreadable moves by nothing rather than by a guess</b>, and the miss is
    ///         the fast path: this runs once per element per pass, and the overwhelming majority of
    ///         elements — every element in the editor — do not carry the property at all, so the
    ///         <see cref="ComputedStyle.TryGet" /> that fails is the whole cost for them.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The element rather than its width and height, so that the two layout-tree reads
    ///         happen after that miss and not before it.</b> Passing them as arguments is the tidier
    ///         signature and evaluates both on every element in the document whether or not anything
    ///         is going to look at them — the accumulation pass is not a place to spend a lookup per
    ///         element on a property almost nothing sets.
    ///     </para>
    /// </remarks>
    public void Of(UiElement element, LengthContext metrics, out float x, out float y) {
        ArgumentNullException.ThrowIfNull(element);

        x = 0f;
        y = 0f;

        if (!element.Style.TryGet(property, out var id) || id == none) {
            return;
        }

        var value = parser.Parse(id);

        if (value.Kind != StyleValueKind.List) {
            x = Resolve(value, element.Width, metrics);
            return;
        }

        var parts = value.Items;

        if (parts.Length > 0) {
            x = Resolve(parts[0], element.Width, metrics);
        }

        if (parts.Length > 1) {
            y = Resolve(parts[1], element.Height, metrics);
        }
    }

    /// <summary>One component, in points.</summary>
    /// <remarks>
    ///     ⚠ <b>Through <see cref="LengthContext.ToLength" /> rather than by reading
    ///     <see cref="StyleValue.Number" />, so that a component this engine has no reading of comes
    ///     back undefined and is dropped.</b> <c>translate: 8px 2fr</c> then moves along x and not
    ///     along y, where taking the number would have moved it two points — a value invented out of a
    ///     unit nobody wrote.
    /// </remarks>
    static float Resolve(StyleValue value, float against, LengthContext metrics) {
        var length = metrics.ToLength(value);

        return length.Unit switch {
            LayoutUnit.Point => length.Value,
            LayoutUnit.Percent => length.Value / 100f * against,
            _ => 0f
        };
    }
}
