// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Ui.Rendering;

/// <summary>One vertex of the interface, in the layout both UI shaders read.</summary>
/// <param name="Position">Where it is, in document pixels.</param>
/// <param name="Texture">
///     An atlas coordinate for text, and the offset from the shape's centre for everything else.
/// </param>
/// <param name="Color">Its colour, in linear space.</param>
/// <param name="Shape">
///     What the shader needs beyond the position: for a box, the index of its record in
///     <see cref="UiGeometry.Shapes" />; for a glyph, the screen-pixel range; for a path triangle,
///     how much of the pixel the shape covers there. Only the first lane is ever read.
/// </param>
/// <remarks>
///     <para>
///         ⚠ <b>One layout for both, rather than one per shader.</b> Two layouts would mean two
///         vertex buffers, two uploads and two places for a batch to point at, to save sixteen bytes
///         on a vertex count that is in the thousands rather than the millions — an interface is not
///         a mesh. What it buys is that a frame is one buffer and one copy however many kinds of
///         thing it draws.
///     </para>
///     <para>
///         Fields a kind does not use are zero, exactly as <see cref="DrawCommand" /> does it, and
///         for the same reason: the consumer switches on the batch anyway.
///     </para>
/// </remarks>
public readonly record struct UiVertex(Vector2 Position, Vector2 Texture, Color4 Color, Vector4 Shape);

/// <summary>A run of vertices a renderer can draw with one pipeline and one state.</summary>
/// <param name="Kind">Which shader draws it.</param>
/// <param name="First">The index of its first index, in the index buffer.</param>
/// <param name="Count">How many indices it covers.</param>
/// <param name="Font">Which font, for text. Zero and unread otherwise.</param>
/// <param name="Clip">The scissor rectangle in force, in document pixels.</param>
/// <remarks>
///     ⚠ <b>The clip is carried on the draw rather than replayed as commands.</b> A draw list pushes
///     and pops; a renderer sets a scissor. Resolving the stack here means the renderer never holds
///     one — and never has to be told that a batch it skipped had left a clip behind.
/// </remarks>
public readonly record struct UiDraw(BatchKind Kind, int First, int Count, int Font, Rectangle Clip) {
    /// <summary>Which texture, for <see cref="BatchKind.Image" />. Zero and unread otherwise.</summary>
    /// <remarks>
    ///     Carried through from the batch, because a texture is a descriptor set the renderer binds
    ///     and the renderer sees only this list. Opaque here for the reason it is opaque on the
    ///     command: naming a texture view would mean this assembly referenced the graphics layer.
    /// </remarks>
    public ulong Image { get; init; }
}

