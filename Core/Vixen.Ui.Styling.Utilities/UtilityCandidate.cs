// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;

namespace Vixen.Ui.Styling.Utilities;

/// <summary>One class name, taken apart.</summary>
/// <param name="Original">The class name as written, which is also the selector it emits.</param>
/// <param name="Variants">The <c>hover:</c>, <c>md:</c>, <c>[&amp;&gt;*]:</c> prefixes, in order.</param>
/// <param name="Name">The utility name — <c>p</c>, <c>bg</c>, <c>flex</c>.</param>
/// <param name="Value">Its value — <c>4</c>, <c>accent</c>, <c>[37px]</c> — or empty.</param>
/// <param name="Negative">
///     Whether it was written with a leading <c>-</c>. A property of the <i>value</i> rather than of
///     the family, because <c>-mt-4</c> sets exactly what <c>mt-4</c> sets and differs only in sign.
/// </param>
/// <param name="Arbitrary">
///     The contents of <c>[…]</c>, when the value is one — and the value half of an arbitrary
///     property, and the <c>var(…)</c> that <c>bg-(--brand)</c> was shorthand for.
/// </param>
/// <param name="Opacity">The <c>/50</c> suffix read as an opacity, or null.</param>
/// <param name="SlashSuffix">
///     The <c>/50</c> suffix as written. Kept as well as <see cref="Opacity" /> because the two
///     readings of a slash are genuinely different: <c>bg-accent/50</c> is half-transparent, and
///     <c>w-2/3</c> is two thirds wide, which as an opacity would be three percent. Which one a
///     slash means is the utility's to decide, not the parser's.
/// </param>
/// <param name="Important">Whether it ended in <c>!</c>.</param>
/// <param name="Property">
///     The CSS property named by an <i>arbitrary property</i> — the <c>mask-type</c> of
///     <c>[mask-type:luminance]</c> — or null for every other candidate.
///     <para>
///         ⚠ <b>A separate field rather than a <see cref="Name" /> that happens to look like a
///         property, because the two are read by different tables and confusing them is the bug.</b>
///         <see cref="Name" /> is a key into <see cref="UtilityFamilies" />' registry; this is a
///         property name to emit verbatim, and there is by construction no family for it. A candidate
///         with a <see cref="Property" /> has an empty <see cref="Name" /> and its value in
///         <see cref="Arbitrary" />.
///     </para>
/// </param>
public readonly record struct UtilityCandidate(
    string Original,
    IReadOnlyList<string> Variants,
    string Name,
    string Value,
    bool Negative,
    string? Arbitrary,
    float? Opacity,
    string? SlashSuffix,
    bool Important,
    string? Property = null
);

