// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Ui.Styling;

/// <summary>What a <c>container-type</c> makes an element answerable about.</summary>
/// <remarks>
///     ⚠ <b>The axes are not a convenience, they are the containment.</b> CSS Containment 3 § 3 lets a
///     box be queried on an axis only if its size on that axis is independent of its contents, and the
///     two useful answers differ in how much that costs. <see cref="InlineSize" /> constrains one axis
///     and is what a panel wants; <see cref="Size" /> constrains both and means the box can no longer
///     grow to fit its content at all, which for a stack of text is almost never what was meant.
/// </remarks>
public enum ContainerKind : byte {
    /// <summary>Not a query container. Queries pass through it to whatever is above.</summary>
    Normal,

    /// <summary>Answerable on the inline axis only — <c>container-type: inline-size</c>.</summary>
    InlineSize,

    /// <summary>Answerable on both axes — <c>container-type: size</c>.</summary>
    Size
}

/// <summary>A query container's box, as the last layout pass measured it.</summary>
/// <param name="Width">Its content-box inline size.</param>
/// <param name="Height">Its content-box block size.</param>
/// <param name="Kind">Which axes it may be asked about.</param>
public readonly record struct ContainerBox(float Width, float Height, ContainerKind Kind);

/// <summary>Evaluates the size conditions in a <c>@container</c> prelude.</summary>
/// <remarks>
///     <para>
///         <b>The same grammar as <see cref="MediaQuery" /> over a different subject, and a
///         deliberately smaller feature set.</b> A container query asks about a <i>box</i>, so the
///         features it may use are the ones a box has: CSS Containment 3 § 5.2 lists <c>width</c>,
///         <c>height</c>, <c>inline-size</c>, <c>block-size</c>, <c>aspect-ratio</c> and
///         <c>orientation</c>, and nothing else.
///     </para>
///     <para>
///         ⚠ <b>Refusing the media-only features is the point rather than an omission.</b>
///         <c>@container (prefers-color-scheme: dark)</c> is not a container query, and the
///         alternative to refusing it is answering it from whatever surface the element happens to be
///         on — a rule that reads as though it were scoped to the panel and is not. Every refusal here
///         names the feature, and the loader turns it into the same diagnostic an unreadable
///         <c>@media</c> gets.
///     </para>
///     <para>
///         ⚠ <b>An axis the container does not constrain is a refusal to match, not a false.</b>
///         Asking <c>(min-height: 200px)</c> of an <c>inline-size</c> container is asking about a
///         number the containment did not make well-defined — the box's height is still its content's
///         — so the query cannot be answered here. It answers <c>false</c> rather than reading the
///         height anyway, which is what a browser does when no eligible container is found.
///     </para>
/// </remarks>
public static class ContainerQuery {
    /// <summary>Evaluates a container condition against a box.</summary>
    /// <param name="condition">The text between the container's name and the block.</param>
    /// <param name="box">The container's measured box.</param>
    /// <param name="matches">Whether the condition holds.</param>
    /// <param name="reason">Why it could not be read, when it could not.</param>
    /// <returns>Whether it could be read at all.</returns>
    /// <remarks>
    ///     Readability is a property of the <i>text</i> and never of the box, which is what lets the
    ///     loader decide it once — the same split <see cref="MediaQuery.TryEvaluate" /> makes, and for
    ///     the same reason: a refusal produced per container per frame arrives in a list nothing
    ///     drains.
    /// </remarks>
    public static bool TryEvaluate(string? condition, ContainerBox box, out bool matches, out string? reason) {
        matches = true;
        reason = null;

        if (string.IsNullOrWhiteSpace(condition)) {
            // `@container name { … }` — a named container with no condition, which is legal and asks
            // only that such a container exists.
            return true;
        }

        foreach (var range in condition.AsSpan().Split(" and ")) {
            var term = condition.AsSpan()[range].Trim();

            if (term.IsEmpty) {
                continue;
            }

            if (term[0] != '(' || term[^1] != ')') {
                reason = $"'{term}' is not a container feature Vixen understands";
                return false;
            }

            if (!TryFeature(term[1..^1].Trim(), box, out var held, out reason)) {
                return false;
            }

            matches &= held;
        }

        return true;
    }

