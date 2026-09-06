// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Yaml.Meta;
using Vixen.Editor.Assets.Content;
using Vixen.Editor.Assets.Models;
using Vixen.Editor.Texturing.Layers;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>
///     A stack resolves the geometry the <i>project</i> has, not the geometry the file carries —
///     <a href="https://github.com/Rikarin/Vixen/issues/934">#934</a>.
/// </summary>
/// <remarks>
///     <para>
///         <b>Both fixtures are files an import changes, and that is what makes them oracles.</b> The
///         first has no texture coordinates at all, so any atlas that comes back was <em>generated</em>
///         — nothing in the bytes on disk could have produced one, and the source-file resolve this
///         replaces returns a refusal for it. The second has a mesh whose name the project renames, so
///         the set matches on a name that appears nowhere in the file.
///     </para>
///     <para>
///         ⚠ <b>A real import through <c>ContentPipeline</c> and not a hand-built artefact.</b> What is
///         under test is a join — the sub-asset names the sidecar carries, the chunk ids the cache
///         carries, and the reader that puts one into the other — and every one of those is an
///         agreement between two files that a fixture writing the answer down would assume rather than
///         check.
///     </para>
///     <para>
///         ⚠ <b>Every quad here spans a distinct rectangle in <em>both</em> axes.</b> A fixture whose
///         islands all span the whole of <c>v</c> cannot tell a vertically flipped atlas from a correct
///         one, which is the trap the suite next door fell into — so the narrowing assertion below is
///         disjointness, which holds whichever way up the atlas is and fails outright for a resolve
///         that ignored the narrowing.
///     </para>
/// </remarks>
public class LayerStackImportedMeshTests {
    /// <summary>A cube with no texture coordinates whatever, so an atlas can only have been generated.</summary>
    const string Cube = """
        o Cube
        v -0.5 -0.5 -0.5
        v 0.5 -0.5 -0.5
        v 0.5 0.5 -0.5
        v -0.5 0.5 -0.5
        v -0.5 -0.5 0.5
        v 0.5 -0.5 0.5
        v 0.5 0.5 0.5
        v -0.5 0.5 0.5
        f 5 6 7
        f 5 7 8
        f 1 4 3
        f 1 3 2
        f 2 3 7
        f 2 7 6
        f 1 5 8
        f 1 8 4
        f 4 8 7
        f 4 7 3
        f 1 2 6
        f 1 6 5

        """;

    /// <summary>
    ///     An import-time unwrap is what the stack resolves to, and before the import the same call is
    ///     refused.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The two halves are one test on purpose, because the difference is the whole claim.</b>
    ///     Asserting only that a bound cube resolves would be green against a resolver that read the
    ///     file — and this file has no <c>vt</c> line in it, so the first half is what proves the
    ///     second half's coordinates came out of <c>Library/</c>. It is also the differential this
    ///     change is: the refusal is exactly what the old resolve returned for a model whose atlas the
    ///     project had generated hours earlier.
    /// </remarks>
    [Fact]
    public async Task An_import_time_unwrap_is_what_a_bound_stack_resolves() {
        using var fixture = new TexturingFixture();
        var stack = Add(fixture, "Hull.obj", Cube, new() { Unwrap = UnwrapMode.Always, UnwrapResolution = 256 });
        var workspace = new ProjectWorkspace(fixture.Paths);
        var geometry = new ProjectMeshSource(workspace);

        // Before the import there is nothing in `Library/` to read, so this is the source file — and
        // the source file is a cube with no atlas.
        Assert.Null(LayerStackMesh.Open(fixture.Project, stack, stack.Sets[0], geometry, out var before));
        Assert.Contains("no texture coordinates", before, StringComparison.Ordinal);
        Assert.Contains("never imported", before, StringComparison.Ordinal);

        await Import(fixture, workspace);

        var mesh = LayerStackMesh.Open(fixture.Project, stack, stack.Sets[0], geometry, out var after);

        Assert.Equal("", after);
        Assert.NotNull(mesh);

        // A cube is twelve triangles and the unwrap does not add or remove any: it assigns the
        // coordinates the file has none of.
        Assert.Equal(12, mesh.Triangles);
        Assert.Equal(36, mesh.Coordinates.Count);

        var coverage = mesh.Coverage(64, 64);

        // ⚠ Both bounds, and the upper one is the half that can fail silently. A generated atlas
        // packs charts with a margin, so a map covering every texel of the square would mean the
        // coordinates had degenerated — which is exactly what `PaintCoverage.Everywhere` looks like,
        // and what the pane showed before a stack knew its mesh at all.
        Assert.InRange(coverage.CoveredTexels, 1, (64 * 64) - 1);
    }

