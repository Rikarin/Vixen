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
using Vixen.Vfx;
using Xunit;

namespace Tests;

/// <summary>
///     An effect authored as <c>Vfx/Output/Light</c>, from the node to the light list.
/// </summary>
/// <remarks>
///     <para>
///         <b><see cref="ParticleLights.Collect" /> had no caller, and the node that produces the
///         renderer it reads has shipped in the editor's library the whole time.</b>
///         <see cref="ParticleRenderFeature.Prepare" /> fell through <c>_ =&gt; Quads(…)</c> for a
///         <see cref="VfxRendererKind.Light" /> effect, so an author who wired the Light output got
///         billboards, no light, and no diagnostic — a failure that presents as a lighting bug in
///         their own scene.
///     </para>
///     <para>
///         Two halves, and both are asserted here: the lights reach a list somebody is filling, and
///         the frame <em>says so</em> when nobody is. A pass that constructs cleanly and emits
///         nothing passes every structural test, which is why the second half is not optional.
///     </para>
/// </remarks>
public class ParticleLightFeatureTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });
    readonly EffectSystem effects = new();

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
        public required RenderGraph Graph { get; init; }
        public required RenderStage Transparent { get; init; }
        public required RenderView Camera { get; init; }
        public required ParticleRenderFeature Particles { get; init; }
        public required MaterialRenderFeature Materials { get; init; }
        public List<RenderLight> Lights { get; } = [];

        public void Dispose() {
            Graph.DisposePool();
            Particles.Dispose();
            System.Dispose();
        }
    }

    Harness Build(bool collecting = true) {
        var system = new RenderSystem();
        var transparent = system.AddStage(new("Transparent"));

        var particles = new ParticleRenderFeature {
            Device = device,
            Pipelines = new(device),
            Describer = new EffectPipelineDescriber(device)
        };

        var materials = new MaterialRenderFeature { Effects = effects };

        particles.Add(materials);
        system.AddFeature(particles);
        effects.AddProvider(new AlwaysCompiles());

        var view = Matrix4x4.LookAt(new(0f, 0f, -10f), Vector3.Zero, Vector3.UnitY);
        var projection = Matrix4x4.PerspectiveFieldOfView(MathF.PI / 3f, 1f, 0.1f, 1000f);

        var camera = new RenderView("camera") {
            Stages = transparent.Mask,
            Position = new(0f, 0f, -10f),
            Camera = new(new(0f, 0f, -10f), Vector3.UnitZ, Vector3.UnitY, MathF.PI / 3f, 1f, 0.1f, 1000f),
            Frustum = new(view * projection)
        };

        system.SetViews([camera]);

        var harness = new Harness {
            System = system,
            Compositor = new(system) { FrameSize = new(16, 16) },
            Graph = new(device),
            Transparent = transparent,
            Camera = camera,
            Particles = particles,
            Materials = materials
        };

        if (collecting) {
            particles.Lights = harness.Lights;
        }

        return harness;
    }

    /// <summary>A burst of light-emitting particles, all at one point.</summary>
    /// <remarks>
    ///     Stepped twice, because <see cref="VfxSystem.Step" /> updates before it spawns: one step
    ///     leaves the burst freshly initialised and never updated, which is a state no frame sees.
    /// </remarks>
    static VfxSystem Emitting(int count, float intensity = 2f, float range = 3f) {
        var effect = new VfxSystem(
            VfxCompiledGraph.Compile(
                [VfxSpawner.Burst(count)],
                [
                    new(VfxOpcode.SetPosition, new Vector4(1f, 2f, 3f, 0f)),
                    new(VfxOpcode.SetSize, new Vector4(1f, 1f, 0f, 0f)),
                    new(VfxOpcode.SetColour, Vector4.One),
                    new(VfxOpcode.SetLifetime, new Vector4(100f, 100f, 0f, 0f))
                ],
                [],
                Math.Max(count, 1),
                VfxRenderer.Light(intensity, range)
            )
        );

        effect.Step(1f / 60f);
        effect.Step(1f / 60f);

        return effect;
    }

    /// <summary>A burst of ordinary billboards, for the contrast every assertion here needs.</summary>
    static VfxSystem Drawing(int count) {
        var effect = new VfxSystem(
            VfxCompiledGraph.Compile(
                [VfxSpawner.Burst(count)],
                [
                    new(VfxOpcode.SetPosition, new Vector4(0f, 0f, 0f, 0f)),
                    new(VfxOpcode.SetSize, new Vector4(0.2f, 0.2f, 0f, 0f)),
                    new(VfxOpcode.SetColour, Vector4.One),
                    new(VfxOpcode.SetLifetime, new Vector4(100f, 100f, 0f, 0f))
                ],
                [],
                Math.Max(count, 1),
                VfxRenderer.Billboard
            )
        );

        effect.Step(1f / 60f);
        effect.Step(1f / 60f);

        return effect;
    }

    static RenderObjectId Add(Harness h, VfxSystem effect) {
        var id = h.System.Objects.Add(
            new() {
                Bounds = new(Vector3.Zero, 4f),
                Stages = h.Transparent.Mask,
                FeatureIndex = h.Particles.Index
            }
        );

        h.Particles.SetSystem(id, effect);
        h.Materials.Assign(h.System, id, new Material("Particle"));

        return id;
    }

    /// <summary>One frame: collect the lights the way extraction does, then run the phases.</summary>
    static void Frame(Harness h) {
        h.Lights.Clear();
        h.Particles.CollectLights();
        h.System.Draw();
    }

    ICommandList Record(Harness h) {
        Frame(h);

        var target = device.CreateTextureView(
            device.CreateTexture(
                new() {
                    Width = 16, Height = 16, Depth = 1,
                    MipLevels = 1, ArrayLayers = 1, SampleCount = 1,
                    Format = PixelFormat.Rgba8UNorm, Usage = TextureUsage.ColourTarget
                }
            )
        );

        var list = device.BeginCommandList();
        list.BeginRenderPass(new([new(target)], name: "Transparent"));

        h.System.Record(
            h.Camera,
            h.Transparent,
            new(list, effects) { Device = device, Output = new([PixelFormat.Rgba8UNorm]) }
        );

        list.EndRenderPass();
        list.Finish();
        device.GraphicsQueue.Submit([list]);

        return list;
    }

    /// <inheritdoc />
    public void Dispose() {
        device.Dispose();
        GC.SuppressFinalize(this);
    }

    // --- The lights ---------------------------------------------------------

    /// <summary>Every particle of a light effect is a point light where it is.</summary>
    [Fact]
    public void A_light_effect_reaches_the_light_list() {
        using var h = Build();
        using var effect = Emitting(6);

        Add(h, effect);
        Frame(h);

        Assert.Equal(6, h.Lights.Count);
        Assert.Equal(6, h.Particles.CollectedLights);
        Assert.Equal(0, h.Particles.RefusedLights);

        Assert.All(h.Lights, light => {
            Assert.Equal(LightKind.Point, light.Kind);
            Assert.Equal(new Vector3(1f, 2f, 3f), light.Position);
            Assert.Equal(2f, light.Intensity);
            Assert.Equal(3f, light.Range);
        });
    }

    /// <summary>
    ///     A light effect submits no geometry, which is the substitution that used to be silent.
    /// </summary>
    /// <remarks>
    ///     <b>The sabotage this file exists for.</b> Restoring <c>_ =&gt; Quads(effect, camera)</c> for
    ///     a <see cref="VfxRendererKind.Light" /> effect puts six billboards in the command stream and
    ///     fails here — which is exactly the picture an author got: the sparks brightened, the wall
    ///     did not.
    /// </remarks>
    [Fact]
    public void A_light_effect_draws_nothing_at_all() {
        using var h = Build();
        using var effect = Emitting(6);

        Add(h, effect);
        Record(h);

        Assert.Empty(device.Recorder!.OfKind(RecordedCommandKind.DrawIndexed));
        Assert.Equal(0, h.Particles.LastParticleCount);
        Assert.Equal(1, h.Particles.LightEffects);
    }

    /// <summary>A frame holding both kinds draws one of them and lights with the other.</summary>
    [Fact]
    public void A_billboard_beside_a_light_still_draws() {
        using var h = Build();
        using var lit = Emitting(4);
        using var drawn = Drawing(5);

        Add(h, lit);
        Add(h, drawn);
        Record(h);

        var draw = Assert.Single(device.Recorder!.OfKind(RecordedCommandKind.DrawIndexed));

        // Six indices a particle, for the five billboards alone: the four lights contributed none.
        Assert.Equal(30, draw.A);
        Assert.Equal(5, h.Particles.LastParticleCount);
        Assert.Equal(4, h.Lights.Count);
    }

    /// <summary>An effect detached from its object lights nothing.</summary>
    /// <remarks>
    ///     <see cref="ParticleRenderFeature.Systems" /> keeps an effect after its object stopped
    ///     pointing at it, so a collection that walked that list would light a scene from an emitter
    ///     the frame does not draw — and would keep doing it for the life of the feature.
    /// </remarks>
    [Fact]
    public void A_detached_effect_lights_nothing() {
        using var h = Build();
        using var effect = Emitting(6);

        var id = Add(h, effect);

        Frame(h);
        Assert.Equal(6, h.Lights.Count);

        h.Particles.SetSystem(id, null);
        Frame(h);

        Assert.Empty(h.Lights);
        Assert.Equal(0, h.Particles.CollectedLights);
    }

    /// <summary>The budget is the frame's, not each effect's, and what it refuses is counted.</summary>
    /// <remarks>
    ///     <c>SceneLighting.Dropped</c>'s shape: a scene at its budget is normal for a deliberate
    ///     effect and a mistake for an accidental one, and only the author can tell which — so the
    ///     overflow is reported rather than thrown or logged.
    /// </remarks>
    [Fact]
    public void The_scene_budget_is_shared_between_every_effect() {
        using var h = Build();
        using var first = Emitting(8);
        using var second = Emitting(8);

        h.Particles.MaxLights = 10;

        Add(h, first);
        Add(h, second);
        Frame(h);

        Assert.Equal(10, h.Lights.Count);
        Assert.Equal(10, h.Particles.CollectedLights);

        // Six refused, not eight: the first effect fitted whole and the second was cut off at two.
        Assert.Equal(6, h.Particles.RefusedLights);
    }

    /// <summary>A budget of none is an effect switched off rather than an argument to object to.</summary>
    [Fact]
    public void A_budget_of_none_contributes_nothing() {
        using var h = Build();
        using var effect = Emitting(6);

        h.Particles.MaxLights = 0;

        Add(h, effect);
        Frame(h);

        Assert.Empty(h.Lights);
        Assert.Equal(6, h.Particles.RefusedLights);
    }

    // --- The degrade --------------------------------------------------------

    /// <summary>A light effect nobody is collecting is reported rather than lost.</summary>
    /// <remarks>
    ///     The state that makes this worse than an unreached type: the effect simulates every frame,
    ///     costs its particles, draws nothing and lights nothing. Every counter stays healthy.
    /// </remarks>
    [Fact]
    public void A_light_nothing_collects_is_reported() {
        using var h = Build(collecting: false);
        using var effect = Emitting(6);

        Add(h, effect);
        h.System.Draw();

        Assert.NotNull(h.Particles.Degraded);
        Assert.Contains("light", h.Particles.Degraded, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CollectLights", h.Particles.Degraded, StringComparison.Ordinal);
    }

    /// <summary>A host that set the sink and then stopped collecting is reported on the next frame.</summary>
    /// <remarks>
    ///     ⚠ This frame, not the worst frame there has ever been — the property that makes the answer
    ///     worth reading, and the one a latched flag would destroy.
    /// </remarks>
    [Fact]
    public void A_collection_that_stops_is_reported_the_next_frame() {
        using var h = Build();
        using var effect = Emitting(6);

        Add(h, effect);
        Frame(h);

        Assert.Null(h.Particles.Degraded);

        // The frame that forgot: the phases run, nothing collected.
        h.System.Draw();

        Assert.NotNull(h.Particles.Degraded);

        // And recovering is as visible as degrading.
        Frame(h);

        Assert.Null(h.Particles.Degraded);
    }

    /// <summary>An overflowing budget is reported too, and says which number to move.</summary>
    [Fact]
    public void A_refused_light_is_reported() {
        using var h = Build();
        using var effect = Emitting(8);

        h.Particles.MaxLights = 3;

        Add(h, effect);
        Frame(h);

        Assert.NotNull(h.Particles.Degraded);
        Assert.Contains("MaxLights", h.Particles.Degraded, StringComparison.Ordinal);
    }

    /// <summary>A frame with no light effects in it says nothing.</summary>
    /// <remarks>
    ///     The half that stops the channel becoming noise: a billboard effect is not a degraded light
    ///     effect, and a feature that reported one would make <c>Degradations</c> useless.
    /// </remarks>
    [Fact]
    public void A_billboard_effect_never_degrades() {
        using var h = Build(collecting: false);
        using var effect = Drawing(6);

        Add(h, effect);
        h.System.Draw();

        Assert.Null(h.Particles.Degraded);
    }

    /// <summary>
    ///     The feature's reason reaches <see cref="GraphicsCompositor.Degradations" /> through the node
    ///     that drew it.
    /// </summary>
    /// <remarks>
    ///     A render feature is not in the compositor tree, so nothing could ever collect one of these.
    ///     <see cref="SingleStageRenderer" /> is the smallest thing that knows both which features
    ///     exist and which stage they drew into.
    /// </remarks>
    [Fact]
    public void The_stage_node_carries_the_features_reason() {
        using var h = Build(collecting: false);
        using var effect = Emitting(6);

        Add(h, effect);

        h.Compositor.Game = new SingleStageRenderer {
            Name = "Camera/Embers",
            View = h.Camera,
            Stage = h.Transparent
        };

        h.Graph.Reset();
        h.Compositor.Build(h.Graph, effects, device);

        List<(string Node, string Reason)> found = [];

        Assert.Equal(1, h.Compositor.Degradations(found));
        Assert.Equal("Camera/Embers", found[0].Node);
        Assert.Contains("Particle", found[0].Reason, StringComparison.Ordinal);
    }

    /// <summary>A healthy frame puts nothing in the list.</summary>
    [Fact]
    public void A_collected_frame_reports_no_degradation() {
        using var h = Build();
        using var effect = Emitting(6);

        Add(h, effect);

        h.Compositor.Game = new SingleStageRenderer { View = h.Camera, Stage = h.Transparent };

        h.Lights.Clear();
        h.Particles.CollectLights();
        h.Graph.Reset();
        h.Compositor.Build(h.Graph, effects, device);

        List<(string Node, string Reason)> found = [];

        Assert.Equal(0, h.Compositor.Degradations(found));
        Assert.Equal(6, h.Lights.Count);
    }

    /// <summary>A stage the feature drew nothing into does not repeat somebody else's reason.</summary>
    /// <remarks>
    ///     What stops one feature's degrade appearing under every node in the tree, which would make
    ///     the list say a shadow pass had a particle problem.
    /// </remarks>
    [Fact]
    public void A_stage_the_feature_did_not_draw_into_stays_quiet() {
        using var h = Build(collecting: false);
        using var effect = Emitting(6);

        var other = h.System.AddStage(new("Opaque"));

        Add(h, effect);

        h.Compositor.Game = new SceneRendererSequence {
            Children = {
                new SingleStageRenderer { Name = "Camera/Embers", View = h.Camera, Stage = h.Transparent },
                new SingleStageRenderer { Name = "Camera/Opaque", View = h.Camera, Stage = other }
            }
        };

        h.Graph.Reset();
        h.Compositor.Build(h.Graph, effects, device);

        List<(string Node, string Reason)> found = [];

        Assert.Equal(1, h.Compositor.Degradations(found));
        Assert.Equal("Camera/Embers", found[0].Node);
    }
}
