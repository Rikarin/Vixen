// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace Vixen.Ui.Styling;

/// <summary>Expands the shorthands ExCSS leaves alone because they contain a <c>var()</c>.</summary>
/// <remarks>
///     <para>
///         <b>The gap this closes, and how invisible it was.</b> ExCSS expands a shorthand while
///         parsing — <c>border-color: #383c43</c> arrives as four <c>border-*-color</c>
///         declarations — and everything downstream is written against that: the layout bridge reads
///         only longhands and says so in its own remarks, and the draw list asks for
///         <c>border-top-color</c> and <c>border-top-left-radius</c> by name. But a shorthand whose
///         value mentions <c>var()</c> cannot be expanded at parse time, because what it expands
///         <i>to</i> is not known until the custom property is resolved. ExCSS therefore hands it
///         back whole, as a declaration named <c>border-color</c> — which nothing reads.
///     </para>
///     <para>
///         ⚠ <b>The symptom is silence.</b> The declaration is present, the cascade carries it, the
///         substitution resolves it, and it reaches a computed style under a name no consumer asks
///         for. Every stylesheet in this repository writes <c>border-color: var(--border)</c>, so
///         every border in the framework was simply not drawn — no diagnostic, no missing value,
///         nothing to notice except a control that looked flat.
///     </para>
///     <para>
///         <b>Expanded at load rather than after substitution</b>, which is where CSS puts it. The
///         difference is the cascade: expanding afterwards would let
///         <c>border-top-color: red</c> from a weaker rule beat <c>border-color: var(--x)</c> from a
///         stronger one, because by then the winner has already been picked per property. Expanding
///         here makes a var-bearing shorthand behave exactly as the same shorthand without one —
///         which is the property worth having, and the one the tests assert against ExCSS's own
///         output rather than against a table written here.
///     </para>
///     <para>
///         ⚠ <b><c>grid-column</c> and <c>grid-row</c> are here for the opposite reason, and it is
///         the reason <see cref="NeedsExpanding" /> exists.</b> ExCSS has never heard of either
///         property, so it hands them back whole <i>whether or not</i> they hold a <c>var()</c> — the
///         var-only rule above would never fire and the shorthand would reach a computed style
///         intact. That was not merely a missing expansion: it forced the layout bridge to apply the
///         shorthand and then each longhand over it in an order fixed in code, so a
///         <c>grid-row-start</c> from a theme sheet silently discarded a later
///         <c>grid-row: 1 / -1</c> from a utility class and the item was auto-placed into a real
///         cell — the grid looked built rather than broken. Expanding at load gives the cascade two
///         comparable declarations, and the later one wins, which is all "declaration order decides"
///         ever needed.
///     </para>
///     <para>
///         ⚠ <b><c>inset</c> is deliberately not here.</b> ExCSS does not know that property either
///         and passes it through whole <i>whether or not</i> it holds a <c>var()</c> — but the layout
///         bridge reads the shorthand itself and no longhand overlaps it, so expanding it would be
///         this file inventing a difference rather than removing one. Neither is <c>flex</c>: ExCSS
///         <i>does</i> expand that one, and its one-value form means <c>flex-grow</c> for a number
///         and <c>flex-basis</c> for a length, so which of those a <c>var()</c> holds is exactly what
///         is not known yet.
///     </para>
///     <para>
///         ⚠ <b><c>place-self</c>, <c>place-items</c> and <c>place-content</c> have the same hole
///         and are deliberately still in it.</b> ExCSS leaves all three whole, and every longhand
///         they cover <i>is</i> read — <c>LayoutStyleBuilder</c> reads all six of
///         <c>align-</c>/<c>justify-items</c>, <c>-self</c> and <c>-content</c> — so each of the
///         three is a declaration that parses, cascades, resolves, and then does nothing whatever.
///         That is the border-colour silence one more time. It is <i>not</i> a precedence bug:
///         nothing overwrites them, because the bridge has no branch for any of the three, so there
///         is no precedence to get wrong. No utility family and no sheet in the repository emits one
///         either. Adding them is a feature with a test surface of its own — three grammars,
///         <c>place-*</c>'s one-value form meaning both axes. Recorded here so the next reader finds
///         the list rather than the gap.
///     </para>
///     <para>
///         ⚠ <b><c>grid-area</c> was the fourth name on that list until named areas landed, and it
///         had to come off it in the same change.</b> A named area is written <c>grid-area: header</c>
///         far more often than it is written as four longhands, so leaving the shorthand inert would
///         have shipped <c>grid-template-areas</c> with no ordinary way to use it — the "finished
///         thing nothing calls" shape, one property wide.
///     </para>
/// </remarks>
public static class ShorthandExpansion {
    /// <summary>The four edges, in the order a box shorthand names them.</summary>
    static readonly string[] Edges = ["top", "right", "bottom", "left"];