    /// <summary>
    ///     A set naming a mesh matches the name the project gave it, not the one the file did.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Nothing in this OBJ is called <c>Body</c>.</b> The name comes from
    ///     <c>ModelImportSettings.SubAssetNames</c>, applied by <c>ImportContext.DeclareSubAsset</c>
    ///     and written into the sidecar — so a resolve that read the file would refuse this stack with
    ///     a list of names the artist does not recognise, which is #934's second bullet in one
    ///     sentence.
    /// </remarks>
    [Fact]
    public async Task A_set_naming_a_renamed_mesh_gets_that_meshs_islands_and_no_others() {
        using var fixture = new TexturingFixture();

        var stack = Add(
            fixture,
            "Panels.obj",
            Quad("lower", 0, 0f, 0.4f, 0f, 0.4f) + Quad("upper", 4, 0.6f, 1f, 0.6f, 1f),
            new() { SubAssetNames = [new() { Source = "lower", Name = "Body" }] }
        );

        var workspace = new ProjectWorkspace(fixture.Paths);
        var geometry = new ProjectMeshSource(workspace);

        await Import(fixture, workspace);

        var body = Narrowed(fixture, stack, "Body", geometry);
        var upper = Narrowed(fixture, stack, "upper", geometry);

        var mine = body.Coverage(64, 64);
        var theirs = upper.Coverage(64, 64);
        var both = 0;

        for (var y = 0; y < 64; y++) {
            for (var x = 0; x < 64; x++) {
                if (mine.IsCovered(x, y) && theirs.IsCovered(x, y)) {
                    both++;
                }
            }
        }

        // ⚠ Disjointness rather than "these texels are covered", because it is the assertion a
        // resolve that ignored the narrowing fails. Handing back both meshes would cover the union
        // twice over; this counts the overlap and the two quads share no part of the unit square.
        Assert.Equal(0, both);
        Assert.NotEqual(0, mine.CoveredTexels);
        Assert.NotEqual(0, theirs.CoveredTexels);

        // ⚠ After the texels and not before them, because this is the assertion that can pass while
        // the resolve is wrong in the other direction — a name is a string and an island is a place.
        Assert.Equal("Body", body.Named);
        Assert.Equal(2, body.Triangles);
        Assert.Equal(2, upper.Triangles);

        // And the name the file gives that mesh no longer resolves, which is the other half of a
        // rename: two names for one thing would be a project with two answers.
        var byFile = With(stack, "lower");

        Assert.Null(LayerStackMesh.Open(fixture.Project, byFile, byFile.Sets[0], geometry, out var gone));
        Assert.Contains("'Body'", gone, StringComparison.Ordinal);
        Assert.Contains("'upper'", gone, StringComparison.Ordinal);
    }

    /// <summary>Resolves the stack narrowed to one mesh, and fails the test if it will not.</summary>
    static LayerStackMesh Narrowed(
        TexturingFixture fixture,
        LayerStackAsset stack,
        string mesh,
        ProjectMeshSource geometry
    ) {
        var narrowed = With(stack, mesh);
        var opened = LayerStackMesh.Open(fixture.Project, narrowed, narrowed.Sets[0], geometry, out var refusal);

        Assert.Equal("", refusal);
        Assert.NotNull(opened);

        return opened;
    }

    /// <summary>The same stack with its first set narrowed to one mesh.</summary>
    static LayerStackAsset With(LayerStackAsset stack, string mesh) =>
        stack with { Sets = [stack.Sets[0] with { Mesh = mesh }] };

    /// <summary>Runs a real import over the fixture's project and rescans what it wrote back.</summary>
    /// <remarks>
    ///     ⚠ <b>The second scan is not tidiness.</b> An import rewrites each sidecar with the
    ///     sub-assets it declared, and the fixture's own database was read before that happened — so a
    ///     resolve against it would be reading the sidecar this test is about to assert on through an
    ///     index that predates it.
    /// </remarks>
    static async Task Import(TexturingFixture fixture, ProjectWorkspace workspace) {
        List<ContentDiagnostic> said = [];

        var summary = await ContentPipeline.ImportAsync(
            workspace,
            ProjectWorkspace.HostTarget,
            said.Add,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(
            summary.Failed == 0,
            "the import failed: " + string.Join("; ", said.Select(diagnostic => diagnostic.Message))
        );

        fixture.Project.Assets.Scan();
    }

    /// <summary>Writes a model and the import settings it is to be imported with, and binds a stack to it.</summary>
    static LayerStackAsset Add(TexturingFixture fixture, string file, string contents, ModelImportSettings settings) {
        var relative = "Assets/" + file;
        var absolute = fixture.Paths.Absolute(relative);

        File.WriteAllText(absolute, contents);
        fixture.Project.Assets.Scan();

        Assert.True(fixture.Project.Assets.TryGetByPath(relative, out _), "the scan missed " + file);

        // ⚠ Read back and rewritten rather than written from scratch, so the GUID the scan minted
        // survives. A sidecar with a fresh GUID is a second asset as far as every index is concerned.
        var sidecar = AssetMetaFile.PathFor(absolute);

        AssetMetaFile.WriteFile(sidecar, AssetMetaFile.ReadFile(sidecar) with { Importer = settings });

        return LayerStackDocument.Starter("Hull") with { Model = relative };
    }

    /// <summary>A named quad occupying a rectangle of the atlas in both axes.</summary>
    /// <param name="name">What the object is called in the file.</param>
    /// <param name="offset">How many vertices precede it, because OBJ indices are file-wide.</param>
    /// <param name="left">Its first <c>u</c>.</param>
    /// <param name="right">Its last <c>u</c>.</param>
    /// <param name="bottom">Its first <c>v</c>.</param>
    /// <param name="top">Its last <c>v</c>.</param>
    /// <returns>The OBJ text.</returns>
    static string Quad(string name, int offset, float left, float right, float bottom, float top) =>
        $"o {name}\n"
        + $"v {left} {bottom} 0\nv {right} {bottom} 0\nv {right} {top} 0\nv {left} {top} 0\n"
        + $"vt {left} {bottom}\nvt {right} {bottom}\nvt {right} {top}\nvt {left} {top}\n"
        + $"f {offset + 1}/{offset + 1} {offset + 2}/{offset + 2} {offset + 3}/{offset + 3}\n"
        + $"f {offset + 1}/{offset + 1} {offset + 3}/{offset + 3} {offset + 4}/{offset + 4}\n";
}
