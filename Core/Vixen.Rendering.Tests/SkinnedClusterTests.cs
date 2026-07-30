// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using Vixen.Core.Mathematics;
using Vixen.Graphics.Null;
using Vixen.Rendering;
using Vixen.Rendering.Features;
using Vixen.Rendering.VirtualGeometry;
using Xunit;

namespace Tests;

/// <summary>
///     Skinning through the pages, the raster and the resolve.
/// </summary>
/// <remarks>
///     <para>
///         <b>The plan asks for an assertion rather than a comment</b>, and this is it. Phase 5's
///         warning is that the vertex-side transform has to agree between the raster and the resolve,
///         because a disagreement lands attributes on the wrong surface — a plausible character, shaded
///         from wherever that vertex was in some other pose. Until skinning there was no second
///         transform to disagree, and the warning was vacuous.
///     </para>
///     <para>
///         Three things are checked and they are different in kind. That the page format round-trips an
///         influence, which is arithmetic and has an oracle. That the pose bound covers the motion,
///         which is a property over random poses rather than an example. And that the two shaders reach
///         the palette by the same source text, which no host test can imply and which is the only
///         defence a duplicated fetch has.
///     </para>
/// </remarks>
public sealed class SkinnedClusterTests {
    /// <summary>
    ///     What a page stores for a vertex is what the mesh weighted it to.
    /// </summary>
    /// <remarks>
    ///     The encoding's own round trip, against the influences that went in rather than against a
    ///     second decoder. A weight is a byte, so it comes back to a 255th and not to the float — which
    ///     is the tolerance and is stated as a fraction of a weight rather than as an epsilon, because
    ///     that is the number the format promises.
    /// </remarks>
    [Fact]
    public void A_page_vertex_carries_the_bones_the_mesh_weighted_it_to() {
        var input = Bar(64);
        var mesh = MeshletBuilder.Build(input);
        var pages = Pages(mesh, input);

        Assert.True(pages.IsSkinned);
        Assert.Equal(MeshletPageBuilder.PositionSize + 10, pages.InfluenceOffset);
        Assert.Equal(24, pages.VertexStride);

        var influences = new VertexInfluence[256];
        var checked_ = 0;

        for (var cluster = 0; cluster < mesh.Meshlets.Length; cluster++) {
            var count = mesh.Meshlets[cluster].VertexCount;
            pages.GetInfluences(cluster, count, influences);

            for (var i = 0; i < count; i++) {
                var source = mesh.Vertices[mesh.Meshlets[cluster].VertexOffset + i];
                var decoded = influences[i];

                Assert.Equal(input.BoneIndices[source * 4], decoded.Bones.X);
                Assert.Equal(input.BoneIndices[(source * 4) + 1], decoded.Bones.Y);

                Assert.Equal(input.BoneWeights[source * 4], decoded.Weights.X, (0.5f / 255f) + 1e-6f);
                Assert.Equal(input.BoneWeights[(source * 4) + 1], decoded.Weights.Y, (0.5f / 255f) + 1e-6f);

                checked_++;
            }
        }

        Assert.True(checked_ > 100, $"Only {checked_} vertices were compared, which is not a mesh.");
    }

    /// <summary>
    ///     A static mesh's page vertex is untouched by skinning existing.
    /// </summary>
    /// <remarks>
    ///     The reason the offset is per mesh at all. Every rock in a project would otherwise carry eight
    ///     bytes of zeros per vertex through every page of every stream — half again the bytes, to say
    ///     nothing about geometry that has no skeleton.
    /// </remarks>
    [Fact]
    public void A_static_mesh_pays_nothing_for_it() {
        var input = Bar(32) with { BoneIndices = [], BoneWeights = [] };
        var mesh = MeshletBuilder.Build(input);
        var pages = Pages(mesh, input);

        Assert.False(pages.IsSkinned);
        Assert.Equal(-1, pages.InfluenceOffset);
        Assert.Equal(16, pages.VertexStride);

        Assert.Throws<InvalidOperationException>(() => pages.GetInfluences(0, 1, new VertexInfluence[1]));
    }

