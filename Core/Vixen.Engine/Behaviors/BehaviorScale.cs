// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Engine.Behaviors;

/// <summary>The figure doc 04's authoring rule is written about, in one place.</summary>
/// <remarks>
///     <para>
///         <b>Both readers of <see cref="BehaviorStore.Population" /> used to carry their own copy</b>
///         — <c>vixen doctor behaviors</c> and the editor's statistics panel — and each explained, in
///         its own remarks, that the number was the document's opinion rather than one the tool had
///         invented. Both remarks were right and the arrangement was still wrong
///         (<a href="https://github.com/Rikarin/Vixen/issues/1230">#1230</a>): the argument for
///         reading the document rather than inventing a number is an argument against holding two
///         copies of it, because the day the document moves the figure the command and the panel
///         disagree and nothing fails.
///     </para>
///     <para>
///         ⚠ <b><c>internal</c>, not public, and that is the whole reason this is a type of its own
///         rather than a member of <see cref="BehaviorStore" />.</b> A threshold is not part of what
///         a store <em>is</em>, and a public constant would be a <c>PublicAPI</c> addition promising
///         a figure the document is free to change. The two readers see it through
///         <c>InternalsVisibleTo</c>, which is what makes them the two readers rather than two
///         authors.
///     </para>
///     <para>
///         ⚠ <b>The seam that is left is the one to the document, and it is gated.</b> The number
///         still lives in prose as well as here, because the opinion is the document's;
///         <c>BehaviorScaleTests</c> reads doc 04 § <i>When to write one</i> and fails when the two
///         stop agreeing, which is the half a constant cannot do for itself.
///     </para>
/// </remarks>
static class BehaviorScale {
    /// <summary>
    ///     How many instances of one behaviour type is enough to be worth a second look, per doc 04 §
    ///     <i>When to write one</i> — two hundred is where re-authoring as a component and a system is
    ///     still cheap and ten thousand is where that section says it is not.
    /// </summary>
    internal const int Many = 200;
}
