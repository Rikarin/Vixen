// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Rendering;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>
///     Recording: the step that turns a sorted list into draw calls — docs/plan/06 § Frame
///     structure, step 7.
/// </summary>
/// <remarks>
///     What the render system owns here is <em>which</em> feature is handed <em>which</em> nodes, in
///     what order and in what groupings. What a draw call actually contains is the feature's, which
///     is the split that lets the renderer own sorting without owning materials — so these tests are
///     about the handover, with a feature that records what it was given and a Null device that
///     remembers what it was told.
/// </remarks>
public class RecordingTests {
    sealed class SpyFeature(string name) : RootRenderFeature {
        public override string Name { get; } = name;

        /// <summary>One entry per <c>Draw</c> call: the nodes it was handed, in order.</summary>
        public List<RenderObjectId[]> Runs { get; } = [];

        public List<(string View, string Stage)> Context { get; } = [];

        protected internal override void Draw(
            RenderSystem system,
            RenderDrawContext context,
            ReadOnlySpan<RenderNode> nodes
        ) {
            Runs.Add(nodes.ToArray().Select(node => node.Object).ToArray());
            Context.Add((context.View!.Name, context.Stage!.Name));

            // A plausible draw, so the command list has something to have recorded.
            foreach (var _ in nodes) {
                context.CommandList.Draw(3);
            }
        }
    }

    static RenderView Camera(RenderStageMask stages, string name = "camera") {
        var view = Matrix4x4.LookAt(Vector3.Zero, new(0f, 0f, 1f), new(0f, 1f, 0f));
        var projection = Matrix4x4.PerspectiveFieldOfView(MathF.PI / 3f, 1f, 0.1f, 1000f);

        return new(name) { Stages = stages, Position = Vector3.Zero, Frustum = new(view * projection) };
    }

    static RenderObject At(float z, RenderStageMask stages, int feature, uint group = 0) =>
        new() {
            Bounds = new(new Vector3(0f, 0f, z), 0.5f),
            Stages = stages,
            FeatureIndex = feature,
            SortGroup = group
        };

    /// <summary>
    ///     A device, a list already inside a render pass, and a context over it.
    /// </summary>
    /// <remarks>
    ///     The pass is opened by the caller, not by <c>RenderSystem.Record</c>, and the Null backend
    ///     refusing a draw outside one is what says so out loud. It has to be that way round: one
    ///     pass may draw several stages, and the attachments belong to the render graph rather than
    ///     to the stage — a stage that opened its own pass could not be one of several in a
    ///     subpass-fused mobile path.
    /// </remarks>
    static (NullDevice Device, ICommandList List, RenderDrawContext Context) Recording() {
        var device = new NullDevice(new() { Record = true });
        var target = device.CreateTexture(Target);
        var view = device.CreateTextureView(target);

        var list = device.BeginCommandList();
        list.BeginRenderPass(new([new(view)], name: "Test"));

        return (device, list, new(list, new EffectSystem()) { Device = device });
    }

    static TextureDescription Target =>
        new() {
            Width = 16,
            Height = 16,
            Depth = 1,
            MipLevels = 1,
            ArrayLayers = 1,
            SampleCount = 1,
            Format = PixelFormat.Rgba8UNorm,
            Usage = TextureUsage.ColourTarget
        };

    [Fact]
    public void A_feature_is_handed_its_own_nodes_and_told_where_it_is() {
        using var system = new RenderSystem();
        var opaque = system.AddStage(new("Opaque"));
        var feature = system.AddFeature(new SpyFeature("Meshes"));

        system.Objects.Add(At(10f, opaque.Mask, feature.Index));
        system.Objects.Add(At(20f, opaque.Mask, feature.Index));

        var camera = Camera(opaque.Mask);
        system.SetViews([camera]);
        system.Draw();

        var (device, list, context) = Recording();
        using (device) {
            system.Record(camera, opaque, context);
            list.EndRenderPass();
            list.Finish();

            // The recorder receives a list's commands when it is submitted, not as they are made.
            device.GraphicsQueue.Submit([list]);

            Assert.Equal(2, Assert.Single(feature.Runs).Length);
            Assert.Equal(("camera", "Opaque"), Assert.Single(feature.Context));

            // Two nodes, two draws — the feature's own calls reached the list.
            Assert.Equal(2, device.Recorder!.CountOf(RecordedCommandKind.Draw));
        }
    }

