// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Styling;

/// <summary>One <c>style()</c> feature: a custom property, and the value it must have or null for "any".</summary>
/// <param name="Property">The custom property, <c>--</c> included.</param>
/// <param name="Value">The value it must compute to, whitespace-normalised, or null for the bare form.</param>
readonly record struct StyleFeature(string Property, string? Value);

/// <summary>A whole <c>style()</c> condition: which container it asks, and how its features combine.</summary>
/// <param name="Name">The container name it asks for, or empty for the parent.</param>
/// <param name="Features">The features, in the order written.</param>
/// <param name="Any">Whether they are <c>or</c>-joined rather than <c>and</c>-joined.</param>
/// <param name="Negated">Whether the one feature is under <c>not</c>.</param>
/// <remarks>
///     A class and not a record struct, because a record struct over an array compares the array by
///     reference and this is never used as a key.
/// </remarks>
sealed record StyleCondition(string Name, StyleFeature[] Features, bool Any, bool Negated);

/// <summary>Reads and answers <c>@container style(…)</c> conditions.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A style query is answered in the cascade and never in <see cref="ContainerScopes" />,
///         because its subject is a computed value and not a box.</b> CSS Conditional 5 makes every
///         element a style container, so the unnamed query is about the <i>parent</i> — and the
///         cascade already holds the parent's resolved style when it resolves the child, which is
///         where inheritance reads it too. <see cref="StyleUpdater" /> stops descending where the
///         <i>inherited portion</i> of a style did not move — <c>InheritedPortionDiffers</c> — and
///         every custom property inherits here (<c>InheritedProperties.Inherits</c> answers yes for
///         any <c>--*</c>), so a parent whose queried value changed always re-resolves the children
///         that ask about it: the invalidation is the one inheritance already has.
///     </para>
///     <para>
///         ⚠ <b>That argument rests on the second clause, and it is the one to re-check if
///         <c>@property</c> ever lands.</b> A registered property with <c>inherits: false</c> can
///         change on the parent without the inherited portion moving, so the walk would stop there
///         and a child's <c>style(--x: …)</c> would answer stale. Querying such a property would
///         then need its own edge from the parent to the children that ask.
///     </para>
///     <para>
///         ⚠ <b>The named form is answered too, and it needed exactly that second edge (#273).</b>
///         <c>@container card style(…)</c> asks the nearest ancestor whose <c>container-name</c>
///         includes <c>card</c> — any element, since every element is a style container — and that
///         can be several levels above the parent. Inheritance does not carry the answer down. An
///         element between the two that declares the same property stops the walk, because its
///         inherited portion did not move, and then the elements below it that ask would answer
///         stale. So <see cref="StyleUpdater" /> re-resolves the whole subtree of a named element
///         whose style moved, and does that only when a sheet declares a named style query. The
///         ancestor's style comes from <see cref="StyleResolver.ResolvedAncestor" />.
///     </para>
///     <para>
///         ⚠ <b>The mixed form is still refused.</b> <c>(min-width: 400px) and style(…)</c> asks
///         the nearest <i>size</i> container. That element is known to <see cref="ContainerScopes" />
///         only as a box, and the cascade knows it only as a style, so neither side holds both
///         halves. Standard properties are refused because no engine here compares a standard
///         property's computed value. <c>and</c>, <c>or</c> and a single <c>not</c> are read over
///         <c>style()</c> features. Mixing <c>and</c> with <c>or</c> needs parentheses in CSS, and
///         a parenthesised group is refused.
///     </para>
/// </remarks>
static class StyleQuery {
    /// <summary>Whether a prelude is a style query at all, as opposed to a size query.</summary>
    /// <param name="prelude">The text between <c>@container</c> and the block.</param>
    /// <returns>Whether it names a <c>style(</c> feature anywhere.</returns>
    public static bool Mentions(string prelude) =>
        prelude.Contains("style(", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads a prelude made of an optional container name and <c>style()</c> features.</summary>
    /// <param name="prelude">The text between <c>@container</c> and the block.</param>
    /// <param name="condition">Receives the condition.</param>
    /// <param name="reason">Why it could not be read, when it could not.</param>
    /// <returns>Whether it is a style query this cascade can answer.</returns>
    public static bool TryRead(string prelude, out StyleCondition condition, out string? reason) {
        condition = new StyleCondition(string.Empty, [], false, false);
        reason = null;

        var text = prelude.AsSpan().Trim();
        var name = string.Empty;

        // Anything that opens with neither a feature nor `not` opens with a container name, which
        // ends at the first space. CSS forbids `not`, `and`, `or` and `none` as names, so a prelude
        // that starts with one of those words is a condition and not a name.
        if (!text.StartsWith("style(", StringComparison.OrdinalIgnoreCase)
            && !text.StartsWith("(", StringComparison.Ordinal)
            && !text.StartsWith("not ", StringComparison.OrdinalIgnoreCase)) {
            var space = text.IndexOfAny(' ', '\t', '\n');

            if (space < 0) {
                reason = $"'{text.ToString()}' names a container and asks it nothing";
                return false;
            }

            name = text[..space].ToString();
            text = text[space..].TrimStart();

            if (name.Equals("none", StringComparison.OrdinalIgnoreCase)
                || name.Equals("and", StringComparison.OrdinalIgnoreCase)
                || name.Equals("or", StringComparison.OrdinalIgnoreCase)) {
                reason = $"'{name}' cannot be a container name";
                return false;
            }
        }

        var negated = false;

        if (text.StartsWith("not ", StringComparison.OrdinalIgnoreCase)) {
            negated = true;
            text = text["not ".Length..].TrimStart();
        }

        var read = new List<StyleFeature>();
        bool? any = null;

        while (!text.IsEmpty) {
            if (text[0] == '(') {
                reason = text.Length > 1 && text[1..].TrimStart().StartsWith("style(", StringComparison.OrdinalIgnoreCase)
                    ? "a parenthesised group of style queries is not supported"
                    : "a style query mixed with a size feature asks the nearest size container, which this cascade does not hold";

                return false;
            }

            if (!text.StartsWith("style(", StringComparison.OrdinalIgnoreCase)) {
                reason = $"'{text.ToString()}' is not a container feature Vixen understands";
                return false;
            }

            var close = Closing(text, "style".Length);

            if (close < 0) {
                reason = $"'{text.ToString()}' has no closing parenthesis";
                return false;
            }

            if (!TryFeature(text["style(".Length..close].Trim(), out var feature, out reason)) {
                return false;
            }

            read.Add(feature);
            text = text[(close + 1)..].TrimStart();

            if (text.IsEmpty) {
                break;
            }

            var joinedByOr = text.StartsWith("or ", StringComparison.OrdinalIgnoreCase);

            if (!joinedByOr && !text.StartsWith("and ", StringComparison.OrdinalIgnoreCase)) {
                reason = $"'{text.ToString()}' does not join two features with 'and' or 'or'";
                return false;
            }

            // ⚠ CSS Conditional 5's grammar gives `and` and `or` no precedence over each other, so
            // `a and b or c` is invalid rather than read one way. Taking either reading would make
            // the rule mean something the author did not write.
            if (any is { } previous && previous != joinedByOr) {
                reason = "'and' and 'or' cannot be mixed without parentheses";
                return false;
            }

            any = joinedByOr;
            text = text[(joinedByOr ? "or ".Length : "and ".Length)..].TrimStart();
        }

        if (read.Count == 0) {
            reason = "a style query needs at least one feature";
            return false;
        }

        // `not` takes one query in parens and not a list, so `not style(--a) and style(--b)` is
        // invalid CSS rather than a negation of either half.
        if (negated && read.Count > 1) {
            reason = "'not' applies to one feature; a negated group needs parentheses, which are not supported";
            return false;
        }

        condition = new StyleCondition(name, [.. read], any == true, negated);
        return true;
    }

    /// <summary>Whether a container's resolved style satisfies a condition.</summary>
    /// <param name="condition">The condition.</param>
    /// <param name="container">
    ///     The style the condition is asked of — the parent's for an unnamed query, the named
    ///     ancestor's for a named one — or null when there is no such element.
    /// </param>
    /// <param name="properties">The table property names are interned in.</param>
    /// <param name="values">The table values are interned in.</param>
    /// <returns>Whether it holds.</returns>
    /// <remarks>
    ///     ⚠ <b>No container is false, negated or not.</b> CSS Conditional 5 makes a query with no
    ///     eligible container <i>unknown</i>, and an unknown query does not apply. So
    ///     <c>not style(--x)</c> on an element with no ancestor called <c>card</c> matches nothing.
    ///     It does not match everything.
    /// </remarks>
    public static bool Holds(StyleCondition condition, ComputedStyle? container, NameTable properties, NameTable values) {
        if (container is null) {
            return false;
        }

        if (condition.Negated) {
            return !Holds(condition.Features[0], container, properties, values);
        }

        foreach (var feature in condition.Features) {
            var held = Holds(feature, container, properties, values);

            if (condition.Any == held) {
                // The first true under `or`, or the first false under `and`, decides it.
                return held;
            }
        }

        return !condition.Any;
    }

    static bool Holds(StyleFeature feature, ComputedStyle container, NameTable properties, NameTable values) {
        var id = properties.Lookup(feature.Property);

        // A property nobody declared anywhere is not in the table at all, and has the
        // guaranteed-invalid initial value — which no feature, bare or valued, matches.
        if (id == NameTable.None || !container.TryGet(id, out var value)) {
            return false;
        }

        return feature.Value is not { } wanted
            || string.Equals(Normalise(values.NameOf(value)), wanted, StringComparison.Ordinal);
    }

    /// <summary>Whether a style carries a container name, which is what a named style query looks for.</summary>
    /// <param name="style">An element's computed style.</param>
    /// <param name="properties">The table property names are interned in.</param>
    /// <param name="values">The table values are interned in.</param>
    /// <param name="name">The name to look for, or null for any name at all.</param>
    /// <returns>Whether it carries that name, or any name.</returns>
    /// <remarks>
    ///     <para>
    ///         <c>container-name</c> wins over the <c>container</c> shorthand's half, the order
    ///         <c>UiDocument.KindOf</c> reads them in. <c>none</c> is no name. ⚠ The value is a
    ///         <i>list</i>: <c>container-name: card panel</c> answers both <c>card</c> and
    ///         <c>panel</c>, CSS Containment 3. Names compare ordinally, because a container name is
    ///         a case-sensitive identifier.
    ///     </para>
    ///     <para>
    ///         A style container needs no <c>container-type</c>. Every element is one, so a name on
    ///         a <c>normal</c> box is still a name a style query can find.
    ///     </para>
    /// </remarks>
    public static bool Names(ComputedStyle style, NameTable properties, NameTable values, string? name) {
        var written = ReadNames(style, properties, values);

        if (written.IsEmpty) {
            return false;
        }

        if (name is null) {
            return true;
        }

        foreach (var range in written.SplitAny(' ', '\t', '\n')) {
            if (written[range].Equals(name, StringComparison.Ordinal)) {
                return true;
            }
        }

        return false;
    }

    static ReadOnlySpan<char> ReadNames(ComputedStyle style, NameTable properties, NameTable values) {
        var longhand = properties.Lookup("container-name");

        if (longhand != NameTable.None && style.TryGet(longhand, out var declared)) {
            return Usable(values.NameOf(declared).AsSpan().Trim());
        }

        var shorthand = properties.Lookup("container");

        if (shorthand == NameTable.None || !style.TryGet(shorthand, out var both)) {
            return default;
        }

        var text = values.NameOf(both).AsSpan();
        var slash = text.IndexOf('/');

        return Usable((slash < 0 ? text : text[..slash]).Trim());

        static ReadOnlySpan<char> Usable(ReadOnlySpan<char> names) =>
            names.Equals("none", StringComparison.OrdinalIgnoreCase) ? default : names;
    }

    /// <summary>A custom property's value as a comparison sees it: trimmed, and runs of space made one.</summary>
    /// <remarks>
    ///     An unregistered custom property's computed value is its token sequence, and whitespace
    ///     between tokens is one token however long it was written — so <c>--x:   a  b</c> and
    ///     <c>style(--x: a b)</c> agree.
    /// </remarks>
    static string Normalise(string? value) =>
        string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    static bool TryFeature(ReadOnlySpan<char> text, out StyleFeature feature, out string? reason) {
        feature = default;
        reason = null;

        var colon = text.IndexOf(':');
        var name = (colon < 0 ? text : text[..colon]).Trim();

        if (!name.StartsWith("--", StringComparison.Ordinal) || name.Length == 2) {
            reason = $"'style({text.ToString()})' asks about '{name.ToString()}', and only custom properties can be queried";
            return false;
        }

        if (colon < 0) {
            feature = new StyleFeature(name.ToString(), null);
            return true;
        }

        var value = Normalise(text[(colon + 1)..].ToString());

        if (value.Length == 0) {
            reason = $"'style({text.ToString()})' has a colon and no value";
            return false;
        }

        feature = new StyleFeature(name.ToString(), value);
        return true;
    }

    static int Closing(ReadOnlySpan<char> text, int open) {
        var depth = 0;

        for (var i = open; i < text.Length; i++) {
            if (text[i] == '(') {
                depth++;
            } else if (text[i] == ')' && --depth == 0) {
                return i;
            }
        }

        return -1;
    }
}
