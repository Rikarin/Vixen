// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Transforms;
using Vixen.Graphics.Null;
using Vixen.Rendering.Ecs;
using Vixen.Rendering.Features;
using Vixen.Rendering;
using Vixen.Rendering.VirtualGeometry;
using Xunit;

namespace Tests;

/// <summary>
///     Blend shapes through the pages, the raster and the resolve.
/// </summary>
/// <remarks>
///     <para>
///         <c>SkinnedClusterTests</c>' counterpart, and it exists for that suite's reason one
///         deformation later: the paged path has three shaders that decode a page vertex, all three
///         have to place it in the same spot, and no host test can imply that they do. Two of the
///         three claims here are host arithmetic with an oracle; the third is about source text,
///         which is the only defence a deliberately duplicated function has.
///     </para>
///     <para>
///         ⚠ <b>What none of this can say is whether the shader ran.</b> That is
///         <c>VirtualGeometryGoldenTests.A_virtualized_mesh_moves_where_its_blend_shapes_say</c>'s
///         job, on a device, and it is the assertion this feature actually needed: the version of the
///         gather that accumulated into a struct member had every table below correct and drew a
///         plane at rest.
///     </para>
/// </remarks>
public sealed class MorphedClusterTests {
    // --- The indirection the whole design turns on --------------------------

    /// <summary>
    ///     ⚠ Every page vertex of every cluster resolves to the mesh vertex it was copied from.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>A page vertex carries no identity of its own</b> — a quantized position and some
    ///         attributes, and nothing that says which mesh vertex it is. That is why a paged morph
    ///         needs two indirections where the classic path needs none, and it is the half of this
    ///         design that can be wrong in a way that still draws a face.
    ///     </para>
    ///     <para>
    ///         Checked against the page's own decoded position rather than against the table it was
    ///         built from: if the source index is wrong, the position the page holds for that vertex
    ///         and the position the mesh holds for the vertex it names are different points.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_page_vertex_resolves_to_the_mesh_vertex_it_came_from() {
        var input = Grid(24);
        var mesh = MeshletBuilder.Build(input);
        var pages = MeshletPageBuilder.Build(mesh, input.Positions, [], new() { PageSize = 16 * 1024 });

        using var device = new NullDevice();
        using var visibility = new GpuClusterVisibility(device);

        var index = MorphIndex.Build(Shapes(input), input.Positions.Length);
        Assert.NotNull(index);

        var entry = visibility.Register(mesh, pages, 0, index);
        var record = visibility.MeshRecords[0];
        var words = visibility.MorphRecords;

        Assert.NotEqual(RasterMesh.NoMorphs, record.MorphClusterBase);

        var positions = new Vector3[256];
        var checked_ = 0;

        for (var cluster = 0; cluster < mesh.Meshlets.Length; cluster++) {
            var count = mesh.Meshlets[cluster].VertexCount;
            pages.GetPositions(cluster, count, positions);

            // The shader's first indirection: the cluster's index within its mesh into the table, and
            // the table's word into the run of source indices.
            var run = words[(int)record.MorphClusterBase + cluster];

            for (var local = 0; local < count; local++) {
                var source = (int)words[(int)run + local];

                Assert.True(
                    source >= 0 && source < input.Positions.Length,
                    $"Cluster {cluster} vertex {local} resolves to source {source} of "
                    + $"{input.Positions.Length}."
                );

                // A page position is quantized against a mesh-wide grid, so it is the mesh's position
                // to within half a step and no closer — which is the format's own promise and is what
                // makes this an identity check rather than an equality one.
                var apart = (positions[local] - input.Positions[source]).Length();

                Assert.True(
                    apart <= pages.QuantizationError * 2f,
                    $"Cluster {cluster} vertex {local} says it is mesh vertex {source}, which is "
                    + $"{apart} away and the grid is {pages.QuantizationStep}."
                );

                checked_++;
            }
        }