    /// <summary>The four corners, in the order <c>border-radius</c> names them.</summary>
    static readonly string[] Corners = ["top-left", "top-right", "bottom-right", "bottom-left"];

    /// <summary>What <c>border-style</c> accepts, which is how a component of <c>border</c> is known.</summary>
    static readonly HashSet<string> BorderStyles = new(StringComparer.OrdinalIgnoreCase) {
        "none", "hidden", "dotted", "dashed", "solid", "double", "groove", "ridge", "inset", "outset"
    };

    /// <summary>The three keywords that are a border width without being a length.</summary>
    static readonly HashSet<string> BorderWidths = new(StringComparer.OrdinalIgnoreCase) {
        "thin", "medium", "thick"
    };

    /// <summary>Whether this is a shorthand this can take apart.</summary>
    /// <param name="property">The property name, as a stylesheet writes it.</param>
    /// <returns>Whether <see cref="TryExpand" /> has anything to say about it.</returns>
    public static bool IsShorthand(string property) {
        ArgumentNullException.ThrowIfNull(property);

        return property switch {
            "margin" or "padding" => true,
            "border-width" or "border-color" or "border-style" => true,
            "border-radius" => true,
            "gap" => true,
            "border" or "border-top" or "border-right" or "border-bottom" or "border-left" => true,
            "grid-column" or "grid-row" or "grid-area" => true,
            _ => false
        };
    }

    /// <summary>Whether this declaration has to be taken apart before the cascade sees it.</summary>
    /// <remarks>
    ///     ⚠ <b>The <c>var()</c> test is not the whole rule, and reading it as one is what left
    ///     <c>grid-row</c> unexpanded.</b> For everything ExCSS understands, a <c>var()</c> is the
    ///     only thing that stops the parser expanding a shorthand itself, so re-expanding a var-free
    ///     one would be second-guessing it. For a property ExCSS does not know — <c>grid-column</c>
    ///     and <c>grid-row</c> — nothing expanded it in the first place, so the condition is simply
    ///     that it arrived. Asking here rather than at the call sites keeps the two rules in the file
    ///     that can explain the difference.
    /// </remarks>
    /// <param name="property">The property name, as a stylesheet writes it.</param>
    /// <param name="value">Its value, with any <c>var()</c> still in it.</param>
    /// <returns>Whether the caller should try <see cref="TryExpand" /> on it.</returns>
    public static bool NeedsExpanding(string property, string value) {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(value);

        if (!IsShorthand(property)) {
            return false;
        }

        return IsPlacement(property) || VarSubstitution.NeedsSubstitution(value);
    }

    /// <summary>The two shorthands ExCSS hands back whole however they are written.</summary>
    static bool IsPlacement(string property) => property is "grid-column" or "grid-row" or "grid-area";

