// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Terrain;

namespace Vixen.Rendering.Terrain;

/// <summary>
///     One patch of terrain, as the vertex stage reads it.
/// </summary>
/// <param name="Origin">Where the patch's low corner is, in sample coordinates.</param>
/// <param name="Step">How many samples one step of the grid patch spans.</param>
/// <param name="Morph">How far the patch has morphed towards its parent's grid, 0…1.</param>
/// <remarks>
///     <para>
///         <b>The C# half of <c>TerrainNode</c> in <c>Terrain.rvn</c>, and the two agree by
///         construction or not at all.</b> The host packs these bytes and the shader reads them as a
///         struct — the same bargain <see cref="GpuCulling" /> describes, and the same reason the
///         duplication is not a smell to be refactored away.
///     </para>
///     <para>
///         ⚠ <b>Twenty-four bytes, and the last four are declared padding.</b> Five floats is twenty
///         in C# and twenty-four in <c>std430</c>, because a struct's alignment is its widest
///         member's — the <c>float2</c>'s eight — and its size rounds up to a multiple of it. A host
///         writing a twenty-byte stride into an array the shader reads at twenty-four does not fail:
///         it draws patch one out of the middle of patch zero, and every patch after that is further
///         wrong. Adding a field changes both files or neither.
///     </para>
/// </remarks>
/// <param name="Level">Which mip level of the atlas it reads.</param>
/// <param name="Padding0">
///     Declared rather than left to the compiler, so the record is twenty-four bytes on both sides.
/// </param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct TerrainNodeRecord(
    Vector2 Origin,
    float Step,
    float Morph,
    float Level,
    float Padding0 = 0f
) {
    /// <summary>How many bytes one of these is.</summary>
    public static int SizeInBytes => Marshal.SizeOf<TerrainNodeRecord>();

    /// <summary>The record for a node the quadtree selected.</summary>
    /// <param name="node">The node.</param>
    /// <param name="gridQuads">How many quads the shared patch spans.</param>
    /// <param name="maxLevel">The last mip level the atlas has.</param>
    /// <returns>The record.</returns>
    /// <remarks>
    ///     ⚠ <b>The level is <c>log2(step)</c> and the step is a power of two, so it is exact.</b> A
    ///     patch that spans four samples per grid step reads level two, which is the level whose
    ///     texels <em>are</em> its vertices — reading level 0 instead gives it a height nothing
    ///     between its own vertices ever had, and the surface swims as the camera moves.
    ///
    ///     ⚠ <b>Clamped to the atlas's chain, which is a <em>tile's</em> rather than the whole
    ///     texture's.</b> An atlas of thirty-two 128-texel tiles is 4096 wide and would allow thirteen
    ///     levels; only eight of them keep a block at one texel or more, and past that a level mixes
    ///     tiles — which is the seam the layout exists to prevent.
    /// </remarks>
    public static TerrainNodeRecord Of(in TerrainLodNode node, int gridQuads, int maxLevel = 0) {
        var step = node.Quads / (float)gridQuads;
        var level = Math.Clamp((float)Math.Log2(Math.Max(step, 1f)), 0f, Math.Max(maxLevel, 0));

        return new(new(node.X, node.Z), step, node.Morph, level, 0f);
    }
}

