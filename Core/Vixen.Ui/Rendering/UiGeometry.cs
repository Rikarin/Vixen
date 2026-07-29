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
    /// <summary>Which external picture, as an index into <c>DrawList.Surfaces</c>.</summary>
    /// <remarks>
    ///     Only meaningful for <see cref="BatchKind.Surface" />; zero and unread otherwise, like every
    ///     other field a kind does not use. Init-only rather than positional so that the four kinds
    ///     that never had one are still constructed with the five arguments they always were.
    /// </remarks>
    public int Surface { get; init; }
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
    /// <summary>The external pictures the surface draws refer to, by index.</summary>
    /// <remarks>
    ///     Carried through from <c>DrawList.Surfaces</c> rather than resolved here, because resolving
    ///     one means knowing what it is and this assembly is the one that does not. Empty for a frame
    ///     with none, which is almost every frame.
    /// </remarks>
    public IReadOnlyList<object> Surfaces { get; init; } = [];
}
