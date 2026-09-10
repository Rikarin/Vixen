// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Ecs;
using Vixen.Animation.Motions;
using Vixen.Animation.StateMachine;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Graphics.Null;
using Vixen.Rendering;
using Vixen.Rendering.Ecs;
using Vixen.Rendering.Features;
using Vixen.Rendering.VirtualGeometry;
using Xunit;

namespace Vixen.Animation.Tests;

/// <summary>
///     The chain between an animated entity and the palette a frame reads: #451's links, from this end.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>What these assert is reach, which is the half every existing skinning test leaves
///         out.</b> <c>SkinningAndInstancingTests</c> and <c>SkinnedClusterTests</c> construct a
///         feature and call <c>SetBones</c> on it directly — the correct unit test of a feature, and
///         silent about whether anything in a scene ever gets there. <see cref="SkinningSystem" />
///         queried a component (<c>SkinnedRenderer</c>) that nothing in the tree ever added, so it
///         walked no chunk in any world the engine builds and returned with every counter healthy.
///     </para>
///     <para>
///         ⚠ <b>Neither of these is evidence that a character draws deformed.</b> They are host
///         arithmetic: a palette in the frame's buffer and a non-zero base index in the instance
///         record the traversal reads. Whether <c>ClusterRaster.rvn</c> then blends it is a picture,
///         it needs a device and a skinned-character sample, and this repository has neither. See
///         <see href="https://github.com/Rikarin/Vixen/issues/451" />.
///     </para>
/// </remarks>
public sealed class SkinningWiringTests {
    readonly Skeleton skeleton = TestRigs.Chain();

    /// <summary>
    ///     A virtualized entity's pose reaches the instance record, through the handle the extraction
    ///     already writes.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The join, the feature and the buffer in one run. <c>FirstBone</c> is never zero for an
    ///         object that got a palette — the frame's buffer starts with an identity nothing points
    ///         at, which is what lets zero mean <c>GpuCulling.NoBones</c> — so the assertion is that
    ///         the record moved off it rather than that a counter incremented.
    ///     </para>
    ///     <para>
    ///         The pose is evaluated first rather than assumed, because a bind-pose palette is every
    ///         matrix identity and the motion radius it implies is zero. A radius that is zero is
    ///         indistinguishable from a radius that was never written, and it is the value that culls
    ///         a swinging limb by where the mesh is not.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_virtualized_entity_gets_its_palette_through_the_handle_the_extraction_wrote() {
        using var device = new NullDevice();
        using var visibility = new GpuClusterVisibility(device);
        using var system = new RenderSystem();
        using var world = new World(nameof(A_virtualized_entity_gets_its_palette_through_the_handle_the_extraction_wrote));

        var meshes = new MeshRenderFeature();
        var virtualized = new VirtualGeometryRenderFeature { Visibility = visibility };

        system.AddFeature(meshes);
        system.AddFeature(virtualized);

        var input = Bar(32);
        var mesh = MeshletBuilder.Build(input);
        var registered = virtualized.Register(mesh, Pages(mesh, input), 0);

        var id = system.Objects.Add(new RenderObject { FeatureIndex = virtualized.Index });
        system.Objects.Data.Data(virtualized.Draws)[id.Index] = new() { Mesh = registered, Scale = 1f };

        var entity = world.Create(
            new AnimatorComponent { Value = Walking() },
            new RenderHandle { Object = id }
        );

        // The phase order, kept: the pose is what PreRender reads and Animation is what wrote it.
        new AnimationSystem().Run(world, 0.25f);

        var skinning = new SkinningSystem { Virtualized = virtualized, Renderer = system };

        skinning.Run(world);

        Assert.Equal(1, skinning.Skinned);
        Assert.Equal(1, skinning.VirtualizedCount);

        // ⚠ And the record the traversal reads, not only the counter. A system that counted an entity
        // and wrote nothing is exactly what a counter cannot tell from one that worked.
        var draw = system.Objects.Data.Data(virtualized.Draws)[id.Index];

        Assert.True(draw.IsSkinned, $"The draw record says the instance has no palette ({draw.FirstBone}).");
        Assert.True(draw.MotionRadius > 0f, "The bound was not inflated, so a moved limb can be culled.");

