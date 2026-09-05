// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Styling;

/// <summary>What a property computes to on an element that never mentioned it.</summary>
/// <remarks>
///     <para>
///         <b>The computed-value stage this cascade does not otherwise have, cut down to the one
///         question that needs it.</b> A <see cref="ComputedStyle" /> here holds only the properties
///         some declaration or some inheritance put in it, so "the element had no
///         <c>margin-left</c>" and "the element's <c>margin-left</c> computes to <c>0px</c>" are the
///         same state and are indistinguishable from the outside. Every reader downstream is fine
///         with that — a missing length simply means zero to the layout store and a missing colour
///         means nothing is painted. <see cref="Animator" /> is not: a transition needs a
///         <i>from</i>-value, and "absent" is not a value to travel from, which is why fading
///         <c>margin-left</c> from an implicit zero did not happen while fading it from a declared
///         <c>0px</c> did.
///     </para>
///     <para>
///         ⚠ <b>Which properties are in here is not a design choice, and reading it as one is what
///         made this look bigger than it is.</b> Two rules decide the whole table, and both are
///         checkable rather than tasteful:
///     </para>
///     <list type="number">
///         <item>
///             <b>The property must not inherit.</b> This cascade materialises inheritance into the
///             computed style — <c>StyleResolver</c> copies an inherited property down from the
///             parent — so an inherited property the element did not declare is <i>already in</i>
///             <see cref="ComputedStyle" />, with the value it really computes to. There is nothing
///             to fill in, and filling it in would be actively wrong: <c>color</c>'s CSS initial is
///             the user agent's text colour, so an entry for it would fade every label from black on
///             the first restyle that touched it.
///         </item>
///         <item>
///             <b>Its initial value must be one <see cref="StyleValue.Lerp" /> can travel from.</b>
///             A keyword initial — <c>left: auto</c>, <c>filter: none</c>, <c>rotate: none</c>,
///             <c>box-shadow: none</c> — interpolates discretely in CSS as well, so an entry for it
///             would buy a jump at the halfway mark in place of no transition at all. That is a
///             different picture and not a better one, and it is the shape a hand-picked table would
///             have taken.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>The border widths are the one place this states a value CSS does not, and the
///         divergence is named rather than smuggled.</b> CSS gives <c>border-width</c> an initial of
///         <c>medium</c>, which computes to <c>3px</c> — and then to <c>0</c> anyway, because
///         <c>border-style</c>'s initial is <c>none</c> and a border with no style has no width.
///         This engine has no <c>medium</c> and paints nothing for an undeclared border, so
///         <c>0px</c> is the value it has always behaved as. An entry of <c>3px</c> would fade every
///         appearing border out of a width no frame ever drew.
///     </para>
///     <para>
///         ⚠ <b>Everything not in the table keeps the behaviour it had</b>: no <i>from</i>-value, so
///         no transition, so the property arrives at its new value the instant the cascade decides
///         it. That is the honest partial — a table that guessed at the rest would put a wrong fade
///         where there is currently a correct snap.
///     </para>
/// </remarks>
sealed class InitialValues {
    /// <summary>Property id to the id of its initial value's text.</summary>
    readonly Dictionary<int, int> table = [];

    /// <summary>Interns the table against one engine's name tables.</summary>
    /// <param name="properties">The table property names are interned in.</param>
    /// <param name="values">The table declaration values are interned in.</param>
    /// <remarks>
    ///     ⚠ Value <i>ids</i> rather than parsed <see cref="StyleValue" />s, so that the animator
    ///     parses these through exactly the <see cref="StyleValueParser" /> it parses a declared
    ///     value with. A second path from text to value is a second set of rounding and a second
    ///     place for <c>transparent</c> to mean something slightly different.
    /// </remarks>
    public InitialValues(NameTable properties, NameTable values) {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(values);

        // ⚠ The four physical longhands AND the four logical ones, because ExCSS expands `margin`
        // into the physical set and a stylesheet may write either — `ms-4` emits
        // `margin-inline-start` and nothing rewrites it to `margin-left`. A table with only half of
        // them would make a logical margin the one shape of this defect that survived the fix.
        string[] edges = [
            "top", "right", "bottom", "left", "block-start", "block-end", "inline-start", "inline-end"
        ];

        foreach (var edge in edges) {
            Add(properties, values, $"margin-{edge}", "0px");
            Add(properties, values, $"padding-{edge}", "0px");
            Add(properties, values, $"border-{edge}-width", "0px");
        }

        string[] corners = ["top-left", "top-right", "bottom-right", "bottom-left"];

        foreach (var corner in corners) {
            Add(properties, values, $"border-{corner}-radius", "0px");
        }

        Add(properties, values, "opacity", "1");
        Add(properties, values, "background-color", "transparent");
        Add(properties, values, "outline-width", "0px");
        Add(properties, values, "outline-offset", "0px");
        Add(properties, values, "flex-grow", "0");

        // ⚠ One, not zero, and it is the one number in this table that is easy to get backwards.
        // CSS Flexbox § 7.2 gives `flex-shrink` an initial of 1 — an item shrinks unless told not to
        // — and this engine's own default moved to match it. A zero here would make every item that
        // gained a `shrink-*` class fade out of "cannot shrink", which is the state the box was
        // never in.
        Add(properties, values, "flex-shrink", "1");

        // ⚠ `gap` is deliberately absent. `row-gap` and `column-gap` have an initial of `normal`,
        // which is a keyword — and for a flex container `normal` computes to zero, so a `0px` entry
        // would be right for flex and wrong for the multi-column layout `normal` was written for.
        // Rule 2 excludes it, and excluding it is the same answer.
    }

    /// <summary>The id of the initial value's text, if the property has one here.</summary>
    /// <param name="property">The interned property name.</param>
    /// <param name="value">Receives the value id.</param>
    /// <returns>Whether there is one.</returns>
    public bool TryGet(int property, out int value) => table.TryGetValue(property, out value);

    void Add(NameTable properties, NameTable values, string property, string initial) =>
        table[properties.Intern(property)] = values.Intern(initial);
}
