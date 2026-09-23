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

    /// <summary>What <c>line-height: normal</c> is worth here, as a multiple of the font size.</summary>
    /// <remarks>
    ///     ⚠ <b>A stand-in, and the one number in this file that is not exact.</b> CSS computes
    ///     <c>normal</c> from the font's own ascender, descender and line gap —
    ///     <c>TextRun.Height</c> does exactly that — and nothing that resolves a length has a font.
    ///     So a <c>1lh</c> on an element whose line height is <c>normal</c> answers this instead, and
    ///     it will differ from the drawn line box by whatever that font's metrics differ from 1.2 by.
    ///     A declared <c>line-height</c> is carried through exactly and does not go near this.
    /// </remarks>
    const float NormalLineHeightFactor = 1.2f;

    readonly float lineHeight;
    readonly float containerInline;
    readonly float containerBlock;

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

    /// <summary>The element's own computed line height in pixels. What <c>lh</c> means.</summary>
    /// <remarks>
    ///     ⚠ <b>Never zero and never <see cref="float.NaN" />, whatever was written in.</b> Both are
    ///     how the rest of the engine spells "the font decides" — <c>UiElement.LineHeight</c> is
    ///     <c>NaN</c> for <c>line-height: normal</c> and a default-constructed context has nothing at
    ///     all — and a length unit whose scale is zero is this file's own documented trap: it makes
    ///     <c>max-height: 1lh</c> a box of no height rather than a declaration that could not be
    ///     resolved. So the two "unset" spellings answer <see cref="NormalLineHeightFactor" /> times
    ///     the font size, which is a stand-in for the font's metrics and is marked as one.
    /// </remarks>
    public float LineHeight {
        get => lineHeight > 0f ? lineHeight : FontSize * NormalLineHeightFactor;
        init => lineHeight = value;
    }

    /// <summary>The same context with a different line height.</summary>
    /// <param name="lineHeight">
    ///     The element's own computed line height in pixels, or <see cref="float.NaN" /> for
    ///     <c>line-height: normal</c>.
    /// </param>
    /// <returns>The context.</returns>
    public LengthContext WithLineHeight(float lineHeight) => this with { LineHeight = lineHeight };

    /// <summary>Which axes an eligible query container was found on.</summary>
    /// <remarks>
    ///     ⚠ <b>A flag and not a sentinel value, because a query container of zero width is a real
    ///     thing and zero is this file's own documented trap.</b> Reading "no container" off a width
    ///     of zero would make <c>10cqw</c> inside a collapsed panel resolve against the
    ///     <i>viewport</i> — a number that is not small but large, which is the direction that shows
    ///     as a layout explosion rather than as nothing being drawn.
    ///     <para>
    ///         <see cref="ContainerKind.Normal" /> is the default, so a context nobody told about
    ///         containers answers the viewport, which is what CSS Containment 3 § 5.3 asks for.
    ///     </para>
    /// </remarks>
    public ContainerKind ContainerAxes { get; init; }

    /// <summary>The query container's inline size. A hundredth of it is <c>1cqi</c> and <c>1cqw</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>The <i>small viewport</i> when there is no eligible container</b> — CSS Containment 3
    ///     § 5.3, not a guess. A container unit outside every container is a viewport unit, which is
    ///     why deleting a <c>container-type</c> from a sheet reflows a document rather than
    ///     collapsing it.
    /// </remarks>
    public float ContainerInlineSize {
        get => ContainerAxes >= ContainerKind.InlineSize ? containerInline : ViewportWidth;
        init => containerInline = value;
    }

    /// <summary>The query container's block size. A hundredth of it is <c>1cqb</c> and <c>1cqh</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>It takes a <c>size</c> container and not an <c>inline-size</c> one</b>, so the
    ///     container answering <c>cqb</c> is often a different ancestor from the one answering
    ///     <c>cqi</c> — and may be no ancestor at all while <c>cqi</c> has one. That is the whole
    ///     reason this is two numbers and one <see cref="ContainerAxes" /> rather than one box:
    ///     containment on the block axis is the thing an author opts into separately, and an
    ///     <c>inline-size</c> container's height is still its content's.
    /// </remarks>
    public float ContainerBlockSize {
        get => ContainerAxes == ContainerKind.Size ? containerBlock : ViewportHeight;
        init => containerBlock = value;
    }

    /// <summary>The same context inside a query container.</summary>
    /// <param name="inlineSize">The nearest inline-axis container's content-box width.</param>
    /// <param name="blockSize">The nearest block-axis container's content-box height.</param>
    /// <param name="axes">Which of the two were found.</param>
    /// <returns>The context.</returns>
    public LengthContext WithContainer(float inlineSize, float blockSize, ContainerKind axes) =>
        this with { ContainerInlineSize = inlineSize, ContainerBlockSize = blockSize, ContainerAxes = axes };

    /// <summary>How many pixels one of a unit is worth.</summary>
    /// <param name="unit">The unit.</param>
    /// <returns>The pixels, or zero for a unit that is not a length.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Prefer <see cref="ToLength" />. Reading a declared value through this method is a
    ///         bug with a plausible-looking answer, and it was written six separate times in one
    ///         assembly before anybody noticed one of them.</b>
    ///         A unit that measures no distance — <c>200ms</c>, <c>90deg</c>, a percentage — comes
    ///         back as <i>zero</i>, and zero is a legal, visible answer for almost everything a
    ///         length is used for: a shadow at no offset, a blur that does not blur, a spread of
    ///         nothing, tracking of exactly <c>normal</c>. Nothing throws, nothing is logged, and the
    ///         frame looks like one where the declaration was never written. Every caller in this
    ///         repository that took a <see cref="StyleValue" /> from a stylesheet has been moved to
    ///         <see cref="ToLength" />, which answers <see cref="StyleLength.Undefined" /> for the
    ///         same input and therefore lets the caller refuse it; this method survives for a caller
    ///         that already knows its unit is a distance and only wants the scale factor.
    ///     </para>
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
        StyleUnit.LineHeight => LineHeight,

        // ⚠ <c>cqi</c> and <c>cqw</c> answer the same number, and <c>cqb</c> and <c>cqh</c> do, which
        // is exact rather than approximate: the inline axis is the horizontal one in every writing
        // mode `Vixen.Ui.Layout` has, and it has one. See <see cref="StyleUnit.ContainerInline" />.
        StyleUnit.ContainerWidth or StyleUnit.ContainerInline => ContainerInlineSize / 100f,
        StyleUnit.ContainerHeight or StyleUnit.ContainerBlock => ContainerBlockSize / 100f,
        StyleUnit.ContainerMin => MathF.Min(ContainerInlineSize, ContainerBlockSize) / 100f,
        StyleUnit.ContainerMax => MathF.Max(ContainerInlineSize, ContainerBlockSize) / 100f,
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
            or StyleUnit.ViewportMax
            or StyleUnit.LineHeight
            or StyleUnit.ContainerWidth
            or StyleUnit.ContainerHeight
            or StyleUnit.ContainerInline
            or StyleUnit.ContainerBlock
            or StyleUnit.ContainerMin
            or StyleUnit.ContainerMax;
}
