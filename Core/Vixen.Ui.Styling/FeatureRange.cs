// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Styling;

/// <summary>How a feature's measured value is compared with the one the author wrote.</summary>
/// <remarks>
///     ⚠ <b><see cref="Below" /> and <see cref="Above" /> are not decoration on
///     <see cref="AtMost" />/<see cref="AtLeast" />.</b> They differ on exactly one value — the
///     threshold — and that is the only width at which a <c>max-width: 448px</c> and a
///     <c>width &lt; 448px</c> disagree at all. Everything else about the two spellings is identical,
///     which is why the gap was invisible until <c>@max-*</c> gave the exclusive form a name.
/// </remarks>
enum FeatureComparison : byte {
    /// <summary><c>(width: 400px)</c> and <c>(width = 400px)</c>.</summary>
    Exact,

    /// <summary><c>(min-width: 400px)</c> and <c>(width &gt;= 400px)</c>.</summary>
    AtLeast,

    /// <summary><c>(max-width: 400px)</c> and <c>(width &lt;= 400px)</c>.</summary>
    AtMost,

    /// <summary><c>(width &gt; 400px)</c>. No prefix spells this.</summary>
    Above,

    /// <summary><c>(width &lt; 400px)</c>. No prefix spells this, which is the whole of the gap.</summary>
    Below
}

/// <summary>One feature term, read from either the prefix form or Media Queries 4's range syntax.</summary>
/// <remarks>
///     A term is always normalised to bounds <i>on the name</i>, so
///     <c>(400px &lt;= width &lt; 600px)</c> arrives as <c>width &gt;= 400px</c> and
///     <c>width &lt; 600px</c> rather than as the order the author typed. The evaluators then never
///     learn that a bound may have been written on the left.
/// </remarks>
readonly ref struct FeatureTerms {
    /// <summary>The feature, with any <c>min-</c>/<c>max-</c> prefix already taken off.</summary>
    public ReadOnlySpan<char> Name { get; init; }

    /// <summary>How the first bound compares.</summary>
    public FeatureComparison Comparison { get; init; }

    /// <summary>The first bound's value, empty for the boolean form <c>(width)</c>.</summary>
    public ReadOnlySpan<char> Value { get; init; }

    /// <summary>Whether a second bound was written, which only the two-sided range form has.</summary>
    public bool HasSecond { get; init; }

    /// <summary>How the second bound compares.</summary>
    public FeatureComparison SecondComparison { get; init; }

    /// <summary>The second bound's value.</summary>
    public ReadOnlySpan<char> SecondValue { get; init; }

    /// <summary>Whether the author wrote a bare feature name, which CSS reads as "non-zero".</summary>
    public bool IsBoolean => Value.IsEmpty && !HasSecond;

    /// <summary>Whether either bound is anything other than plain equality.</summary>
    /// <remarks>
    ///     What a <i>discrete</i> feature asks, because Media Queries 5 § 2.4.2 gives it no range
    ///     type at all: <c>(min-color-gamut: p3)</c> and <c>(color-gamut &gt; srgb)</c> are both
    ///     nonsense, and answering them as equality is a query that quietly means something the
    ///     author did not write.
    /// </remarks>
    public bool IsRanged => Comparison != FeatureComparison.Exact || HasSecond;
}

/// <summary>Reads a media or container feature, in either spelling, and compares one.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Shared rather than copied, because the two spellings have to agree in both
///         evaluators or a stylesheet means one thing under <c>@media</c> and another under
///         <c>@container</c>.</b> The prefix forms were duplicated once already and the duplicate is
///         what let <c>max-</c> stay inclusive in both places without anybody having to decide it
///         twice.
///     </para>
///     <para>
///         The subset is Media Queries 4 § 2.4's range syntax minus <c>or</c> and <c>not</c>, which
///         <see cref="MediaQuery" /> does not have either: <c>(width &lt; 24rem)</c>,
///         <c>(width &gt;= 600px)</c>, <c>(400px &lt;= width &lt; 600px)</c> and the reversed
///         one-sided form <c>(600px &gt; width)</c>.
///     </para>
/// </remarks>
static class FeatureRange {
    /// <summary>Reads a feature's name and bounds.</summary>
    /// <param name="feature">The text inside the parentheses, already trimmed.</param>
    /// <param name="terms">Receives the name and up to two bounds on it.</param>
    /// <param name="reason">Why it could not be read, when it could not.</param>
    /// <returns>Whether it could be read at all.</returns>
    public static bool TryRead(ReadOnlySpan<char> feature, out FeatureTerms terms, out string? reason) {
        terms = default;
        reason = null;

        if (!TryOperator(feature, 0, out var first, out var at, out var length)) {
            return TryPrefixed(feature, out terms, out reason);
        }

        var left = feature[..at].Trim();
        var after = feature[(at + length)..];

        if (feature.IndexOf(':') >= 0) {
            reason = $"'{feature.ToString()}' mixes the colon and the comparison forms";
            return false;
        }

        if (!TryOperator(after, 0, out var second, out var secondAt, out var secondLength)) {
            // One-sided, and either order: `width < 24rem` or `24rem > width`. The name is the side
            // that reads as one, which a length never does — it starts with a digit or a sign.
            var right = after.Trim();

            if (left.IsEmpty || right.IsEmpty) {
                reason = $"'{feature.ToString()}' is missing a side of its comparison";
                return false;
            }

            var reversed = !IsName(left) && IsName(right);
            var name = reversed ? right : left;

            if (!IsName(name)) {
                reason = $"'{feature.ToString()}' compares two values rather than a feature";
                return false;
            }

            terms = new FeatureTerms {
                Name = name,
                Comparison = reversed ? Flip(first) : first,
                Value = reversed ? left : right
            };

            return true;
        }

        var middle = after[..secondAt].Trim();
        var end = after[(secondAt + secondLength)..].Trim();

        if (left.IsEmpty || middle.IsEmpty || end.IsEmpty || !IsName(middle)) {
            reason = $"'{feature.ToString()}' is not a range Vixen can read";
            return false;
        }

        // ⚠ Both operators must point the same way, and neither may be `=`. `(400px < width > 600px)`
        // is not a range CSS defines and is not one a reader can guess at either — Media Queries 4
        // § 2.4.2 requires the pair to match — so it is refused rather than read as a conjunction.
        if (first == FeatureComparison.Exact
            || second == FeatureComparison.Exact
            || PointsUp(first) != PointsUp(second)) {
            reason = $"'{feature.ToString()}' compares in two directions at once";
            return false;
        }

        terms = new FeatureTerms {
            Name = middle,
            Comparison = Flip(first),
            Value = left,
            HasSecond = true,
            SecondComparison = second,
            SecondValue = end
        };

        return true;
    }

