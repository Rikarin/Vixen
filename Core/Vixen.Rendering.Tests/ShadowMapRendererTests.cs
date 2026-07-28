// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Graphics.RenderGraph;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Features;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>
///     Cascaded shadow maps as compositor nodes — docs/plan/06 § Lighting.
/// </summary>
/// <remarks>
///     The claim being tested is structural rather than visual: <strong>a cascade is a view.</strong>
///     Four cascades are four <see cref="RenderView" />s over one stage, culled and sorted
///     independently by machinery that knows nothing about shadows, and drawn into four tiles of one
///     texture. If that holds, nothing in the mesh feature, the material feature or the sort key had
///     to change to support them — and nothing did.
/// </remarks>
public class ShadowMapRendererTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });
    readonly EffectSystem effects = new();

    // --- Fixture ------------------------------------------------------------

    static Effect Compiled(EffectKey key) =>
        new() {
            Key = key,
            Stages = [new(ShaderStage.Vertex, [1, 2, 3, 4], "main")]
        };

    sealed class AlwaysCompiles : IEffectProvider {
        public Effect? TryGet(EffectKey key) => Compiled(key);
    }

    sealed class Harness : IDisposable {
        public required RenderSystem System { get; init; }
        public required GraphicsCompositor Compositor { get; init; }
        public required RenderGraph Graph { get; init; }
        public required RenderStage Caster { get; init; }
        public required ShadowMapRenderer Shadows { get; init; }
        public required MeshRenderFeature Meshes { get; init; }
        public required MaterialRenderFeature Materials { get; init; }
        public required BufferHandle Vertices { get; init; }

        public void Dispose() {
            Graph.DisposePool();
            System.Dispose();
        }
    }

    Harness Build(int cascades = 4) {
        var system = new RenderSystem();

        var meshes = new MeshRenderFeature {
            Pipelines = new(device),
            Describer = new EffectPipelineDescriber(device)
        };

        var materials = new MaterialRenderFeature { Effects = effects };
        meshes.Add(materials);
        system.AddFeature(meshes);
        effects.AddProvider(new AlwaysCompiles());

        // Depth-only: no colour attachment, and the stage carries the depth bias a caster wants.
        var caster = system.AddStage(
            new("ShadowCaster") { Rasterizer = new(CullMode.Front, DepthBias: 1f, DepthBiasSlope: 2f) }
        );

        var shadows = new ShadowMapRenderer {
            Name = "Shadows",
            CasterStage = caster,
            Atlas = "ShadowAtlas",
            CascadeCount = cascades,
            Resolution = 512,
            ShadowDistance = 100f,
            Eye = Vector3.Zero,
            Forward = new(0f, 0f, -1f),
            LightDirection = Vector3.Normalize(new(0f, -1f, 0f))
        };

        var compositor = new GraphicsCompositor(system) { Game = shadows, FrameSize = shadows.AtlasSize };

        // Imported rather than declared, and that is not a shortcut for a test: a shadow atlas that
        // nothing samples is a transient whose only writer the graph would cull for producing
        // something nobody wanted. A real atlas is host-owned and read by the forward pass; an
        // import is the shape that says so.
        compositor.Imports["ShadowAtlas"] = Imported(shadows.AtlasSize);

        return new() {
            System = system,
            Compositor = compositor,
            Graph = new(device),
            Caster = caster,
            Shadows = shadows,
            Meshes = meshes,
            Materials = materials,
            Vertices = device.CreateBuffer(new() { Size = 1024, Usage = BufferUsage.Vertex })
        };
    }

    static void AddCaster(Harness h, Vector3 at, float radius = 1f) {
        var id = h.System.Objects.Add(
            new() { Bounds = new(at, radius), Stages = h.Caster.Mask, FeatureIndex = h.Meshes.Index }
        );

        h.System.Objects.Data.Data(h.Meshes.Draws)[id.Index] = new() {
            VertexBuffer = h.Vertices, Count = 3, InstanceCount = 1
        };

        h.Materials.Assign(h.System, id, new("DepthOnly"));
    }

    ImportedTexture Imported(Int2 size) {
        var description = new TextureDescription(
            PixelFormat.Depth32Float,
            size.X,
            size.Y,
            TextureUsage.DepthStencilTarget | TextureUsage.Sampled,
            Name: "ShadowAtlas"
        );

        var texture = device.CreateTexture(description);
        return new(texture, device.CreateTextureView(texture), description);
    }

    void Frame(Harness h) {
        var list = device.BeginCommandList();

        // Reset at the top of a frame rather than the bottom, so the graph a frame produced is still
        // there to be asked about afterwards — which is how a test sees what was culled.
        h.Graph.Reset();
        h.Compositor.Build(h.Graph, effects, device);
        h.Graph.Execute(list);

        list.Finish();
        device.GraphicsQueue.Submit([list]);
    }

    /// <inheritdoc />
    public void Dispose() {
        device.Dispose();
        GC.SuppressFinalize(this);
    }

    // --- A cascade is a view ------------------------------------------------

    [Fact]
    public void Four_cascades_are_four_views_over_one_stage() {
        using var h = Build();
        h.Compositor.Collect();

        Assert.Equal(4, h.Shadows.Views.Count);
        Assert.Equal(4, h.System.Views.Count);

        foreach (var view in h.Shadows.Views) {
            Assert.True(view.Stages.Contains(h.Caster.Index));
        }
    }

    /// <summary>The views are reused between frames rather than rebuilt.</summary>
    /// <remarks>
    ///     A view carries a visibility bitset sized to the scene. Allocating four of them per frame
    ///     would be the renderer's largest per-frame allocation, for four objects whose only changing
    ///     field is a matrix.
    /// </remarks>
    [Fact]
    public void The_cascade_views_survive_between_frames() {
        using var h = Build();

        h.Compositor.Collect();
        var first = h.Shadows.Views.ToArray();

        h.Compositor.Collect();

        Assert.Equal(4, h.Shadows.Views.Count);
        Assert.Equal(first, h.Shadows.Views);
    }

    /// <summary>
    ///     A caster in one cascade's range is culled out of the others.
    /// </summary>
    /// <remarks>
    ///     Ordinary frustum culling doing shadow work, with nothing shadow-specific involved: the
    ///     cascade's frustum comes from its own fitted projection, so a distant object is simply
    ///     outside the near cascade the way anything else outside a frustum is.
    /// </remarks>
    [Fact]
    public void A_caster_is_culled_out_of_the_cascades_that_do_not_reach_it() {
        using var h = Build();
        AddCaster(h, new(0f, 0f, -90f));

        h.Compositor.Collect();
        h.System.Draw();

        var seen = h.Shadows.Views.Count(view => h.System.Visibility.VisibleCount(view.Index) > 0);

        Assert.True(seen >= 1, "the caster is in no cascade at all");
        Assert.True(seen < 4, "the caster is in every cascade, so nothing was culled");
    }

    // --- The atlas ----------------------------------------------------------

    /// <summary>
    ///     One pass, one viewport per cascade — not four passes.
    /// </summary>
    /// <remarks>
    ///     On a tile-based GPU four passes are four loads and four stores of a depth buffer nothing
    ///     reads outside the frame; on a desktop one they are four barriers for no reason.
    /// </remarks>
    [Fact]
    public void The_atlas_is_one_pass_with_a_viewport_per_cascade() {
        using var h = Build();
        AddCaster(h, new(0f, 0f, -10f));

        Frame(h);

        Assert.Single(device.Recorder!.OfKind(RecordedCommandKind.BeginRenderPass));
        Assert.Equal(4, device.Recorder.CountOf(RecordedCommandKind.SetViewport));
        Assert.Equal(4, device.Recorder.CountOf(RecordedCommandKind.SetScissor));
    }

    /// <summary>
    ///     A shadow pass has no colour attachment at all.
    /// </summary>
    /// <remarks>
    ///     Not an omission — a colour target here is bandwidth spent on a value nothing ever reads,
    ///     which on a mobile tiler is the single most expensive mistake available in a shadow pass.
    /// </remarks>
    [Fact]
    public void A_shadow_pass_writes_depth_and_nothing_else() {
        using var h = Build();
        AddCaster(h, new(0f, 0f, -10f));

        Frame(h);

        var begin = Assert.Single(device.Recorder!.OfKind(RecordedCommandKind.BeginRenderPass));

        Assert.Equal(0, begin.A);
        Assert.Equal(1, begin.B);
    }

    /// <summary>Every cascade draws into its own tile, and the tiles tile the atlas.</summary>
    [Fact]
    public void Each_cascade_draws_into_its_own_tile() {
        using var h = Build();
        AddCaster(h, new(0f, 0f, -10f));

        Frame(h);

        var corners = device.Recorder!
            .OfKind(RecordedCommandKind.SetScissor)
            .Select(command => (command.C, command.D))
            .ToArray();

        Assert.Equal(4, corners.Distinct().Count());
        Assert.Equal(new Int2(1024, 1024), h.Shadows.AtlasSize);
    }

    /// <summary>A single cascade is one viewport over the whole atlas.</summary>
    [Fact]
    public void One_cascade_is_one_viewport() {
        using var h = Build(cascades: 1);
        AddCaster(h, new(0f, 0f, -10f));

        Frame(h);

        Assert.Single(device.Recorder!.OfKind(RecordedCommandKind.SetViewport));
        Assert.Equal(new Int2(512, 512), h.Shadows.AtlasSize);
    }

    /// <summary>A caster is drawn once per cascade that can see it, not once in total.</summary>
    /// <remarks>
    ///     The <c>RenderObject</c>/<c>RenderNode</c> separation in its clearest form: one object,
    ///     extracted once, appearing as a node in as many cascades as reach it.
    /// </remarks>
    [Fact]
    public void A_caster_near_the_camera_is_drawn_in_every_cascade_that_reaches_it() {
        using var h = Build();

        // Large enough to sit inside every cascade's fitted sphere.
        AddCaster(h, new(0f, 0f, -5f), radius: 60f);

        Frame(h);

        Assert.Equal(4, device.Recorder!.CountOf(RecordedCommandKind.Draw));
        Assert.Equal(1, h.System.Objects.LiveCount);
    }

    /// <summary>Without an atlas the node draws nothing rather than throwing.</summary>
    [Fact]
    public void No_atlas_means_no_pass() {
        using var h = Build();
        h.Shadows.Atlas = string.Empty;
        AddCaster(h, new(0f, 0f, -10f));

        Frame(h);

        Assert.Empty(device.Recorder!.OfKind(RecordedCommandKind.BeginRenderPass));
    }

    // --- Taken from the scene rather than copied ----------------------------

    /// <summary>
    ///     A view carrying a camera supplies the fit, and the node's own scalars are ignored.
    /// </summary>
    /// <remarks>
    ///     The scalars were seven copies of something the frame already knew. A host that set both
    ///     could set them differently, and a cascade fitted to a field of view the camera no longer
    ///     has puts the shadow distance somewhere the setting does not say — which shows up as
    ///     shadows fading in at the wrong distance and gets attributed to the shadow distance.
    /// </remarks>
    [Fact]
    public void A_views_camera_supplies_the_fit() {
        var camera = new RenderView("camera") {
            Camera = RenderCamera.Default with { Position = new(0f, 0f, 50f) }
        };

        var node = new ShadowMapRenderer {
            CasterStage = new("Caster"),
            Atlas = "Atlas",
            CascadeCount = 1,
            Camera = camera,

            // Deliberately somewhere else. The camera wins, so these change nothing.
            Eye = new(1000f, 1000f, 1000f),
            Forward = new(1f, 0f, 0f)
        };

        var fitted = Fit(node);
        var loose = Fit(
            new ShadowMapRenderer {
                CasterStage = new("Caster"),
                Atlas = "Atlas",
                CascadeCount = 1,
                Eye = new(0f, 0f, 50f)
            }
        );

        Assert.Equal(loose.Centre, fitted.Centre, Close);
        Assert.True(MathF.Abs(fitted.Centre.X) < 1f, $"the node's own eye leaked in: {fitted.Centre}");
    }

    /// <summary>The scene's sun casts the shadows, when there is one to ask.</summary>
    /// <remarks>
    ///     A host copying the sun's direction onto the shadow node every frame is a host that will one
    ///     day forget, leaving a level lit from one direction and shadowed from another. An interface
    ///     rather than a reference to the lighting feature, so a scripted or cinematic sun supplies it
    ///     and nothing else changes.
    /// </remarks>
    [Fact]
    public void The_scenes_sun_casts_the_shadows() {
        var sun = new Sunlight(RenderLight.Directional(new(1f, -1f, 0f), new(1f)));

        var node = new ShadowMapRenderer {
            CasterStage = new("Caster"),
            Atlas = "Atlas",
            CascadeCount = 1,
            Sun = sun,
            LightDirection = new(0f, -1f, 0f)
        };

        var withSun = Fit(node);
        var without = Fit(
            new ShadowMapRenderer {
                CasterStage = new("Caster"),
                Atlas = "Atlas",
                CascadeCount = 1,
                LightDirection = new(0f, -1f, 0f)
            }
        );

        // The same slice, so the same sphere — and a different light, so a different projection.
        Assert.Equal(without.Centre, withSun.Centre, Close);
        Assert.NotEqual(without.ViewProjection, withSun.ViewProjection);
    }

    /// <summary>A source with no sun leaves the written-down direction in charge.</summary>
    [Fact]
    public void A_scene_with_no_sun_falls_back_to_the_direction() {
        var node = new ShadowMapRenderer {
            CasterStage = new("Caster"),
            Atlas = "Atlas",
            CascadeCount = 1,
            Sun = new Sunlight(null),
            LightDirection = new(0f, -1f, 0f)
        };

        var without = Fit(
            new ShadowMapRenderer {
                CasterStage = new("Caster"),
                Atlas = "Atlas",
                CascadeCount = 1,
                LightDirection = new(0f, -1f, 0f)
            }
        );

        Assert.Equal(without.ViewProjection, Fit(node).ViewProjection);
    }

    /// <summary>A camera and a view that describe one volume, because setting one sets the other.</summary>
    [Fact]
    public void Setting_a_views_camera_sets_its_matrix_and_position() {
        var view = new RenderView("camera");
        var camera = RenderCamera.Default with { Position = new(3f, 4f, 5f) };

        view.Camera = camera;

        Assert.Equal(camera.Position, view.Position);
        Assert.Equal(camera.ViewProjection, view.ViewProjection);
        Assert.Equal(new BoundingFrustum(camera.ViewProjection), view.Frustum);
    }

    /// <summary>Two centres are the same when they agree to a millimetre.</summary>
    static readonly Func<Vector3, Vector3, bool> Close = (a, b) => (a - b).Length() < 0.001f;

    /// <summary>Runs the collect phase far enough to get the cascades out.</summary>
    static ShadowCascade Fit(ShadowMapRenderer node) {
        using var system = new RenderSystem();
        var compositor = new GraphicsCompositor(system) { Game = node };

        system.AddStage(node.CasterStage);
        compositor.Collect();

        return node.Cascades[0];
    }

    /// <summary>A sun somebody decided on, which is all a shadow renderer needs to know.</summary>
    sealed class Sunlight(RenderLight? sun) : ISunSource {
        public RenderLight? Sun { get; } = sun;
    }
}
