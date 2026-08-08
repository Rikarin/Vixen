// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Layout;

/// <summary>
///     A set of adjoining vertical margins that have collapsed into one, per CSS 2.1 §8.3.1.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Two numbers rather than one, because collapsing is not <c>max</c>.</b> §8.3.1 says the
///         collapsed margin is "the largest of the adjoining margins" when they are all positive, the
///         most negative when they are all negative, and — the case a single running maximum cannot
///         express — <i>the sum of the largest positive and the most negative</i> when both signs are
///         present. So the running state has to keep both extremes and add them only at the end.
///         Collapsing 10 and −8 and 4 is 10 + (−8) = 2, not 10 and not 4.
///     </para>
///     <para>
///         Order does not matter and no margin is ever removed, which is what makes this a monoid over
///         <see cref="With(CollapsibleMargin)" /> with <see cref="Zero" /> as its identity — the reason
///         a block container can fold its children's margin sets together as it walks them without
///         caring how deep any one of them came from.
///     </para>
/// </remarks>
/// <param name="Positive">The largest non-negative margin in the set, or zero.</param>
/// <param name="Negative">The most negative margin in the set, or zero.</param>
readonly record struct CollapsibleMargin(float Positive, float Negative) {
    /// <summary>The empty set, which resolves to zero.</summary>
    public static readonly CollapsibleMargin Zero = new(0f, 0f);

    /// <summary>A set holding one margin.</summary>
    public static CollapsibleMargin From(float margin) =>
        margin >= 0f ? new CollapsibleMargin(margin, 0f) : new CollapsibleMargin(0f, margin);

    /// <summary>This set with one more margin adjoining it.</summary>
    public CollapsibleMargin With(float margin) =>
        margin >= 0f
            ? new CollapsibleMargin(MathF.Max(Positive, margin), Negative)
            : new CollapsibleMargin(Positive, MathF.Min(Negative, margin));

    /// <summary>This set with another whole set adjoining it.</summary>
    public CollapsibleMargin With(CollapsibleMargin other) =>
        new(MathF.Max(Positive, other.Positive), MathF.Min(Negative, other.Negative));

    /// <summary>The single margin the set collapses to.</summary>
    public float Resolve() => Positive + Negative;
}
