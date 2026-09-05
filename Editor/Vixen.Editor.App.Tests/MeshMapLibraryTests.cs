// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Core.Yaml.Meta;
using Vixen.Editor.Assets.MeshMaps;
using Vixen.Editor.Core;
using Vixen.Geometry;
using Vixen.Geometry.Remeshing;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>
///     Doc 48 § 4.8's binding, both halves in one process: what a bake writes and what reads it back.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The round trip is the instrument, and a test that wrote its own sidecars would not
///         be one.</b> <a href="https://github.com/Rikarin/Vixen/issues/702">#702</a> is not "there
///         is no reader"; it is that <see cref="MeshMapNaming.UsageKey" /> was <em>written</em> by
///         <see cref="ProjectMeshMapBaker" /> and read by nothing — so the claim under test is that
///         the two agree, and a fixture that minted the sidecars itself would only prove that this
///         file agrees with itself. Every test here bakes through the real baker and then asks
///         <see cref="MeshMapLibrary" /> for a map by usage.
///     </para>
///     <para>
///         ⚠ <b>This is also where the writer's refusal is proved</b> —
///         <a href="https://github.com/Rikarin/Vixen/issues/731">#731</a>, which is
///         <a href="https://github.com/Rikarin/Vixen/issues/724">#724</a>'s shape in another
///         assembly. It belongs beside the reader rather than beside the other write tests, because
///         what makes the null dangerous is precisely that something now resolves through
///         <see cref="MeshMapSet.Maps" />.
///     </para>
/// </remarks>
public sealed class MeshMapLibraryTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-meshmaplib-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() {
        try {
            if (Directory.Exists(root)) {
                Directory.Delete(root, recursive: true);
            }
        } catch (IOException) {
            // A file the test wrote and the OS has not let go of. Not what is under test.
        }
    }

    /// <summary>Every usage a bake wrote is resolvable by that usage, and by nothing else.</summary>
    /// <remarks>
    ///     ⚠ <b>All nine rather than one, and derived from <see cref="MeshMapNaming.Every" /> rather
    ///     than listed.</b> A theory with an <c>InlineData</c> per usage passes silently on the day a
    ///     tenth map is added and not listed — which is the shape of the defect the whole read side
    ///     is being built to avoid.
    /// </remarks>
    [Fact]
    public void Every_baked_usage_resolves_by_its_usage() {
        var project = Project();
        var set = new ProjectMeshMapBaker(project).Bake(Barrel, "Barrel", Sheet(), Sheet(), Settings());
        var library = MeshMapLibrary.Index(project.Assets);

        Assert.Equal(["Barrel"], library.Sets);

        foreach (var usage in MeshMapNaming.Every) {
            Assert.True(library.TryResolve("Barrel", usage, out var map), $"no {usage} map resolved");

            // The identity, not the path: the same GUID the bake handed back for that usage.
            Assert.Equal(set.Maps[usage], map.Map);
            Assert.Equal(usage, map.Usage);
            Assert.Equal(Barrel, map.Model);
        }
    }

    /// <summary>
    ///     A renamed file still resolves, which is what binding by usage rather than by path means.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The one assertion that separates this from <c>Path.Combine</c>, and the reason
    ///     <see cref="MeshMapNaming" /> says the sidecar wins.</b> A resolver over
    ///     <see cref="MeshMapNaming.FileName" /> passes every other test in this file and fails this
    ///     one — so this is the test that says the file name is a convenience. The sidecar travels
    ///     with the file, keeping its GUID and its usage, which is exactly what the asset database's
    ///     rename does.
    /// </remarks>
    [Fact]
    public void A_renamed_map_still_resolves_by_its_usage() {
        var project = Project();
        var set = new ProjectMeshMapBaker(project).Bake(Barrel, "Barrel", Sheet(), Sheet(), Settings());
        var curvature = set.Files.Single(file => file.EndsWith("_curvature.png", StringComparison.Ordinal));
        var renamed = Path.Combine(Path.GetDirectoryName(curvature)!, "an artist tidied this.png");

        File.Move(curvature, renamed);
        File.Move(AssetMetaFile.PathFor(curvature), AssetMetaFile.PathFor(renamed));
        project.Assets.Scan();

        var library = MeshMapLibrary.Index(project.Assets);

        Assert.True(
            library.TryResolve("Barrel", MeshMapUsage.Curvature, out var map),
            "a renamed curvature map stopped resolving, so the binding is over the file name"
        );

        Assert.Equal(set.Maps[MeshMapUsage.Curvature], map.Map);
        Assert.EndsWith("an artist tidied this.png", map.Path, StringComparison.Ordinal);
    }

    /// <summary>A quantized map carries its scale back, and a map with no range carries none.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A quantized measurement whose scale lives somewhere else is half of the ways this
    ///         goes wrong.</b> Displacement and curvature are stored as <c>0.5 + 0.5·v/range</c> in
    ///         eight bits, so a reader that got the map and not the range would decode a shape rather
    ///         than a measurement — and there is nothing in the pixels that says so.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The curvature map here carries <i>no</i> scale, which refutes the obvious reading
    ///         of <see cref="MeshMapNaming.ScaleKey" />'s "present only on the two signed maps".</b>
    ///         A range is <c>max |v|</c> over the whole bake, so a target that is a plane has a
    ///         curvature range of exactly zero and <c>ProjectMeshMapBaker.Describe</c> then removes
    ///         the key rather than writing <c>0</c>. An absent scale therefore means "nothing was
    ///         measured" and not "the key was lost", and a reader that treated it as an error would
    ///         refuse every flat surface in a project.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_quantized_map_carries_its_scale_and_a_flat_one_carries_none() {
        var project = Project();

        // A source lifted off the target, so the displacement is a real distance and its range is
        // not zero — a flat bake would make the assertion below true for the wrong reason.
        new ProjectMeshMapBaker(project).Bake(Barrel, "Barrel", Raised(0.25f), Sheet(), Settings());

        var library = MeshMapLibrary.Index(project.Assets);

        Assert.True(library.TryResolve("Barrel", MeshMapUsage.Displacement, out var displacement));

        Assert.True(
            displacement.Scale > 0f,
            "the displacement map of a source a quarter of a unit off the target came back with no "
            + "scale, so nothing can decode it back into a distance"
        );

        foreach (var usage in MeshMapNaming.Every) {
            if (usage == MeshMapUsage.Displacement) {
                continue;
            }

            Assert.True(library.TryResolve("Barrel", usage, out var map));

            // Curvature included: the target is a plane, so its range is genuinely zero. See above.
            Assert.Equal(0f, map.Scale);
        }
    }

    /// <summary>
    ///     A model with one set resolves by the model; a model with two refuses rather than guessing.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The convenience and its limit in one test, because shipping the first without the
    ///     second is how a graph binds the barrel's lid.</b> A model is not a set: every mesh of one
    ///     model has its own nine maps, and "the normal map of this model" then has as many answers
    ///     as the model has meshes. Returning the first is a picture that is right on the machine
    ///     the bake ran on and wrong on the next.
    /// </remarks>
    [Fact]
    public void A_model_resolves_only_while_it_has_one_set() {
        var project = Project();
        var baker = new ProjectMeshMapBaker(project);

        baker.Bake(Barrel, "Body", Sheet(), Sheet(), Settings());

        var one = MeshMapLibrary.Index(project.Assets);

        Assert.Equal(["Body"], one.SetsOf(Barrel));
        Assert.True(one.TryResolve(Barrel, MeshMapUsage.Normal, out var only));
        Assert.Equal("Body", only.Set);

        baker.Bake(Barrel, "Lid", Sheet(), Sheet(), Settings());

        var two = MeshMapLibrary.Index(project.Assets);

        Assert.Equal(["Body", "Lid"], two.SetsOf(Barrel));

        Assert.False(
            two.TryResolve(Barrel, MeshMapUsage.Normal, out _),
            "a model with two baked sets answered 'the normal map of this model', which has two answers"
        );

        // And naming the mesh is what makes it answerable again.
        Assert.True(two.TryResolve("Lid", MeshMapUsage.Normal, out var lid));
        Assert.Equal("Lid", lid.Set);
    }

    /// <summary>Another model's set of the same name is a different set, and resolves separately.</summary>
    /// <remarks>
    ///     ⚠ <b>Two models whose meshes are both called <c>Cube</c> is Blender's default and every
    ///     exporter's fallback</b>, and <see cref="ProjectMeshMapBaker" /> writes the second one as
    ///     <c>Cube_2</c> rather than over the first. The reader has to agree with that, or the
    ///     collision the writer went to some trouble to avoid comes back at the binding.
    /// </remarks>
    [Fact]
    public void Two_models_with_one_mesh_name_resolve_to_their_own_maps() {
        var project = Project();
        var baker = new ProjectMeshMapBaker(project);
        var first = baker.Bake(Barrel, "Cube", Sheet(), Sheet(), Settings());
        var second = baker.Bake(Crate, "Cube", Sheet(), Sheet(), Settings());

        Assert.NotEqual(first.Mesh, second.Mesh);

        var library = MeshMapLibrary.Index(project.Assets);

        Assert.True(library.TryResolve(Barrel, MeshMapUsage.Normal, out var barrel));
        Assert.True(library.TryResolve(Crate, MeshMapUsage.Normal, out var crate));

        Assert.Equal(first.Maps[MeshMapUsage.Normal], barrel.Map);
        Assert.Equal(second.Maps[MeshMapUsage.Normal], crate.Map);
        Assert.NotEqual(barrel.Map, crate.Map);
    }

    /// <summary>An ordinary picture in the project is not a mesh map.</summary>
    /// <remarks>
    ///     ⚠ <b>The half of the index that can be false.</b> Every assertion above is satisfied by an
    ///     <see cref="MeshMapLibrary.Index" /> that admitted every PNG it found and guessed the usage
    ///     from the file name — and that resolver would then bind a hand-authored
    ///     <c>Rock_normal.png</c> as a baked mesh map, which is #723's shape at the read end.
    /// </remarks>
    [Fact]
    public void A_picture_with_no_usage_in_its_sidecar_is_not_a_mesh_map() {
        var project = Project();

        new ProjectMeshMapBaker(project).Bake(Barrel, "Barrel", Sheet(), Sheet(), Settings());

        // Named exactly like one of ours, and authored by a person rather than by a bake.
        File.WriteAllBytes(Path.Combine(project.Paths.Assets, "Rock_normal.png"), Png());
        project.Assets.Scan();

        var library = MeshMapLibrary.Index(project.Assets);

        Assert.Equal(["Barrel"], library.Sets);
        Assert.False(library.TryResolve("Rock", MeshMapUsage.Normal, out _));
    }

    /// <summary>
    ///     A map the database did not pick up stops the bake instead of becoming a null in the set.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><see href="https://github.com/Rikarin/Vixen/issues/731" />, and the input is a
    ///         sidecar a scan refuses to touch.</b> <c>AssetDatabase</c> will not re-create a
    ///         <c>.meta</c> whose GUID it cannot read — minting a new one would break every reference
    ///         through the old — so the file is left out of the index, the read-back misses, and the
    ///         bake used to record <see cref="AssetReference.Null" /> and report success.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What that produces is a set that reports nine maps and resolves eight.</b>
    ///         <see cref="MeshMapSet.Maps" /> is the by-usage index every generator binds through, so
    ///         a null there is a generator that reads nothing with nothing said anywhere — the same
    ///         end as #724's bindless fallback, reached from the other asset type.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_map_the_database_did_not_pick_up_stops_the_bake() {
        var project = Project();
        var directory = Path.Combine(project.Paths.Assets, MeshMapNaming.DefaultFolder);

        Directory.CreateDirectory(directory);

        // A sidecar with no readable GUID, which is what a truncated write or a bad merge leaves.
        File.WriteAllText(
            AssetMetaFile.PathFor(
                Path.Combine(directory, MeshMapNaming.FileName("Barrel", MeshMapUsage.Curvature))
            ),
            "\0not a meta"
        );

        var baker = new ProjectMeshMapBaker(project);

        Assert.ThrowsAny<Exception>(() => baker.Bake(Barrel, "Barrel", Sheet(), Sheet(), Settings()));

        // And nothing in the project claims to be a set with a hole in it: the index holds only the
        // maps that did resolve, and the one that did not is absent rather than null.
        project.Assets.Scan();

        var library = MeshMapLibrary.Index(project.Assets);

        Assert.False(
            library.TryResolve("Barrel", MeshMapUsage.Curvature, out _),
            "the unreadable map resolved, so the sidecar the scan refused was somehow indexed"
        );
    }

    /// <summary>A four-texel PNG, for a picture that is a picture and nothing else.</summary>
    static byte[] Png() => Vixen.Core.Imaging.PngCodec.Encode(new(2, 2, new byte[2 * 2 * 4]));

    /// <inheritdoc cref="MeshMapAssetTests" />
    static readonly AssetId Barrel = AssetId.Parse("0f8c1d2e3a4b5c6d7e8f90a1b2c3d4e5");

    /// <inheritdoc cref="Barrel" />
    static readonly AssetId Crate = AssetId.Parse("1a2b3c4d5e6f708192a3b4c5d6e7f809");

    EditorProject Project() {
        var directory = Path.Combine(root, Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(Path.Combine(directory, "Assets"));

        return new(new ProjectPaths(directory));
    }

    /// <summary>Everything § D12 lists, at a size a test can afford.</summary>
    static BakeSettings Settings() =>
        new() {
            Resolution = 8,
            Gutter = 1,
            SearchRadius = 0.5f,
            Maps = Geometry.Remeshing.MeshMaps.All,
            OcclusionSamples = 4
        };

    /// <summary>A unit square with an atlas over the whole of it, which is enough to bake into.</summary>
    static EditMesh Sheet() {
        var mesh = new EditMesh();
        var corners = new int[3, 3];

        for (var i = 0; i < 3; i++) {
            for (var j = 0; j < 3; j++) {
                corners[i, j] = mesh.AddPosition(new(i / 2f, 0f, j / 2f));
            }
        }

        for (var i = 0; i < 2; i++) {
            for (var j = 0; j < 2; j++) {
                Span<int> loop = [corners[i, j], corners[i + 1, j], corners[i + 1, j + 1], corners[i, j + 1]];

                mesh.AddFace(loop, 0);
            }
        }

        var coordinates = new Vector2[mesh.CornerCount];

        for (var face = 0; face < mesh.FaceCount; face++) {
            var entry = mesh.Faces[face];
            var loop = mesh.CornersOf(face);

            for (var corner = 0; corner < loop.Length; corner++) {
                var position = mesh.Positions[loop[corner]];

                coordinates[entry.Start + corner] = new(position.X, position.Z);
            }
        }

        mesh.SetTexCoords(coordinates);

        return mesh;
    }

    /// <summary>The same sheet, lifted, so a bake against it measures a displacement it can report.</summary>
    static EditMesh Raised(float height) {
        var mesh = Sheet();

        for (var vertex = 0; vertex < mesh.PositionCount; vertex++) {
            mesh.MovePosition(vertex, mesh.Positions[vertex] + new Vector3(0f, height, 0f));
        }

        return mesh;
    }
}
