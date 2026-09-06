// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Texturing.Layers;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>
///     A layer stack that knows what it is painted on — <a
///     href="https://github.com/Rikarin/Vixen/issues/920">#920</a>.
/// </summary>
/// <remarks>
///     <para>
///         <b>What is asserted is the coverage map and not the binding.</b> A suite that checked the
///         stack had a model path and that a resolver returned something non-null would be green
///         against a resolver that read the wrong mesh, dropped every triangle, or rasterised the
///         atlas upside down — so every test here ends at a texel: which ones an island covers, and
///         which ones it does not.
///     </para>
///     <para>
///         ⚠ <b>The third of those took a fixture the first two did not.</b> Every quad below spans
///         the whole of <c>v</c> except one, and against the others alone a map flipped in <c>v</c>
///         is pixel-identical to a correct one — the remark above claimed a coverage the suite did
///         not have until <a href="https://github.com/Rikarin/Vixen/issues/955">#955</a>.
///         <c>A_quad_in_the_top_half_of_v_covers_the_top_rows_and_not_the_bottom</c> is the one that
///         settles it.
///     </para>
///     <para>
///         ⚠ <b>The models are OBJ text written by the test, parsed by the same
///         <c>ModelReader.Read</c> the importer uses.</b> A hand-built <c>MeshData</c> would prove the
///         arithmetic and not the join, and the join is where #920 lived: nothing anywhere turned a
///         project asset into UV triangles. The one thing worth knowing about the parse is that
///         <c>ModelReader</c> asks Assimp for <c>FlipUVs</c>, so an OBJ's <c>vt 0 0</c> arrives as
///         <c>v = 1</c> — the row a coverage map and the 2D view both count from the top.
///     </para>
/// </remarks>
public class LayerStackMeshTests {
    /// <summary>A stack with no model says so, and says it in a sentence an artist can act on.</summary>
    /// <remarks>
    ///     ⚠ <b>The refusal is the ordinary state of every stack the moment it is created</b>, which
    ///     is why it is a sentence rather than an exception and why it names the panel: the whole of
    ///     #920 is that a <c>.vxlayers</c> arrives naming no geometry at all.
    /// </remarks>
    [Fact]
    public void A_stack_that_names_no_model_is_refused_by_a_sentence_naming_the_panel() {
        using var fixture = new TexturingFixture();
        var stack = LayerStackDocument.Starter("Hull");

        Assert.Equal("", stack.Model);
        Assert.Null(LayerStackMesh.Open(fixture.Project, stack, stack.Sets[0], out var refusal));
        Assert.Contains("names no model", refusal, StringComparison.Ordinal);
        Assert.Contains("Layer Stack panel", refusal, StringComparison.Ordinal);
    }

    /// <summary>A bound model's islands are the texels the coverage map covers, and only those.</summary>
    /// <remarks>
    ///     ⚠ <b>Both halves, because only one of them can fail silently.</b> A resolver that covered
    ///     everything would satisfy "the island's texels are covered" perfectly — and that is
    ///     precisely the state #920 reports, since <c>PaintCoverage.Everywhere</c> is what the paint
    ///     pane supplied. So the assertion that matters is the second: the half of the atlas no
    ///     triangle reaches must be uncovered.
    /// </remarks>
    [Fact]
    public void A_bound_models_islands_cover_their_own_texels_and_no_others() {
        using var fixture = new TexturingFixture();
        var stack = Bound(fixture, Quad("hull", 0f, 0.5f), "Hull.obj");

        var mesh = LayerStackMesh.Open(fixture.Project, stack, stack.Sets[0], out var refusal);

        Assert.Equal("", refusal);
        Assert.NotNull(mesh);
        Assert.Equal(2, mesh.Triangles);
        Assert.Equal(6, mesh.Coordinates.Count);

        var coverage = mesh.Coverage(64, 64);

        // The quad spans u ∈ [0, 0.5] and the whole of v, so the left half is surface and the right
        // half is the gap between islands the seam dilation exists for.
        Assert.True(coverage.IsCovered(8, 32), "the middle of the island is not covered.");
        Assert.False(coverage.IsCovered(56, 32), "a texel no triangle reaches is covered.");

        // ⚠ Conservative rasterisation makes this a bound rather than an equality — a texel a
        // triangle clips the corner of is marked, which is `PaintCoverage`'s own stated direction.
        // Half of 4096 with one boundary column of slop is the window a correct map lands in.
        Assert.InRange(coverage.CoveredTexels, 64 * 32, 64 * 33);
    }

