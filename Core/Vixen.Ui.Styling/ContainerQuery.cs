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
/// <remarks>
///     ⚠ <b>The two font sizes are part of the box, and so part of the key its scope is interned
///     under.</b> A container query's <c>em</c> is the container's own computed font, so a container
///     whose font changes at an unchanged size answers <c>(min-width: 30em)</c> differently. Carrying
///     the font here makes that a different box, which is a different scope, which is the invalidation
///     a resize already gets. Keeping the fonts beside the box would have needed an edge of its own.
/// </remarks>
public readonly record struct ContainerBox(float Width, float Height, ContainerKind Kind) {
    /// <summary>The container's computed font size, in pixels: what <c>em</c> measures in its queries.</summary>
    /// <remarks>Sixteen, CSS's <c>medium</c>, when a box is built without saying.</remarks>
    public float FontSize { get; init; } = 16f;

    /// <summary>The root font size, in pixels: what <c>rem</c> measures in its queries.</summary>
    /// <remarks>
    ///     The document's <c>RootFontSize</c>, the one every other <c>rem</c> in the document is
    ///     measured against, so a query and a declaration written in the same unit agree. Sixteen when
    ///     a box is built without saying.
    /// </remarks>
    public float RootFontSize { get; init; } = 16f;
}

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
///         ⚠ <b>An axis the container does not constrain makes the container ineligible, and the query
///         asks the next one up (#1429).</b> Asking <c>(min-height: 200px)</c> of an <c>inline-size</c>
///         container is asking about a number the containment did not make well-defined — the box's
///         height is still its content's. CSS Containment 3 § 5.1 does not answer that <c>false</c>:
///         the container a query asks is the nearest ancestor that is a valid query container for
///         <i>every</i> feature in it, so the <c>inline-size</c> box is skipped and a <c>size</c>
///         container above it answers. <see cref="Requires" /> is what the walk in
///         <see cref="ContainerConditions" /> reads to skip it. This evaluator still answers
///         <c>false</c> for a box that cannot answer rather than reading the height anyway, but the
///         walk never hands it one; <c>false</c> is only right when no eligible container exists at all.
///     </para>
/// </remarks>
public static class ContainerQuery {
    /// <summary>The least containment a box needs to be asked every feature in a condition.</summary>
    /// <param name="condition">The text between the container's name and the block.</param>
    /// <returns>
    ///     <see cref="ContainerKind.Size" /> when any feature reads the block axis — <c>height</c>,
    ///     <c>block-size</c>, and <c>aspect-ratio</c> and <c>orientation</c>, which read both — and
    ///     otherwise <see cref="ContainerKind.InlineSize" />, which every size query container satisfies.
    /// </returns>
    /// <remarks>
    ///     ⚠ <b>Never <see cref="ContainerKind.Normal" />, not even for an empty condition.</b>
    ///     <c>@container card { … }</c> asks only that a query container of that name exists, and a
    ///     <c>normal</c> box is not a size query container. A term that cannot be read counts as
    ///     inline: the loader refuses such a prelude before it becomes a group, so no walk asks it.
    /// </remarks>
    internal static ContainerKind Requires(string? condition) =>
        TryWalk(condition, default, out _, out var requires, out _) ? requires : ContainerKind.InlineSize;

