// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Styling;

namespace Vixen.Ui;

/// <summary>The inherited text lengths, resolved once and inherited already absolute.</summary>
/// <param name="LineHeight">The line box's height in points, or the font's own when unset.</param>
/// <param name="LineHeightFactor">
///     The multiplier, when the declaration was a bare number — <c>null</c> when it was a length.
/// </param>
/// <param name="LetterSpacing">Extra space between characters, in points.</param>
/// <param name="WordSpacing">Extra space between words, in points.</param>
/// <param name="TextIndent">The first line's indent, in points.</param>
public readonly record struct ComputedText(
    float LineHeight,
    float? LineHeightFactor,
    float LetterSpacing,
    float WordSpacing,
    float TextIndent
) {
    /// <summary>What an element with no ancestor declaring any of them gets.</summary>
    /// <remarks>
    ///     A <c>LineHeight</c> of zero means "the font's own", which is what <c>TextRun</c> already
    ///     uses and what CSS calls <c>normal</c>. Zero rather than a sentinel because zero is not a
    ///     line height anybody can ask for and every consumer already has to handle "no font".
    /// </remarks>
    public static readonly ComputedText Initial = new(0f, null, 0f, 0f, 0f);
}

public sealed partial class UiDocument {
    /// <summary>
    ///     Resolves the four inherited text lengths against this element, given its parent's already
    ///     resolved ones.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The computed-value stage doc 14 recorded as owed, and it is the same correction
    ///         <c>font-size</c> got in 4d.</b> This cascade inherits <i>specified</i> values and CSS
    ///         inherits <i>computed</i> ones. For <c>line-height</c>, <c>letter-spacing</c>,
    ///         <c>word-spacing</c> and <c>text-indent</c>, an inherited <c>em</c> would therefore be
    ///         measured a second time against the <i>descendant's</i> font size — so
    ///         <c>letter-spacing: 0.1em</c> on a 16px panel gives a 32px heading inside it twice the
    ///         spacing it was meant to have, rather than the same spacing.
    ///     </para>
    ///     <para>
    ///         Unlike <c>font-size</c> the error does not compound, because none of the four feeds
    ///         back into the unit it is written in — which is why this was bounded and survived.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A bare-number <c>line-height</c> is the exception and must <i>not</i> be computed
    ///         before it is inherited.</b> CSS is explicit about this and it is not an oversight in the
    ///         specification: <c>line-height: 1.5</c> means "one and a half times whatever size the
    ///         text is", so it inherits as the <i>number</i> and is resolved per element. Computing it
    ///         to pixels on the ancestor and inheriting that gives every descendant the ancestor's
    ///         leading — a 32px heading inside a 16px panel would get 24px lines, which is less than
    ///         its own text is tall. So the factor is carried alongside the resolved length and wins
    ///         wherever it is present.
    ///     </para>
    /// </remarks>
    ComputedText ResolveText(ComputedStyle style, in ComputedText parent, float fontSize) {
        var context = Viewport.WithFontSize(fontSize);

        // Declared here wins; otherwise the parent's already-computed value carries down unchanged.
        // ⚠ The parent's, not the parent's *declaration* — that is the whole of the fix.
        var factor = parent.LineHeightFactor;
        var height = parent.LineHeight;

        if (Builder.TryLineHeight(style, context, out var declaredFactor, out var declaredHeight)) {
            factor = declaredFactor;
            height = declaredHeight;
        }

        return new ComputedText(
            factor is { } multiplier ? multiplier * fontSize : height,
            factor,
            Builder.TryTextLength(style, Builder.LetterSpacingId, context, out var letter) ? letter : parent.LetterSpacing,
            Builder.TryTextLength(style, Builder.WordSpacingId, context, out var word) ? word : parent.WordSpacing,
            Builder.TryTextLength(style, Builder.TextIndentId, context, out var indent) ? indent : parent.TextIndent
        );
    }
}