    /// <summary>
    ///     The blend is a weighted average of the matrices, with the stored weights renormalised.
    /// </summary>
    /// <remarks>
    ///     <b>The renormalisation is the assertion.</b> Quantized weights do not sum to one, and a blend
    ///     that used them as they stand scales every vertex by their sum — a uniform deflation toward
    ///     the origin that reads as a scale bug and not as a weight bug. Here the weights sum to three
    ///     quarters and the answer is still the translation, not three quarters of it.
    /// </remarks>
    [Fact]
    public void The_blend_renormalises_what_the_page_stored() {
        Matrix4x4[] palette = [
            Matrix4x4.Identity,
            Matrix4x4.FromTranslation(new(10f, 0f, 0f)),
            Matrix4x4.FromTranslation(new(0f, 20f, 0f))
        ];

        var influence = new VertexInfluence(new(1, 2, 0, 0), new(0.375f, 0.375f, 0f, 0f));
        var blended = influence.Blend(palette, 0);

        // The matrix itself, not a point put through it. A blend of weights summing to three quarters
        // has a bottom-right of three quarters, and the host's TransformPosition divides that out where
        // the shader's TransformPoint does not — so asking a transformed point would be asking the one
        // question that hides the bug on the side that does not have it.
        Assert.Equal(5f, blended.M41, 1e-5f);
        Assert.Equal(10f, blended.M42, 1e-5f);
        Assert.Equal(1f, blended.M44, 1e-6f);

        var moved = Matrix4x4.TransformPosition(Vector3.Zero, blended);

        // And the base is added, so an instance's palette is found where the record says it is rather
        // than at the start of every other instance's.
        Matrix4x4[] shifted = [Matrix4x4.FromTranslation(new(-99f, 0f, 0f)), .. palette];
        var again = new VertexInfluence(new(1, 2, 0, 0), new(0.375f, 0.375f, 0f, 0f)).Blend(shifted, 1);

        Assert.Equal(moved, Matrix4x4.TransformPosition(Vector3.Zero, again));
    }

    /// <summary>
    ///     The motion radius bounds how far the pose actually moves the mesh.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A property rather than an example, because what is being claimed is a bound and a bound
    ///         is a statement about every case. Random rotations, random translations and random points
    ///         inside the mesh's bound: the displacement never exceeds what the traversal was told to
    ///         expand by.
    ///     </para>
    ///     <para>
    ///         The failure this prevents is not a crash. A bound that is too small culls geometry that
    ///         is on screen — an arm that vanishes when it swings past the frustum edge, at one camera
    ///         angle, which is the failure nobody can reproduce from a screenshot.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_motion_radius_covers_every_point_the_pose_moves() {
        var considered = 0;

        Gen.Select(Gen.Float[-2f, 2f], Gen.Float[-2f, 2f], Gen.Float[-2f, 2f], Gen.Float[-3.2f, 3.2f], Gen.Float[-1f, 1f], Gen.Float[-1f, 1f], Gen.Float[-1f, 1f])
            .Sample(
                pose => {
                    var (tx, ty, tz, angle, px, py, pz) = pose;

                    var bone = Matrix4x4.FromRotationY(angle) * Matrix4x4.FromTranslation(new(tx, ty, tz));
                    Matrix4x4[] palette = [bone];

                    var centre = new Vector3(0f, 1f, 0f);
                    const float radius = 1.5f;
                    var bound = GpuClusterCulling.MotionRadiusFor(palette, centre, radius);

                    // A point inside the bound, which is what the bound is a claim about.
                    var offset = new Vector3(px, py, pz);

                    if (offset.Length() > 1f) {
                        offset = Vector3.Normalize(offset);
                    }

                    var point = centre + (offset * radius);
                    var moved = (Matrix4x4.TransformPosition(point, bone) - point).Length();

                    considered++;

                    return moved <= bound + 1e-3f;
                },
                iter: 2000
            );

        Assert.True(considered > 1000, $"Only {considered} poses were considered.");