    /// <summary>Evaluates a container condition against a box.</summary>
    /// <param name="condition">The text between the container's name and the block.</param>
    /// <param name="box">The container's measured box.</param>
    /// <param name="matches">Whether the condition holds.</param>
    /// <param name="reason">Why it could not be read, when it could not.</param>
    /// <returns>Whether it could be read at all.</returns>
    /// <remarks>
    ///     <para>
    ///         Readability is a property of the <i>text</i> and never of the box, which is what lets the
    ///         loader decide it once — the same split <see cref="MediaQuery.TryEvaluate" /> makes, and
    ///         for the same reason: a refusal produced per container per frame arrives in a list
    ///         nothing drains.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The grammar is CSS Containment 3's, less the parenthesised group (#273):</b>
    ///         <c>not (f)</c>, or features joined by <c>and</c>, or features joined by <c>or</c>.
    ///         Only <c>and</c> used to be read — split on the literal <c>" and "</c> — so an
    ///         <c>or</c> left one term that was not a feature and every <c>or</c> query was refused.
    ///         Mixing <c>and</c> with <c>or</c> needs parentheses in CSS, because neither binds tighter,
    ///         and a parenthesised group is refused rather than read one way.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A feature the box cannot answer makes the whole condition false, under <c>not</c>
    ///         and <c>or</c> too.</b> The query is <i>unknown</i> there, and unknown does not apply.
    ///         Negating the <c>false</c> a single feature answers would match every <c>inline-size</c>
    ///         box with <c>not (min-height: …)</c>. No walk reaches that branch, since
    ///         <see cref="Requires" /> skips such a box (#1429); a direct caller can.
    ///     </para>
    /// </remarks>
    public static bool TryEvaluate(string? condition, ContainerBox box, out bool matches, out string? reason) =>
        TryWalk(condition, box, out matches, out _, out reason);

    /// <summary>Reads a condition's shape and asks each feature of a box, in one pass.</summary>
    /// <param name="condition">The condition text.</param>
    /// <param name="box">The box to ask, <c>default</c> when only readability and axes are wanted.</param>
    /// <param name="matches">Whether it holds of the box.</param>
    /// <param name="requires">The least containment that can answer every feature.</param>
    /// <param name="reason">Why it could not be read, when it could not.</param>
    /// <returns>Whether it could be read.</returns>
    /// <remarks>
    ///     One walk for both questions, so that what <see cref="Requires" /> says a query reads and
    ///     what <see cref="TryEvaluate" /> asks cannot drift apart: a second parser that saw fewer
    ///     features would pick a container the evaluator then found unable to answer.
    /// </remarks>
    static bool TryWalk(
        string? condition,
        ContainerBox box,
        out bool matches,
        out ContainerKind requires,
        out string? reason
    ) {
        matches = true;
        requires = ContainerKind.InlineSize;
        reason = null;

        var text = condition.AsSpan().Trim();

        if (text.IsEmpty) {
            // `@container name { … }` — a named container with no condition, which is legal and asks
            // only that such a container exists.
            return true;
        }

        var negated = StartsWithWord(text, "not");

        if (negated) {
            text = text["not".Length..].TrimStart();
        }

        bool? any = null;
        var count = 0;
        var result = false;
        var unknown = false;

        while (true) {
            if (text.IsEmpty || text[0] != '(') {
                reason = $"'{text.ToString()}' is not a container feature Vixen understands";
                return false;
            }

            var close = StyleQuery.Closing(text, 0);

            if (close < 0) {
                reason = $"'{text.ToString()}' has no closing parenthesis";
                return false;
            }

            var feature = text[1..close].Trim();

            if (feature.StartsWith('(') || StartsWithWord(feature, "not")) {
                reason = "a parenthesised group of container features is not supported";
                return false;
            }

            if (!TryFeature(feature, box, out var held, out var answerable, out var block, out reason)) {
                return false;
            }

            if (block) {
                requires = ContainerKind.Size;
            }

            unknown |= !answerable;
            result = count++ == 0 ? held : any == true ? result || held : result && held;
            text = text[(close + 1)..].TrimStart();

            if (text.IsEmpty) {
                break;
            }

            var joinedByOr = StartsWithWord(text, "or");

            if (!joinedByOr && !StartsWithWord(text, "and")) {
                reason = $"'{text.ToString()}' does not join two features with 'and' or 'or'";
                return false;
            }

            if (any is { } previous && previous != joinedByOr) {
                reason = "'and' and 'or' cannot be mixed without parentheses";
                return false;
            }

            any = joinedByOr;
            text = text[(joinedByOr ? "or".Length : "and".Length)..].TrimStart();
        }

        // `not` takes one query in parentheses and not a list, so `not (a) and (b)` is invalid CSS
        // rather than a negation of either reading.
        if (negated && count > 1) {
            reason = "'not' applies to one feature; a negated group needs parentheses, which are not supported";
            return false;
        }

        matches = !unknown && (negated ? !result : result);
        return true;
    }

