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
///     What the shader needs to evaluate the primitive: half-width, half-height, corner radius and
///     border thickness for a box; the screen-pixel range and nothing else for a glyph.
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
public readonly record struct UiDraw(BatchKind Kind, int First, int Count, int Font, Rectangle Clip);

/// <summary>A frame's worth of interface geometry.</summary>
/// <param name="Vertices">Every vertex, in painting order.</param>
/// <param name="Indices">Triangles into <paramref name="Vertices" />.</param>
/// <param name="Draws">What to draw, in painting order.</param>
public readonly record struct UiGeometry(
    IReadOnlyList<UiVertex> Vertices,
    IReadOnlyList<ushort> Indices,
    IReadOnlyList<UiDraw> Draws
);
