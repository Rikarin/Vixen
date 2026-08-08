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
///     ones and already narrowed by the clip that was in force where it opened.
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