    /// <summary>Takes a shorthand apart into the longhands its consumers read.</summary>
    /// <param name="property">The property name. <see cref="IsShorthand" /> must hold.</param>
    /// <param name="value">Its value, with any <c>var()</c> still in it.</param>
    /// <param name="into">Where the longhands are appended, name first.</param>
    /// <returns>
    ///     Whether it could be expanded. False means the value is one this cannot divide up — the
    ///     caller should keep the declaration as it stands and say so, rather than guess.
    /// </returns>
    public static bool TryExpand(string property, string value, List<KeyValuePair<string, string>> into) {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(into);

        // Before the split, because a placement's two edges are whole values rather than components:
        // `span 2 / span 2` is two of them and five of what `Split` returns.
        if (property == "grid-area") {
            return Area(value, into);
        }

        if (IsPlacement(property)) {
            return Placement(property, value, into);
        }

        var parts = Split(value);

        if (parts.Count == 0) {
            return false;
        }

        switch (property) {
            case "margin" or "padding":
                return Box(parts, into, edge => $"{property}-{edge}");

            case "border-width" or "border-color" or "border-style":
                var suffix = property["border-".Length..];
                return Box(parts, into, edge => $"border-{edge}-{suffix}");

            case "border-radius":
                return Radius(parts, into);

            case "gap":
                return Gap(parts, into);

            case "border":
                return Border(parts, into, Edges);

            case "border-top" or "border-right" or "border-bottom" or "border-left":
                return Border(parts, into, [property["border-".Length..]]);

            default:
                return false;
        }
    }

    /// <summary>CSS's one-to-four-value box rule: one is all, two are vertical then horizontal.</summary>
    static bool Box(List<string> parts, List<KeyValuePair<string, string>> into, Func<string, string> name) {
        if (!Sides(parts, out var top, out var right, out var bottom, out var left)) {
            return false;
        }

        into.Add(new(name("top"), top));
        into.Add(new(name("right"), right));
        into.Add(new(name("bottom"), bottom));
        into.Add(new(name("left"), left));

        return true;
    }

    static bool Sides(List<string> parts, out string top, out string right, out string bottom, out string left) {
        switch (parts.Count) {
            case 1:
                top = right = bottom = left = parts[0];
                return true;

            case 2:
                top = bottom = parts[0];
                right = left = parts[1];

                return true;

            case 3:
                top = parts[0];
                right = left = parts[1];
                bottom = parts[2];

                return true;

            case 4:
                top = parts[0];
                right = parts[1];
                bottom = parts[2];
                left = parts[3];

                return true;

            default:
                top = right = bottom = left = string.Empty;
                return false;
        }
    }

    /// <summary>
    ///     The corners, clockwise from the top left, each doubled into the horizontal and vertical
    ///     radius the longhand takes — which is what ExCSS emits for the var-free form.
    /// </summary>
    /// <remarks>
    ///     ⚠ The elliptical form, <c>border-radius: 4px / 8px</c>, is refused rather than
    ///     approximated. Its two lists are a different shape from the one this understands, and a
    ///     rounded corner that came out circular when it was written as an ellipse is worse than one
    ///     that says it could not be read.
    /// </remarks>
    static bool Radius(List<string> parts, List<KeyValuePair<string, string>> into) {
        if (parts.Contains("/")) {
            return false;
        }

        string[] corners;

        switch (parts.Count) {
            case 1:
                corners = [parts[0], parts[0], parts[0], parts[0]];
                break;

            case 2:
                corners = [parts[0], parts[1], parts[0], parts[1]];
                break;

            case 3:
                corners = [parts[0], parts[1], parts[2], parts[1]];
                break;

            case 4:
                corners = [parts[0], parts[1], parts[2], parts[3]];
                break;

            default:
                return false;
        }

        for (var index = 0; index < Corners.Length; index++) {
            into.Add(new($"border-{Corners[index]}-radius", $"{corners[index]} {corners[index]}"));
        }

        return true;
    }

