// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Styling;

/// <summary>Which properties a child gets from its parent without asking.</summary>
/// <remarks>
///     <para>
///         Inheritance is not a convenience, it is what makes a stylesheet finite. Setting
///         <c>color</c> on a panel and having every label inside it follow is the difference between
///         a theme and a per-element assignment for every element.
///     </para>
///     <para>
///         The list is CSS's, and it is short for a reason worth knowing: a property inherits when
///         inheriting it is nearly always what someone wants. Text properties inherit; box properties
///         do not, because a panel with <c>padding: 8px</c> whose every descendant also got 8px would
///         be unusable. Getting this list wrong in either direction produces a UI that looks broken
///         in a way that reads as a layout bug.
///     </para>
///     <para>
///         Custom properties (<c>--x</c>) always inherit and are not in the list — they are
///         recognised by their name, since there is no finite set of them.
///     </para>
///     <para>
///         ⚠ <b>This cascade inherits <i>specified</i> values; CSS inherits <i>computed</i> ones,
///         and the difference is not cosmetic.</b> A child inheriting <c>font-size: 1.5em</c> as
///         text would resolve that <c>em</c> against its own parent a second time, so a size meant
///         to be applied once compounds at every level — a two-deep tree comes out at 2.25× where
///         CSS says 1.5×, and the error grows with depth. CSS avoids it by computing
///         <c>font-size</c> to an absolute length before anyone inherits it.
///     </para>
///     <para>
///         <b><c>font-size</c> is therefore removed from this list and inherited in computed form by
///         <c>Vixen.Ui</c> instead</b>, which is the same thing CSS does and in the same place — an
///         element that declares none simply keeps its parent's resolved pixel size. Nothing else
///         needs the specified string, and <c>UiElement.FontSize</c> is the value every consumer
///         actually wants.
///     </para>
///     <para>
///         <b><c>line-height</c> and <c>letter-spacing</c> have since joined it</b>, computed and
///         inherited by <c>Vixen.Ui</c> for the same reason and by the same mechanism. Both take
///         relative units, and both are read by the text layout, so the bounded one-level error they
///         used to carry was one the renderer could see.
///     </para>
///     <para>
///         ⚠ <c>line-height</c> is the one where computing is not simply resolving. A <i>unitless</i>
///         <c>1.5</c> inherits as the number and is multiplied by each descendant's own font size,
///         where <c>1.5em</c> inherits as the length the ancestor resolved. That distinction is the
///         whole reason the unitless form exists, so the computed value carries which of the two it
///         is rather than collapsing both to pixels.
///     </para>
///     <para>
///         ⚠ <b>That gap is now closed, and closing it was a removal from this list rather than an
///         addition to it.</b> <c>text-indent</c> left the sentence when <c>LineWrapper</c> learned a
///         first-line width, and <c>word-spacing</c> left it when <c>TextRun</c> learned which
///         characters CSS separates words with. Neither joined this list on gaining a reader: both
///         take relative units, so both are computed and inherited beside <c>line-height</c> and
///         <c>letter-spacing</c> for the reason the note below gives. <c>word-spacing</c> had been
///         <i>in</i> this list all along, which was the more interesting half — see the note there.
///     </para>
/// </remarks>
public sealed class InheritedProperties {
    static readonly string[] Names = [
        "color",
        "font-family",

        // ⚠ `font-size` is CSS-inherited and is deliberately *not* here. See the type's remarks:
        // this cascade inherits specified values and CSS inherits computed ones, and font size is
        // the property where the difference compounds.
        "font-style",
        "font-weight",
        "font-stretch",
        "font-variant",

        // ⚠ These two are here rather than beside `line-height` because neither takes a relative
        // unit: a feature list is a list of four-character tags and a keyword table, so the
        // specified value and the computed one are the same string and inheriting it is exactly
        // CSS. What `UiDocument.ResolveText` does with them is parse — once per style pass, off
        // this element's own computed style, so a child that declares one of the two keeps the
        // other. See its remarks: building the set from the parent's answer instead would give the
        // two properties one slot to fight over.
        "font-feature-settings",
        "font-variant-numeric",

        // ⚠ `line-height`, `letter-spacing`, `word-spacing` and `text-indent` are CSS-inherited and
        // are deliberately *not* here, for the same reason `font-size` is not: all four take relative
        // units, and inheriting the text `1.5em` would resolve it against the descendant's font size
        // rather than the ancestor's. `Vixen.Ui` inherits their computed values instead — see
        // `UiElement.LineHeight` and `UiElement.TextIndent`.
        //
        // ⚠ `word-spacing` was in this list until it gained a reader, and being in it was wrong the
        // whole time rather than merely premature. It takes relative units exactly as its three
        // siblings do, so the list was a trap with a fuse on it: everything about the property looked
        // correct while nothing read it, and the day a consumer landed `word-spacing: 0.5em` would
        // have compounded down every descendant by the ratio of the font sizes — a defect visible
        // only as text that is slightly too loose, in the one direction nobody measures.
        "text-align",
        "text-transform",
        "white-space",

        // CSS Text 4 § 4 splits `white-space` into a collapsing half and a wrapping half, and both
        // halves inherit. `UiDocument.WrapsOf` reads this one beside `white-space`.
        "text-wrap",
        "word-break",
        "overflow-wrap",

        // ⚠ `text-overflow` is NOT CSS-inherited, and it is here on purpose. CSS applies it to a
        // block container, where it ellipsises the inline content of that container's own line
        // boxes — so it reaches a child span's glyphs without inheriting. Vixen has no line box
        // shared between elements (see `InlineKnownGaps.txt`: one node produces one box), so
        // inheritance is the only route from the container the class is written on to the element
        // that owns the glyphs. The full argument, and what it over-applies, is on
        // `UiDocument.EllipsisOf`.
        "text-overflow",

        // ⚠ <b>None of these five is CSS-inherited, and all five are here for `text-overflow`'s
        // reason, one step stronger.</b> CSS does not inherit a decoration; it <i>propagates</i> one.
        // A block container's `text-decoration-line` decorates the in-flow inline content of its own
        // line boxes — the line is drawn by the ancestor, across the descendants — which is why a
        // child cannot switch it off with `text-decoration: none` and why the ancestor's colour and
        // thickness are the ones used. Vixen has no line box shared between elements (one node
        // produces one box; see `InlineKnownGaps.txt`), so there is no ancestor to draw the line and
        // propagation has nowhere to happen. Inheritance is the only route from the container the
        // class is written on to the element that owns the glyphs — and that route is the whole
        // feature, because a `.vxml` interpolation emits the text as a *child* element, so
        // `&lt;div class="underline"&gt;{Label}&lt;/div&gt;` decorates nothing at all without it.
        //
        // ⚠ <b>What it costs, stated rather than left to be found.</b> A descendant can escape a
        // decoration with `no-underline`, where CSS says it cannot — the forgiving direction, and
        // `text-clip` is already the same shape of opt-out one line above. And a relative thickness
        // or offset resolves against the descendant's own font size rather than the decorating box's,
        // which is `line-height`'s objection two comments up; kept anyway, because it is invisible
        // for the pixel values every utility emits and, where it does show, scaling a mark with the
        // text it marks is the answer somebody would have wanted.
        "text-decoration-line",
        "text-decoration-color",
        "text-decoration-style",
        "text-decoration-thickness",
        "text-underline-offset",
        "direction",
        "visibility",
        "cursor",

        // ⚠ <b>CSS-inherited, and here for `fill`/`stroke`'s reason as much as for CSS's.</b>
        // CSS Basic UI 4 § 4.1 inherits it, and the case that makes the line load-bearing is the
        // narrow one the `fill`/`stroke` note below states: a `caret-accent` is as likely to be
        // written on a form row or a panel as on the field itself, and without this it would
        // resolve, compute, and stop one element short of `TextField.CaretColour` — which, with
        // `CodeEditor`'s copy of the same two lines, is the only thing that reads it.
        "caret-color",

        // ⚠ <b>SVG's two, and they inherit for the reason <c>color</c> does — but the case that makes
        // it load-bearing is narrower than it looks.</b> SVG 2 § 13.2 has both inherit, and an icon is
        // almost never the element anyone writes the class on: `fill-accent` goes on the button, and
        // the <c>&lt;icon&gt;</c> is a child of it. Without these two lines that class would resolve,
        // compute, and stop one element short of the only thing that reads it — a family that works
        // when written directly on an icon and silently does nothing everywhere it is actually
        // written, which is worse than inert because it looks intermittent.
        "fill",
        "stroke",

        // Vixen's own, and inherited for the same reason `color` is: a panel that dims its contents
        // should not have to name every one of them.
        "tint",
        "font-feature-settings"
    ];

