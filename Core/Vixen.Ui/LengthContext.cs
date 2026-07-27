// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Layout;
using Vixen.Ui.Styling;

namespace Vixen.Ui;

/// <summary>What a relative length is relative to.</summary>
/// <param name="FontSize">
///     The element's own computed font size, in pixels. What <c>em</c> means — except on
///     <c>font-size</c> itself, where <c>em</c> means the parent's and this must therefore be the
///     parent's too.
/// </param>
/// <param name="RootFontSize">The root element's computed font size. What <c>rem</c> means.</param>
/// <param name="ViewportWidth">The surface's width in pixels. A hundredth of it is <c>1vw</c>.</param>
/// <param name="ViewportHeight">The surface's height. A hundredth of it is <c>1vh</c>.</param>
/// <remarks>
///     <para>
///         This is the context <c>Vixen.Ui.Styling</c> deliberately does not have. The cascade
///         decides <i>which</i> declaration wins without knowing what any of it measures, and that
///         separation is what lets a stylesheet be resolved once and applied to two surfaces of
///         different sizes.
///     </para>
///     <para>
///         Everything here is in device-independent pixels. DPI scaling is applied at the far end,
///         when the draw list becomes geometry, so a stylesheet is not re-resolved because a window
///         moved to another monitor.
///     </para>
/// </remarks>
public readonly record struct LengthContext(
    float FontSize,
    float RootFontSize,
    float ViewportWidth,
    float ViewportHeight
) {
    /// <summary>CSS's initial font size, which is what a document with no <c>font-size</c> gets.</summary>
    public const float InitialFontSize = 16f;

    /// <summary>A context for a surface, before any element's own font size is known.</summary>
    /// <param name="width">The surface's width.</param>
    /// <param name="height">Its height.</param>
    /// <param name="rootFontSize">The root's font size.</param>
    /// <returns>The context.</returns>
    public static LengthContext ForViewport(float width, float height, float rootFontSize = InitialFontSize) =>
        new(rootFontSize, rootFontSize, width, height);

    /// <summary>The same context with a different font size.</summary>
    /// <param name="fontSize">The element's own computed font size.</param>
    /// <returns>The context.</returns>
    public LengthContext WithFontSize(float fontSize) => this with { FontSize = fontSize };

    /// <summary>How many pixels one of a unit is worth.</summary>
    /// <param name="unit">The unit.</param>
    /// <returns>The pixels, or zero for a unit that is not a length.</returns>
    /// <remarks>
    ///     ⚠ <see cref="StyleUnit.Percent" /> is not here, and its absence is the point. A percentage
    ///     resolves against the containing block, which only the layout pass knows — so it is carried
    ///     through to <see cref="LayoutUnit.Percent" /> unresolved rather than turned into a number
    ///     here. Resolving it against anything available at this point would be resolving it against
    ///     the wrong thing.
    /// </remarks>
    public float PixelsPer(StyleUnit unit) => unit switch {
        StyleUnit.Pixels => 1f,
        StyleUnit.Em => FontSize,
        StyleUnit.Rem => RootFontSize,
        StyleUnit.ViewportWidth => ViewportWidth / 100f,
        StyleUnit.ViewportHeight => ViewportHeight / 100f,
        StyleUnit.ViewportMin => MathF.Min(ViewportWidth, ViewportHeight) / 100f,
        StyleUnit.ViewportMax => MathF.Max(ViewportWidth, ViewportHeight) / 100f,
        _ => 0f
    };

    /// <summary>Turns a parsed value into a length layout can use.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The length, or <see cref="StyleLength.Undefined" /> if it is not one.</returns>
    /// <remarks>
    ///     <para>
    ///         A bare <c>0</c> is a length in CSS and only a zero one, which is why a number is
    ///         accepted here but only that number. Accepting any bare number would make
    ///         <c>width: 42</c> mean 42 pixels, which no stylesheet author intended and no browser
    ///         does.
    ///     </para>
    ///     <para>
    ///         Anything unrecognised comes back undefined rather than zero. Zero is a valid, visible
    ///         answer — an element of no width — and using it for "I did not understand this" turns
    ///         a typo into a blank screen with nothing said about it.
    ///     </para>
    /// </remarks>
    public StyleLength ToLength(StyleValue value) => value.Kind switch {
        StyleValueKind.Number when value.Number == 0f => StyleLength.Zero,
        StyleValueKind.Length when value.Unit == StyleUnit.Percent => StyleLength.Percent(value.Number),
        StyleValueKind.Length when IsLength(value.Unit) => StyleLength.Points(value.Number * PixelsPer(value.Unit)),
        _ => StyleLength.Undefined
    };

    /// <summary>Whether a unit measures distance at all.</summary>
    /// <remarks>
    ///     Asked explicitly rather than by testing <see cref="PixelsPer" /> against zero, because a
    ///     surface of zero width would then make <c>50vw</c> indistinguishable from <c>200ms</c> —
    ///     one is a length that happens to be nothing, the other is not a length.
    /// </remarks>
    static bool IsLength(StyleUnit unit) =>
        unit is StyleUnit.Pixels
            or StyleUnit.Em
            or StyleUnit.Rem
            or StyleUnit.ViewportWidth
            or StyleUnit.ViewportHeight
            or StyleUnit.ViewportMin
            or StyleUnit.ViewportMax;
}