    /// <summary>Whether a measured value satisfies a bound.</summary>
    /// <param name="actual">What the surface or box measures.</param>
    /// <param name="comparison">How to compare.</param>
    /// <param name="wanted">What the author wrote.</param>
    /// <returns>Whether the bound holds.</returns>
    public static bool Holds(float actual, FeatureComparison comparison, float wanted) =>
        comparison switch {
            FeatureComparison.AtLeast => actual >= wanted,
            FeatureComparison.AtMost => actual <= wanted,
            FeatureComparison.Above => actual > wanted,
            FeatureComparison.Below => actual < wanted,
            _ => actual == wanted
        };

    static bool TryPrefixed(ReadOnlySpan<char> feature, out FeatureTerms terms, out string? reason) {
        reason = null;

        var colon = feature.IndexOf(':');
        var name = (colon < 0 ? feature : feature[..colon]).Trim();
        var value = colon < 0 ? [] : feature[(colon + 1)..].Trim();

        var comparison = FeatureComparison.Exact;

        if (name.StartsWith("min-", StringComparison.OrdinalIgnoreCase)) {
            comparison = FeatureComparison.AtLeast;
            name = name[4..];
        } else if (name.StartsWith("max-", StringComparison.OrdinalIgnoreCase)) {
            comparison = FeatureComparison.AtMost;
            name = name[4..];
        }

        terms = new FeatureTerms { Name = name, Comparison = comparison, Value = value };
        return true;
    }

    static bool TryOperator(
        ReadOnlySpan<char> text,
        int from,
        out FeatureComparison comparison,
        out int at,
        out int length
    ) {
        comparison = FeatureComparison.Exact;
        at = -1;
        length = 0;

        for (var i = from; i < text.Length; i++) {
            switch (text[i]) {
                case '<':
                    at = i;
                    var below = i + 1 < text.Length && text[i + 1] == '=';
                    comparison = below ? FeatureComparison.AtMost : FeatureComparison.Below;
                    length = below ? 2 : 1;

                    return true;

                case '>':
                    at = i;
                    var above = i + 1 < text.Length && text[i + 1] == '=';
                    comparison = above ? FeatureComparison.AtLeast : FeatureComparison.Above;
                    length = above ? 2 : 1;

                    return true;

                case '=':
                    at = i;
                    comparison = FeatureComparison.Exact;
                    length = 1;

                    return true;

                default:
                    continue;
            }
        }

        return false;
    }

    // ⚠ A feature name, never a value. `width` is one and `24rem` is not, which is what tells
    // `24rem > width` from `width > 24rem` without a table of every feature both evaluators know.
    static bool IsName(ReadOnlySpan<char> text) {
        if (text.IsEmpty) {
            return false;
        }

        foreach (var c in text) {
            if (!char.IsAsciiLetter(c) && c != '-') {
                return false;
            }
        }

        return true;
    }

    static bool PointsUp(FeatureComparison comparison) =>
        comparison is FeatureComparison.AtLeast or FeatureComparison.Above;

    static FeatureComparison Flip(FeatureComparison comparison) =>
        comparison switch {
            FeatureComparison.AtLeast => FeatureComparison.AtMost,
            FeatureComparison.AtMost => FeatureComparison.AtLeast,
            FeatureComparison.Above => FeatureComparison.Below,
            FeatureComparison.Below => FeatureComparison.Above,
            _ => FeatureComparison.Exact
        };
}