    readonly HashSet<int> ids = [];
    readonly Dictionary<int, bool> custom = [];
    readonly NameTable properties;

    /// <summary>Interns the inherited property names into a table.</summary>
    /// <param name="properties">The table property names live in.</param>
    public InheritedProperties(NameTable properties) {
        ArgumentNullException.ThrowIfNull(properties);
        this.properties = properties;

        foreach (var name in Names) {
            ids.Add(properties.Intern(name));
        }
    }

    /// <summary>Whether a property inherits.</summary>
    /// <param name="property">Its interned name.</param>
    /// <returns>Whether a child gets it from its parent unasked.</returns>
    /// <remarks>
    ///     Whether an id is a custom property is answered once per id and remembered. The alternative
    ///     is a string prefix test per property per element per restyle, which would spend the
    ///     interning back.
    /// </remarks>
    public bool Inherits(int property) {
        if (ids.Contains(property)) {
            return true;
        }

        if (custom.TryGetValue(property, out var isCustom)) {
            return isCustom;
        }

        isCustom = IsCustomProperty(properties.NameOf(property));
        custom[property] = isCustom;
        return isCustom;
    }

    /// <summary>Whether two styles differ in any property a child would have inherited.</summary>
    /// <param name="before">One style.</param>
    /// <param name="after">The other.</param>
    /// <returns>Whether a child of an element holding these could be affected by the difference.</returns>
    /// <remarks>
    ///     <para>
    ///         The question a restyle pass asks at every element it touches, and asking the coarser
    ///         version instead — <i>did anything at all change</i> — is what made selecting one row
    ///         of a grid restyle its hundred cells. A highlight that changes <c>background</c> cannot
    ///         reach a child; one that changes <c>color</c> reaches all of them. Only the second is a
    ///         reason to descend.
    ///     </para>
    ///     <para>
    ///         A merge over two arrays already sorted by property id, so it costs one pass over the
    ///         two tables and no allocation.
    ///     </para>
    /// </remarks>
    public bool InheritedPortionDiffers(ComputedStyle before, ComputedStyle after) {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        if (ReferenceEquals(before, after)) {
            return false;
        }

        var left = before.Properties;
        var right = after.Properties;
        int i = 0, j = 0;

        while (i < left.Length && j < right.Length) {
            if (left[i] == right[j]) {
                if (before.Values[i] != after.Values[j] && Inherits(left[i])) {
                    return true;
                }

                i++;
                j++;
            } else if (left[i] < right[j]) {
                // Set on the way in and gone on the way out.
                if (Inherits(left[i++])) {
                    return true;
                }
            } else if (Inherits(right[j++])) {
                return true;
            }
        }

        while (i < left.Length) {
            if (Inherits(left[i++])) {
                return true;
            }
        }

        while (j < right.Length) {
            if (Inherits(right[j++])) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a property name is a custom property.</summary>
    /// <param name="name">The property name.</param>
    /// <returns>Whether it begins with <c>--</c>.</returns>
    public static bool IsCustomProperty(string name) {
        ArgumentNullException.ThrowIfNull(name);
        return name.StartsWith("--", StringComparison.Ordinal);
    }
}