/// <summary>
///     The grid every terrain patch is drawn from.
/// </summary>
/// <remarks>
///     <para>
///         <b>One mesh, drawn many times.</b> Every node a frame selects is this same lattice, scaled
///         and placed by its <see cref="TerrainNodeRecord" /> — which is what makes a terrain one
///         indirect instanced draw however many patches it takes. [docs/plan/31 § D3].
///     </para>
///     <para>
///         <b>There are no vertices, and that is deliberate.</b> A regular lattice's positions are two
///         divisions of the vertex index, so the shader computes them from <c>SV_VertexID</c> and
///         uploading 33² of them per frame would be sending it something it can count. The reflection
///         confirms it: <c>Terrain.reflect.json</c> has an empty <c>VertexInputs</c>. What is uploaded
///         is the index buffer, once, and one record per patch.
///     </para>
///     <para>
///         ⚠ <b>The winding is counter-clockwise seen from above</b>, which is what the engine's
///         cull-back convention wants for ground. Getting it backwards produces a terrain that is
///         invisible from above and solid from below — a whole-screen version of the flipped-winding
///         failure the diagnostic playbook describes, and one that looks like nothing drawing at all.
///     </para>
/// </remarks>
public static class TerrainGridPatch {
    /// <summary>How many vertices a patch of this many quads has.</summary>
    /// <param name="gridQuads">How many quads the patch spans along each axis.</param>
    /// <returns>The vertex count.</returns>
    public static int VertexCount(int gridQuads) => (gridQuads + 1) * (gridQuads + 1);

    /// <summary>How many indices a patch of this many quads needs.</summary>
    /// <param name="gridQuads">How many quads the patch spans along each axis.</param>
    /// <returns>The index count — six per quad.</returns>
    public static int IndexCount(int gridQuads) => gridQuads * gridQuads * 6;

    /// <summary>Fills the index buffer for a patch.</summary>
    /// <param name="gridQuads">How many quads the patch spans along each axis. A power of two.</param>
    /// <param name="indices">
    ///     Where to put them; at least <see cref="IndexCount" /> long.
    /// </param>
    /// <returns>How many indices were written.</returns>
    /// <exception cref="ArgumentException">The patch is not one a terrain draws.</exception>
    /// <remarks>
    ///     <b>The diagonal alternates.</b> Splitting every quad the same way makes a regular lattice
    ///     of parallel diagonals, and on a heightfield that reads as a corduroy grain running across
    ///     the whole terrain — most visible exactly where the ground is nearly flat, which is where an
    ///     artist looks hardest. Alternating in a checker cancels it.
    /// </remarks>
    public static int FillIndices(int gridQuads, Span<uint> indices) {
        if (gridQuads < 2 || (gridQuads & (gridQuads - 1)) != 0) {
            throw new ArgumentException(
                $"A grid patch must span a power of two quads of at least two; it was {gridQuads}.",
                nameof(gridQuads)
            );
        }

        var required = IndexCount(gridQuads);

        if (indices.Length < required) {
            throw new ArgumentException(
                $"A {gridQuads}-quad patch needs {required} indices, not {indices.Length}.",
                nameof(indices)
            );
        }

        var side = gridQuads + 1;
        var at = 0;

        for (var z = 0; z < gridQuads; z++) {
            for (var x = 0; x < gridQuads; x++) {
                var topLeft = (uint)((z * side) + x);
                var topRight = topLeft + 1;
                var bottomLeft = (uint)(((z + 1) * side) + x);
                var bottomRight = bottomLeft + 1;

                if (((x + z) & 1) == 0) {
                    indices[at++] = topLeft;
                    indices[at++] = bottomLeft;
                    indices[at++] = topRight;

                    indices[at++] = topRight;
                    indices[at++] = bottomLeft;
                    indices[at++] = bottomRight;
                } else {
                    indices[at++] = topLeft;
                    indices[at++] = bottomLeft;
                    indices[at++] = bottomRight;

                    indices[at++] = topLeft;
                    indices[at++] = bottomRight;
                    indices[at++] = topRight;
                }
            }
        }

        return at;
    }

    /// <summary>Where a vertex of the patch sits, before the morph.</summary>
    /// <param name="vertex">Its index, as <c>SV_VertexID</c> gives it.</param>
    /// <param name="gridQuads">How many quads the patch spans.</param>
    /// <returns>Its grid coordinates.</returns>
    /// <remarks>
    ///     The C# mirror of the two divisions the vertex stage does. Here so a test can walk the
    ///     patch the way the shader will, which is the only way to check the two agree without a
    ///     device in the room.
    /// </remarks>
    public static (int X, int Z) VertexOf(int vertex, int gridQuads) {
        var side = gridQuads + 1;
        return (vertex % side, vertex / side);
    }
}