/// <summary>Takes a class name apart into what it asks for.</summary>
/// <remarks>
///     <para>
///         The grammar is <c>[variant:]*utility[-value][/opacity][!]</c>, and every one of those
///         separators can also appear <i>inside</i> an arbitrary value. <c>[&amp;>*]:p-4</c> has a
///         variant containing no colon but plenty of brackets; <c>bg-[url(a/b.png)]</c> has a slash
///         that is not an opacity; <c>content-['a:b']</c> has a colon that is not a variant
///         separator. So every split here is bracket-aware, and the naive version of each one is a
///         bug that only shows up on the arbitrary values people reach for precisely when nothing
///         else will do.
///     </para>
///     <para>
///         Deliberately permissive about what a <i>utility</i> is: this only splits, and whether
///         <c>p-4</c> means anything is <see cref="UtilityFamilies" />' question. Scanning is
///         over-inclusive by design — a false positive costs one unused rule — so failing here has
///         to mean "this is not shaped like a utility at all", never "I do not know this one".
///     </para>
///     <para>
///         ⚠ <b>Three escape hatches and not one, and the two beside <c>w-[37px]</c> are both decided
///         in this method because neither has a family to decide it.</b> The arbitrary <i>value</i>
///         <c>w-[37px]</c> is a known family given a value the theme does not hold. The arbitrary
///         <i>property</i> <c>[mask-type:luminance]</c> is a property with no family at all, and it
///         parses to an empty <see cref="UtilityCandidate.Name" /> with the property in
///         <see cref="UtilityCandidate.Property" /> — the one candidate a registry lookup must not be
///         asked about. And <c>bg-(--brand)</c> is v4's shorthand for <c>bg-[var(--brand)]</c>, which
///         is rewritten into the arbitrary value here so that nothing downstream has to know there
///         were ever two spellings.
///     </para>
///     <para>
///         ⚠ <b>Both of the new two are shape-tested rather than waved through, and a malformed one
///         produces no candidate at all.</b> <c>[1..:red]</c> and <c>[mask type:red]</c> are refused
///         by <c>IsPropertyName</c> here, and the value half is refused by
///         <c>UtilityFamilies.IsPlausibleValue</c> exactly as <c>w-[1..]</c> is — the <c>text[1..]</c>
///         defect has two more ways in now and both are closed.
///     </para>
/// </remarks>
public static class UtilityParser {
    /// <summary>Takes a class name apart.</summary>
    /// <param name="candidate">The class name.</param>
    /// <param name="parsed">Receives the parts.</param>
    /// <returns>Whether it is shaped like a utility.</returns>
    public static bool TryParse(string candidate, [NotNullWhen(true)] out UtilityCandidate parsed) {
        parsed = default;

        if (string.IsNullOrWhiteSpace(candidate)) {
            return false;
        }

        var text = candidate.AsSpan();
        var variants = new List<string>();

        // Variants first, left to right. The last colon at bracket depth zero ends them.
        while (true) {
            var colon = IndexAtTopLevel(text, ':');
            if (colon < 0) {
                break;
            }

            var variant = text[..colon].Trim();
            if (variant.IsEmpty) {
                return false;
            }

            variants.Add(variant.ToString());
            text = text[(colon + 1)..];
        }

        if (text.IsEmpty) {
            return false;
        }

        // `-mt-4` is `mt-4` with the sign flipped. Stripped here rather than registered as a family
        // of its own, because every negatable family would otherwise need a second entry differing
        // from the first only in sign — and the sign is a property of the value, not of the family.
        var negative = text[0] == '-';
        if (negative) {
            text = text[1..];

            if (text.IsEmpty) {
                return false;
            }
        }

        var important = text[^1] == '!';
        if (important) {
            text = text[..^1];
        }

        float? opacity = null;
        string? slashSuffix = null;
        var slash = LastIndexAtTopLevel(text, '/');

        if (slash > 0) {
            slashSuffix = text[(slash + 1)..].ToString();
            text = text[..slash];

            if (TryOpacity(slashSuffix, out var fraction)) {
                opacity = fraction;
            }
        }

        string? arbitrary = null;
        string? property = null;
        var open = IndexAtTopLevel(text, '[');
        if (open >= 0) {
            if (text[^1] != ']') {
                return false;
            }

            var inside = text[(open + 1)..^1];
            text = text[..open];

            if (text.Length > 0 && text[^1] == '-') {
                text = text[..^1];
            }

            // Nothing to the left of the bracket is the *arbitrary property* — `[mask-type:luminance]`
            // — rather than a nameless arbitrary value. The two are the same characters up to this
            // point and mean entirely different things, and this is the only place that can tell them
            // apart: below here the name is a registry key, and an arbitrary property has no family by
            // construction. Before this existed the empty name simply failed the check at the bottom
            // of this method and the class was silently unknown.
            if (text.IsEmpty) {
                var split = IndexAtTopLevel(inside, ':');

                // The colon has to be there and has to have a property name in front of it. `[red]`
                // is not an arbitrary property and neither is `[:red]`.
                if (split <= 0) {
                    return false;
                }

                // ⚠ The property name is *not* underscore-converted and the value is. A space is
                // never part of a property name, so a `_` in one is a `_` — `--my_var` is a custom
                // property somebody meant — where a `_` in the value is the space a class attribute
                // cannot hold.
                property = inside[..split].ToString();

                if (!IsPropertyName(property)) {
                    return false;
                }

                arbitrary = inside[(split + 1)..].ToString().Replace('_', ' ');
            } else {
                // `_` stands for a space, because a class attribute cannot contain one. Tailwind's
                // convention, and the only workable one: `grid-cols-[1fr_auto]` has to say two things.
                arbitrary = inside.ToString().Replace('_', ' ');
            }
        } else if (TryVariableShorthand(text, out var variable, out var before)) {
            // v4's `bg-(--brand)`, which is exactly `bg-[var(--brand)]` and is rewritten into it here
            // so that one arbitrary-value path serves both spellings.
            arbitrary = variable;
            text = before;
        }

        var whole = text.ToString();
        var name = whole;
        var value = string.Empty;

        if (arbitrary is null) {
            // The name is the longest prefix that is a known family, because both `p` and
            // `place-items` exist and a first-hyphen split would call the latter `place`.
            var split = UtilityFamilies.SplitName(whole);
            name = split.Name;
            value = split.Value;
        }

        // An arbitrary property is the one candidate with no name at all, and that is what it is: the
        // escape hatch for a property the family table has never heard of.
        if (name.Length == 0 && property is null) {
            return false;
        }

        parsed = new UtilityCandidate(
            candidate,
            variants,
            name,
            value,
            negative,
            arbitrary,
            opacity,
            slashSuffix,
            important,
            property
        );

        return true;
    }