    /// <summary>
    ///     <c>grid-column</c> and <c>grid-row</c>, split on their one slash into the two edges the
    ///     bridge reads.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>An omitted end edge is written out as <c>auto</c> rather than left off</b>, which
    ///         is the one place this differs from <see cref="Border" /> above and the difference is
    ///         not a preference. A shorthand resets every longhand it covers, and here that reset is
    ///         expressible: <c>auto</c> is the initial value, <c>GridPlacement.TryParse</c> reads the
    ///         word, and omitting it would let a <c>grid-column-end: 4</c> from a weaker rule survive
    ///         a later <c>grid-column: 1</c> — the same silent-precedence bug one property over.
    ///         <c>border</c> leaves its missing components off only because <c>medium</c> and
    ///         <c>currentcolor</c> are words this framework's value parsers cannot read.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>auto</c> is only <i>half</i> of what CSS Grid §8.4 says, and the other half is
    ///         <see cref="Duplicated" />.</b> The rule is that an omitted second value repeats the
    ///         first <i>when the first is a</i> <c>&lt;custom-ident&gt;</c>, and is <c>auto</c>
    ///         otherwise — so <c>grid-column: sidebar</c> means <c>sidebar / sidebar</c> and
    ///         <c>grid-column: 2</c> means <c>2 / auto</c>. ⚠ This paragraph used to say the rule was
    ///         unreachable because a named line had nowhere to be stored, and warned that whoever
    ///         added names had to add the duplication in the same change. That is this change: an
    ///         area named once covers its whole area, and duplicating only the numeric case would
    ///         have made every one-word <c>grid-area</c> a single row of a multi-row area — which
    ///         lays out, and is wrong by exactly the height of the area.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A <c>var()</c> with no slash beside it is refused</b>, because the slash may be
    ///         inside it: <c>grid-column: var(--place)</c> where <c>--place</c> is <c>1 / 3</c> is a
    ///         start edge and an end edge, and calling the whole thing the start would turn a working
    ///         declaration into a refused one. Left whole, it reaches the bridge's own shorthand
    ///         reading after substitution, exactly as before — which is why those two branches stay.
    ///         A <c>var()</c> on either side of a slash that <i>is</i> written is fine, because then
    ///         each edge is already its own value.
    ///     </para>
    /// </remarks>
    static bool Placement(string property, string value, List<KeyValuePair<string, string>> into) {
        var slash = TopLevelSlash(value);

        if (slash < 0) {
            var only = value.Trim();

            if (only.Length == 0 || VarSubstitution.NeedsSubstitution(only)) {
                return false;
            }

            into.Add(new($"{property}-start", only));
            into.Add(new($"{property}-end", Duplicated(only)));

            return true;
        }

        var start = value[..slash].Trim();
        var end = value[(slash + 1)..].Trim();

        // A second slash is `grid-area`'s four-edge form written under the wrong name, and taking its
        // first two edges would place the item in a real but wrong cell. Refused, and the loader says
        // so — the same judgement `GridPlacement.TryParseShorthand` makes on the value it is handed.
        if (start.Length == 0 || end.Length == 0 || TopLevelSlash(end) >= 0) {
            return false;
        }

        into.Add(new($"{property}-start", start));
        into.Add(new($"{property}-end", end));

        return true;
    }

