// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Rendering.Water;
using Vixen.Terrain;
using Vixen.Water;
using Xunit;

namespace Tests;

/// <summary>
///     The surface mesh's device half — [docs/plan/35 § D4], and what is silent when it is wrong.
/// </summary>
/// <remarks>
///     <para>
///         The no-crack and morph-continuity properties belong to <c>WaterSurfaceMeshTests</c>, where
///         they are arithmetic. What is here is the seam between that arithmetic and the bytes: the
///         record's stride, and whether the world position the host computes is the one the vertex
///         stage will reach from the record it was handed.
///     </para>
///     <para>
///         ⚠ <b>Both failures are invisible in the way that costs a week.</b> A stride that disagrees
///         does not fault — it draws patch one out of the middle of patch zero. A placement that
///         disagrees draws water in the wrong place, which reads as a broken quadtree.
///     </para>
/// </remarks>
public sealed class WaterSurfacePassTests {
    static WaterFieldDescription Window(int resolution = 129, float extent = 256f) =>
        new() { Origin = new(1024f, -512f), Extent = extent, Resolution = resolution };

    static WaterSurfaceMesh Mesh(int gridQuads = 8) =>
        new(Window(), TerrainLodRanges.Default with { NearRange = 32f }, gridQuads);

    /// <summary>The record is sixteen bytes, which is what <c>std430</c> reads it at.</summary>
    /// <remarks>
    ///     ⚠ A <c>float2</c> aligns to eight, so the struct aligns to eight and rounds up to a
    ///     multiple of it: two floats after it land at 8 and 12 and the total is 16 exactly. A fifth
    ///     field would be 20 here and 24 there, and the mismatch is a silently wrong picture.
    /// </remarks>
    [Fact]
    public void The_node_record_is_the_stride_the_shader_reads() {
        Assert.Equal(16, WaterNodeRecord.SizeInBytes);
        Assert.Equal(16, Marshal.SizeOf<WaterNodeRecord>());
    }

    /// <summary>
    ///     Where the vertex stage lands from the record is where the host says the vertex is.
    /// </summary>
    /// <remarks>
    ///     The whole of what a record has to get right. The shader computes
    ///     <c>origin + morphedIndex * step</c>; <see cref="WaterSurfaceMesh.GroundOf" /> is the C#
    ///     form of the same expression, and this holds the two together over every vertex of a patch
    ///     at three morphs — including the one that matters, a fully morphed patch, where the odd
    ///     indices have slid onto their even neighbours.
    /// </remarks>
    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    [InlineData(1f)]
    public void The_record_places_a_patch_where_the_host_says_it_is(float morph) {
        var mesh = Mesh();
        var node = new TerrainLodNode(16, 32, 16, 1, morph);
        var record = WaterNodeRecord.Of(node, mesh.Window.Origin, mesh.MetresPerQuad, mesh.GridQuads);

        for (var gridZ = 0; gridZ <= mesh.GridQuads; gridZ++) {
            for (var gridX = 0; gridX <= mesh.GridQuads; gridX++) {
                // The shader's two lines, transliterated.
                var morphedX = gridX - ((gridX % 2) * morph);
                var morphedZ = gridZ - ((gridZ % 2) * morph);

                var shader = record.Origin + (new Vector2(morphedX, morphedZ) * record.Step);
                var host = mesh.GroundOf(node, gridX, gridZ);

                Assert.Equal(host.X, shader.X, 4);
                Assert.Equal(host.Y, shader.Y, 4);
            }
        }
    }

    /// <summary>The record's origin is world metres, not texels — the one place it differs.</summary>
    [Fact]
    public void The_records_origin_is_world_metres() {
        var mesh = Mesh();
        var record = WaterNodeRecord.Of(new(8, 0, 8, 0, 0f), mesh.Window.Origin, mesh.MetresPerQuad, mesh.GridQuads);

        Assert.Equal(1024f + (8f * mesh.MetresPerQuad), record.Origin.X, 4);
        Assert.Equal(-512f, record.Origin.Y, 4);
    }

    /// <summary>
    ///     Depth is tested and never written, which is what makes § D8's pass possible at all.
    /// </summary>
    /// <remarks>
    ///     ⚠ A surface that wrote depth would put itself where the composite looks for what is
    ///     <em>behind</em> the water, and the water would be integrated against itself — clear
    ///     everywhere, at every depth, with nothing in the capture to say why. Asserted here rather
    ///     than trusted at the pipeline because a state is a fact a test can hold without a device
    ///     that renders.
    /// </remarks>
    [Fact]
    public void The_surface_tests_depth_and_does_not_write_it() {
        Assert.False(WaterSurfacePass.DepthState.DepthWrite);
        Assert.True(WaterSurfacePass.DepthState.DepthTest);

        // Reverse-Z: near is 1, far is 0, and a surface wins by being nearer.
        Assert.Equal(CompareFunction.Greater, WaterSurfacePass.DepthState.DepthCompare);

        // And the winding the shared lattice is generated for.
        Assert.Equal(CullMode.Back, WaterSurfacePass.Raster.Cull);
    }

    /// <summary>A node with nothing wired up costs a frame with no water, not an exception.</summary>
    /// <remarks>
    ///     <c>!ScreenProbeGather</c>'s terms, and <c>!Water</c>'s: a document may name a node in a
    ///     host that has not supplied a device, a zone system or a view, and the frame that results
    ///     should be one without water rather than one that does not render.
    /// </remarks>
    [Fact]
    public void An_unwired_node_draws_nothing_and_does_not_throw() {
        using var node = new WaterMeshRenderer {
            Name = "WaterSurface",
            Surface = "WaterSurface",
            Normal = "WaterNormal",
            SceneDepth = "SceneDepth"
        };

        Assert.Equal(0, node.ZonesDrawn);
        Assert.Equal(0, node.PatchesDrawn);
        Assert.Equal(0, node.DroppedPatches);
    }
}
