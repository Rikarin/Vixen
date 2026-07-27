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
///     Caching a shadow atlas across frames — docs/plan/06 § Lighting, "re-render a cascade only
///     when its content changed".
/// </summary>
/// <remarks>
///     <para>
///         Two things have to be true together, and neither is worth much alone. The cascade's
///         projection has to <em>stop moving</em>, which is what <see cref="ShadowMapRenderer.Slack" />
///         buys — a tight fit re-fits the moment the camera moves a texel, and then there is never
///         anything to keep. And the static casters have to be separable from the moving ones, which
///         is what a second <see cref="RenderStage" /> is: "which objects, in what order" is exactly
///         what a stage means, so no filtering machinery is needed for it.
///     </para>
///     <para>
///         The number the whole thing is judged by is <see cref="ShadowMapRenderer.StaticRebuilds" />:
///         "it caches" is otherwise a claim nothing can check.
///     </para>
/// </remarks>
public class ShadowCacheTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });
    readonly EffectSystem effects = new();

    // --- Fixture ------------------------------------------------------------

    static Effect Compiled(EffectKey key) =>
        new() { Key = key, Stages = [new(ShaderStage.Vertex, [1, 2, 3, 4], "main")] };

    sealed class AlwaysCompiles : IEffectProvider {
        public Effect? TryGet(EffectKey key) => Compiled(key);
    }

    sealed class Harness : IDisposable {
        public required RenderSystem System { get; init; }
        public required GraphicsCompositor Compositor { get; init; }
        public required RenderGraph Graph { get; init; }
        public required RenderStage Statics { get; init; }
        public required RenderStage Movers { get; init; }
        public required ShadowMapRenderer Shadows { get; init; }
        public required MeshRenderFeature Meshes { get; init; }
        public required MaterialRenderFeature Materials { get; init; }
        public required BufferHandle Vertices { get; init; }

        public void Dispose() {
            Graph.DisposePool();
            System.Dispose();
        }
    }

    ImportedTexture Depth(Int2 size, string name) {
        var description = new TextureDescription(
            PixelFormat.Depth32Float,
            size.X,
            size.Y,
            TextureUsage.DepthStencilTarget | TextureUsage.Sampled | TextureUsage.CopySource
            | TextureUsage.CopyDestination,
            Name: name
        );

        var texture = device.CreateTexture(description);
        return new(texture, device.CreateTextureView(texture), description);
    }

    Harness Build(bool cached = true) {
        var system = new RenderSystem();

        var meshes = new MeshRenderFeature {
            Pipelines = new(device),
            Describer = new EffectPipelineDescriber(device)
        };

        var materials = new MaterialRenderFeature { Effects = effects };
        meshes.Add(materials);
        system.AddFeature(meshes);
        effects.AddProvider(new AlwaysCompiles());

        var statics = system.AddStage(new("ShadowCasterStatic"));
        var movers = system.AddStage(new("ShadowCaster"));

        var shadows = new ShadowMapRenderer {
            Name = "Shadows",
            CasterStage = movers,
            Atlas = "ShadowAtlas",
            CascadeCount = 2,
            Resolution = 256,
            ShadowDistance = 100f,
            Slack = 0.25f,
            Eye = Vector3.Zero,
            Forward = new(0f, 0f, -1f),
            LightDirection = Vector3.Normalize(new(0f, -1f, 0f))
        };

        var size = shadows.AtlasSize;
        var compositor = new GraphicsCompositor(system) { Game = shadows, FrameSize = size };

        // Both atlases are imports. The working one because nothing in this fixture samples it, and
        // the cache because a cache outlives its frame by definition — the graph's pool exists to
        // recycle memory whose lifetime ends inside one.
        compositor.Imports["ShadowAtlas"] = Depth(size, "ShadowAtlas");

        if (cached) {
            shadows.StaticCasterStage = statics;
            shadows.StaticAtlas = "ShadowCache";
            compositor.Imports["ShadowCache"] = Depth(size, "ShadowCache");
        }

        return new() {
            System = system,
            Compositor = compositor,
            Graph = new(device),
            Statics = statics,
            Movers = movers,
            Shadows = shadows,
            Meshes = meshes,
            Materials = materials,
            Vertices = device.CreateBuffer(new() { Size = 1024, Usage = BufferUsage.Vertex })
        };
    }

    static void AddCaster(Harness h, RenderStage stage, Vector3 at, float radius = 30f) {
        var id = h.System.Objects.Add(
            new() { Bounds = new(at, radius), Stages = stage.Mask, FeatureIndex = h.Meshes.Index }
        );

        h.System.Objects.Data.Data(h.Meshes.Draws)[id.Index] = new() {
            VertexBuffer = h.Vertices, Count = 3, InstanceCount = 1
        };

        h.Materials.Assign(h.System, id, new("DepthOnly"));
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

    int PassesNamed(string name) =>
        device.Recorder!
            .OfKind(RecordedCommandKind.BeginRenderPass)
            .Count(command => string.Equals(command.Text, name, StringComparison.Ordinal));

    /// <inheritdoc />
    public void Dispose() {
        device.Dispose();
        GC.SuppressFinalize(this);
    }

    // --- The cache ----------------------------------------------------------

    /// <summary>
    ///     A settled scene draws its static casters once and copies the result thereafter.
    /// </summary>
    /// <remarks>
    ///     Three frames, one rebuild. The copy is not free — it is a whole depth atlas per frame —
    ///     so this is a trade rather than a win: it pays when a level's worth of geometry would
    ///     otherwise be rasterised into every cascade every frame.
    /// </remarks>
    [Fact]
    public void A_settled_scene_rebuilds_the_cache_once() {
        using var h = Build();
        AddCaster(h, h.Statics, new(0f, 0f, -10f));

        Frame(h);
        Frame(h);
        Frame(h);

        Assert.Equal(1, h.Shadows.StaticRebuilds);
        Assert.Equal(1, PassesNamed("Shadows.Static"));
        Assert.Equal(3, PassesNamed("Shadows"));
        Assert.Equal(3, device.Recorder!.CountOf(RecordedCommandKind.CopyTexture));
    }

    /// <summary>
    ///     A static caster is drawn once ever; a moving one is drawn every frame.
    /// </summary>
    /// <remarks>
    ///     The point of the whole arrangement, and the reason a stage is the right filter: neither
    ///     the mesh feature nor the render system knows anything about caching, and the split is
    ///     "which stage did the host put this object in".
    /// </remarks>
    [Fact]
    public void A_static_caster_is_drawn_once_and_a_mover_every_frame() {
        using var h = Build();
        AddCaster(h, h.Statics, new(0f, 0f, -10f));
        AddCaster(h, h.Movers, new(0f, 0f, -12f));

        Frame(h);
        Frame(h);
        Frame(h);

        // Two cascades for the static caster, once; two cascades for the mover, three times.
        Assert.Equal(2 + (2 * 3), device.Recorder!.CountOf(RecordedCommandKind.Draw));
    }

    /// <summary>Saying the static content changed redraws it.</summary>
    /// <remarks>
    ///     Supplied rather than detected, because "static" is a claim the scene makes and the
    ///     renderer cannot check it — a host that moves a static caster without saying so gets a
    ///     shadow where the object used to be, which is the bargain the word already implied.
    /// </remarks>
    [Fact]
    public void Bumping_the_static_version_rebuilds_the_cache() {
        using var h = Build();
        AddCaster(h, h.Statics, new(0f, 0f, -10f));

        Frame(h);
        h.Shadows.StaticVersion++;
        Frame(h);

        Assert.Equal(2, h.Shadows.StaticRebuilds);
    }

    // --- What slack buys ----------------------------------------------------

    /// <summary>
    ///     A camera moving inside the slack does not re-fit, and therefore does not redraw.
    /// </summary>
    /// <remarks>
    ///     This is the whole reason `Slack` exists. With a tight fit the projection changes the
    ///     moment the camera moves a texel, and a cache that is invalidated every frame is not a
    ///     cache — it is a copy plus a redraw.
    /// </remarks>
    [Fact]
    public void A_camera_move_inside_the_slack_keeps_the_cache() {
        using var h = Build();
        AddCaster(h, h.Statics, new(0f, 0f, -10f));

        Frame(h);

        for (var step = 1; step <= 4; step++) {
            h.Shadows.Eye = new(0f, 0f, -0.25f * step);
            Frame(h);
        }

        Assert.Equal(1, h.Shadows.StaticRebuilds);
    }

    /// <summary>And a camera moving out of it does re-fit, so the cascade still covers its slice.</summary>
    /// <remarks>
    ///     The other direction. A cascade kept past the point where it covers the slice would leave
    ///     the far edge of the world unshadowed, which is worse than redrawing it.
    /// </remarks>
    [Fact]
    public void A_camera_move_past_the_slack_rebuilds_it() {
        using var h = Build();
        AddCaster(h, h.Statics, new(0f, 0f, -10f));

        Frame(h);

        h.Shadows.Eye = new(0f, 0f, -60f);
        Frame(h);

        Assert.Equal(2, h.Shadows.StaticRebuilds);
    }

    /// <summary>Slack costs resolution, and the number says how much.</summary>
    /// <remarks>
    ///     The same texels over 1.5625 times the area at twenty-five per cent. Worth stating as an
    ///     assertion rather than a comment, because it is the price of everything above.
    /// </remarks>
    [Fact]
    public void Slack_widens_the_cascade_by_exactly_what_it_says() {
        var tight = ShadowCascades.Fit(
            Vector3.Zero, new(0f, 0f, -1f), new(0f, 1f, 0f), new(0f, -1f, 0f),
            MathF.PI / 3f, 16f / 9f, 1f, 50f, 1024
        );

        var slack = ShadowCascades.Fit(
            Vector3.Zero, new(0f, 0f, -1f), new(0f, 1f, 0f), new(0f, -1f, 0f),
            MathF.PI / 3f, 16f / 9f, 1f, 50f, 1024, slack: 0.25f
        );

        Assert.Equal(tight.Radius * 1.25f, slack.Radius, 3);
    }

    // --- The uncached path --------------------------------------------------

    /// <summary>With no static stage nothing is cached, and the frame is what it always was.</summary>
    [Fact]
    public void Without_a_static_stage_there_is_one_pass_and_no_copy() {
        using var h = Build(cached: false);
        AddCaster(h, h.Movers, new(0f, 0f, -10f));

        Frame(h);
        Frame(h);

        Assert.Equal(0, h.Shadows.StaticRebuilds);
        Assert.Equal(2, PassesNamed("Shadows"));
        Assert.Empty(device.Recorder!.OfKind(RecordedCommandKind.CopyTexture));
    }

    /// <summary>
    ///     A cache whose atlas name is bound to nothing is refused, not worked around.
    /// </summary>
    /// <remarks>
    ///     The only sensible fallback would be to draw the moving casters and silently lose every
    ///     static one, which is a level with no shadows on it and nothing anywhere saying why. Names
    ///     are the one thing a document can get wrong, so a wrong one is reported like every other.
    /// </remarks>
    [Fact]
    public void A_cache_bound_to_nothing_is_refused() {
        using var h = Build();
        h.Shadows.StaticAtlas = "NotBound";
        AddCaster(h, h.Statics, new(0f, 0f, -10f));

        var thrown = Assert.Throws<CompositorBindingException>(() => Frame(h));

        Assert.Equal("NotBound", thrown.Name);
        Assert.Equal("target", thrown.Kind);
    }

    /// <summary>A static stage with no atlas at all is refused for the same reason.</summary>
    [Fact]
    public void A_static_stage_with_no_atlas_is_refused() {
        using var h = Build();
        h.Shadows.StaticAtlas = string.Empty;
        AddCaster(h, h.Statics, new(0f, 0f, -10f));

        Assert.Throws<CompositorBindingException>(() => Frame(h));
    }
}