        Assert.True(checked_ > 500, $"Only {checked_} page vertices were resolved, which is not a mesh.");
        Assert.Equal(0, entry.Source);
    }

    /// <summary>
    ///     The entries a page vertex gathers are the entries the host would apply to the mesh vertex.
    /// </summary>
    /// <remarks>
    ///     The second indirection, and the arithmetic with it. Walking the tables by hand the way the
    ///     shader does and comparing with <see cref="MorphIndex.Apply" /> is what says the layout the
    ///     registration wrote and the layout the shader reads are the same layout — the two are
    ///     decided in different files and nothing else compares them.
    /// </remarks>
    [Fact]
    public void Walking_the_tables_by_hand_gives_what_the_host_kernel_gives() {
        var input = Grid(16);
        var mesh = MeshletBuilder.Build(input);
        var pages = MeshletPageBuilder.Build(mesh, input.Positions, [], new() { PageSize = 16 * 1024 });

        using var device = new NullDevice();
        using var visibility = new GpuClusterVisibility(device);

        var index = MorphIndex.Build(Shapes(input), input.Positions.Length);
        Assert.NotNull(index);

        visibility.Register(mesh, pages, 0, index);

        var record = visibility.MeshRecords[0];
        var words = visibility.MorphRecords;

        // ⚠ Two, and the second negative: a vertex both shapes move is a corrective, and a negative
        // weight is a shape authored as the inverse of its neighbour. Both are applied.
        float[] weights = [0.75f, -0.4f];
        var moved = 0;

        for (var vertex = 0; vertex < input.Positions.Length; vertex++) {
            var wanted = Vector3.Zero;
            var normal = Vector3.Zero;

            index.Apply(vertex, weights, ref wanted, ref normal);

            // The shader's walk, in C#: the vertex's run, then four words an entry — the shape's slot
            // and six quantized components across three words.
            var got = Vector3.Zero;
            var first = words[(int)record.MorphRunBase + vertex];
            var last = words[(int)record.MorphRunBase + vertex + 1];

            for (var slot = first; slot < last; slot++) {
                var at = (int)(record.MorphEntryBase + (slot * MorphIndex.EntryWords));
                var shape = (int)words[at];

                var a = words[at + 1];
                var b = words[at + 2];

                got += new Vector3(Low(a), High(a), Low(b))
                    * index.PositionSteps[shape]
                    * weights[shape];
            }

            Assert.True(
                (wanted - got).Length() <= 1e-5f,
                $"Vertex {vertex}: the tables give {got} and MorphIndex gives {wanted}."
            );

            if (wanted.Length() > 1e-6f) {
                moved++;
            }
        }

        // ⚠ Without this the whole loop passes on a mesh nothing moves, comparing zero with zero.
        Assert.True(moved > 100, $"Only {moved} vertices moved, which is not a shape.");
    }

    // --- Built and fed ------------------------------------------------------

    /// <summary>
    ///     ⚠ An entity whose mesh has a cluster hierarchy is given its weights by the systems a frame
    ///     runs, without anything here touching a feature.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The "built but never fed" assertion, and it is the one this whole task exists to
    ///         make true.</b> `MorphRenderFeatureTests` makes it for the suballocated path. Everything
    ///         else here drives the feature or the tables directly, which says the machinery works and
    ///         says nothing about whether a frame reaches it — and "a frame that does not reach it"
    ///         is precisely the defect that made a head with twenty shapes draw at rest.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The fall-through is what routes it, rather than a question about the mesh.</b>
    ///         `MorphRenderFeature.SetWeights` answers false for an object it never attached — which
    ///         is exactly the virtualized object — so neither system has to know what a cluster
    ///         hierarchy is. Here the first feature is absent entirely, which is the same branch.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_entity_with_a_hierarchy_is_given_its_weights_by_the_systems_a_frame_runs() {
        var input = Grid(12);
        var mesh = MeshletBuilder.Build(input);
        var pages = MeshletPageBuilder.Build(mesh, input.Positions, [], new() { PageSize = 16 * 1024 });

        using var device = new NullDevice(new());
        using var visibility = new GpuClusterVisibility(device);
        using var system = new RenderSystem();
        using var world = new World(nameof(An_entity_with_a_hierarchy_is_given_its_weights_by_the_systems_a_frame_runs));

        var buffer = new GeometryBuffer(device, SurfaceVertex.SizeInBytes, 4096, 8192);
        var residency = new GeometryResidency(buffer);

        var meshes = new MeshRenderFeature();
        var transforms = new TransformRenderFeature();
        var materials = new MaterialRenderFeature();
        var virtualized = new VirtualGeometryRenderFeature { Visibility = visibility };

        var opaque = system.AddStage(new("Opaque"));

        meshes.Add(transforms);
        meshes.Add(materials);

        system.AddFeature(meshes);
        system.AddFeature(virtualized);

        var registered = virtualized.Register(mesh, pages, 0, MorphIndex.Build(Shapes(input), input.Positions.Length));
        Assert.Equal(0, registered);

        var extraction = new MeshExtractionSystem(system, meshes, transforms, materials, residency) {
            Stages = opaque.Mask,
            Virtualized = virtualized,
            Clusters = new OneCluster(registered),
            Meshes = new OneTriangle()
        };

        var weights = new MorphWeightSystem { Virtualized = virtualized, Renderer = system };

        var entity = world.Create();

        MeshRenderables.Attach(world, entity, MeshRenderables.Default(Rock));
        world.Add(entity, new WorldTransform { Value = Matrix4x4.Identity });
        world.Add(entity, new BlendShapeWeights { Weights = [1f, 0.5f] });

        extraction.Extract(world);

        Assert.Equal(1, extraction.VirtualizedCount);

        weights.Run(world);

        Assert.Equal(1, weights.Weighted);
        Assert.Equal(1, weights.VirtualizedCount);

        // ⚠ And the record the traversal reads, not only the counter. A system that counted an entity
        // and wrote nothing is what a counter cannot tell from one that worked.
        var id = world.Read<RenderHandle>(entity).Object;
        var draw = system.Objects.Data.Data(virtualized.Draws)[id.Index];

        Assert.True(draw.IsMorphed, "The draw record says the instance has no weights.");
        Assert.NotEqual(0, draw.FirstWeight);
        Assert.True(draw.MorphRadius > 0f, "The bound was not inflated, so the expression can be culled.");

        // The names came back out of what was actually registered, which is what a clip binds by.
        var shapes = world.Read<BlendShapeWeights>(entity).Shapes;

        Assert.NotNull(shapes);
        Assert.Equal(["shrink", "lift"], shapes);
        Assert.Equal(1, weights.Bound);

        buffer.Dispose();
    }

    /// <summary>A source with one registered hierarchy, for every reference asked.</summary>
    sealed class OneCluster(int mesh) : IVirtualGeometrySource {
        public ClusterState TryGet(AssetReference reference, out int index, out BoundingSphere bounds) {
            index = mesh;
            bounds = new(Vector3.Zero, 2f);

            return ClusterState.Ready;
        }

        /// <summary>The one triangle <see cref="OneTriangle" /> supplies the vertices for.</summary>
        public bool TryGetCaster(AssetReference reference, out int[] triangles) {
            triangles = [0, 1, 2];

            return true;
        }
    }

    /// <summary>The fallback mesh a clustered model also has, which nothing here draws.</summary>
    sealed class OneTriangle : IMeshSource {
        public bool TryGet(AssetReference reference, out MeshData mesh) {
            mesh = new() {
                Name = "fallback",
                Positions = [new(0f, 0f, 0f), new(1f, 0f, 0f), new(0f, 1f, 0f)],
                Normals = [new(0f, 0f, 1f), new(0f, 0f, 1f), new(0f, 0f, 1f)],
                TexCoords = [new(0f, 0f), new(1f, 0f), new(0f, 1f)],
                Indices = [0, 1, 2]
            };

            return true;
        }
    }

    static readonly AssetReference Rock = new(new AssetId(new("77777777-7777-7777-7777-777777777777")));

    // --- The three copies ---------------------------------------------------

    /// <summary>The three shaders that decode a page vertex morph it by the same source text.</summary>
    /// <remarks>
    ///     <para>
    ///         <c>SkinnedClusterTests.The_raster_and_the_resolve_skin_by_the_same_arithmetic</c>'s
    ///         argument, one deformation later. The gather is duplicated for the reason
    ///         <c>Skinning</c> already gives — indexing a buffer is an access chain in the shader that
    ///         declares the binding — and the only defence a duplicated fetch has is that the copies
    ///         are the same characters.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The two rasters place a vertex and the resolve places the same vertex again.</b> A
    ///         disagreement between them does not look like a bug: the picture is a plausible face
    ///         shaded from wherever that vertex was at rest.
    ///     </para>
    /// </remarks>
    [Fact]
    public void All_three_shaders_gather_by_the_same_arithmetic() {
        var raster = Body(Source("ClusterRaster.rvn"));
        var software = Body(Source("ClusterSoftwareRaster.rvn"));
        var resolve = Body(Source("VisibilityResolve.rvn"));

        Assert.Contains("morphs[int(mesh.morphClusterBase + clusterIndex)]", raster, StringComparison.Ordinal);
        Assert.Equal(raster, software);
        Assert.Equal(raster, resolve);
    }

    /// <summary>
    ///     ⚠ All three morph before they skin, and the order is not a preference.
    /// </summary>
    /// <remarks>
    ///     A delta is authored in the mesh's own space, which is the space a page decodes into and the
    ///     space a bone matrix's <c>inverseBindPose * boneWorld</c> starts from. Skinning a rest vertex
    ///     and then adding a delta puts a jaw's displacement in the head's bind pose rather than in its
    ///     pose — a character whose face opens its mouth toward wherever it was standing at import.
    ///     The classic path has this order by construction, because the morph is a pre-pass and the
    ///     skinning reads its output.
    /// </remarks>
    [Fact]
    public void All_three_shaders_morph_before_they_skin() {
        foreach (var file in new[] { "ClusterRaster.rvn", "ClusterSoftwareRaster.rvn", "VisibilityResolve.rvn" }) {
            var text = Source(file);

            var morph = text.IndexOf("mesh.morphRunBase != RasterMesh.NoMorphs", StringComparison.Ordinal);
            var skin = text.IndexOf("mesh.influenceOffset != RasterMesh.NoInfluences", StringComparison.Ordinal);

            Assert.True(morph > 0, $"{file} never tests whether its mesh is morphed.");
            Assert.True(skin > 0, $"{file} never tests whether its mesh is skinned.");
            Assert.True(morph < skin, $"{file} skins before it morphs.");
        }
    }

    /// <summary>
    ///     ⚠ The resolve morphs the normal and the two rasters do not.
    /// </summary>
    /// <remarks>
    ///     A visibility buffer carries an identity, so a raster needs a position and nothing else and
    ///     morphing a normal there would be work thrown away. The resolve is the pass that reconstructs
    ///     the surface, and a resolve left unmorphed is a face whose geometry opens its mouth and whose
    ///     shading does not — which reads as a lighting bug rather than as a missing feature, and is
    ///     exactly the sort of half-applied deformation this suite exists to name.
    /// </remarks>
    [Fact]
    public void Only_the_resolve_morphs_the_normal() {
        Assert.Contains("normal = normal + delta.normal", Source("VisibilityResolve.rvn"), StringComparison.Ordinal);

        Assert.DoesNotContain(
            "normal = normal + delta.normal",
            Source("ClusterRaster.rvn"),
            StringComparison.Ordinal
        );

        Assert.DoesNotContain(
            "normal = normal + delta.normal",
            Source("ClusterSoftwareRaster.rvn"),
            StringComparison.Ordinal
        );
    }

    /// <summary>
    ///     ⚠ The shader's loop bound and the host's refusal are the same number.
    /// </summary>
    /// <remarks>
    ///     The gather is a counted loop, so a vertex whose run were longer than the bound would have
    ///     its remaining shapes dropped where nothing could see it. <see cref="MorphIndex.Build" />
    ///     refuses such a mesh instead — which is only a guarantee while the two numbers agree, and
    ///     they are written in different languages in different files.
    /// </remarks>
    [Fact]
    public void The_shaders_loop_bound_is_the_one_the_host_refuses_past() {
        Assert.Contains(
            $"const val MaxShapes = {MorphIndex.MaxShapesPerVertex}",
            Source("ClusterRaster.rvn"),
            StringComparison.Ordinal
        );

        var targets = new MorphTargetData[MorphIndex.MaxShapesPerVertex + 1];

        for (var shape = 0; shape < targets.Length; shape++) {
            targets[shape] = MorphTargetData.Encode($"shape{shape}", [0], [new(1f, 0f, 0f)], []);
        }

        var failure = Assert.Throws<ArgumentException>(() => MorphIndex.Build(targets, 2));

        Assert.Contains("Vertex 0 is moved by", failure.Message, StringComparison.Ordinal);
    }

    // --- Fixtures -----------------------------------------------------------

    /// <summary>A grid whose triangles a partitioner can actually cut into several clusters.</summary>
    static MeshletBuildInput Grid(int segments) {
        var positions = new List<Vector3>();
        var indices = new List<int>();

        for (var y = 0; y <= segments; y++) {
            for (var x = 0; x <= segments; x++) {
                positions.Add(new(((float)x / segments) - 0.5f, ((float)y / segments) - 0.5f, 0f));
            }
        }

        for (var y = 0; y < segments; y++) {
            for (var x = 0; x < segments; x++) {
                var a = (y * (segments + 1)) + x;

                indices.AddRange([a, a + 1, a + segments + 1]);
                indices.AddRange([a + 1, a + segments + 2, a + segments + 1]);
            }
        }

        return new() { Positions = [.. positions], Indices = [.. indices] };
    }

    /// <summary>One shape that pulls the grid toward its centre, plus a sparse one over it.</summary>
    /// <remarks>
    ///     Two, and the second sparse, because a corrective is the case a re-indexing gets wrong: a
    ///     vertex both shapes move has a run of two, and a table that kept only the last entry it
    ///     wrote per vertex passes every test built on one shape.
    /// </remarks>
    static MorphTargetData[] Shapes(in MeshletBuildInput input) {
        var count = input.Positions.Length;
        var indices = new int[count];
        var deltas = new Vector3[count];

        for (var index = 0; index < count; index++) {
            indices[index] = index;
            deltas[index] = input.Positions[index] * -0.5f;
        }

        var sparse = new int[count / 4];
        var lifts = new Vector3[sparse.Length];

        for (var index = 0; index < sparse.Length; index++) {
            sparse[index] = index * 4;
            lifts[index] = new(0f, 0f, 0.25f);
        }

        return [
            MorphTargetData.Encode("shrink", indices, deltas, []),
            MorphTargetData.Encode("lift", sparse, lifts, [])
        ];
    }

    static float Low(uint word) => (short)(word & 0xFFFF);

    static float High(uint word) => (short)(word >> 16);

    /// <summary>The text of one shader's <c>Morphed</c>, from its signature to its closing brace.</summary>
    static string Body(string text) {
        const string signature = "func Morphed(instance: CullInstance, mesh: RasterMesh, clusterIndex: uint, local: uint): MorphDelta {";

        var start = text.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "The shader declares no Morphed.");

        var end = text.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        Assert.True(end > start, "Morphed has no closing brace at method indentation.");

        return text[start..end];
    }

    static string Source(string file) {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent) {
            var candidate = Path.Combine(directory.FullName, "Raven", "Library", "Pipeline", file);

            if (File.Exists(candidate)) {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException($"Raven/Library/Pipeline/{file} was not found above '{AppContext.BaseDirectory}'.");
    }
}