    /// <summary>A quad in the top half of the file's <c>v</c> covers the top rows and not the bottom.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The one claim every other fixture in this file is blind to.</b> Every other quad
    ///         here spans the whole of <c>v</c>, so a coverage map rasterised upside down is
    ///         indistinguishable from a correct one and the suite's own remark about ruling that out
    ///         was three quarters true — <a href="https://github.com/Rikarin/Vixen/issues/955">#955</a>.
    ///         It is a live hazard rather than a tidy-up because this engine's UV convention is not
    ///         uniform: clip <c>y</c> = +1 is the top, the screen helpers negate <c>y</c>, and the
    ///         cluster grid deliberately does not.
    ///     </para>
    ///     <para>
    ///         <b>The chain has two links and they point opposite ways, which is why the whole of it
    ///         has to be asserted rather than either end.</b> An OBJ's <c>v</c> counts up from the
    ///         bottom, <c>ModelReader</c> asks Assimp for <c>FlipUVs</c>, and a coverage row counts
    ///         down from the top — so <c>vt … 1</c> is row 0 and this quad is the atlas's top half.
    ///         Invert either link on its own and the covered rows are the other half.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_quad_in_the_top_half_of_v_covers_the_top_rows_and_not_the_bottom() {
        using var fixture = new TexturingFixture();
        var stack = Bound(fixture, Quad("hull", 0f, 1f, 0.5f, 1f), "Band.obj");

        var mesh = LayerStackMesh.Open(fixture.Project, stack, stack.Sets[0], out var refusal);

        Assert.Equal("", refusal);
        Assert.NotNull(mesh);

        var coverage = mesh.Coverage(64, 64);

        // The extremes first: an upside-down map has these two exactly the wrong way round.
        Assert.True(coverage.IsCovered(32, 0), "the atlas's first row is not covered.");
        Assert.False(coverage.IsCovered(32, 63), "the atlas's last row is covered.");

        // And well inside each half, so the claim does not rest on the boundary row conservative
        // rasterisation is allowed to over-mark.
        Assert.True(coverage.IsCovered(32, 8), "a row the island occupies is not covered.");
        Assert.False(coverage.IsCovered(32, 56), "a row no triangle reaches is covered.");

