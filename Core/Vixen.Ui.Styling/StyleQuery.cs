// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Styling;

/// <summary>One <c>style()</c> feature: a custom property, and the value it must have or null for "any".</summary>
/// <param name="Property">The custom property, <c>--</c> included.</param>
/// <param name="Value">The value it must compute to, whitespace-normalised, or null for the bare form.</param>
readonly record struct StyleFeature(string Property, string? Value);

/// <summary>Reads and answers <c>@container style(…)</c> conditions.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A style query is answered in the cascade and never in <see cref="ContainerScopes" />,
///         because its subject is a computed value and not a box.</b> CSS Conditional 5 makes every
///         element a style container, so the unnamed query is about the <i>parent</i> — and the
///         cascade already holds the parent's resolved style when it resolves the child, which is
///         where inheritance reads it too. <see cref="StyleUpdater" /> stops descending only where a
///         style did not move, so a parent whose value changed always re-resolves the children that
///         ask about it: the invalidation is the one inheritance already has.
///     </para>
///     <para>
///         ⚠ <b>That is exactly why the named and the mixed forms are refused rather than
///         approximated.</b> <c>@container card style(…)</c> asks the nearest ancestor <i>called</i>
///         <c>card</c>, and <c>(min-width: 400px) and style(…)</c> the nearest <i>size</i> container
///         — both possibly several levels up. A grandparent whose value changed under a parent whose
///         style did not would never reach the element asking, so either form would answer
///         confidently and stale. <c>or</c>, <c>not</c> and standard properties are refused for the
///         reasons <see cref="ContainerQuery" /> refuses them and because no engine compares a
///         standard property's computed value here.
///     </para>
/// </remarks>
static class StyleQuery {
    /// <summary>Whether a prelude is a style query at all, as opposed to a size query.</summary>
    /// <param name="prelude">The text between <c>@container</c> and the block.</param>
    /// <returns>Whether it names a <c>style(</c> feature anywhere.</returns>
    public static bool Mentions(string prelude) =>
        prelude.Contains("style(", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads a condition made only of <c>and</c>-joined <c>style()</c> features.</summary>
    /// <param name="condition">The condition, with any container name already taken off.</param>
    /// <param name="features">Receives the features, in the order written.</param>
    /// <param name="reason">Why it could not be read, when it could not.</param>
    /// <returns>Whether it is a style query this cascade can answer.</returns>
    public static bool TryRead(string condition, out StyleFeature[] features, out string? reason) {
        features = [];
        reason = null;

        var text = condition.AsSpan().Trim();
        var read = new List<StyleFeature>();

        if (text.StartsWith("not ", StringComparison.OrdinalIgnoreCase)) {
            reason = "'not' is not supported in a container query";
            return false;
        }

        while (!text.IsEmpty) {
            if (text[0] == '(') {
                reason = "a style query mixed with a size feature asks the nearest size container, which this cascade does not hold";
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

            if (text.StartsWith("or ", StringComparison.OrdinalIgnoreCase)) {
                reason = "'or' is not supported in a container query";
                return false;
            }

            if (!text.StartsWith("and ", StringComparison.OrdinalIgnoreCase)) {
                reason = $"'{text.ToString()}' does not join two features with 'and'";
                return false;
            }

            text = text["and ".Length..].TrimStart();
        }

        if (read.Count == 0) {
            reason = "a style query needs at least one feature";
            return false;
        }

        features = [.. read];
        return true;
    }

    /// <summary>Whether a parent's resolved style satisfies every feature.</summary>
    /// <param name="features">The features.</param>
    /// <param name="parent">The parent's resolved style, or null for a root, which has no container.</param>
    /// <param name="properties">The table property names are interned in.</param>
    /// <param name="values">The table values are interned in.</param>
    /// <returns>Whether all of them hold.</returns>
    public static bool Holds(StyleFeature[] features, ComputedStyle? parent, NameTable properties, NameTable values) {
        if (parent is null) {
            return false;
        }

        foreach (var feature in features) {
            var id = properties.Lookup(feature.Property);

            // A property nobody declared anywhere is not in the table at all, and has the
            // guaranteed-invalid initial value — which no feature, bare or valued, matches.
            if (id == NameTable.None || !parent.TryGet(id, out var value)) {
                return false;
            }

            if (feature.Value is { } wanted && !string.Equals(Normalise(values.NameOf(value)), wanted, StringComparison.Ordinal)) {
                return false;
            }
        }

        return true;
    }

    /// <summary>A custom property's value as a comparison sees it: trimmed, and runs of space made one.</summary>
    /// <remarks>
    ///     An unregistered custom property's computed value is its token sequence, and whitespace
    ///     between tokens is one token however long it was written — so <c>--x:   a  b</c> and
    ///     <c>style(--x: a b)</c> agree.
    /// </remarks>
    static string Normalise(string? value) =>
        string.Join(' ', (value ?? string.Empty).Split((char[]?) null, StringSplitOptions.RemoveEmptyEntries));

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
