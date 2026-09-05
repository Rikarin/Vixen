// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Editor.Assets.MeshMaps;
using Vixen.Editor.Core;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.TextureGraph;
using Vixen.Geometry;
using Vixen.Geometry.Remeshing;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>
///     Doc 48 § 4.8's exit criterion: one generator, authored once, on two meshes with no rewiring.
/// </summary>
/// <remarks>
///     <para>
///         <b>Both halves of the binding in one process, which is the only place either can be
///         checked.</b> A bake writes nine PNGs with a sidecar naming what each measures; a graph
///         asks for a measurement and names no file. Neither production assembly may reference the
///         other — <c>Vixen.Editor.Assets</c> owns the vocabulary and drags every importer in the
///         tree with it, and <c>Vixen.Editor.TextureGraph</c> is deliberately narrow — so the node
///         spells the nine suffixes a second time and this file is what stops the two spellings
///         drifting.
///     </para>
///     <para>
///         ⚠ <b>The interesting assertion is that the two bakes bind <em>different</em> files, and
///         it took a wrong first draft to see why.</b> "Both compiled" is true of a generator that
///         resolved nothing at all, and "both resolved" is true of one that resolved the same map
///         twice — which is exactly what a resolver that ignored its set would do, and it would look
///         perfect on a project with one mesh in it. So every case below compares the identities
///         against what each bake actually returned.
///     </para>
///     <para>
///         ⚠ <b>And the graph is compiled <em>once</em>.</b> That is not an economy: doc 48 § 4.8's
///         claim is that the plan is the same plan for every mesh and only the external table
///         differs, so compiling twice would hide the thing under test. A generator that had to be
///         recompiled per mesh would be a generator with a mesh in it.
///     </para>
/// </remarks>
public sealed class MeshMapGeneratorBindingTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-meshmapbind-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() {
        try {
            if (Directory.Exists(root)) {
                Directory.Delete(root, recursive: true);
            }
        } catch (IOException) {
            // A file the test wrote and the OS has not let go of. Not what is under test.
        }
    }

    static readonly AssetId Barrel = AssetId.Parse("0f8c1d2e3a4b5c6d7e8f90a1b2c3d4e5");
    static readonly AssetId Crate = AssetId.Parse("1a2b3c4d5e6f708192a3b4c5d6e7f809");

    /// <summary>
    ///     ⚠ One compound, two meshes, no rewiring — and the two bakes read different files.
    /// </summary>
    /// <remarks>
    ///     <b><a href="https://github.com/Rikarin/Vixen/issues/702">#702</a>'s second box and
    ///     <a href="https://github.com/Rikarin/Vixen/issues/573">#573</a>'s first exit line.</b>
    ///     The generator is a shipped <c>.vxtexgraph</c> — content, not a fixture — so what is proved
    ///     is that the file in <c>Compounds/Generators</c> works, rather than that a graph this test
    ///     built works.
    /// </remarks>
    [Fact]
    public void One_generator_binds_two_meshes_with_no_rewiring() {
        var project = Project();
        var baker = new ProjectMeshMapBaker(project);

        var barrel = baker.Bake(Barrel, "Barrel", Sheet(), Sheet(), Settings());
        var crate = baker.Bake(Crate, "Crate", Raised(0.3f), Sheet(), Settings());

        var library = MeshMapLibrary.Index(project.Assets);

        // Compiled once. Everything below reads the same compilation.
        var compiler = Compile("Generators/Curvature Edge Wear", out var externals);

        Assert.NotEmpty(externals);

        foreach (var (set, baked) in new[] { ("Barrel", barrel), ("Crate", crate) }) {
            MeshMapBinding binding = new(library, set);

            foreach (var external in externals) {
                Assert.True(
                    binding.TryResolve(external.Asset, out var map, out var problem),
                    $"'{external.Asset}' did not bind on '{set}': {problem}"
                );

                // The identity the bake handed back for that usage on that mesh — not merely "a
                // map", and not the other mesh's.
                Assert.Equal(baked.Maps[map.Usage], map.Map);
                Assert.Equal(set, map.Set);
            }
        }

        // ⚠ And the two are genuinely different files. A resolver that ignored its set would pass
        // everything above on a project with one mesh in it, and this repository has shipped that
        // shape of test before.
        var curvature = Assert.Single(externals, external => external.Asset == "meshmap:curvature");

        Assert.True(new MeshMapBinding(library, "Barrel").TryResolve(curvature.Asset, out var one, out _));
        Assert.True(new MeshMapBinding(library, "Crate").TryResolve(curvature.Asset, out var two, out _));
        Assert.NotEqual(one.Map, two.Map);
        Assert.NotEqual(one.Path, two.Path);
    }

    /// <summary>
    ///     ⚠ Every usage a bake writes is one the node offers, and the reference round-trips.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The keeper for the vocabulary that is written down twice.</b> Renaming a suffix in
    ///         <see cref="MeshMapNaming.Suffix" /> — which its own remarks say silently unbinds every
    ///         shipped generator — or adding a tenth <see cref="MeshMapUsage" /> the node has not
    ///         heard of is red here, in both directions: the node has to accept every suffix the bake
    ///         writes, and the reference it emits has to parse back to the usage it was given.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Driven from <see cref="MeshMapNaming.Every" /> rather than listed</b>, because a
    ///         list is the thing that goes quiet on the day a tenth map lands.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_usage_a_bake_writes_is_one_the_node_offers() {
        Assert.NotEmpty(MeshMapNaming.Every);

        foreach (var usage in MeshMapNaming.Every) {
            NodeGraphModel graph = new();
            var node = graph.Add("Source/Mesh Map");
            var output = graph.Add("Output/Output");

            node.SetText("Map", MeshMapNaming.Suffix(usage));
            graph.Connect(new(node.Id, "Out"), new(output.Id, "Input"));

            TextureGraphCompiler compiler = new(Registry()) { BaseWidth = 64, BaseHeight = 64 };
            var compilation = compiler.Compile(graph);

            Assert.Equal([], compilation.Diagnostics.Select(one => one.Message).ToArray());

            var external = Assert.Single(compiler.Externals);

            Assert.True(
                MeshMapReference.TryParse(external.Asset, out var parsed),
                $"'{external.Asset}' is not a reference the resolver reads."
            );

            Assert.Equal(usage, parsed);
            Assert.Equal(MeshMapReference.For(usage), external.Asset);
        }
    }

    /// <summary>
    ///     A set baked without a map is refused with a sentence rather than resolved to nothing.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b><c>MeshMapBake.Always</c> guarantees only the normal and the displacement</b>, so a
    ///     generator asking for curvature of a set baked without it is a legitimate state and not a
    ///     bug. What must not happen is a black stand-in: doc 48's own finding is that a generator's
    ///     smartness is entirely in the bakes, so a mask quietly computed from a missing map is a
    ///     flat colour with a generator's name on it — which is the state #702 found the read side
    ///     in, arriving by a different door.
    /// </remarks>
    [Fact]
    public void A_set_baked_without_a_map_is_refused_with_a_sentence() {
        var project = Project();

        new ProjectMeshMapBaker(project).Bake(
            Barrel,
            "Barrel",
            Sheet(),
            Sheet(),
            Settings() with { Maps = MeshMaps.None }
        );

        MeshMapBinding binding = new(MeshMapLibrary.Index(project.Assets), "Barrel");

        // The two a bake always writes still resolve, so the refusal below is about the map and not
        // about the set.
        Assert.True(binding.TryResolve(MeshMapReference.For(MeshMapUsage.Normal), out _, out _));

        Assert.False(binding.TryResolve(MeshMapReference.For(MeshMapUsage.Curvature), out _, out var problem));
        Assert.Contains("curvature", problem, StringComparison.Ordinal);
        Assert.Contains("Barrel", problem, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ A bitmap's reference is not this resolver's, and saying so is not the same as failing.
    /// </summary>
    /// <remarks>
    ///     A compilation's external list mixes imported bitmaps with mesh maps and a host walks it
    ///     once. A resolver that reported <c>Assets/Textures/rust.png</c> as an unresolvable mesh map
    ///     would make every graph containing a <c>Source/Bitmap</c> look broken — so "not mine" is
    ///     <see langword="false" /> with an empty problem, and the caller tells the two apart.
    /// </remarks>
    [Fact]
    public void A_bitmaps_reference_is_not_a_mesh_map() {
        var project = Project();

        new ProjectMeshMapBaker(project).Bake(Barrel, "Barrel", Sheet(), Sheet(), Settings());

        MeshMapBinding binding = new(MeshMapLibrary.Index(project.Assets), "Barrel");

        Assert.False(binding.TryResolve("Assets/Textures/rust.png", out _, out var problem));
        Assert.Empty(problem);

        // ⚠ And a mesh-map reference naming a usage this build does not bake is the other case, which
        // has to be told apart from that: it is the resolver's, and it fails with a sentence.
        Assert.False(binding.TryResolve("meshmap:curvatur", out _, out var typo));
        Assert.NotEmpty(typo);
    }

    /// <summary>Binding by model, which is the convenience and its limit.</summary>
    /// <remarks>
    ///     A model with one set is a question with an answer; a model with two is not, and the
    ///     message names the sets because the caller's next move is to pick one.
    /// </remarks>
    [Fact]
    public void A_model_binds_only_while_it_has_one_set() {
        var project = Project();
        var baker = new ProjectMeshMapBaker(project);

        baker.Bake(Barrel, "Body", Sheet(), Sheet(), Settings());

        Assert.True(MeshMapBinding.TryFor(MeshMapLibrary.Index(project.Assets), Barrel, out var one, out _));
        Assert.Equal("Body", one.Set);

        baker.Bake(Barrel, "Lid", Sheet(), Sheet(), Settings());

        Assert.False(MeshMapBinding.TryFor(MeshMapLibrary.Index(project.Assets), Barrel, out _, out var problem));
        Assert.Contains("Body", problem, StringComparison.Ordinal);
        Assert.Contains("Lid", problem, StringComparison.Ordinal);
    }

    /// <summary>Compiles one shipped compound, once, and returns what it asked the host for.</summary>
    static TextureGraphCompiler Compile(string compound, out IReadOnlyList<TextureGraphExternal> externals) {
        var registry = Registry();
        var library = TextureCompoundLibrary.Publish(registry, folder: null, out var problems);

        Assert.Empty(problems);

        NodeGraphModel graph = new();
        var used = graph.Add(compound);
        var output = graph.Add("Output/Output");

        graph.Connect(new(used.Id, "Out"), new(output.Id, "Input"));

        TextureGraphCompiler compiler = new(registry) {
            BaseWidth = 128,
            BaseHeight = 128,
            SubGraphSource = library
        };

        var compilation = compiler.Compile(graph);

        Assert.Equal([], compilation.Diagnostics.Select(one => one.Message).ToArray());

        externals = compiler.Externals;

        return compiler;
    }

    static NodeTypeRegistry Registry() {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        return registry;
    }

    EditorProject Project() {
        var directory = Path.Combine(root, Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(Path.Combine(directory, "Assets"));

        return new(new ProjectPaths(directory));
    }

    /// <inheritdoc cref="MeshMapLibraryTests" />
    static BakeSettings Settings() =>
        new() {
            Resolution = 8,
            Gutter = 1,
            SearchRadius = 0.5f,
            Maps = MeshMaps.All,
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

    /// <summary>The same sheet, lifted, so the two meshes are not the same bake twice.</summary>
    static EditMesh Raised(float height) {
        var mesh = Sheet();

        for (var vertex = 0; vertex < mesh.PositionCount; vertex++) {
            mesh.MovePosition(vertex, mesh.Positions[vertex] + new Vector3(0f, height, 0f));
        }

        return mesh;
    }
}
