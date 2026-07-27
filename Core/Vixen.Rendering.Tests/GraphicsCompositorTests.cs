// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Features;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>
///     The frame's structure as data — docs/plan/06's third idea taken from Stride.
/// </summary>
/// <remarks>
///     Two claims, and both are the kind that a comment cannot establish. The <em>collect</em> phase
///     has to make the frame's view list a consequence of the tree rather than something a host sets
///     beside it; and the pass's attachment formats have to reach the pipeline, because a pipeline
///     built for the wrong formats is one the validation layers reject and a driver silently
///     mis-renders.
/// </remarks>
public class GraphicsCompositorTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });
    readonly EffectSystem effects = new();

    // --- Fixture ------------------------------------------------------------

    static Effect Compiled(EffectKey key) =>
        new() {
            Key = key,
            Stages = [
                new(ShaderStage.Vertex, [1, 2, 3, 4], "main"),
                new(ShaderStage.Fragment, [5, 6, 7, 8], "main")
            ]
        };

    sealed class AlwaysCompiles : IEffectProvider {
        public Effect? TryGet(EffectKey key) => Compiled(key);
    }

    sealed class Harness : IDisposable {
        public required RenderSystem System { get; init; }
        public required GraphicsCompositor Compositor { get; init; }
        public required RenderStage Opaque { get; init; }
        public required RenderStage Transparent { get; init; }
        public required RenderView Camera { get; init; }
        public required MeshRenderFeature Meshes { get; init; }
        public required MaterialRenderFeature Materials { get; init; }
        public required BufferHandle Vertices { get; init; }

        public void Dispose() => System.Dispose();
    }

    Harness Build() {
        var system = new RenderSystem();

        var meshes = new MeshRenderFeature {
            Pipelines = new(device),
            Describer = new EffectPipelineDescriber(device)
        };

        var materials = new MaterialRenderFeature { Effects = effects };
        meshes.Add(materials);
        system.AddFeature(meshes);

        effects.AddProvider(new AlwaysCompiles());

        var view = Matrix4x4.LookAt(Vector3.Zero, new(0f, 0f, 1f), new(0f, 1f, 0f));
        var projection = Matrix4x4.PerspectiveFieldOfView(MathF.PI / 3f, 1f, 0.1f, 1000f);

        return new() {
            System = system,
            Compositor = new(system),
            Opaque = system.AddStage(new("Opaque")),
            Transparent = system.AddStage(new("Transparent", RenderSortMode.BackToFront) {
                Blend = BlendState.AlphaBlend,
                DepthStencil = DepthStencilState.TestOnly
            }),
            Camera = new("camera") { Position = Vector3.Zero, Frustum = new(view * projection) },
            Meshes = meshes,
            Materials = materials,
            Vertices = device.CreateBuffer(new() { Size = 1024, Usage = BufferUsage.Vertex })
        };
    }

    static void AddMesh(Harness h, float z, Material material, RenderStageMask stages) {
        var id = h.System.Objects.Add(
            new() {
                Bounds = new(new Vector3(0f, 0f, z), 1f),
                Stages = stages,
                FeatureIndex = h.Meshes.Index
            }
        );

        h.System.Objects.Data.Data(h.Meshes.Draws)[id.Index] = new() {
            VertexBuffer = h.Vertices, Count = 3, InstanceCount = 1
        };

        h.Materials.Assign(h.System, id, material);
    }

    TextureViewHandle Target(PixelFormat format, TextureUsage usage = TextureUsage.ColourTarget) =>
        device.CreateTextureView(
            device.CreateTexture(
                new() {
                    Width = 16, Height = 16, Depth = 1,
                    MipLevels = 1, ArrayLayers = 1, SampleCount = 1,
                    Format = format, Usage = usage
                }
            )
        );

    RenderPassRenderer Pass(PixelFormat format, params SceneRenderer[] children) {
        var pass = new RenderPassRenderer { Name = format.ToString() };
        pass.ColourTargets.Add(new(Target(format), format));

        foreach (var child in children) {
            pass.Children.Add(child);
        }

        return pass;
    }

    void Frame(Harness h) {
        var list = device.BeginCommandList();
        h.Compositor.Draw(new(list, effects) { Device = device });
        list.Finish();
        device.GraphicsQueue.Submit([list]);
    }

    /// <inheritdoc />
    public void Dispose() {
        device.Dispose();
        GC.SuppressFinalize(this);
    }

    // --- Collect ------------------------------------------------------------

    /// <summary>
    ///     A view's stage mask is what the tree draws, not what a host remembered to set.
    /// </summary>
    /// <remarks>
    ///     The point of having a collect phase at all. A stage is in the mask because a node draws
    ///     it, so a stage nothing draws costs no culling and a stage that is drawn cannot have been
    ///     left out of the mask.
    /// </remarks>
    [Fact]
    public void The_stage_mask_is_derived_from_what_the_tree_draws() {
        using var h = Build();

        h.Compositor.Game = Pass(
            PixelFormat.Rgba8UNorm,
            new SingleStageRenderer { View = h.Camera, Stage = h.Opaque }
        );

        h.Compositor.Collect();

        Assert.True(h.Camera.Stages.Contains(h.Opaque.Index));
        Assert.False(h.Camera.Stages.Contains(h.Transparent.Index));
        Assert.Equal([h.Camera], h.Compositor.Views);
    }

    [Fact]
    public void A_view_nothing_draws_is_not_in_the_frame() {
        using var h = Build();
        var unused = new RenderView("probe");

        h.Compositor.Game = Pass(
            PixelFormat.Rgba8UNorm,
            new SingleStageRenderer { View = h.Camera, Stage = h.Opaque }
        );

        h.Compositor.Collect();

        Assert.DoesNotContain(unused, h.Compositor.Views);
        Assert.DoesNotContain(unused, h.System.Views);
    }

    /// <summary>A stage taken out of the tree stops being collected for.</summary>
    /// <remarks>
    ///     The mask is rebuilt each frame rather than accumulated. A mask that only ever gained bits
    ///     would keep sorting a stage nothing draws — work that produces a list no one reads, and
    ///     which nothing would ever report.
    /// </remarks>
    [Fact]
    public void A_stage_removed_from_the_tree_stops_being_collected() {
        using var h = Build();

        var opaque = new SingleStageRenderer { View = h.Camera, Stage = h.Opaque };
        var transparent = new SingleStageRenderer { View = h.Camera, Stage = h.Transparent };
        var pass = Pass(PixelFormat.Rgba8UNorm, opaque, transparent);

        h.Compositor.Game = pass;
        h.Compositor.Collect();

        Assert.True(h.Camera.Stages.Contains(h.Transparent.Index));

        pass.Children.Remove(transparent);
        h.Compositor.Collect();

        Assert.True(h.Camera.Stages.Contains(h.Opaque.Index));
        Assert.False(h.Camera.Stages.Contains(h.Transparent.Index));
    }

    [Fact]
    public void A_disabled_node_neither_collects_nor_draws() {
        using var h = Build();
        AddMesh(h, 10f, new Material("Lit"), h.Opaque.Mask);

        var pass = Pass(
            PixelFormat.Rgba8UNorm,
            new SingleStageRenderer { View = h.Camera, Stage = h.Opaque, Enabled = false }
        );

        h.Compositor.Game = pass;
        Frame(h);

        Assert.False(h.Camera.Stages.Contains(h.Opaque.Index));
        Assert.Empty(device.Recorder!.OfKind(RecordedCommandKind.Draw));
    }

    // --- The pass -----------------------------------------------------------

    /// <summary>
    ///     Clearing is a load action on an attachment, not a pass of its own.
    /// </summary>
    /// <remarks>
    ///     Which is why there is no <c>ClearRenderer</c>. A clear issued as its own operation costs a
    ///     tile-based GPU a full extra pass writing a colour the next pass overwrites — the opposite
    ///     of what a mobile-first renderer wants.
    /// </remarks>
    [Fact]
    public void A_cleared_pass_is_one_pass() {
        using var h = Build();
        AddMesh(h, 10f, new Material("Lit"), h.Opaque.Mask);

        h.Compositor.Game = Pass(
            PixelFormat.Rgba8UNorm,
            new SingleStageRenderer { View = h.Camera, Stage = h.Opaque }
        );

        Frame(h);

        var begin = Assert.Single(device.Recorder!.OfKind(RecordedCommandKind.BeginRenderPass));

        Assert.Equal(1, begin.A);
        Assert.Equal(0, begin.B);
        Assert.Single(device.Recorder.OfKind(RecordedCommandKind.EndRenderPass));
        Assert.Single(device.Recorder.OfKind(RecordedCommandKind.Draw));
    }

    /// <summary>
    ///     The same objects drawn into two passes of different formats are two pipelines.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The load-bearing claim of the whole arrangement. A pipeline is compiled for the exact
    ///         attachment formats it writes, so an <c>Rgba8UNorm</c> pipeline reused in an
    ///         <c>Rgba16Float</c> pass is invalid — and the failure mode is not a clean error but a
    ///         validation-layer complaint on one driver and a wrong image on another.
    ///     </para>
    ///     <para>
    ///         Before the output was part of <see cref="PipelineKey" /> this test would report one
    ///         pipeline and pass silently.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Two_passes_of_different_formats_are_two_pipelines() {
        using var h = Build();
        AddMesh(h, 10f, new Material("Lit"), h.Opaque.Mask);

        h.Compositor.Game = new SceneRendererSequence {
            Children = {
                Pass(PixelFormat.Rgba8UNorm, new SingleStageRenderer { View = h.Camera, Stage = h.Opaque }),
                Pass(PixelFormat.Rgba16Float, new SingleStageRenderer { View = h.Camera, Stage = h.Opaque })
            }
        };

        Frame(h);

        Assert.Equal(2, device.Recorder!.CountOf(RecordedCommandKind.BeginRenderPass));
        Assert.Equal(2, device.Recorder.CountOf(RecordedCommandKind.Draw));
        Assert.Equal(2, h.Meshes.Pipelines!.Count);
    }

    /// <summary>The same objects drawn into two passes of the same format are one pipeline.</summary>
    /// <remarks>
    ///     The other half of the previous test, and the reason the output holds formats rather than
    ///     textures: two passes writing different images of the same format share every pipeline, so
    ///     a swapchain that hands out a new image each frame invalidates nothing.
    /// </remarks>
    [Fact]
    public void Two_passes_of_the_same_format_share_one_pipeline() {
        using var h = Build();
        AddMesh(h, 10f, new Material("Lit"), h.Opaque.Mask);

        h.Compositor.Game = new SceneRendererSequence {
            Children = {
                Pass(PixelFormat.Rgba8UNorm, new SingleStageRenderer { View = h.Camera, Stage = h.Opaque }),
                Pass(PixelFormat.Rgba8UNorm, new SingleStageRenderer { View = h.Camera, Stage = h.Opaque })
            }
        };

        Frame(h);

        Assert.Equal(2, device.Recorder!.CountOf(RecordedCommandKind.Draw));
        Assert.Equal(1, h.Meshes.Pipelines!.Count);
    }

    /// <summary>Two stages drawn into one pass are two pipelines, because their state differs.</summary>
    /// <remarks>
    ///     Opaque writes depth and does not blend; transparent tests depth and does. Same shader,
    ///     same attachments, different pipelines — which is exactly why <c>Effect</c> holds bytecode
    ///     rather than a pipeline.
    /// </remarks>
    [Fact]
    public void Two_stages_in_one_pass_are_two_pipelines() {
        using var h = Build();
        var material = new Material("Lit");

        AddMesh(h, 10f, material, h.Opaque.Mask | h.Transparent.Mask);

        h.Compositor.Game = Pass(
            PixelFormat.Rgba8UNorm,
            new SingleStageRenderer { View = h.Camera, Stage = h.Opaque },
            new SingleStageRenderer { View = h.Camera, Stage = h.Transparent }
        );

        Frame(h);

        Assert.Single(device.Recorder!.OfKind(RecordedCommandKind.BeginRenderPass));
        Assert.Equal(2, device.Recorder.CountOf(RecordedCommandKind.Draw));
        Assert.Equal(2, h.Meshes.Pipelines!.Count);
    }

    /// <summary>
    ///     A pass with no depth attachment draws, rather than failing to build a pipeline.
    /// </summary>
    /// <remarks>
    ///     A stage carries a depth state because "Opaque" means depth-written wherever it is drawn;
    ///     a pass may have no depth attachment at all. Taking the stage's state literally would
    ///     produce a description that fails validation with "tests depth but declares no depth
    ///     attachment" — true, and unhelpful, because the reusable half is the stage.
    /// </remarks>
    [Fact]
    public void A_stage_that_tests_depth_still_draws_into_a_pass_that_has_none() {
        using var h = Build();
        AddMesh(h, 10f, new Material("Lit"), h.Opaque.Mask);

        Assert.True(h.Opaque.DepthStencil.DepthTest);

        h.Compositor.Game = Pass(
            PixelFormat.Rgba8UNorm,
            new SingleStageRenderer { View = h.Camera, Stage = h.Opaque }
        );

        Frame(h);

        Assert.Single(device.Recorder!.OfKind(RecordedCommandKind.Draw));
        Assert.Equal(1, h.Meshes.Pipelines!.Count);
    }

    /// <summary>A pass that does have depth builds a depth-testing pipeline into it.</summary>
    [Fact]
    public void A_pass_with_depth_keeps_the_stages_depth_state() {
        using var h = Build();
        AddMesh(h, 10f, new Material("Lit"), h.Opaque.Mask);

        var pass = Pass(
            PixelFormat.Rgba8UNorm,
            new SingleStageRenderer { View = h.Camera, Stage = h.Opaque }
        );

        pass.DepthTarget = new(
            Target(PixelFormat.Depth32Float, TextureUsage.DepthStencilTarget),
            PixelFormat.Depth32Float
        );

        h.Compositor.Game = pass;
        Frame(h);

        var begin = Assert.Single(device.Recorder!.OfKind(RecordedCommandKind.BeginRenderPass));

        Assert.Equal(1, begin.B);
        Assert.Equal(PixelFormat.Depth32Float, pass.Output.DepthFormat);
        Assert.Single(device.Recorder.OfKind(RecordedCommandKind.Draw));
    }

    // --- The frame ----------------------------------------------------------

    /// <summary>Collect runs before culling, so a view declared this frame is culled for.</summary>
    /// <remarks>
    ///     The ordering the whole phase split exists for. A node that declared its view during
    ///     drawing would have declared it after culling had already run without it, and the objects
    ///     it wanted would be missing from a list that reports nothing wrong.
    /// </remarks>
    [Fact]
    public void A_view_declared_this_frame_is_culled_and_drawn_in_it() {
        using var h = Build();
        AddMesh(h, 10f, new Material("Lit"), h.Opaque.Mask);
        AddMesh(h, -10f, new Material("Lit"), h.Opaque.Mask);

        h.Compositor.Game = Pass(
            PixelFormat.Rgba8UNorm,
            new SingleStageRenderer { View = h.Camera, Stage = h.Opaque }
        );

        Frame(h);

        // Two objects, one behind the camera: the frustum ran with this view, not without it.
        Assert.Single(device.Recorder!.OfKind(RecordedCommandKind.Draw));
        Assert.Equal(1, h.System.Visibility.VisibleCount(h.Camera.Index));
    }

    /// <summary>A compositor with no root draws nothing rather than throwing.</summary>
    [Fact]
    public void An_empty_compositor_draws_nothing() {
        using var h = Build();

        Frame(h);

        Assert.Empty(device.Recorder!.OfKind(RecordedCommandKind.BeginRenderPass));
    }
}
