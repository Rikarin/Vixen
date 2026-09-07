// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Ui.Rendering;

/// <summary>Which channel of an image a draw shows, as a grey, or all of them as a colour.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The numbering is the wire format.</b> <c>Ui.rvn</c>'s <c>UiImage</c> stage rounds
///         <c>shape.y</c> to an integer and branches on it, exactly as <c>MaskComposite</c>'s own
///         remark describes for the mask buffer — so these values are the contract and not an
///         implementation detail. <see cref="All" /> is zero because zero is what every image quad in
///         the repository already carries: a command that says nothing about channels has to keep
///         drawing the picture it drew before there was anything to say.
///     </para>
///     <para>
///         <b>One choice rather than a set of flags</b>, matching
///         <c>Vixen.Ui.Controls.Advanced.ImageChannels</c>, whose own remark argues it: "what is in
///         the green channel" is one channel at a time, and a tool that also wants "what does this
///         look like without its alpha" offers a second control rather than one enum meaning two
///         things.
///     </para>
/// </remarks>
public enum UiImageChannel : byte {
    /// <summary>Colour, with the image's own alpha. What an image drawn into a panel is.</summary>
    All = 0,

    /// <summary>Red alone, as an opaque grey.</summary>
    Red = 1,

    /// <summary>Green alone, as an opaque grey.</summary>
    Green = 2,

    /// <summary>Blue alone, as an opaque grey.</summary>
    Blue = 3,

    /// <summary>Alpha alone, as an opaque grey.</summary>
    Alpha = 4
}

/// <summary>How an image draw shows the numbers it samples: which channels, through which curve.</summary>
/// <param name="Channel">Which channel, or <see cref="UiImageChannel.All" /> for the colour.</param>
/// <param name="ShowStoredValues">
///     Whether the stored numbers reach the screen unchanged rather than as colour.
/// </param>
/// <remarks>
///     <para>
///         <b>What this is for</b>: <see href="https://github.com/Rikarin/Vixen/issues/611">#611</see>.
///         <c>ImageView</c> shipped a channel picker and a colour-space toggle that changed nothing
///         about the picture, because the image command carried a tint and a source rectangle and
///         nothing else — and a tint multiplies, where isolating the alpha as a grey is a swizzle and
///         undoing a transfer function is a curve. This is the field that was missing.
///     </para>
///     <para>
///         ⚠ <b>The default is the identity, unlike <c>DrawCommand.Filter</c>'s.</b> A zeroed
///         <c>UiColorMatrix</c> maps every colour to black, which is why that field is nullable; a
///         zeroed one of these is <see cref="UiImageChannel.All" /> with no curve, which is precisely
///         what every image drawn before #611 asked for. So this rides <c>DrawCommand</c> as a plain
///         value and a caller that has never heard of it is already correct.
///     </para>
///     <para>
///         ⚠ <b><see cref="ShowStoredValues" /> is named for what it does rather than for a colour
///         space, and the rename is the point.</b> The control one assembly up calls the two choices
///         sRGB and linear, which describes what is *in* the texture; a draw command cannot know
///         that — the same texture is a colour to one viewer and a roughness field to the next.
///         What it can say is where the numbers should land: <c>UiWindowSurface</c> presents to a
///         <c>Bgra8UNormSrgb</c> target, so the hardware encodes on write and a stored 0.5 reaches
///         the glass as 188/255. Setting this decodes first, the two cancel, and 0.5 shows as
///         128/255 — the number the author typed.
///     </para>
///     <para>
///         <b>Carried per vertex, on three components of <c>shape</c> that this pipeline never
///         read.</b> That is what keeps two images asking for different channels in one batch: a
///         per-draw uniform would have split the batch, and a new vertex attribute would have moved
///         the stride, <c>UiVertex.vert.spv</c>, the hand-written GLSL twin and every other pipeline
///         with it. See <see cref="Shape" />.
///     </para>
/// </remarks>
public readonly record struct UiImageView(UiImageChannel Channel, bool ShowStoredValues) {
    /// <summary>Whether this asks for anything at all — the colour, unaltered.</summary>
    /// <remarks>
    ///     The same question <c>UiColorMatrix.IsIdentity</c> answers, for the same reason: a
    ///     builder that can tell "nothing was asked for" from "the identity was asked for" can drop
    ///     the second and keep the picture.
    /// </remarks>
    public bool IsIdentity => Channel == UiImageChannel.All && !ShowStoredValues;

    /// <summary>
    ///     This request as the <c>y</c> and <c>z</c> of the <c>shape</c> stream, with <c>x</c> given.
    /// </summary>
    /// <param name="premultiplied">
    ///     What <c>shape.x</c> carries: whether the sampled texel already has its alpha in it.
    /// </param>
    /// <returns>The vertex's <c>shape</c>.</returns>
    /// <remarks>
    ///     ⚠ <b>One place builds this vector, and that is the whole of why it is a method.</b> The
    ///     image path emits quads from three call sites — a whole image, each of a nine-slice's nine
    ///     cells, and a composited layer — and a swizzle spelled out at two of the three is the
    ///     defect where a nine-sliced image ignores the channel picker and nothing says so. The
    ///     shader reads <c>y</c> with a <c>round</c>, so the cast is a widening and not a
    ///     quantisation.
    /// </remarks>
    public Vector4 Shape(float premultiplied = 0f) =>
        new(premultiplied, (float)Channel, ShowStoredValues ? 1f : 0f, 0f);
}