    /// <summary>
    ///     Two features' work interleaves at the sort order rather than being gathered per feature.
    /// </summary>
    /// <remarks>
    ///     The decision worth pinning. Gathering a feature's nodes would save a handover and reorder
    ///     the stage — which for a transparent stage means reordering blended draws and changing the
    ///     image. The sort order is what the stage promised, so recording splits at each feature
    ///     change and hands over a run.
    /// </remarks>
    [Fact]
    public void Runs_follow_the_sort_order_rather_than_gathering_each_feature() {
        using var system = new RenderSystem();
        var opaque = system.AddStage(new("Opaque"));
        var meshes = system.AddFeature(new SpyFeature("Meshes"));
        var sprites = system.AddFeature(new SpyFeature("Sprites"));

        // Interleaved by depth: mesh, sprite, mesh.
        var nearMesh = system.Objects.Add(At(5f, opaque.Mask, meshes.Index));
        var middleSprite = system.Objects.Add(At(15f, opaque.Mask, sprites.Index));
        var farMesh = system.Objects.Add(At(25f, opaque.Mask, meshes.Index));

        var camera = Camera(opaque.Mask);
        system.SetViews([camera]);
        system.Draw();

        var (device, list, context) = Recording();
        using (device) {
            system.Record(camera, opaque, context);

            Assert.Equal([[nearMesh], [farMesh]], meshes.Runs);
            Assert.Equal([[middleSprite]], sprites.Runs);
        }
    }

    /// <summary>
    ///     Objects that share a sort group arrive in one run, which is what the sort was for.
    /// </summary>
    /// <remarks>
    ///     The payoff of putting the group in the key's high bits: nodes sharing a pipeline are
    ///     already adjacent, so a feature binds once and draws many. Handing over one node at a time
    ///     would have thrown that away at the last step.
    /// </remarks>
    [Fact]
    public void Objects_sharing_a_group_arrive_in_one_run() {
        using var system = new RenderSystem();
        var opaque = system.AddStage(new("Opaque"));
        var feature = system.AddFeature(new SpyFeature("Meshes"));

        for (var i = 0; i < 8; i++) {
            system.Objects.Add(At(10f + i, opaque.Mask, feature.Index, group: 1));
        }

        var camera = Camera(opaque.Mask);
        system.SetViews([camera]);
        system.Draw();

        var (device, list, context) = Recording();
        using (device) {
            system.Record(camera, opaque, context);
            Assert.Equal(8, Assert.Single(feature.Runs).Length);
        }
    }

    [Fact]
    public void Recording_a_stage_with_no_work_records_nothing() {
        using var system = new RenderSystem();
        var opaque = system.AddStage(new("Opaque"));
        var feature = system.AddFeature(new SpyFeature("Meshes"));

        // Behind the camera, so it is culled.
        system.Objects.Add(At(-10f, opaque.Mask, feature.Index));

        var camera = Camera(opaque.Mask);
        system.SetViews([camera]);
        system.Draw();

        var (device, list, context) = Recording();
        using (device) {
            system.Record(camera, opaque, context);
            Assert.Empty(feature.Runs);
        }
    }

    /// <summary>
    ///     Each view and stage records separately, which is what lets them go on different threads.
    /// </summary>
    [Fact]
    public void Each_view_and_stage_records_into_its_own_list() {
        using var system = new RenderSystem();
        var opaque = system.AddStage(new("Opaque"));
        var casters = system.AddStage(new("ShadowCaster"));
        var feature = system.AddFeature(new SpyFeature("Meshes"));

        system.Objects.Add(At(10f, opaque.Mask | casters.Mask, feature.Index));

        var camera = Camera(opaque.Mask);
        var shadow = Camera(casters.Mask, "shadow");
        system.SetViews([camera, shadow]);
        system.Draw();

        using var device = new NullDevice(new() { Record = true });
        var view = device.CreateTextureView(device.CreateTexture(Target));

        var cameraList = device.BeginCommandList();
        cameraList.BeginRenderPass(new([new(view)], name: "Camera"));
        system.Record(camera, opaque, new(cameraList, new EffectSystem()));

        var shadowList = device.BeginCommandList();
        shadowList.BeginRenderPass(new([new(view)], name: "Shadow"));
        system.Record(shadow, casters, new(shadowList, new EffectSystem()));

        Assert.Equal([("camera", "Opaque"), ("shadow", "ShadowCaster")], feature.Context);
    }

    /// <summary>The context does not outlive the recording it describes.</summary>
    /// <remarks>
    ///     A feature that stashed the context and read <c>View</c> later would get whatever the last
    ///     recording left, which is the shape of bug that only appears once a second view exists.
    /// </remarks>
    [Fact]
    public void The_context_forgets_its_view_and_stage_when_the_recording_ends() {
        using var system = new RenderSystem();
        var opaque = system.AddStage(new("Opaque"));
        var feature = system.AddFeature(new SpyFeature("Meshes"));

        system.Objects.Add(At(10f, opaque.Mask, feature.Index));

        var camera = Camera(opaque.Mask);
        system.SetViews([camera]);
        system.Draw();

        var (device, _, context) = Recording();
        using (device) {
            system.Record(camera, opaque, context);

            Assert.Null(context.View);
            Assert.Null(context.Stage);
        }
    }
}
