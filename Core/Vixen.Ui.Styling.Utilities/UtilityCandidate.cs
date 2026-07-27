// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;

namespace Vixen.Ui.Styling.Utilities;

/// <summary>One class name, taken apart.</summary>
/// <param name="Original">The class name as written, which is also the selector it emits.</param>
/// <param name="Variants">The <c>hover:</c>, <c>md:</c>, <c>[&amp;&gt;*]:</c> prefixes, in order.</param>
/// <param name="Name">The utility name — <c>p</c>, <c>bg</c>, <c>flex</c>.</param>
/// <param name="Value">Its value — <c>4</c>, <c>accent</c>, <c>[37px]</c> — or empty.</param>
/// <param name="Arbitrary">The contents of <c>[…]</c>, when the value is one.</param>
/// <param name="Opacity">The <c>/50</c> suffix read as an opacity, or null.</param>
/// <param name="SlashSuffix">
///     The <c>/50</c> suffix as written. Kept as well as <see cref="Opacity" /> because the two
///     readings of a slash are genuinely different: <c>bg-accent/50</c> is half-transparent, and
///     <c>w-2/3</c> is two thirds wide, which as an opacity would be three percent. Which one a
///     slash means is the utility's to decide, not the parser's.
/// </param>
/// <param name="Important">Whether it ended in <c>!</c>.</param>
public readonly record struct UtilityCandidate(
    string Original,
    IReadOnlyList<string> Variants,
    string Name,
    string Value,
    string? Arbitrary,
    float? Opacity,
    string? SlashSuffix,
    bool Important
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
        var open = IndexAtTopLevel(text, '[');
        if (open >= 0) {
            if (text[^1] != ']') {
                return false;
            }

            arbitrary = text[(open + 1)..^1].ToString();

            // `_` stands for a space, because a class attribute cannot contain one. Tailwind's
            // convention, and the only workable one: `grid-cols-[1fr_auto]` has to say two things.
            arbitrary = arbitrary.Replace('_', ' ');
            text = text[..open];

            if (text.Length > 0 && text[^1] == '-') {
                text = text[..^1];
            }
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

        if (name.Length == 0) {
            return false;
        }

        parsed = new UtilityCandidate(
            candidate,
            variants,
            name,
            value,
            arbitrary,
            opacity,
            slashSuffix,
            important
        );

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