/// <summary>A subtree drawn into a surface of its own, and composited back once.</summary>
/// <param name="First">The index of its first <see cref="UiDraw" />.</param>
/// <param name="Count">How many draws it covers. Its composite draw is the one after them.</param>
/// <param name="Bounds">
///     The part of the surface it actually inks, in document pixels, already rounded out to whole
///     ones, already widened by <see cref="UiLayer.Blur" /> where there is one, and already narrowed
///     by the clip that was in force where it opened.
/// </param>
/// <param name="Alpha">What the composite is faded by.</param>
/// <remarks>
///     <para>
///         ⚠ <b>The offscreen surface is the size of the whole viewport, not of
///         <paramref name="Bounds" />, and that is the decision that keeps the two renderers
///         honest.</b> A surface the size of the group would need every vertex inside it translated
///         by the group's origin, on both paths, in the same direction, with the same rounding — and
///         a disagreement there is a subtree drawn a pixel off, which no unit test would be looking
///         at and which the goldens would report as a diff somewhere else entirely. At viewport size
///         there is no translation to get wrong: a layer's contents are drawn at exactly the
///         coordinates they would have had, and the composite samples the texel under each pixel.
///         What it costs is memory, on a frame that has a translucent group at all.
///     </para>
///     <para>
///         ⚠ <b><paramref name="Bounds" /> is the <i>ink</i>, not the element's box.</b> Opacity does
///         not clip — CSS Compositing 1 § 3 isolates a group, it does not bound it — so a child
///         overflowing its half-opaque parent must still be composited. The bounds are therefore
///         computed from the vertices the group actually emitted, which is the only description of
///         "everything it drew" available at this stage, and are what the composite quad covers.
///         Sizing it to the element's rectangle instead would cut off exactly the overflow that
///         <c>overflow: visible</c> promises to keep.
///     </para>
/// </remarks>
public readonly record struct UiLayer(int First, int Count, Rectangle Bounds, float Alpha) {
    /// <summary>The number the composite draw names its surface by.</summary>
    /// <remarks>
    ///     ⚠ <b>Assigned here rather than by a renderer, because two renderers have to agree on
    ///     it.</b> A composited group is drawn as an ordinary <see cref="BatchKind.Image" />, and an
    ///     image names its texture with a number this assembly deliberately does not interpret — so
    ///     whoever executes the frame has to know which number stands for which group. Deriving it
    ///     from the layer's index means the GPU renderer and the software one arrive at the same
    ///     answer by construction rather than by both having been written carefully.
    /// </remarks>
    public ulong Image { get; init; }

    /// <summary>The standard deviation of the Gaussian its surface is blurred by, in document pixels.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The standard deviation, and <i>not</i> the half-extent the shadow path calls a
    ///         blur.</b> Filter Effects 1 § 8.4 defines <c>blur(r)</c> as a Gaussian with σ equal to
    ///         <c>r</c>, so the length in the stylesheet arrives here unchanged. <c>box-shadow</c>'s
    ///         third length is the <i>total</i> distance an edge fades over and
    ///         <c>DrawListBuilder.EmitShadow</c> halves it on the way to the shader — copying that
    ///         halving here would make <c>filter: blur(8px)</c> half the blur CSS asks for, on a
    ///         picture nobody can check against a number.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Zero is the ordinary case and the only value that changes nothing.</b> A group
    ///         with a blur costs two more render passes than one without — see
    ///         <c>UiRenderer.Compose</c> — so a consumer that ignores this draws the group unblurred
    ///         rather than incorrectly, which is the same bargain <see cref="Image" /> makes with a
    ///         consumer that ignores layers entirely.
    ///     </para>
    /// </remarks>
    public float Blur { get; init; }

    /// <summary>The colour transform its surface is put through, or null where there is none.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Costs neither a surface nor a pass, and that is the whole reason it is a separate
    ///         field from <see cref="Blur" /> rather than a second thing "the filter" means.</b> A
    ///         blur is a neighbourhood operation, so it cannot read and write one attachment and the
    ///         device needs a scratch target and two more passes for it; <see cref="Bounds" /> has to
    ///         be outset by the kernel as well, because coverage moves. A colour matrix is per pixel.
    ///         It moves no coverage, so the bounds are untouched, and it can be folded into the
    ///         composite draw the group was going to make anyway — see <c>UiRenderer.Compose</c>,
    ///         where a filtered group costs one pipeline change and forty-eight bytes of push
    ///         constant over an unfiltered one. A design that spent a viewport-sized target on this
    ///         would be paying a blur's price for something that is not a blur.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Where in the pipeline it is applied is deliberately <i>not</i> specified, because
    ///         it does not matter and pinning it down would cost one of the executors a pass.</b> The
    ///         transform is affine in premultiplied colour and a Gaussian is a weighted sum, so the
    ///         two commute exactly; the transform is also linear, so it commutes with a bilinear
    ///         sampler. <c>SoftwareUiRasterizer</c> therefore applies it to the finished surface, at
    ///         the same seam it blurs at, and <c>UiRenderer</c> applies it in the fragment stage of
    ///         the composite. <c>UiCompositingTests</c> is what holds the two to the same picture.
    ///     </para>
    ///     <para>
    ///         ⚠ Null and not a zeroed matrix — see <see cref="UiColorMatrix" />, whose default maps
    ///         every colour to black. A consumer that ignores this composites the group in full
    ///         colour, which is the same bargain <see cref="Blur" /> makes.
    ///     </para>
    /// </remarks>
    public UiColorMatrix? Filter { get; init; }

    /// <summary>The coverage this group's <c>mask-image</c> multiplies its surface by, or null.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Where this is applied <i>is</i> specified, which is the whole difference between
    ///         it and <see cref="Filter" /> one paragraph up.</b> That field can be left to each
    ///         executor's convenience because a colour matrix is the same map at every pixel and so
    ///         passes through both a Gaussian and a bilinear tap. A mask is a scalar that varies with
    ///         position and passes through neither: <c>m(p)·Σ wᵢsᵢ</c> is not <c>Σ wᵢ·m(pᵢ)·sᵢ</c>.
    ///         So the seam is named rather than left open — <b>both executors apply the mask in the
    ///         composite draw</b>, after the blur and after the matrix, from the composite quad's own
    ///         texture coordinate. A mask folded into the surface by one of them would be a different
    ///         picture wherever the ramp crosses a blurred edge, which is precisely where anyone
    ///         would put one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The mask's box is carried inside <see cref="UiMask" /> and is not
    ///         <see cref="Bounds" />.</b> These bounds are the group's ink and have already been
    ///         outset by <see cref="Blur" />; CSS resolves <c>mask-image</c> against the border box,
    ///         which has not. Reading the bounds here would make a ramp shift when a blur was added
    ///         beside it.
    ///     </para>
    ///     <para>
    ///         ⚠ Null and not a zeroed mask — a zeroed <see cref="UiMask" /> covers nothing, so a
    ///         consumer that mistook the default for the absence would erase the group rather than
    ///         draw it plainly. A consumer that ignores this field composites the group unmasked,
    ///         which is the same bargain <see cref="Blur" /> and <see cref="Filter" /> make.
    ///     </para>
    /// </remarks>
    public UiMask? Mask { get; init; }

    /// <summary>The widest kernel either executor will run, in texels each side of the centre.</summary>
    /// <remarks>
    ///     ⚠ <b>A cap and not a limit on what may be asked for, and the two executors truncate
    ///     identically because they both come here for the number.</b> Three sigma each side is 385
    ///     taps at <c>blur-16</c>, twice, over a viewport-sized target; past this the tail being
    ///     dropped is under a thousandth of the kernel's mass and the weights are renormalised over
    ///     the taps actually taken, so the picture dims by nothing. What it is <i>not</i> is a
    ///     silent divergence: a software path that summed the whole kernel and a shader that stopped
    ///     at sixty-four would disagree by more than <c>UiCompositingTests</c> allows, which is how
    ///     this constant came to be shared rather than written twice.
    /// </remarks>
    public const int MaximumKernel = 64;

    /// <summary>How many texels each side of the centre a blur of this width reads.</summary>
    /// <param name="blur">The standard deviation, in document pixels.</param>
    /// <param name="scale">How many target texels one document pixel is.</param>
    /// <returns>Zero when there is no blur, and never more than <see cref="MaximumKernel" />.</returns>
    /// <remarks>
    ///     ⚠ <b>Three sigma, and the ceiling is taken after the scale rather than before.</b> The
    ///     builder outsets <see cref="Bounds" /> by this at a scale of one, in document pixels, and an
    ///     executor asks for it again at the scale it is drawing at — so the outset is always at least
    ///     the kernel, because rounding a document pixel up and then multiplying can only overshoot
    ///     multiplying and then rounding up. A halo clipped by its own bounds is the failure this
    ///     asymmetry exists to make impossible, and it looks like a soft edge with a hard line across
    ///     it.
    /// </remarks>
    public static int KernelRadius(float blur, float scale) =>
        blur <= 0f || scale <= 0f || !float.IsFinite(blur) || !float.IsFinite(scale)
            ? 0
            : Math.Min(MaximumKernel, (int) MathF.Ceiling(3f * blur * scale));
}