    /// <summary>Whether a text opens with a keyword followed by whitespace, which is how CSS separates one.</summary>
    static bool StartsWithWord(ReadOnlySpan<char> text, string word) =>
        text.Length > word.Length
        && text.StartsWith(word, StringComparison.OrdinalIgnoreCase)
        && char.IsWhiteSpace(text[word.Length]);

    static bool TryFeature(
        ReadOnlySpan<char> feature,
        ContainerBox box,
        out bool matches,
        out bool answerable,
        out bool block,
        out string? reason
    ) {
        matches = false;
        answerable = false;
        block = false;
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

        block = name.Equals("height", StringComparison.OrdinalIgnoreCase)
            || name.Equals("block-size", StringComparison.OrdinalIgnoreCase);

        if (name.Equals("orientation", StringComparison.OrdinalIgnoreCase)
            || name.Equals("aspect-ratio", StringComparison.OrdinalIgnoreCase)) {
            // Both axes, so the block axis among them.
            block = true;
            bool readable;

            if (name.Equals("orientation", StringComparison.OrdinalIgnoreCase)) {
                if (terms.IsRanged) {
                    reason = "'orientation' is discrete, so it has no range or min-/max- form";
                    return false;
                }

                readable = TryOrientation(value, box, out matches, out reason);
            } else {
                readable = TryRatio(terms, box, out matches, out reason);
            }

            // Both axes, so both have to be contained. An `inline-size` container has a height that
            // is still its content's, and a ratio computed from it would move as the content moved.
            // ⚠ Asked after the text is read rather than before, for the reason the lengths below
            // give: the loader's `default` box is not a `size` container either.
            answerable = box.Kind == ContainerKind.Size;

            if (!answerable) {
                matches = false;
            }

            return readable;
        }

        if (!inline && !block) {
            reason = $"'{name}' is not a container feature Vixen supports";
            return false;
        }

        // ⚠ <b>The lengths are read before the containment test, and that order is what makes the
        // loader's check mean anything.</b> The loader asks once, against `default` — a box whose
        // `Kind` is `Normal`, which answers nothing — and treats "readable" as a fact about the text.
        // With the containment test first, that box returned before any length was parsed, so
        // `@container (min-width: 30furlongs)` loaded with no diagnostic and then failed per element
        // per frame into `ContainerConditions`' `&& matches`, which reads a refusal as "no". Every
        // unreadable length in a `@container` prelude was a silent never-match, not the load
        // diagnostic the loader's own remark promised. `em` and `rem` resolve here too, against the
        // container's own font and the root's (#1373).
        var wanted = 0f;
        var second = 0f;

        if (!terms.IsBoolean && !MediaQuery.TryLength(value, box.FontSize, box.RootFontSize, out wanted)) {
            reason = $"'{value}' is not a length Vixen can compare";
            return false;
        }

        if (terms.HasSecond && !MediaQuery.TryLength(terms.SecondValue, box.FontSize, box.RootFontSize, out second)) {
            reason = $"'{terms.SecondValue}' is not a length Vixen can compare";
            return false;
        }

        // ⚠ The containment test, and it comes before the comparison rather than after it. An
        // `inline-size` container's height is not a fact this query may read at all, so there is no
        // number to compare. `ContainerConditions` never hands this a box `Requires` rules out — it
        // walks past it to one that can answer (#1429) — so this is a guard for a direct caller, and
        // `false` is what a query resolves to only when no eligible container exists.
        answerable = inline
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

        matches = FeatureRange.Holds(actual, terms.Comparison, wanted);

        if (terms.HasSecond) {
            matches &= FeatureRange.Holds(actual, terms.SecondComparison, second);
        }

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
