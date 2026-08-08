// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Runtime.InteropServices;

namespace Vixen.Ui.Layout;

/// <summary>What one half of a track sizing function is measured in.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is deliberately not <see cref="LayoutUnit" />, and the reason is <c>fr</c>.</b> A
///         flexible length is not a length: CSS Grid §7.2.3 says it resolves against the <i>leftover</i>
///         space after every non-flexible track has taken its share, so it has no value at all until
///         §12.7 runs. Adding it to <see cref="LayoutUnit" /> would put a unit into
///         <see cref="StyleLength" /> that every flex-path <c>Resolve</c> would answer <c>NaN</c> for
///         and no flex-path caller would know to ask about — a silent zero in the one place the store
///         has no oracle. Grid gets its own vocabulary instead, which is the same reason it gets its
///         own algorithm.
///     </para>
///     <para>
///         <see cref="FitContent" /> is a maximum only. CSS Grid §7.2.2 defines
///         <c>fit-content(x)</c> as <c>minmax(auto, max-content)</c> with the result clamped by
///         <c>x</c>, so it is stored as that clamp and never appears as a minimum.
///     </para>
/// </remarks>
public enum GridSizingKind : byte {
    /// <summary>Content-based, and stretchable by <c>align-content</c>/<c>justify-content</c>.</summary>
    Auto,

    /// <summary>The largest minimum any item in the track needs.</summary>
    MinContent,

    /// <summary>The largest maximum any item in the track wants.</summary>
    MaxContent,

    /// <summary>An absolute length.</summary>
    Points,

    /// <summary>A fraction of the grid container's content box on this axis.</summary>
    Percent,

    /// <summary>A <c>fr</c>: a share of the space left over. Maximum only.</summary>
    Flex,

    /// <summary>A <c>max-content</c> maximum, clamped by an argument. Maximum only.</summary>
    FitContent
}

/// <summary>One half of a track sizing function — a minimum or a maximum.</summary>
/// <param name="Kind">What kind of size this is.</param>
/// <param name="Value">
///     The number, for <see cref="GridSizingKind.Points" />, <see cref="GridSizingKind.Percent" />,
///     <see cref="GridSizingKind.Flex" /> and <see cref="GridSizingKind.FitContent" />. Meaningless
///     otherwise.
/// </param>
/// <param name="IsFitContentPercent">
///     Whether a <see cref="GridSizingKind.FitContent" /> argument was written as a percentage.
/// </param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct GridSizingFunction(GridSizingKind Kind, float Value, bool IsFitContentPercent = false) {
    /// <summary>The initial value of every track's minimum and of an unstated maximum.</summary>
    public static readonly GridSizingFunction Auto = new(GridSizingKind.Auto, 0f);

    /// <summary><c>min-content</c>.</summary>
    public static readonly GridSizingFunction MinContent = new(GridSizingKind.MinContent, 0f);

    /// <summary><c>max-content</c>.</summary>
    public static readonly GridSizingFunction MaxContent = new(GridSizingKind.MaxContent, 0f);

    /// <summary>An absolute length.</summary>
    /// <param name="points">How many points.</param>
    /// <returns>The function.</returns>
    public static GridSizingFunction Points(float points) => new(GridSizingKind.Points, points);

    /// <summary>A fraction of the container's content box on this axis.</summary>
    /// <param name="percent">The percentage, where 100 means all of it.</param>
    /// <returns>The function.</returns>
    public static GridSizingFunction Percent(float percent) => new(GridSizingKind.Percent, percent);

    /// <summary>A share of the leftover space.</summary>
    /// <param name="fraction">How many <c>fr</c>.</param>
    /// <returns>The function.</returns>
    public static GridSizingFunction Flex(float fraction) => new(GridSizingKind.Flex, fraction);

    /// <summary>
    ///     Whether this function is a fixed length — one that needs no content to resolve.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A percentage is fixed only against a definite size</b>, per CSS Grid §7.2.1: against
    ///     an indefinite one it behaves as <c>auto</c>. That is why this takes the size rather than
    ///     reading a field, and why every caller has to have decided what it is sizing against first.
    /// </remarks>
    /// <param name="containingSize">The container's content-box size on this axis, or NaN.</param>
    /// <returns>Whether it resolves to a number right now.</returns>
    public bool IsFixed(float containingSize) =>
        Kind == GridSizingKind.Points || (Kind == GridSizingKind.Percent && !float.IsNaN(containingSize));

    /// <summary>Whether this function is content-based, per CSS Grid §12's "intrinsic" classes.</summary>
    /// <remarks>
    ///     <c>auto</c> counts, and an unresolvable percentage counts because §7.2.1 makes it
    ///     <c>auto</c>. <c>fit-content()</c> counts because its floor is <c>auto</c> and its ceiling
    ///     is <c>max-content</c>.
    /// </remarks>
    /// <param name="containingSize">The container's content-box size on this axis, or NaN.</param>
    /// <returns>Whether the track's content decides this number.</returns>
    public bool IsIntrinsic(float containingSize) =>
        Kind is GridSizingKind.Auto or GridSizingKind.MinContent or GridSizingKind.MaxContent or GridSizingKind.FitContent
        || (Kind == GridSizingKind.Percent && float.IsNaN(containingSize));

    /// <summary>Resolves a fixed function to a number, or NaN.</summary>
    /// <param name="containingSize">The container's content-box size on this axis, or NaN.</param>
    /// <returns>The length, or NaN when the content decides it.</returns>
    public float Resolve(float containingSize) => Kind switch {
        GridSizingKind.Points => Value,
        GridSizingKind.Percent when !float.IsNaN(containingSize) => Value * containingSize * 0.01f,
        _ => float.NaN
    };

    /// <inheritdoc />
    public override string ToString() => Kind switch {
        GridSizingKind.Points => Value.ToString(CultureInfo.InvariantCulture) + "px",
        GridSizingKind.Percent => Value.ToString(CultureInfo.InvariantCulture) + "%",
        GridSizingKind.Flex => Value.ToString(CultureInfo.InvariantCulture) + "fr",
        GridSizingKind.FitContent => "fit-content("
            + Value.ToString(CultureInfo.InvariantCulture)
            + (IsFitContentPercent ? "%)" : "px)"),
        GridSizingKind.MinContent => "min-content",
        GridSizingKind.MaxContent => "max-content",
        _ => "auto"
    };
}