/// <summary>A frame's worth of interface geometry.</summary>
/// <param name="Vertices">Every vertex, in painting order.</param>
/// <param name="Indices">Triangles into <paramref name="Vertices" />.</param>
/// <param name="Draws">What to draw, in painting order.</param>
/// <param name="Shapes">One record per box, indexed by its vertices' <c>Shape.X</c>.</param>
/// <remarks>
///     ⚠ <b>Thirty-two-bit indices, and not because a frame is expected to need them.</b> Almost none
///     do: sixteen bits reach 65 535 vertices, which is sixteen thousand quads, and a dense editor
///     window is a few thousand. The reason is what happens to the one frame that does — an index
///     that wraps draws geometry from the top of the frame in the middle of it, silently, and the
///     picture is wrong rather than absent. Emitting the wider index costs two bytes per index on
///     about an eighth of the frame's bytes, and buys never having to reason about the ceiling
///     again.
/// </remarks>
public readonly record struct UiGeometry(
    IReadOnlyList<UiVertex> Vertices,
    IReadOnlyList<uint> Indices,
    IReadOnlyList<UiDraw> Draws,
    IReadOnlyList<UiShape> Shapes
) {
    /// <summary>The composited groups, innermost last, or empty on a frame with none.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Init-only rather than a fifth positional parameter, so that no caller changes.</b>
    ///         The same argument <see cref="UiDraw.Image" /> makes: this is a record whose constructor
    ///         is called by every host and every test that builds geometry by hand, and a frame with no
    ///         translucent group — which is nearly all of them — is entitled to say nothing about them.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Sorted by <see cref="UiLayer.First" />, and the ranges nest rather than overlap.</b>
    ///         A group is a bracketed region of the draw list, so an inner group's range lies wholly
    ///         inside its outer one's — which is what lets a consumer execute them with a stack and
    ///         never a search. A consumer that finds two ranges partly overlapping is looking at a bug
    ///         in the builder, not at a case to handle.
    ///     </para>
    /// </remarks>
    public IReadOnlyList<UiLayer> Layers { get; init; } = [];
}