    /// <summary><c>grid-area</c>, CSS Grid §8.4's four-edge shorthand.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The order is row-start, column-start, row-end, column-end — the two axes
    ///         interleaved rather than one after the other</b>, which is the same order
    ///         <c>margin</c>'s four values go round the box and the reason a hand-written expansion
    ///         reads wrong: <c>grid-area: 1 / 2 / 3 / 4</c> is rows 1–3 and columns 2–4, not rows 1–2.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Each omitted value follows §8.4's duplication rule against the value <i>two</i>
    ///         places before it</b>, not against the one beside it: the fourth falls back to the
    ///         second, the third to the first, and the second to the first. So
    ///         <c>grid-area: header</c> is <c>header</c> on all four edges and an item covering the
    ///         whole named area, while <c>grid-area: 2</c> is one cell at row 2 with three
    ///         <c>auto</c>s. This is the one place a one-word declaration means four things, and it
    ///         is by far the commonest way a named area is written.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Written out as four longhands even where three of them are <c>auto</c></b>, for
    ///         <see cref="Placement" />'s reason: a shorthand resets everything it covers, and an
    ///         omitted longhand would let a stronger-cascaded <c>grid-row-end</c> survive a later
    ///         <c>grid-area</c> that meant to replace it.
    ///     </para>
    /// </remarks>
    static bool Area(string value, List<KeyValuePair<string, string>> into) {
        List<string> edges = [];
        var from = 0;

        while (true) {
            var slash = TopLevelSlash(value[from..]);

            if (slash < 0) {
                edges.Add(value[from..].Trim());
                break;
            }

            edges.Add(value.Substring(from, slash).Trim());
            from += slash + 1;

            if (edges.Count == 4) {
                // A fifth component: `grid-area` has four edges and nothing sensible to do with a
                // value that names five, so the whole declaration is left for the loader to report.
                return false;
            }
        }

        if (edges.Count == 0 || edges.Exists(static edge => edge.Length == 0 || VarSubstitution.NeedsSubstitution(edge))) {
            return false;
        }

        var rowStart = edges[0];
        var columnStart = edges.Count > 1 ? edges[1] : Duplicated(rowStart);
        var rowEnd = edges.Count > 2 ? edges[2] : Duplicated(rowStart);
        var columnEnd = edges.Count > 3 ? edges[3] : Duplicated(columnStart);

        into.Add(new("grid-row-start", rowStart));
        into.Add(new("grid-column-start", columnStart));
        into.Add(new("grid-row-end", rowEnd));
        into.Add(new("grid-column-end", columnEnd));

        return true;
    }

    /// <summary>§8.4's fallback for an omitted edge: the value it repeats, or <c>auto</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>Anything that is not a bare identifier is <c>auto</c>, including <c>auto</c> itself
    ///     and including <c>span 2</c>.</b> A number does not repeat — <c>grid-row: 2</c> is one row
    ///     and not rows 2 to 2 — and a span repeated against itself would be over-constrained and
    ///     dropped by §8.3 anyway. The test is therefore "does this look like a
    ///     <c>&lt;custom-ident&gt;</c>", which here means a first character that cannot start a
    ///     number and no whitespace in it.
    ///     <br />
    ///     ⚠ <b>The digit test is on the first character only, and that is not a shortcut.</b>
    ///     <c>A1</c>, <c>col2</c> and <c>main-1</c> are area names the conformance oracle accepts, so
    ///     rejecting an edge for holding a digit anywhere would silently drop the duplication for
    ///     most of the names a real layout uses — and drop it into <c>auto</c>, which lays out.
    /// </remarks>
    static string Duplicated(string edge) {
        if (edge.Length == 0 || edge.Equals("auto", StringComparison.OrdinalIgnoreCase)) {
            return "auto";
        }

        // A sign or a stop starts a number unless a letter follows it: `-1` is a line and `-minus`
        // is one of the names the conformance oracle accepts.
        if (edge[0] is (>= '0' and <= '9') or '.'
            || (edge[0] is '+' or '-' && (edge.Length == 1 || edge[1] is (>= '0' and <= '9') or '.'))) {
            return "auto";
        }

        foreach (var code in edge) {
            if (char.IsWhiteSpace(code)) {
                return "auto";
            }
        }

        return edge;
    }

    /// <summary>The first slash that is not inside a function, or −1.</summary>
    static int TopLevelSlash(string value) {
        var depth = 0;

        for (var index = 0; index < value.Length; index++) {
            switch (value[index]) {
                case '(':
                    depth++;
                    break;

                case ')':
                    depth--;
                    break;

                case '/' when depth == 0:
                    return index;
            }
        }

        return -1;
    }

    /// <summary>Row then column, which is the order the shorthand names them and not the enum's.</summary>
    static bool Gap(List<string> parts, List<KeyValuePair<string, string>> into) {
        if (parts.Count is not (1 or 2)) {
            return false;
        }

        into.Add(new("row-gap", parts[0]));
        into.Add(new("column-gap", parts[^1]));

        return true;
    }