/// <summary>One track's sizing function: a minimum and a maximum, per CSS Grid §7.2.</summary>
/// <remarks>
///     ⚠ <b>Every track is a <c>minmax()</c> internally, including the ones nobody wrote one for.</b>
///     §12.1 runs on a base size and a growth limit per track, so a bare <c>100px</c> is
///     <c>minmax(100px, 100px)</c>, a bare <c>auto</c> is <c>minmax(auto, auto)</c> and a bare
///     <c>1fr</c> is <c>minmax(auto, 1fr)</c> — the last of which is the one people are surprised by,
///     and the reason an <c>1fr</c> track refuses to shrink below its content.
/// </remarks>
/// <param name="Min">The minimum sizing function. Never <see cref="GridSizingKind.Flex" />.</param>
/// <param name="Max">The maximum sizing function.</param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct GridTrackSize(GridSizingFunction Min, GridSizingFunction Max) {
    /// <summary>An <c>auto</c> track, which is the initial value of the implicit sizing functions.</summary>
    public static readonly GridTrackSize Auto = new(GridSizingFunction.Auto, GridSizingFunction.Auto);

    /// <summary>A track written as a single sizing function rather than as a <c>minmax()</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>A bare <c>fr</c> is <c>minmax(auto, Nfr)</c> and not <c>minmax(0, Nfr)</c></b>, per
    ///     §7.2.3. Reading it as a zero minimum is the single most common way to make a grid whose
    ///     <c>1fr</c> columns crush their own text, and the corpus's <c>0fr</c> families are exactly
    ///     the fixtures that tell the two apart: a <c>0fr</c> track gets no share of the leftover
    ///     space at all, so whatever width it has came from its floor.
    /// </remarks>
    /// <param name="single">The function.</param>
    /// <returns>The track.</returns>
    public static GridTrackSize Single(GridSizingFunction single) =>
        single.Kind == GridSizingKind.Flex
            ? new GridTrackSize(GridSizingFunction.Auto, single)
            : new GridTrackSize(single, single);

    /// <summary>A <c>minmax()</c> track.</summary>
    /// <remarks>
    ///     ⚠ <b>A flexible minimum is <c>auto</c>.</b> §7.2.2's grammar does not admit
    ///     <c>&lt;flex&gt;</c> as a <c>minmax()</c> minimum, and CSS's rule for an invalid value in an
    ///     otherwise valid declaration is that the declaration is dropped — but the corpus never
    ///     writes one, so normalising is both cheaper and impossible to observe.
    /// </remarks>
    /// <param name="minimum">The floor.</param>
    /// <param name="maximum">The ceiling.</param>
    /// <returns>The track.</returns>
    public static GridTrackSize MinMax(GridSizingFunction minimum, GridSizingFunction maximum) =>
        new(minimum.Kind == GridSizingKind.Flex ? GridSizingFunction.Auto : minimum, maximum);

    /// <summary><c>fit-content(x)</c> — a <c>max-content</c> ceiling clamped by an argument.</summary>
    /// <param name="limit">The clamp.</param>
    /// <param name="isPercent">Whether the argument was a percentage.</param>
    /// <returns>The track.</returns>
    public static GridTrackSize FitContent(float limit, bool isPercent) =>
        new(GridSizingFunction.Auto, new GridSizingFunction(GridSizingKind.FitContent, limit, isPercent));

    /// <summary>Whether this track's maximum is a <c>fr</c>, which is what §12.7 grows.</summary>
    public bool IsFlexible => Max.Kind == GridSizingKind.Flex;

    /// <inheritdoc />
    public override string ToString() =>
        Max.Kind == GridSizingKind.FitContent ? Max.ToString()
        : Min == Max ? Min.ToString()
        : Min.Kind == GridSizingKind.Auto && Max.Kind == GridSizingKind.Flex ? Max.ToString()
        : $"minmax({Min},{Max})";
}