    /// <summary>Whether a run of text is a CSS identifier, and so usable as a property name.</summary>
    /// <remarks>
    ///     ⚠ <b>The property half of the same argument <c>UtilityFamilies.IsPlausibleValue</c> makes
    ///     about the value half, and it is needed for the same reason.</b> An arbitrary property
    ///     bypasses the family table entirely, so nothing downstream will ever say that
    ///     <c>[1..:red]</c> is not a declaration — it would be written into the sheet, refused by
    ///     ExCSS, and dropped without a word, which is the <c>text[1..]</c> defect one field over.
    ///     A malformed arbitrary property must produce <i>no rule at all</i>, and this is where that
    ///     is decided.
    ///     <para>
    ///         A leading <c>--</c> is allowed because a custom property is a property:
    ///         <c>[--my-gap:4px]</c> is a thing people write, and CSS Variables § 2 makes it an
    ///         identifier like any other.
    ///     </para>
    /// </remarks>
    static bool IsPropertyName(string text) {
        if (text.Length == 0) {
            return false;
        }

        // A custom property is `--` and then an identifier body, which may not itself be empty.
        var body = text.StartsWith("--", StringComparison.Ordinal) ? 2 : 0;

        if (body == text.Length) {
            return false;
        }

        // A property name never begins with a digit, and CSS Syntax 3 § 4.3.8 would need it escaped
        // if one did. Neither does it begin with a hyphen unless it is the custom-property `--`.
        if (body == 0 && !char.IsAsciiLetter(text[0]) && text[0] != '_') {
            return false;
        }

        for (var i = body; i < text.Length; i++) {
            if (!char.IsAsciiLetterOrDigit(text[i]) && text[i] is not ('-' or '_')) {
                return false;
            }
        }

        return true;
    }

    /// <summary>Reads v4's <c>bg-(--brand)</c> as the <c>bg-[var(--brand)]</c> it is shorthand for.</summary>
    /// <param name="text">The utility text, brackets already ruled out.</param>
    /// <param name="variable">Receives the <c>var(…)</c> the shorthand stands for.</param>
    /// <param name="before">Receives the text to the left of the parenthesis, its hyphen removed.</param>
    /// <returns>Whether the text is the shorthand.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Rewritten into the arbitrary value rather than given a path of its own</b>, so that
    ///         <c>IsPlausibleValue</c>, the border-edge colour test and everything else downstream see
    ///         one shape and cannot disagree about the other. The two spellings are the same utility in
    ///         v4 and the only honest way to keep them the same utility here is for the second to stop
    ///         existing this early.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Only a custom property is taken, and the rest is left exactly as it was.</b> The
    ///         scanner is over-inclusive and hands this method every <c>f(x)</c> in every C# file; a
    ///         rule that claimed any parenthesised tail would turn <c>Foo(bar)</c> into a utility named
    ///         <c>Foo</c> with a nonsense value, where today it is one unrecognised candidate and
    ///         costs nothing. <c>--</c> is what v4 requires and it is also what makes this safe.
    ///     </para>
    /// </remarks>
    static bool TryVariableShorthand(
        ReadOnlySpan<char> text,
        [NotNullWhen(true)] out string? variable,
        out ReadOnlySpan<char> before
    ) {
        variable = null;
        before = text;

        if (text.Length == 0 || text[^1] != ')') {
            return false;
        }

        var open = IndexAtTopLevel(text, '(');
        if (open < 0) {
            return false;
        }

        var inside = text[(open + 1)..^1];

        if (!inside.StartsWith("--", StringComparison.Ordinal) || !IsPropertyName(inside.ToString())) {
            return false;
        }

        var head = text[..open];

        if (head.Length > 0 && head[^1] == '-') {
            head = head[..^1];
        }

        // `(--brand)` with no utility in front of it names no property to set it on. That is an
        // arbitrary *property*'s job and it is written with brackets.
        if (head.IsEmpty) {
            return false;
        }

        variable = $"var({inside})";
        before = head;

        return true;
    }

    static bool TryOpacity(ReadOnlySpan<char> text, out float fraction) {
        fraction = 0f;

        if (text.IsEmpty) {
            return false;
        }

        // `/[0.35]` is the arbitrary form, and it is a fraction rather than a percentage.
        if (text[0] == '[' && text[^1] == ']') {
            return float.TryParse(
                text[1..^1],
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out fraction
            );
        }

        if (!int.TryParse(text, out var percent)) {
            return false;
        }

        fraction = percent / 100f;
        return true;
    }

    /// <summary>The first occurrence of a separator outside any bracket.</summary>
    /// <remarks>
    ///     The separator is tested <i>before</i> the bracket depth is updated, and it has to be:
    ///     searching for <c>[</c> itself is exactly what finding an arbitrary value means, and a
    ///     version that opened the bracket first could never find one. That was a bug — every
    ///     arbitrary value silently stopped being one.
    /// </remarks>
    static int IndexAtTopLevel(ReadOnlySpan<char> text, char separator) {
        var depth = 0;

        for (var i = 0; i < text.Length; i++) {
            var c = text[i];

            if (c == separator && depth == 0) {
                return i;
            }

            if (c is '[' or '(') {
                depth++;
            } else if (c is ']' or ')') {
                depth--;
            }
        }

        return -1;
    }

    static int LastIndexAtTopLevel(ReadOnlySpan<char> text, char separator) {
        var depth = 0;
        var found = -1;

        for (var i = 0; i < text.Length; i++) {
            var c = text[i];

            if (c == separator && depth == 0) {
                found = i;
            }

            if (c is '[' or '(') {
                depth++;
            } else if (c is ']' or ')') {
                depth--;
            }
        }

        return found;
    }
}
