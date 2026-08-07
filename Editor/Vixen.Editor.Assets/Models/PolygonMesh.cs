// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Geometry;

namespace Vixen.Editor.Assets.Models;

/// <summary>One mesh on its way to a file, still made of faces rather than of triangles.</summary>
/// <remarks>
///     <para>
///         <b>docs/plan/41 § Part 4 asks for quads and <see cref="MeshData" /> cannot carry one.</b>
///         That type is what a vertex buffer looks like — one vertex per corner, three corners per
///         triangle — so a retopology that went through it arrived at the file already triangulated
///         and already exploded, with every quad an island of four vertices joined to nothing.
///         Measured on a 5 766-quad result: 11 532 triangles, 23 064 positions, and the only edges
///         with two faces were the diagonals inside each split quad.
///     </para>
///     <para>
///         ⚠ <b>This is the writer's input and not a rendering structure, and the difference is the
///         whole point.</b> <see cref="EditMesh" /> shares a position between the faces that meet
///         there and keeps a face's corner loop at whatever length it is, which is what OBJ's
///         <c>f a b c d</c> wants. Going to <see cref="MeshData" /> is a one-way trip and the writers
///         that must take it — glTF and GLB, which are triangles-only by specification — take it at
///         the point of writing, where they can say so.
///     </para>
/// </remarks>
public sealed record PolygonMesh {
    /// <summary>The geometry, with its faces at whatever number of sides they have.</summary>
    public required EditMesh Mesh { get; init; }

    /// <summary>What to call it in the file.</summary>
    public string Name { get; init; } = "Mesh";

    /// <summary>
    ///     One texture coordinate per corner — <see cref="EditMesh.CornerCount" /> of them — or empty
    ///     for a mesh that has none.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Per corner rather than per position, because a seam is a position whose corners
    ///     disagree.</b> Welding these onto positions would erase every seam in the atlas, which is
    ///     the one thing an unwrap exists to produce.
    /// </remarks>
    public IReadOnlyList<Vector2> TexCoords { get; init; } = [];
}
