// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace Vixen.Ui.Composition;

/// <summary>Turns a component's stylesheet into one that only reaches its own elements.</summary>
/// <remarks>
///     <para>
///         <b>A class on every element the component built, and the same class welded onto the end of
///         every selector.</b> <c>.row { … }</c> becomes <c>.row.v-1f2e { … }</c>, and only elements
///         this component created carry <c>v-1f2e</c> — so a component's <c>.row</c> cannot style a
///         caller's <c>.row</c> that happens to be inside it, which is the whole content of
///         <c>scoped</c>.
///     </para>
///     <para>
///         ⚠ <b>Welded to the end rather than prefixed to the front.</b> A descendant prefix —
///         <c>.v-1f2e .row</c> — reads as the obvious implementation and is wrong twice: it misses the
///         component's own root, which is the element a stylesheet most often wants, and it matches a
///         caller's <c>.row</c> projected into a slot, which is exactly what scoping is supposed to
///         stop.
///     </para>
///     <para>
///         ⚠ <b>The scope is per type, not per instance.</b> Every instance of a component shares one
///         class, because they share one stylesheet — a per-instance scope would mean a rule set per
///         row of a list, which is the cost that made the unscoped version worth fixing.
///     </para>
///     <para>
///         ⚠ <b>Nothing inside <c>@keyframes</c> is touched.</b> Its blocks are keyed by
///         <c>from</c>, <c>to</c> and percentages, which are not selectors — appending a class to
///         <c>50%</c> produces a rule that parses and never matches, and the animation quietly loses
///         its middle.
///     </para>
/// </remarks>
public static class ScopedStyles {
    /// <summary>The class a component's elements carry, derived from its type.</summary>
    /// <param name="component">The component's type.</param>
    /// <returns>A class name, stable for the life of the process and across instances.</returns>
    /// <remarks>
    ///     ⚠ <b>From the full name, not the short one.</b> Two components called <c>Row</c> in
    ///     different namespaces are two components, and a scope they shared would let one's styles
    ///     reach the other's elements — which is the bug scoping exists to prevent, reintroduced by
    ///     the naming of the thing that prevents it.
    /// </remarks>
    public static string ScopeOf(Type component) {
        ArgumentNullException.ThrowIfNull(component);

        var name = component.FullName ?? component.Name;
        var hash = 2166136261u;

        foreach (var character in name) {
            hash = (hash ^ character) * 16777619u;
        }

        return string.Create(null, stackalloc char[16], $"v-{hash:x8}");
    }

    /// <summary>Rewrites a stylesheet so that every rule in it also asks for the scope class.</summary>
    /// <param name="css">The stylesheet.</param>
    /// <param name="scope">The class, without a leading dot.</param>
    /// <returns>The rewritten stylesheet.</returns>
    public static string Scope(string css, string scope) {
        ArgumentNullException.ThrowIfNull(css);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);

        var output = new StringBuilder(css.Length + 32);
        Rewrite(css.AsSpan(), scope, output);

        return output.ToString();
    }

    static void Rewrite(ReadOnlySpan<char> css, string scope, StringBuilder output) {
        var start = 0;

        for (var i = 0; i < css.Length; i++) {
            if (css[i] == ';' && css[..i].LastIndexOf('{') < css[..i].LastIndexOf('}')) {
                // A statement at this level — `@import`, `@namespace` — which has no selector.
                output.Append(css[start..(i + 1)]);
                start = i + 1;
                continue;
            }

            if (css[i] != '{') {
                continue;
            }

            var prelude = css[start..i];
            var body = Block(css, i, out var end);

            if (prelude.TrimStart().StartsWith("@", StringComparison.Ordinal)) {
                output.Append(prelude).Append('{');

                // ⚠ A nested at-rule's *contents* are selectors and its prelude is not, so this
                // recurses — except for `@keyframes`, whose contents are offsets.
                if (Named(prelude, "keyframes")) {
                    output.Append(body);
                } else {
                    Rewrite(body, scope, output);
                }

                output.Append('}');
            } else {
                Selectors(prelude, scope, output);
                output.Append('{').Append(body).Append('}');
            }

            start = end + 1;
            i = end;
        }

        if (start < css.Length) {
            output.Append(css[start..]);
        }
    }

    /// <summary>The contents of the block that opens at <paramref name="open" />.</summary>
    static ReadOnlySpan<char> Block(ReadOnlySpan<char> css, int open, out int close) {
        var depth = 0;

        for (var i = open; i < css.Length; i++) {
            if (css[i] == '{') {
                depth++;
            } else if (css[i] == '}' && --depth == 0) {
                close = i;
                return css[(open + 1)..i];
            }
        }

        close = css.Length - 1;
        return css[Math.Min(open + 1, css.Length)..];
    }

    static bool Named(ReadOnlySpan<char> prelude, string rule) {
        var trimmed = prelude.TrimStart();
        return trimmed.Length > 1 && trimmed[1..].TrimStart().StartsWith(rule, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Welds the scope onto every selector of a comma-separated list.</summary>
    static void Selectors(ReadOnlySpan<char> list, string scope, StringBuilder output) {
        var depth = 0;
        var start = 0;

        for (var i = 0; i <= list.Length; i++) {
            if (i < list.Length) {
                if (list[i] is '(' or '[') {
                    depth++;
                } else if (list[i] is ')' or ']') {
                    depth--;
                }

                // ⚠ Top-level commas only: `:is(.a, .b)` is one selector with a comma in it, and
                // splitting there would produce two halves that are each a syntax error.
                if (list[i] != ',' || depth > 0) {
                    continue;
                }
            }

            Weld(list[start..i], scope, output);

            if (i < list.Length) {
                output.Append(',');
            }

            start = i + 1;
        }
    }

    static void Weld(ReadOnlySpan<char> selector, string scope, StringBuilder output) {
        var trimmed = selector.TrimEnd();

        if (trimmed.IsEmpty) {
            output.Append(selector);
            return;
        }

        // ⚠ The trailing whitespace is trimmed *before* the class is appended, and that is the whole
        // of the difference between scoping and not. `.a > .b ` with the space left on becomes
        // `.a > .b .v-x`, which is a descendant selector — it stops matching the element the rule was
        // written for and starts matching its children.
        output.Append(trimmed).Append('.').Append(scope).Append(selector[trimmed.Length..]);
    }
}
