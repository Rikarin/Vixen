// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Rendering;

/// <summary>What a composited group's <c>backdrop-filter</c> does to the picture behind it.</summary>
/// <param name="Blur">The Gaussian standard deviation, in document pixels. As <see cref="UiLayer.Blur" />.</param>
/// <param name="Alpha">
///     What <c>opacity()</c> fades the filtered backdrop by, one being unchanged. ⚠ The only one of
///     the nine functions that <see cref="Matrix" /> could not carry — see the remark.
/// </param>
/// <param name="Matrix">The colour transform the eight remaining functions compose into, or null.</param>
/// <remarks>
///     <para>
///         ⚠ <b>The same three shapes <see cref="UiLayer" /> already executes, pointed at a different
///         picture — and that is the whole of why this feature needed a compositor change rather than
///         a shader.</b> A blur, a colour matrix and a quad are exactly what a <c>filter</c> costs. The
///         difference is <i>what is convolved</i>: a <c>filter</c> reads the group's own surface, and
///         this reads the destination the group is about to composite <b>into</b>. Nothing behind the
///         group has been drawn at the moment its surface is rendered, so the backdrop is not read
///         back — it is <i>re-rendered</i>, from the same draw list, up to the group's own first draw.
///         See <c>UiRenderer.Capture</c> and <c>SoftwareUiRasterizer.Frame.Run</c>.
///     </para>
///     <para>
///         ⚠ <b>Every <see cref="UiLayer" /> is a backdrop root, which is what makes this a single
///         prefix and not a walk up the ancestors.</b> Filter Effects 2 § 2 says an element forms a
///         backdrop root if it has a filter, an opacity below one, a mask or a clip path — and a group
///         exists in this engine for precisely those reasons and no other. So a nested group's
///         backdrop is its <i>parent's own surface so far</i>, which starts from transparent black,
///         and only a top-level group's backdrop includes whatever the host painted before the
///         interface. There is no accumulation to get wrong.
///     </para>
///     <para>
///         ⚠ <b><see cref="Alpha" /> rides the backdrop quad's vertex alpha rather than the matrix,
///         for <see cref="UiDropShadow" />'s reason.</b> <see cref="UiColorMatrix" /> has three rows
///         and cannot scale alpha, and <c>backdrop-opacity-50</c> is one of the ten roots this exists
///         for. Both executors multiply all four channels of an already-premultiplied sample by the
///         quad's alpha, so that is where it can go — and the group's own opacity multiplies into the
///         same number, which is what CSS means by the backdrop image being painted inside the
///         element's own stacking context.
///     </para>
///     <para>
///         ⚠ <b>The order among the three is free, and unlike <see cref="UiLayer.Shadow" /> it is free
///         for a reason rather than by convention.</b> A Gaussian is a weighted sum whose weights sum
///         to one, a colour matrix is affine in premultiplied colour, and an alpha scale is a scalar:
///         all three commute exactly. There is no second Gaussian here to fail to commute with, which
///         is what a drop shadow is and why <c>backdrop-filter</c> refuses <c>drop-shadow()</c>.
///     </para>
///     <para>
///         ⚠ <b>The picture is clipped to the group's <i>border box</i> and not to
///         <see cref="UiLayer.Bounds" />, and not to its corner radius either.</b> The bounds are the
///         group's <i>ink</i>, which a child overflowing its parent makes larger than the element —
///         filtering the backdrop over that would put a blurred rectangle outside the panel that asked
///         for it. So <see cref="UiLayer.BackdropBounds" /> is carried separately. What is <i>not</i>
///         closed is the radius: CSS clips the filtered backdrop to the border box including its
///         curve, and <c>rounded-2xl backdrop-blur-md</c> therefore shows square corners here. See
///         <c>docs/guide/ui/compositing.md</c>, which states it as a divergence and prices closing it.
///     </para>
/// </remarks>
public readonly record struct UiBackdrop(float Blur, float Alpha, UiColorMatrix? Matrix = null) {
    /// <summary>Whether this would change the picture, and so whether it is worth a surface.</summary>
    /// <remarks>
    ///     ⚠ <b>A group whose backdrop came out the identity is not opened for it, which is the same
    ///     departure from CSS <c>DrawListBuilder.ElementFilter.Any</c> makes and for the same
    ///     price.</b> <c>UtilityComposition.BackdropFilter</c> assembles all nine functions into every
    ///     declaration it emits, so a bare <c>backdrop-blur-0</c> would otherwise buy a viewport-sized
    ///     surface, a capture pass and two blur passes to convolve the picture into itself.
    /// </remarks>
    public bool IsIdentity =>
        Blur <= 0f && Alpha >= 1f && (Matrix is null || Matrix.Value.IsIdentity);
}