        // And a rest pose expands nothing at all, so a skinned mesh standing still is culled as tightly
        // as a static one. Without this the bound is a constant tax on having a skeleton.
        Assert.Equal(0f, GpuClusterCulling.MotionRadiusFor([Matrix4x4.Identity], new(0f, 1f, 0f), 1.5f));
    }

    /// <summary>
    ///     A cluster the bind pose puts outside the frustum is kept when the pose can reach in.
    /// </summary>
    /// <remarks>
    ///     The behaviour the radius exists for, asserted through the CPU mirror of the traversal — so
    ///     what is checked is the decision rather than the arithmetic that feeds it. Setting the radius
    ///     to zero drops the cluster, which is what the bind-pose bound alone does and is the bug.
    /// </remarks>
    [Fact]
    public void A_pose_that_reaches_into_the_view_is_not_culled_by_the_bind_pose() {
        var input = Bar(32);
        var mesh = MeshletBuilder.Build(input);
        var pages = Pages(mesh, input);
        var scene = GpuClusterCulling.Flatten(mesh, pages);

        // The instance sits to the left of a frustum whose left plane is at x = 0, by more than the
        // mesh's own extent — so nothing of its bind pose is inside.
        var instance = new CullInstance {
            ClusterCount = (uint)scene.Clusters.Length,
            RootCount = (uint)scene.Roots.Length,
            Position = new(-40f, 0f, 0f),
            Scale = 1f,
            Flags = GpuCulling.Alive,
            StagesLow = 1u
        };

        var view = Frustum();

        // Traverse rather than Cut: Cut is the brute-force oracle and deliberately tests no frustum,
        // because a cut is what the error says and rejection is what a view says.
        Assert.Empty(GpuClusterCulling.Traverse(scene, instance with { MotionRadius = 0f }, view, _ => true).Visible);

        // A pose that can swing the geometry thirty-eight units to the right reaches the plane, and the
        // traversal has to consider it rather than reject it by where the mesh is not.
        Assert.NotEmpty(GpuClusterCulling.Traverse(scene, instance with { MotionRadius = 38f }, view, _ => true).Visible);
    }

    /// <summary>
    ///     A pose given to the feature reaches the instance record the raster reads.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The wiring, end to end on the host: a palette added to the frame's buffer, a base index in
    ///         the draw, and a record carrying both that and the radius the pose implies. An object
    ///         given no palette carries <see cref="GpuCulling.NoBones" />, which is zero — and zero can
    ///         mean "none" only because the frame's palette begins with an identity nothing points at,
    ///         so that is asserted here rather than assumed.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_pose_reaches_the_record_and_an_unskinned_object_carries_none() {
        using var device = new NullDevice();
        using var visibility = new GpuClusterVisibility(device);

        var system = new RenderSystem();
        var feature = new VirtualGeometryRenderFeature { Visibility = visibility };

        system.AddFeature(feature);

        var input = Bar(32);
        var mesh = MeshletBuilder.Build(input);
        var registered = feature.Register(mesh, Pages(mesh, input), 0);

        var skinned = system.Objects.Add(new RenderObject());
        var rigid = system.Objects.Add(new RenderObject());

        var draws = system.Objects.Data.Data(feature.Draws);
        draws[skinned.Index] = new() { Mesh = registered, Scale = 1f };
        draws[rigid.Index] = new() { Mesh = registered, Scale = 1f };

        feature.BeginBones();

        Matrix4x4[] palette = [Matrix4x4.FromTranslation(new(0f, 3f, 0f)), Matrix4x4.Identity];
        feature.SetBones(system, skinned, palette);

        // Never zero: the identity the frame seeds is at zero and no instance points at it.
        Assert.True(system.Objects.Data.Data(feature.Draws)[skinned.Index].FirstBone > 0);
        Assert.True(system.Objects.Data.Data(feature.Draws)[skinned.Index].MotionRadius > 0f);
        Assert.Equal(0, system.Objects.Data.Data(feature.Draws)[rigid.Index].FirstBone);

        system.Prepare();

        var records = visibility.InstanceRecords;

        Assert.NotEqual(GpuCulling.NoBones, records[skinned.Index].FirstBone);
        Assert.Equal(GpuCulling.NoBones, records[rigid.Index].FirstBone);
        Assert.Equal(0f, records[rigid.Index].MotionRadius);
        Assert.True(records[skinned.Index].MotionRadius > 0f);
    }

    /// <summary>
    ///     A registration tells the raster whether its vertices carry influences.
    /// </summary>
    [Fact]
    public void A_registered_mesh_says_where_its_influences_are() {
        using var device = new NullDevice();
        using var visibility = new GpuClusterVisibility(device);

        var skinned = Bar(32);
        var skinnedMesh = MeshletBuilder.Build(skinned);
        visibility.Register(skinnedMesh, Pages(skinnedMesh, skinned), 0);

        var rigid = Bar(32) with { BoneIndices = [], BoneWeights = [] };
        var rigidMesh = MeshletBuilder.Build(rigid);
        visibility.Register(rigidMesh, Pages(rigidMesh, rigid), 1);

        Assert.Equal((uint)(MeshletPageBuilder.PositionSize + 10), visibility.MeshRecords[0].InfluenceOffset);
        Assert.Equal(RasterMesh.NoInfluences, visibility.MeshRecords[1].InfluenceOffset);
    }

    /// <summary>
    ///     The raster and the resolve reach the palette by the same source text.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The assertion phase 5 asks for.</b> The blend is shared — both call
    ///         <c>Skinning.BlendMatrix</c>, the same function the shadow pass uses — but the four
    ///         palette reads cannot be, because indexing a palette has to happen in the shader that
    ///         declares it or the whole sixteen kilobytes is copied at every call. So the fetch is
    ///         duplicated, and what keeps two copies from drifting is that a test compares them.
    ///     </para>
    ///     <para>
    ///         Character for character, deliberately. A tolerance here would be a tolerance for one of
    ///         them fetching bone <c>1</c> where the other fetches bone <c>2</c>, which is the entire
    ///         failure mode: a picture that looks like a character, shaded as though it were in a
    ///         different pose.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_raster_and_the_resolve_skin_by_the_same_arithmetic() {
        var raster = Source("Pipeline", "ClusterRaster.rvn");
        var resolve = Source("Pipeline", "VisibilityResolve.rvn");

        Assert.Equal(Skin(raster), Skin(resolve));

        // Both ask the mesh record, and both treat an unskinned instance as unskinned — a guard on one
        // side only is a mesh drawn posed and shaded in its bind pose.
        const string guard = "if (mesh.influenceOffset != RasterMesh.NoInfluences && instance.firstBone != Cull.NoBones) {";

        Assert.Contains(guard, raster, StringComparison.Ordinal);
        Assert.Contains(guard, resolve, StringComparison.Ordinal);

        // The blend is the library's, not either shader's own.
        Assert.Contains("Skinning.BlendMatrix(palette, Influences.Weights(", raster, StringComparison.Ordinal);
        Assert.Contains("Skinning.BlendMatrix(palette, Influences.Weights(", resolve, StringComparison.Ordinal);

        // And the resolve skins the normal as well as the position: a skinned surface whose normals stay
        // in the bind pose is lit from the wrong side of every joint that moved.
        Assert.Contains("Math.TransformDirection(skin, normal)", resolve, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Both passes read this frame's records rather than whichever the ring last wrote.
    /// </summary>
    /// <remarks>
    ///     A base index and not a descriptor offset, in both — because the resolve's set is filled by
    ///     <see cref="EffectSetWriter" />, which binds a storage buffer whole and has nowhere to put an
    ///     offset. A descriptor offset would be right in the raster and a frame stale in the resolve,
    ///     which is the two transforms disagreeing by a route no shader change could be blamed for.
    /// </remarks>
    [Fact]
    public void Both_passes_reach_this_frames_region_the_same_way() {
        var raster = Source("Pipeline", "ClusterRaster.rvn");
        var resolve = Source("Pipeline", "VisibilityResolve.rvn");

        Assert.Contains("residency[int(residencyBase + record.page)]", raster, StringComparison.Ordinal);
        Assert.Contains("residency[int(residencyBase + record.page)]", resolve, StringComparison.Ordinal);
        Assert.Contains("int(boneBase + instance.firstBone)", raster, StringComparison.Ordinal);
        Assert.Contains("int(boneBase + instance.firstBone)", resolve, StringComparison.Ordinal);
        Assert.Contains("instances[int(instanceBase) + instanceIndex]", raster, StringComparison.Ordinal);
        Assert.Contains("instances[int(instanceBase + Cull.VisibleInstance(packed))]", resolve, StringComparison.Ordinal);
    }

    /// <summary>The body of a shader's <c>Skin</c> function, whitespace and all.</summary>
    static string Skin(string source) {
        var start = source.IndexOf("func Skin(at: uint, instance: CullInstance): mat4 {", StringComparison.Ordinal);
        Assert.True(start >= 0, "The shader has no Skin function.");

        var end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        Assert.True(end > start, "The Skin function does not end.");

        return source[start..end];
    }

    /// <summary>A view whose left plane is the plane x = 0, everything else wide open.</summary>
    static CullView Frustum() {
        var view = new CullView {
            Position = new(0f, 0f, 0f),
            ErrorScale = 0f,
            ErrorThreshold = 1f,
            StagesLow = 1u
        };

        view.Planes[0] = new(0f, 0f, 1f, 1000f);
        view.Planes[1] = new(0f, 0f, -1f, 1000f);
        view.Planes[2] = new(1f, 0f, 0f, 0f);
        view.Planes[3] = new(-1f, 0f, 0f, 1000f);
        view.Planes[4] = new(0f, 1f, 0f, 1000f);
        view.Planes[5] = new(0f, -1f, 0f, 1000f);

        return view;
    }

    /// <summary>The pages a skinned mesh ships, laid out the way <c>ModelCompiler</c> lays them out.</summary>
    static MeshletPageSet Pages(MeshletMesh mesh, MeshletBuildInput input) {
        if (!input.IsSkinned) {
            return MeshletPageBuilder.Build(
                mesh,
                input.Positions,
                new byte[input.Positions.Length * 10],
                new() { PageSize = 8 * 1024, AttributeStride = 10 }
            );
        }

        const int stride = 18;
        var attributes = new byte[input.Positions.Length * stride];

        for (var i = 0; i < input.Positions.Length; i++) {
            var at = (i * stride) + 10;

            for (var influence = 0; influence < 4; influence++) {
                attributes[at + influence] = (byte)input.BoneIndices[(i * 4) + influence];
                attributes[at + 4 + influence] = (byte)MathF.Round(input.BoneWeights[(i * 4) + influence] * 255f);
            }
        }

        return MeshletPageBuilder.Build(
            mesh,
            input.Positions,
            attributes,
            new() { PageSize = 8 * 1024, AttributeStride = stride, InfluenceOffset = MeshletPageBuilder.PositionSize + 10 }
        );
    }

    /// <summary>
    ///     A long thin grid of quads weighted along its length, which is a limb as far as this is
    ///     concerned: two bones, a smooth transition between them, and enough triangles to make a DAG.
    /// </summary>
    static MeshletBuildInput Bar(int segments) {
        var positions = new List<Vector3>();
        var indices = new List<int>();
        var bones = new List<int>();
        var weights = new List<float>();

        for (var i = 0; i <= segments; i++) {
            var t = (float)i / segments;

            for (var side = 0; side < 2; side++) {
                positions.Add(new(t * 4f, side == 0 ? -0.5f : 0.5f, 0f));

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
            Positions = [.. positions],
            Indices = [.. indices],
            BoneIndices = [.. bones],
            BoneWeights = [.. weights]
        };
    }

    static string Source(string folder, string file) {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent) {
            var candidate = Path.Combine(directory.FullName, "Raven", "Library", folder, file);

            if (File.Exists(candidate)) {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException($"Raven/Library/{folder}/{file} was not found above '{AppContext.BaseDirectory}'.");
    }
}