    /// <summary>
    ///     A width, a style and a colour in any order — and a <c>var()</c> takes whichever one of
    ///     the three is left over.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Only when exactly one role is left.</b> <c>border: 1px solid var(--c)</c> has a
    ///     length and a style keyword, so the <c>var()</c> can only be the colour and that is a fact
    ///     rather than a guess. <c>border: 1px var(--rest)</c> leaves two roles for one value and
    ///     <c>border: var(--all)</c> leaves three; both are refused, because a colour written into
    ///     the width slot is a border that silently changes thickness with the theme.
    ///     <para>
    ///         Components that are not given are left out rather than reset to their initial values.
    ///         A shorthand does reset them, but <c>medium</c> and <c>currentcolor</c> are two
    ///         keywords this framework's value parsers do not read — emitting them would replace a
    ///         missing declaration with an unreadable one, which is the same outcome and harder to
    ///         explain.
    ///     </para>
    /// </remarks>
    static bool Border(List<string> parts, List<KeyValuePair<string, string>> into, string[] edges) {
        if (parts.Count > 3) {
            return false;
        }

        string? width = null, style = null, colour = null;
        var deferred = 0;

        foreach (var part in parts) {
            if (VarSubstitution.NeedsSubstitution(part)) {
                deferred++;
                continue;
            }

            if (BorderStyles.Contains(part)) {
                if (style is not null) {
                    return false;
                }

                style = part;
            } else if (IsWidth(part)) {
                if (width is not null) {
                    return false;
                }

                width = part;
            } else {
                if (colour is not null) {
                    return false;
                }

                colour = part;
            }
        }

        var open = (width is null ? 1 : 0) + (style is null ? 1 : 0) + (colour is null ? 1 : 0);

        if (deferred > 1 || (deferred == 1 && open != 1)) {
            return false;
        }

        if (deferred == 1) {
            var value = parts.Single(VarSubstitution.NeedsSubstitution);

            if (width is null) {
                width = value;
            } else if (style is null) {
                style = value;
            } else {
                colour = value;
            }
        }

        foreach (var edge in edges) {
            Add(into, $"border-{edge}-width", width);
            Add(into, $"border-{edge}-style", style);
            Add(into, $"border-{edge}-color", colour);
        }

        return true;
    }

    static void Add(List<KeyValuePair<string, string>> into, string name, string? value) {
        if (value is not null) {
            into.Add(new(name, value));
        }
    }

    static bool IsWidth(string part) =>
        BorderWidths.Contains(part)
        || part.StartsWith("calc(", StringComparison.OrdinalIgnoreCase)
        || (part.Length > 0 && (char.IsAsciiDigit(part[0]) || part[0] is '.' or '+' or '-'));

    /// <summary>Splits a value on top-level whitespace, so a <c>var()</c> stays in one piece.</summary>
    /// <remarks>
    ///     Depth-counted rather than split on spaces, because <c>var(--pad, 4px 8px)</c> is one
    ///     component containing two of them and a naive split turns a one-value shorthand into a
    ///     three-value one. The slash of an elliptical <c>border-radius</c> comes out as its own
    ///     component, which is how <see cref="Radius" /> recognises the form it refuses.
    /// </remarks>
    static List<string> Split(string value) {
        List<string> parts = [];
        var current = new StringBuilder();
        var depth = 0;

        foreach (var character in value) {
            switch (character) {
                case '(':
                    depth++;
                    current.Append(character);

                    break;

                case ')':
                    depth--;
                    current.Append(character);

                    break;

                case '/' when depth == 0:
                    Flush(parts, current);
                    parts.Add("/");

                    break;

                default:
                    if (depth == 0 && char.IsWhiteSpace(character)) {
                        Flush(parts, current);
                    } else {
                        current.Append(character);
                    }

                    break;
            }
        }

        Flush(parts, current);
        return parts;
    }

    static void Flush(List<string> parts, StringBuilder current) {
        if (current.Length > 0) {
            parts.Add(current.ToString());
            current.Clear();
        }
    }
}
