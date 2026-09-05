// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Styling;

/// <summary>What an <c>@layer</c> rule turned out to be.</summary>
/// <param name="Names">The layer names its prelude declared, in order.</param>
/// <param name="Body">The block's contents, or <see langword="null" /> for the statement form.</param>
public readonly record struct LayerRule(IReadOnlyList<string> Names, string? Body);

/// <summary>Reads the <c>@layer</c> rules ExCSS hands back unparsed.</summary>
/// <remarks>
///     <para>
///         ExCSS 4.3.2 predates cascade layers. It does not fail on one — it hands the whole rule
///         back as an unknown rule with its text intact, which
///         [the spike](../../docs/plan/spikes/vcss-excss/RESULT.md) established before any of this was
///         built. So this reads the prelude and hands the body back to ExCSS, and the rest of the
///         engine never learns that the front end had a gap.
///     </para>
///     <para>
///         Two forms, and they do different jobs. The statement <c>@layer a, b;</c> <i>fixes the
///         order</i> without contributing rules, which is the only way to say "utilities lose to
///         components" at the top of a file when the utilities are generated into it later. The block
///         <c>@layer a { … }</c> contributes rules to a layer, creating it at that position if
///         nothing has named it yet. An anonymous <c>@layer { … }</c> is its own layer that nothing
///         can ever add to again.
///     </para>
///     <para>
///         Deliberately a small hand-written reader rather than a tokenizer. A prelude is a
///         comma-separated list of dotted identifiers and a body is brace-balanced text; anything
///         more elaborate would be inventing a problem. What it does have to get right is
///         <i>brace balance inside strings and comments</i>, because a <c>content: "}"</c> in the
///         body would otherwise cut the layer in half.
///     </para>
/// </remarks>
public static class LayerRuleParser {
    /// <summary>Whether some unparsed rule text is an <c>@layer</c> rule.</summary>
    /// <param name="text">The rule as written.</param>
    /// <returns>Whether it starts with <c>@layer</c>.</returns>
    public static bool IsLayerRule(string? text) {
        if (text is null) {
            return false;
        }

        var span = text.AsSpan().TrimStart();
        if (!span.StartsWith("@layer", StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        // `@layers-are-nice { }` is not an @layer rule, and neither is `@layer` as a bare prefix of
        // some at-rule a later CSS level invents.
        var rest = span["@layer".Length..];
        return rest.Length == 0 || rest[0] is ' ' or '\t' or '\r' or '\n' or '{' or ';' or ',';
    }

    /// <summary>Reads an <c>@layer</c> rule.</summary>
    /// <param name="text">The rule as written.</param>
    /// <param name="rule">Receives its names and body.</param>
    /// <returns>Whether it could be read.</returns>
    public static bool TryParse(string? text, out LayerRule rule) {
        rule = default;

        if (!IsLayerRule(text)) {
            return false;
        }

        var span = text!.AsSpan();
        var start = span.IndexOf('@') + "@layer".Length;
        var body = (string?) null;
        var preludeEnd = -1;

        for (var i = start; i < span.Length; i++) {
            if (span[i] == ';') {
                preludeEnd = i;
                break;
            }

            if (span[i] != '{') {
                continue;
            }

            preludeEnd = i;
            var close = FindMatchingBrace(span, i);
            if (close < 0) {
                return false;
            }

            body = span[(i + 1)..close].ToString();
            break;
        }

        if (preludeEnd < 0) {
            // `@layer base` with neither a body nor a terminator. ExCSS gave us the whole rule, so
            // this is the end of the stylesheet rather than a truncation we could recover from.
            preludeEnd = span.Length;
        }

        var prelude = span[start..preludeEnd];
        var names = new List<string>();

        foreach (var range in prelude.Split(',')) {
            var name = prelude[range].Trim();
            if (!name.IsEmpty) {
                names.Add(name.ToString());
            }
        }

        // An anonymous block layer is legal and gets a name nothing can spell, so that a later
        // `@layer "" { … }` cannot reopen it. A *statement* with no names is not legal.
        if (names.Count == 0 && body is null) {
            return false;
        }

        rule = new LayerRule(names, body);
        return true;
    }

    /// <summary>Finds the brace closing the one at <paramref name="open" />.</summary>
    /// <param name="span">The rule text.</param>
    /// <param name="open">The index of the opening brace.</param>
    /// <returns>The index of the closing brace, or -1 if there is none.</returns>
    /// <remarks>
    ///     <para>
    ///         Strings and comments are skipped rather than counted. A body containing
    ///         <c>content: "}"</c> is the whole reason this is not <c>IndexOf('}')</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And an escape outside a string is stepped over, which is what a generated
    ///         utility sheet is full of.</b> CSS Syntax 3 § 4.3.7 lets any character be escaped
    ///         anywhere an identifier may appear, and a class name is where that actually happens
    ///         here: <c>font-features-["onum"_1]</c> escapes to the selector
    ///         <c>.font-features-\[\"onum\"_1\]</c>. Read without this branch the first <c>\"</c>
    ///         opens a string that runs to the <c>"onum"</c> in the declaration, the braces after it
    ///         are counted inside a string that does not exist, and the layer is cut in the wrong
    ///         place — so the rule survives with an empty value rather than failing, which is the
    ///         shape of wrongness nothing reports. Every arbitrary value carrying a quoted string
    ///         was affected, not only this family: <c>content-['x']</c> and
    ///         <c>bg-[url("a.png")]</c> are the same selector.
    ///     </para>
    /// </remarks>
    static int FindMatchingBrace(ReadOnlySpan<char> span, int open) {
        var depth = 0;

        for (var i = open; i < span.Length; i++) {
            var c = span[i];

            switch (c) {
                // An escaped character is one character of an identifier and never a delimiter.
                case '\\':
                    i++;
                    break;

                case '"' or '\'': {
                    i = SkipString(span, i);
                    if (i < 0) {
                        return -1;
                    }

                    break;
                }

                case '/' when i + 1 < span.Length && span[i + 1] == '*': {
                    var end = span[(i + 2)..].IndexOf("*/", StringComparison.Ordinal);
                    if (end < 0) {
                        return -1;
                    }

                    i += 2 + end + 1;
                    break;
                }

                case '{':
                    depth++;
                    break;

                case '}':
                    depth--;
                    if (depth == 0) {
                        return i;
                    }

                    break;

                default:
                    break;
            }
        }

        return -1;
    }

    static int SkipString(ReadOnlySpan<char> span, int start) {
        var quote = span[start];

        for (var i = start + 1; i < span.Length; i++) {
            if (span[i] == '\\') {
                i++;
                continue;
            }

            if (span[i] == quote) {
                return i;
            }
        }

        return -1;
    }
}