        Assert.True(world.IsAlive(entity));
    }

    /// <summary>
    ///     An entity on the ordinary mesh path is not claimed by the virtualized feature.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The half a zeroed record gets wrong.</b> <c>VirtualGeometryDraw.Mesh</c> documents
    ///         <c>-1</c> as "draws none" and nothing writes it: an object extracted down the ordinary
    ///         path never has its entry in that array touched, so it reads registration zero and
    ///         <c>IsDrawable</c> answers true. A fall-through that trusted <c>IsDrawable</c> would give
    ///         every suballocated character's palette to the traversal and none to the feature that
    ///         draws it — the same shape as the morph defect one deformation over, where a face drew
    ///         at rest with every weight applied to nothing.
    ///     </para>
    ///     <para>
    ///         The discriminator is <see cref="RenderObject.FeatureIndex" />, which whoever added the
    ///         object wrote, and which every other root feature already tests.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_ordinary_meshs_palette_goes_to_the_feature_that_draws_it() {
        using var device = new NullDevice();
        using var visibility = new GpuClusterVisibility(device);
        using var system = new RenderSystem();
        using var world = new World(nameof(An_ordinary_meshs_palette_goes_to_the_feature_that_draws_it));

        var meshes = new MeshRenderFeature();
        using var feature = new SkinningRenderFeature { Device = device };
        var virtualized = new VirtualGeometryRenderFeature { Visibility = visibility };

        meshes.Add(feature);
        system.AddFeature(meshes);
        system.AddFeature(virtualized);

        // A registration exists, which is what makes registration zero a plausible answer for a record
        // nobody wrote — with none, the mistake this guards against would be invisible.
        var input = Bar(16);
        var mesh = MeshletBuilder.Build(input);

        Assert.Equal(0, virtualized.Register(mesh, Pages(mesh, input), 0));

        var id = system.Objects.Add(new RenderObject { FeatureIndex = meshes.Index });

        world.Create(new AnimatorComponent { Value = Walking() }, new RenderHandle { Object = id });
        new AnimationSystem().Run(world, 0.25f);

        var skinning = new SkinningSystem {
            Feature = feature,
            Virtualized = virtualized,
            Renderer = system
        };

        skinning.Run(world);

        Assert.Equal(1, skinning.Skinned);
        Assert.Equal(0, skinning.VirtualizedCount);

        Assert.Equal(skeleton.JointCount, system.Objects.Data.Data(feature.Palettes)[id.Index].BoneCount);
        Assert.False(system.Objects.Data.Data(virtualized.Draws)[id.Index].IsSkinned);
    }

    /// <summary>
    ///     ⚠ An entity carrying only the old join component is not what the query finds.
    /// </summary>
    /// <remarks>
    ///     <c>SkinnedRenderer</c> is added to an entity by nothing in the tree, so a system keyed on it
    ///     walked no chunk in any scene the engine builds — which is how skinning came to be complete,
    ///     tested and unreachable, with <see cref="SkinningSystem.Run" /> returning immediately and no
    ///     counter reading anything but healthy. Asserted with a renderer and a feature attached, so
    ///     the zero is the query's answer rather than the early return's.
    /// </remarks>
    [Fact]
    public void The_component_nothing_ever_wrote_is_not_the_join_any_more() {
        using var device = new NullDevice();
        using var visibility = new GpuClusterVisibility(device);
        using var system = new RenderSystem();
        using var world = new World(nameof(The_component_nothing_ever_wrote_is_not_the_join_any_more));

        var virtualized = new VirtualGeometryRenderFeature { Visibility = visibility };

        system.AddFeature(virtualized);

        var id = system.Objects.Add(new RenderObject { FeatureIndex = virtualized.Index });

        world.Create(
            new AnimatorComponent { Value = Walking() },
            new SkinnedRenderer { RenderObject = id }
        );

        var skinning = new SkinningSystem { Virtualized = virtualized, Renderer = system };

        skinning.Run(world);

        Assert.Equal(0, skinning.Skinned);
    }

    /// <summary>A run with no renderer and no feature writes nothing and throws nothing.</summary>
    /// <remarks>
    ///     A headless server has an animator and no device, and that is the ordinary case rather than a
    ///     misconfiguration — see <see cref="SkinningSystem.Renderer" />.
    /// </remarks>
    [Fact]
    public void A_run_with_no_renderer_is_a_harmless_no_op() {
        using var world = new World(nameof(A_run_with_no_renderer_is_a_harmless_no_op));

        world.Create(new AnimatorComponent { Value = Walking() }, new RenderHandle { Object = new(0) });

        var skinning = new SkinningSystem();

        skinning.Run(world);

        Assert.Equal(0, skinning.Skinned);
    }

    Animator Walking() {
        var clip = AnimationClip.Create(
            TestRigs.Translate("Walk", "Root", Vector3.Zero, new(0f, 0f, -4f)),
            skeleton
        );

        var animator = new Animator(skeleton) { RootMotion = RootMotionMode.Disabled };

        animator.AddLayer("Base", new([new AnimationState("Walk", new ClipMotion(clip))]));

        return animator;
    }

    /// <summary>
    ///     A long thin grid of quads weighted along its length, which is a limb as far as this is
    ///     concerned: two bones, a smooth transition between them, and enough triangles to make a DAG.
    /// </summary>
    /// <remarks><c>SkinnedClusterTests.Bar</c>, which is in another assembly.</remarks>
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

    /// <summary>The pages a skinned build input makes, influences and all.</summary>
    static MeshletPageSet Pages(MeshletMesh mesh, MeshletBuildInput input) {
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
}
