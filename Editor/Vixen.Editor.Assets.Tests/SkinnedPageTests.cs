// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.Assets.Models;
using Vixen.Rendering;
using Vixen.Rendering.VirtualGeometry;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

/// <summary>
///     What a skinned mesh's pages carry, and what a static one still does not.
/// </summary>
/// <remarks>
///     <para>
///         The build half of skinning through the virtualized path. What ships is the vertex layout, so
///         this is the file that decides it: a skinned page vertex is twenty-four bytes and a static one
///         is still sixteen, which is the difference between a format that charges every mesh for a
///         skeleton and one that charges the meshes that have one.
///     </para>
///     <para>
///         Driven through <see cref="ModelCompiler.CompilePages" /> rather than through an import,
///         because the source formats the importer reads that carry skin weights are the ones this
///         repository has no fixture for — and what is being checked is the packing, which is here.
///     </para>
/// </remarks>
public sealed class SkinnedPageTests {
    /// <summary>What a byte weight promises: half a 255th, which only rounding achieves.</summary>
    const float Tolerance = (0.5f / 255f) + 1e-6f;

    /// <summary>
    ///     A skinned mesh's page vertex carries its four influences, and they decode to what went in.
    /// </summary>
    /// <remarks>
    ///     The whole round trip through the shipping layout: a mesh with weights in, a page set out, and
    ///     the influences read back through the decoder the format documents. A weight comes back to a
    ///     255th, which is what a byte promises and is finer than the position it is applied to.
    /// </remarks>
    [Fact]
    public void A_skinned_mesh_ships_its_influences() {
        var mesh = Bar(48);
        var hierarchy = ModelCompiler.CompileMeshlets(mesh, new(), Report);
        Assert.NotNull(hierarchy);

        var pages = ModelCompiler.CompilePages(mesh, hierarchy, Report);
        Assert.NotNull(pages);

        Assert.True(pages.IsSkinned);
        Assert.Equal(ModelCompiler.PageInfluenceOffset, pages.InfluenceOffset);
        Assert.Equal(MeshletPageBuilder.PositionSize + ModelCompiler.SkinnedPageAttributeStride, pages.VertexStride);
        Assert.Equal(24, pages.VertexStride);

        var influences = new VertexInfluence[256];
        var compared = 0;

        for (var cluster = 0; cluster < hierarchy.Meshlets.Length; cluster++) {
            var count = hierarchy.Meshlets[cluster].VertexCount;
            pages.GetInfluences(cluster, count, influences);

            for (var i = 0; i < count; i++) {
                var source = hierarchy.Vertices[hierarchy.Meshlets[cluster].VertexOffset + i];

                Assert.Equal(mesh.BoneIndices[source * 4], influences[i].Bones.X);
                Assert.Equal(mesh.BoneIndices[(source * 4) + 1], influences[i].Bones.Y);
                // Half a 255th, not a whole one — which is the assertion that rounding is rounding.
                // Truncation is also within a 255th and is wrong in the same direction every time, so it
                // loses up to four of them from every vertex's total; the shader renormalises, turning
                // that into a uniform deflation toward the skeleton — a scale bug with no scale in it.
                Assert.Equal(mesh.BoneWeights[source * 4], influences[i].Weights.X, Tolerance);
                Assert.Equal(mesh.BoneWeights[(source * 4) + 1], influences[i].Weights.Y, Tolerance);

                compared++;
            }
        }

        Assert.True(compared > 80, $"Only {compared} vertices were compared, which is not a mesh.");
    }

    /// <summary>
    ///     The same mesh without a skeleton still ships a sixteen-byte vertex.
    /// </summary>
    /// <remarks>
    ///     The reason the offset is per mesh. Eight bytes of zeros per vertex is half again the size of
    ///     every page of every static mesh in a project, paid to describe a skeleton none of them has.
    /// </remarks>
    [Fact]
    public void A_static_mesh_still_ships_sixteen_bytes_a_vertex() {
        var mesh = Bar(48);
        mesh.BoneIndices = [];
        mesh.BoneWeights = [];

        var hierarchy = ModelCompiler.CompileMeshlets(mesh, new(), Report);
        Assert.NotNull(hierarchy);

        var pages = ModelCompiler.CompilePages(mesh, hierarchy, Report);
        Assert.NotNull(pages);

        Assert.False(pages.IsSkinned);
        Assert.Equal(-1, pages.InfluenceOffset);
        Assert.Equal(16, pages.VertexStride);
    }

    /// <summary>
    ///     A skeleton too large for a byte is a build error, not a mesh drawn by the wrong bones.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A page vertex stores a bone index in one byte, so a mesh weighted to bone 300 cannot be
    ///         paged. Clamping it would draw that vertex by bone 255 — a limb attached to the wrong joint
    ///         on one character, which is a modelling bug as far as anyone looking at it can tell.
    ///     </para>
    ///     <para>
    ///         The mesh keeps its hierarchy and loses its pages, which is the same degradation an
    ///         unpageable cluster gets: it draws through the classic path and the build says why.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_skeleton_past_the_palette_is_refused() {
        var mesh = Bar(48);
        mesh.BoneIndices[4] = MeshletPageBuilder.MaxBones;

        var hierarchy = ModelCompiler.CompileMeshlets(mesh, new(), Report);
        Assert.NotNull(hierarchy);

        var problems = new List<string>();
        var pages = ModelCompiler.CompilePages(mesh, hierarchy, (severity, message) => {
            if (severity == ImportSeverity.Error) {
                problems.Add(message);
            }
        });

        Assert.Null(pages);
        Assert.Contains(problems, message => message.Contains("256 bones", StringComparison.Ordinal));
    }

    static void Report(ImportSeverity severity, string message) =>
        Assert.True(severity != ImportSeverity.Error, message);

    /// <summary>A strip of quads weighted from one bone to another along its length.</summary>
    static MeshData Bar(int segments) {
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var texCoords = new List<Vector2>();
        var indices = new List<int>();
        var bones = new List<int>();
        var weights = new List<float>();

        for (var i = 0; i <= segments; i++) {
            var t = (float)i / segments;

            for (var side = 0; side < 2; side++) {
                positions.Add(new(t * 4f, side == 0 ? -0.5f : 0.5f, 0f));
                normals.Add(new(0f, 0f, 1f));
                texCoords.Add(new(t, side));

                bones.AddRange([0, 1, 0, 0]);
                weights.AddRange([1f - t, t, 0f, 0f]);
            }
        }

        for (var i = 0; i < segments; i++) {
            var a = i * 2;

            indices.AddRange([a, a + 1, a + 2]);
            indices.AddRange([a + 1, a + 3, a + 2]);
        }

        return new() {
            Name = "Bar",
            Positions = [.. positions],
            Normals = [.. normals],
            TexCoords = [.. texCoords],
            Indices = [.. indices],
            BoneIndices = [.. bones],
            BoneWeights = [.. weights]
        };
    }
}
