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
///         ⚠ <b><c>inset</c> is deliberately not here.</b> ExCSS does not know the property and
///         passes it through whole <i>whether or not</i> it holds a <c>var()</c> — so the layout
///         bridge reads the shorthand itself, and expanding it would be this file inventing a
///         difference rather than removing one. Neither is <c>flex</c>: its one-value form means
///         <c>flex-grow</c> for a number and <c>flex-basis</c> for a length, and which of those a
///         <c>var()</c> holds is exactly what is not known yet.
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
            _ => false
        };
    }

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