    static bool TryFeature(ReadOnlySpan<char> feature, ContainerBox box, out bool matches, out string? reason) {
        matches = false;
        reason = null;

        if (!FeatureRange.TryRead(feature, out var terms, out reason)) {
            return false;
        }

        var name = terms.Name;
        var value = terms.Value;

        // Vixen has no vertical writing mode, so the logical names are the physical ones. Kept as
        // separate spellings rather than rewritten at load because that is what the author wrote, and
        // because the day a writing mode arrives this is the one place that has to change.
        var inline = name.Equals("width", StringComparison.OrdinalIgnoreCase)
            || name.Equals("inline-size", StringComparison.OrdinalIgnoreCase);

        var block = name.Equals("height", StringComparison.OrdinalIgnoreCase)
            || name.Equals("block-size", StringComparison.OrdinalIgnoreCase);

        if (name.Equals("orientation", StringComparison.OrdinalIgnoreCase)
            || name.Equals("aspect-ratio", StringComparison.OrdinalIgnoreCase)) {
            // Both axes, so both have to be contained. An `inline-size` container has a height that
            // is still its content's, and a ratio computed from it would move as the content moved.
            if (box.Kind != ContainerKind.Size) {
                return true;
            }

            if (name.Equals("orientation", StringComparison.OrdinalIgnoreCase)) {
                if (terms.IsRanged) {
                    reason = "'orientation' is discrete, so it has no range or min-/max- form";
                    return false;
                }

                return TryOrientation(value, box, out matches, out reason);
            }

            return TryRatio(terms, box, out matches, out reason);
        }

        if (!inline && !block) {
            reason = $"'{name}' is not a container feature Vixen supports";
            return false;
        }

        // ⚠ The containment test, and it comes before the number rather than after it. An
        // `inline-size` container's height is not a fact this query may read at all, so there is no
        // number to compare — `false` here is "no eligible container", which is what the specification
        // says an unanswerable query resolves to.
        var answerable = inline
            ? box.Kind is ContainerKind.InlineSize or ContainerKind.Size
            : box.Kind == ContainerKind.Size;

        if (!answerable) {
            return true;
        }

        var actual = inline ? box.Width : box.Height;

        if (terms.IsBoolean) {
            // The boolean form, which CSS defines as "the feature is non-zero".
            matches = actual != 0f;
            return true;
        }

        if (!MediaQuery.TryLength(value, out var wanted)) {
            reason = $"'{value}' is not a length Vixen can compare";
            return false;
        }

        matches = FeatureRange.Holds(actual, terms.Comparison, wanted);

        if (!terms.HasSecond) {
            return true;
        }

        if (!MediaQuery.TryLength(terms.SecondValue, out var second)) {
            reason = $"'{terms.SecondValue}' is not a length Vixen can compare";
            return false;
        }

        matches &= FeatureRange.Holds(actual, terms.SecondComparison, second);

        return true;
    }

    static bool TryOrientation(ReadOnlySpan<char> value, ContainerBox box, out bool matches, out string? reason) {
        matches = false;
        reason = null;

        var landscape = box.Width >= box.Height;

        if (value.Equals("landscape", StringComparison.OrdinalIgnoreCase)) {
            matches = landscape;
            return true;
        }

        if (value.Equals("portrait", StringComparison.OrdinalIgnoreCase)) {
            matches = !landscape;
            return true;
        }

        reason = $"'{value}' is not an orientation";
        return false;
    }

    static bool TryRatio(FeatureTerms terms, ContainerBox box, out bool matches, out string? reason) {
        matches = false;
        reason = null;

        if (!TryRatioValue(terms.Value, out var wanted)) {
            reason = $"'{terms.Value}' is not a ratio";
            return false;
        }

        var second = 0f;

        if (terms.HasSecond && !TryRatioValue(terms.SecondValue, out second)) {
            reason = $"'{terms.SecondValue}' is not a ratio";
            return false;
        }

        if (box.Height == 0f) {
            // No ratio exists, so nothing about it is true. Not a refusal: the text was readable and
            // the box simply has no answer this frame.
            return true;
        }

        var actual = box.Width / box.Height;

        // ⚠ Equality on a ratio is the one comparison that cannot be exact — `16/9` is not a float —
        // so it keeps its epsilon while every ordering is the plain one.
        matches = terms.Comparison == FeatureComparison.Exact
            ? Math.Abs(actual - wanted) < 1e-4f
            : FeatureRange.Holds(actual, terms.Comparison, wanted);

        if (terms.HasSecond) {
            matches &= FeatureRange.Holds(actual, terms.SecondComparison, second);
        }

        return true;
    }

    static bool TryRatioValue(ReadOnlySpan<char> text, out float ratio) {
        ratio = 0f;

        var slash = text.IndexOf('/');

        if (slash < 0) {
            return float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out ratio)
                && ratio > 0f;
        }

        if (!float.TryParse(text[..slash].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            || !float.TryParse(
                text[(slash + 1)..].Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var denominator
            )
            || denominator == 0f) {
            return false;
        }

        ratio = numerator / denominator;
        return ratio > 0f;
    }
}