        // ⚠ The count is what stops "covered" and "not covered" both being satisfied by a map that
        // covers everything: half the atlas, plus the one boundary row of slop `FromTriangles` is
        // conservative by. It is the same window the u-narrowed fixture above lands in, transposed.
        Assert.InRange(coverage.CoveredTexels, 64 * 32, 64 * 33);
    }

    /// <summary>A set that names one mesh gets that mesh's islands and not the model's.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the test that makes <c>TextureSetAsset.Mesh</c> load-bearing rather than
    ///     decoration.</b> Two texture sets are two material slots, and a model file splits into one
    ///     mesh per slot — so a stack that resolved the whole model would let the brush paint the
    ///     <c>Body</c> set anywhere the <c>Head</c> set has surface, which is a coverage map that is
    ///     wrong exactly where an artist would use it. The right half is <em>another set's</em>
    ///     island here, not empty atlas, which is why covering it would look plausible.
    /// </remarks>
    [Fact]
    public void A_set_naming_one_mesh_gets_that_meshs_islands_alone() {
        using var fixture = new TexturingFixture();
        var stack = Bound(fixture, Quad("left", 0f, 0.5f) + Quad("right", 0.5f, 1f), "Split.obj");

        var whole = LayerStackMesh.Open(fixture.Project, stack, stack.Sets[0], out _);

        Assert.NotNull(whole);
        Assert.Equal(4, whole.Triangles);
        Assert.True(whole.Coverage(64, 64).IsCovered(56, 32), "the unnarrowed stack lost the right mesh.");

        var narrowed = stack with {
            Sets = [stack.Sets[0] with { Mesh = "left" }]
        };

        var left = LayerStackMesh.Open(fixture.Project, narrowed, narrowed.Sets[0], out var refusal);

        Assert.Equal("", refusal);
        Assert.NotNull(left);
        Assert.Equal(2, left.Triangles);

        var coverage = left.Coverage(64, 64);

        Assert.True(coverage.IsCovered(8, 32), "the named mesh's own island is not covered.");
        Assert.False(coverage.IsCovered(56, 32), "the other set's island is covered by this one's map.");
    }

    /// <summary>A set naming a mesh the model has not got is refused, with the names it does have.</summary>
    [Fact]
    public void A_set_naming_a_mesh_the_model_lacks_is_refused_by_name() {
        using var fixture = new TexturingFixture();
        var stack = Bound(fixture, Quad("left", 0f, 0.5f) + Quad("right", 0.5f, 1f), "Split.obj");

        var narrowed = stack with {
            Sets = [stack.Sets[0] with { Mesh = "torso" }]
        };

        Assert.Null(LayerStackMesh.Open(fixture.Project, narrowed, narrowed.Sets[0], out var refusal));
        Assert.Contains("'torso'", refusal, StringComparison.Ordinal);
        Assert.Contains("'left'", refusal, StringComparison.Ordinal);
        Assert.Contains("'right'", refusal, StringComparison.Ordinal);
    }

    /// <summary>A mesh with no atlas is refused rather than resolved to a map covering nothing.</summary>
    /// <remarks>
    ///     ⚠ <b>The two are indistinguishable from the pane and are opposite states.</b> A coverage
    ///     map with no covered texel refuses every stamp, so an unwrapped model bound to a stack
    ///     would read as a brush that has stopped working — and the artist would look at the brush.
    /// </remarks>
    [Fact]
    public void A_model_with_no_texture_coordinates_is_refused_rather_than_covering_nothing() {
        using var fixture = new TexturingFixture();
        var stack = Bound(
            fixture,
            "o bare\nv 0 0 0\nv 1 0 0\nv 1 1 0\nf 1 2 3\n",
            "Bare.obj"
        );

        Assert.Null(LayerStackMesh.Open(fixture.Project, stack, stack.Sets[0], out var refusal));
        Assert.Contains("no texture coordinates", refusal, StringComparison.Ordinal);
    }

    /// <summary>A path that is not a model at all is refused before anything tries to parse it.</summary>
    /// <remarks>
    ///     The sentence names which files are models, because the reference is a path an artist typed
    ///     or dropped and "Assimp could not determine the format" is a message about a library.
    /// </remarks>
    [Fact]
    public void A_reference_that_is_not_a_model_is_refused_with_the_formats_that_are() {
        using var fixture = new TexturingFixture();
        var stack = LayerStackDocument.Starter("Hull") with { Model = "Assets/Rust.png" };

        Add(fixture, "Rust.png", "not a picture either");

        Assert.Null(LayerStackMesh.Open(fixture.Project, stack, stack.Sets[0], out var refusal));
        Assert.Contains("is not a model this build reads", refusal, StringComparison.Ordinal);
        Assert.Contains(".obj", refusal, StringComparison.Ordinal);
    }

    /// <summary>A reference to nothing at all is a sentence about the project, not a crash.</summary>
    [Fact]
    public void A_reference_to_a_file_that_is_not_in_the_project_is_a_sentence() {
        using var fixture = new TexturingFixture();
        var stack = LayerStackDocument.Starter("Hull") with { Model = "Assets/Gone.obj" };

        Assert.Null(LayerStackMesh.Open(fixture.Project, stack, stack.Sets[0], out var refusal));
        Assert.Contains("is not in this project's assets", refusal, StringComparison.Ordinal);
    }

    /// <summary>The binding survives the file, on the stack and on the set both.</summary>
    /// <remarks>
    ///     ⚠ <b>And is written only when it is set.</b> A <c>.vxlayers</c> is a file people merge, so
    ///     a <c>model:</c> under every stack that has never seen a mesh is a key nobody chose — the
    ///     same rule every other member of this format follows.
    /// </remarks>
    [Fact]
    public void The_binding_survives_the_file_and_is_absent_when_it_is_not_set() {
        var stack = LayerStackDocument.Starter("Hull") with { Model = "Assets/Hull.obj" };

        stack = stack with { Sets = [stack.Sets[0] with { Mesh = "body" }] };

        var text = LayerStackYaml.Write(stack);
        var read = LayerStackYaml.Read(text);

        Assert.Equal("Assets/Hull.obj", read.Model);
        Assert.Equal("body", read.Sets[0].Mesh);

        var bare = LayerStackYaml.Write(LayerStackDocument.Starter("Hull"));

        Assert.DoesNotContain("model:", bare, StringComparison.Ordinal);
        Assert.DoesNotContain("mesh:", bare, StringComparison.Ordinal);
        Assert.Equal("", LayerStackYaml.Read(bare).Model);
    }

    /// <summary>A stack bound to a model written into the fixture's project.</summary>
    static LayerStackAsset Bound(TexturingFixture fixture, string obj, string file) {
        Add(fixture, file, obj);

        return LayerStackDocument.Starter("Hull") with { Model = "Assets/" + file };
    }

    /// <summary>Writes a file into the project and scans it in, as <c>AddGraph</c> does.</summary>
    static AssetId Add(TexturingFixture fixture, string file, string contents) {
        var relative = "Assets/" + file;

        File.WriteAllText(fixture.Paths.Absolute(relative), contents);
        fixture.Project.Assets.Scan();

        Assert.True(fixture.Project.Assets.TryGetByPath(relative, out var entry), "the scan missed " + file);

        return entry.Guid;
    }

    /// <summary>
    ///     A named quad spanning <paramref name="from" />…<paramref name="to" /> in <c>u</c> and
    ///     <paramref name="low" />…<paramref name="high" /> in the file's own <c>v</c>.
    /// </summary>
    /// <param name="name">What the object is called, which is the mesh name a set narrows to.</param>
    /// <param name="from">Where the island starts in <c>u</c>.</param>
    /// <param name="to">Where it ends.</param>
    /// <param name="low">Where it starts in the OBJ's <c>v</c>, which counts up from the bottom.</param>
    /// <param name="high">Where it ends. ⚠ <c>1</c> is the atlas's <em>first</em> row, not its last.</param>
    /// <returns>The OBJ text.</returns>
    /// <remarks>
    ///     ⚠ <b>OBJ indices are one-based and <em>file-wide</em>, so two quads in one file do not
    ///     both start at 1.</b> The offset is what makes a two-object fixture describe two islands
    ///     rather than one object and one degenerate triangle, and getting it wrong is a fixture that
    ///     silently proves less than it says.
    /// </remarks>
    static string Quad(string name, float from, float to, float low = 0f, float high = 1f) {
        var index = name == "right" ? 4 : 0;

        return $"o {name}\n"
            + $"v {from} 0 0\nv {to} 0 0\nv {to} 1 0\nv {from} 1 0\n"
            + $"vt {from} {low}\nvt {to} {low}\nvt {to} {high}\nvt {from} {high}\n"
            + $"f {index + 1}/{index + 1} {index + 2}/{index + 2} {index + 3}/{index + 3}\n"
            + $"f {index + 1}/{index + 1} {index + 3}/{index + 3} {index + 4}/{index + 4}\n";
    }
}
