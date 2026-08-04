// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Vixen.App;
using Vixen.Core.Mathematics;
using Vixen.Engine.Renderer;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.DistanceFields;
using Vixen.Rendering.IrradianceFields;
using Vixen.Rendering.Lighting;
using Vixen.Rendering.Materials;
using Vixen.Rendering.PostFx;
using Vixen.Rendering.SurfaceCache;
using Vixen.Shaders;

namespace Vixen.Samples.ThirdPersonShooter;

/// <summary>One authored box, as the level states it — the shared input of every field this project bakes.</summary>
/// <param name="Position">Where the entity stands.</param>
/// <param name="Rotation">Which way it is turned.</param>
/// <param name="Centre">The box's centre, relative to the entity.</param>
/// <param name="Half">Half the box's size on each axis.</param>
/// <remarks>
///     Its own type rather than a tuple because three consumers read it — the clipmap's instances,
///     the surface cache's cards and (through the first) the collision-shaped occlusion the level
///     already had — and a tuple with four <see cref="Vector3" />-shaped members is an argument
///     order mistake waiting for its third caller.
/// </remarks>
readonly record struct ArenaBox(Vector3 Position, Quaternion Rotation, Vector3 Centre, Vector3 Half);

/// <summary>The host-owned half of the frame's global illumination and its GPU culling.</summary>
/// <remarks>
///     <para>
///         <b>Every object here is one a document cannot create</b>, gathered into one type so that
///         <c>Arena.SupplyFrame</c> stays a narrative rather than a page of assignments. The
///         document's <c>!GpuCulling</c>, <c>!HiZ</c>, <c>!SurfaceCache</c>, <c>!ScreenProbeGather</c>
///         and <c>!Reflections</c> nodes each capture what the builder holds at reload — a
///         visibility group, two depth pyramids, a card store, four probe dispatches — and a node
///         built with nothing does nothing, which is exactly what the first build (before this game
///         exists) gets.
///     </para>
///     <para>
///         <b>The surface cache is captured and lit at load, on the terms the clipmap already
///         earned.</b> This level is boxes whose exact half-extents the scene states, so the traced
///         reference capture over the composited clipmap is exact where a runtime rasterising
///         capture would be a scene-draw delegate this project does not own. The CPU radiosity then
///         lights and bounces the store once — the sun stands still (<c>Arena.SunPeriod</c> is zero)
///         so a static answer is the honest one — and that CPU-side radiance is what the irradiance
///         filler's rays read through <see cref="SurfaceCacheRadiance" />. Per frame, the two
///         <em>device</em> dispatches keep the device planes by the same arithmetic, which is the
///         shipping path and what the screen-probe trace samples behind its hits.
///     </para>
///     <para>
///         ⚠ <b><see cref="Feed" /> runs every frame, and skipping it is a silent dark.</b> The
///         compute fills read their composed sources out of their own parameter collections, and
///         the textures behind those names are made by compositor nodes on their first record — an
///         object this type cannot reach at wire time and that a document reload replaces. So the
///         names are re-asserted each frame, which costs nothing when nothing changed: re-writing
///         an equal value does not bump a <c>ParameterCollection</c>'s version.
///     </para>
/// </remarks>
sealed class ArenaIllumination : IDisposable {
    /// <summary>Texels per world unit along a card's axes. Coarse deliberately — a card feeds
    ///     bounce light, not a close-up — and the atlas budget is what it buys.</summary>
    const float CardTexelsPerUnit = 0.5f;

    /// <summary>The most texels a card gets along either axis, so the floor cannot eat the atlas.</summary>
    const int CardMaxResolution = 32;

    /// <summary>How far past a box's surface its cards' boxes reach, so capture rays start outside.</summary>
    const float CardMargin = 0.15f;

    /// <summary>How far the CPU lighting's rays look. The arena is 64 m across; further buys steps, not light.</summary>
    const float TraceDistance = 40f;

    bool disposed;

    ArenaIllumination() { }

    /// <summary>The culling dispatch's group — what <c>RenderSystem.Visibility</c> becomes.</summary>
    public GpuVisibilityGroup Visibility { get; private set; } = null!;

    /// <summary>The depth pyramid the document's <c>!HiZ</c> builds and next frame's cull tests.</summary>
    public HiZPyramid Occluders { get; private set; } = null!;

    /// <summary>The nearest-reduced chain the screen marches skip by — not the culling pyramid,
    ///     because the two reductions answer opposite questions about a cell.</summary>
    public HiZPyramid TraceChain { get; private set; } = null!;

    /// <summary>The card store — § L4's cache, captured at load and kept by the document's node.</summary>
    public SurfaceCacheStore Cache { get; private set; } = null!;

    /// <summary>The direct-light dispatch the cache node records each frame.</summary>
    public SurfaceCacheLightFill CacheLight { get; private set; } = null!;

    /// <summary>The bounce dispatch, whose record advances the cache's swap.</summary>
    public SurfaceCacheGatherFill CacheBounce { get; private set; } = null!;

    /// <summary>What fills the irradiance field's probes, on the CPU, budgeted by the document.</summary>
    public TracedIrradianceFiller Filler { get; private set; } = null!;

    /// <summary>The screen-probe dispatches — the gather node captures all four at build.</summary>
    public ScreenProbeTraceFill ProbeTracer { get; private set; } = null!;

    /// <inheritdoc cref="ProbeTracer" />
    public ScreenProbeResolve ProbeResolver { get; private set; } = null!;

    /// <inheritdoc cref="ProbeTracer" />
    public ScreenProbeAccumulateFill ProbeAccumulator { get; private set; } = null!;

    /// <inheritdoc cref="ProbeTracer" />
    public ScreenProbeFilterFill ProbeFilter { get; private set; } = null!;

    /// <summary>How many cards the level's boxes produced.</summary>
    public int CardCount { get; private set; }

    /// <summary>How many cards the atlas had no room for. Nonzero is a smaller level than this one.</summary>
    public int CardsDropped { get; private set; }

    /// <summary>How many texels the load-time capture found a surface for.</summary>
    public int CapturedTexels { get; private set; }

    /// <summary>The largest change the last load-time gather saw — near zero is a converged cache.</summary>
    public float RadiosityChange { get; private set; }

    /// <summary>The full-sphere average of the Preetham sky, for the single-colour sky knobs.</summary>
    public Vector3 AverageSky { get; private set; }

    /// <summary>Builds every host-owned object the frame's GI and culling nodes need, and hands
    ///     them to the builder for the reload that follows.</summary>
    /// <param name="graphics">The host's graphics, for the device and the effect system.</param>
    /// <param name="field">The clipmap the frame composites — the one geometry every trace here marches.</param>
    /// <param name="frame">The baked sky, for the radiance every miss answers with.</param>
    /// <param name="sunDirection">Which way the level's sun sends its light.</param>
    /// <param name="boxes">The level's authored boxes.</param>
    /// <param name="instances">The same boxes as distance fields, for the load-time composite.</param>
    /// <param name="logger">Where the wire-up reports itself.</param>
    /// <returns>The wired illumination.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    ///     ⚠ <b>Before the document reload, or the nodes capture nulls</b> — the same ordering
    ///     <c>Arena.SupplyFrame</c> already states for the field slots, extended to the eleven this
    ///     fills. And the clipmap is composited <em>here</em>, once, at the arena's centre, because
    ///     the capture and the radiosity below march it at load — the node recomposites it at the
    ///     camera on the first frame and owns it from then on.
    /// </remarks>
    public static ArenaIllumination Wire(
        AppGraphics graphics,
        GlobalDistanceField field,
        ArenaFrame frame,
        Vector3 sunDirection,
        List<ArenaBox> boxes,
        List<DistanceFieldInstance> instances,
        ILogger logger
    ) {
        ArgumentNullException.ThrowIfNull(graphics);
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(boxes);
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(logger);

        var device = graphics.Device;
        var effects = graphics.Effects;
        var builder = graphics.Renderer.Host.Builder;

        // One compute pipeline cache for everything wired here, because a pipeline is keyed by
        // module and layout and lives as long as the device — a cache per consumer would compile
        // the same module once per consumer.
        var pipelines = new ComputePipelineCache(device);
        var wired = new ArenaIllumination();

        wired.AverageSky = AverageSkyRadiance(frame);

        // From the surface toward the sun, and the sun's irradiance on a perpendicular surface —
        // the same two numbers SunFromSky writes onto the light, so the cards and the level are lit
        // by one sun.
        var towardSun = Vector3.Normalize(-sunDirection);
        var sunIrradiance = frame.SunIlluminance
            * new Vector3(frame.SunTint.R, frame.SunTint.G, frame.SunTint.B);

        // ── doc 22 § culling: the group, and the two pyramids ───────────────────────────────────
        // The group is what RenderSystem.Visibility becomes when the document's !GpuCulling node is
        // built; the Furthest pyramid is what its occlusion half tests and what the !HiZ node
        // reduces; the Nearest chain is the screen marches' — a different object on purpose,
        // because a chain of farthest texels skips rays through walls.
        wired.Visibility = new(device) { Effects = effects, Pipelines = pipelines };
        wired.Occluders = new(device) { Effects = effects, Pipelines = pipelines };

        wired.TraceChain = new(device) {
            Effects = effects,
            Pipelines = pipelines,
            Reduction = HiZReduction.Nearest
        };

        builder.Visibility = wired.Visibility;
        builder.Occluders = wired.Occluders;
        builder.TracePyramid = wired.TraceChain;

        // The reflections node resolves its kernel through the effect system rather than through
        // shader modules, and two effect systems are two variant caches disagreeing about one
        // shader — so it gets the same one every material compiles through.
        builder.Effects = effects;

        // ── doc 19 § L4: the cards, captured and lit before the first frame ─────────────────────
        // The composite first, because everything below marches it. Centred on the arena rather
        // than on a camera that does not exist yet; level 3 of the default clipmap reaches ±64 m,
        // which holds the whole 64 m level.
        field.Update(new(0f, 4f, 0f), CollectionsMarshal.AsSpan(instances), parallel: true);

        wired.Cache = new(new SurfaceCacheAtlas(new(256, 256)));

        foreach (var box in boxes) {
            wired.AddCards(box);
        }

        // The traced reference capture, not the runtime one. SurfaceCardCapture wants a delegate
        // that draws the scene from a card's own camera, which is machinery this project does not
        // own — and does not need: a level of exact boxes captured through an exact field has no
        // bake error for a rasteriser to improve on. A level of real meshes is where the runtime
        // capture earns its slots.
        var capture = new TracedCardCapture(field, new Concrete());

        for (var card = 0; card < wired.Cache.Cards.Count; card++) {
            wired.CapturedTexels += capture.Capture(wired.Cache, card);
        }

        // And lit, once, on the CPU. The sun stands still — Arena.SunPeriod is zero — so a static
        // answer is the honest one, and it is the only answer the CPU irradiance filler below can
        // read: the per-frame dispatches author the *device* planes, which no CPU ray can sample.
        // Two gathers, because the second carries the first bounce's light into the corners and the
        // third would move things by less than the ray noise it costs.
        var radiosity = new CardRadiosity(field) {
            Rays = 12,
            MaxDistance = TraceDistance,
            Sky = direction => PhysicalSky.Radiance(direction, frame.Atmosphere)
        };

        radiosity.Light(wired.Cache, towardSun, sunIrradiance);
        radiosity.Gather(wired.Cache);
        wired.RadiosityChange = radiosity.Gather(wired.Cache);

        // The per-frame authors of the device planes — the shipping half of § L4, checked against
        // the CPU pair above by the golden tests. Their field slot is the clipmap, whose names Feed
        // re-asserts every frame once the node's texture exists.
        wired.CacheLight = new(device) {
            Effects = effects,
            Pipelines = pipelines,
            Descriptors = builder.Descriptors,
            Source = "GlobalDistanceField",
            TowardSun = towardSun,
            SunIrradiance = sunIrradiance,
            MaxDistance = TraceDistance
        };

        wired.CacheBounce = new(device) {
            Effects = effects,
            Pipelines = pipelines,
            Descriptors = builder.Descriptors,
            Source = "GlobalDistanceField",
            CacheSource = MaterialCompiler.SurfaceCacheShader,
            SkyColour = wired.AverageSky,
            MaxDistance = TraceDistance
        };

        builder.SurfaceCache = wired.Cache;
        builder.SurfaceCacheLightFill = wired.CacheLight;
        builder.SurfaceCacheGatherFill = wired.CacheBounce;

        // ── doc 19 § L2: what fills the probe field ─────────────────────────────────────────────
        // The CPU filler, over the same clipmap the frame composites and the cache lit above: a hit
        // answers the card's bounced radiance, a miss answers the Preetham sky, so the probes carry
        // real multi-bounce light in the same cd/m² everything else here is lit in. The budget —
        // and therefore the whole CPU cost — is the document's; see the !IrradianceField node.
        wired.Filler = new(
            field,
            new SurfaceCacheRadiance(wired.Cache, new PreethamRadiance(frame)),
            new IrradianceFillSettings {
                RayCount = 32,
                MaxDistance = TraceDistance,

                // Half the previous answer survives a refill: enough to average the 32-ray noise
                // away under a static sun, small enough that the field converges in two sweeps.
                Hysteresis = 0.5f,
                SunDirection = towardSun
            }
        );

        builder.IrradianceFiller = wired.Filler;

        // ── doc 19 § L3: the screen-probe chain ─────────────────────────────────────────────────
        // Wired one increment before the document's !ScreenProbeGather turned on, which is why
        // turning it on was one line there and none here. The compose sources are this scene's:
        // the clipmap behind the field slot, the probe field behind the far slot, the cards behind
        // the cache slot, the sky as the miss — so a screen ray that hits reads § L4's bounce and
        // a ray that escapes reads the same far light the material's ambient used to.
        wired.ProbeTracer = new(device) {
            Effects = effects,
            Pipelines = pipelines,
            Descriptors = builder.Descriptors,
            Source = "GlobalDistanceField",
            FarSource = MaterialCompiler.IrradianceFieldShader,
            CacheSource = MaterialCompiler.SurfaceCacheShader,
            SkyColour = wired.AverageSky,
            MaxDistance = TraceDistance
        };

        wired.ProbeResolver = new(device) {
            Effects = effects,
            Pipelines = pipelines,
            Descriptors = builder.Descriptors
        };

        wired.ProbeAccumulator = new(device) {
            Effects = effects,
            Pipelines = pipelines,
            Descriptors = builder.Descriptors
        };

        wired.ProbeFilter = new(device) {
            Effects = effects,
            Pipelines = pipelines,
            Descriptors = builder.Descriptors
        };

        builder.ScreenProbeTracer = wired.ProbeTracer;
        builder.ScreenProbeResolver = wired.ProbeResolver;
        builder.ScreenProbeAccumulator = wired.ProbeAccumulator;
        builder.ScreenProbeFilter = wired.ProbeFilter;

        SampleLog.IlluminationWired(
            logger,
            wired.CardCount,
            wired.CardsDropped,
            wired.CapturedTexels,
            wired.RadiosityChange
        );

        return wired;
    }

    /// <summary>Re-asserts the composed sources every consumer reads, against this frame's nodes.</summary>
    /// <param name="host">The renderer's host, whose builder holds the nodes the document made.</param>
    /// <remarks>
    ///     <para>
    ///         On <c>ArenaFrame.ApplySky</c>'s pattern and for its reason: the objects these names
    ///         point at are made by <c>CompositorBuilder</c> on a node's first record, so anything
    ///         captured earlier is null and anything captured once is stale after a reload. Called
    ///         from <c>ThirdPersonShooterGame.OnUpdate</c>, every frame, because re-asserting an
    ///         unchanged value costs nothing.
    ///     </para>
    ///     <para>
    ///         The reflections branch is the ApplySky pattern verbatim: <c>ReflectionRenderer.Trace</c>
    ///         exists only after the node's first build, and the composed sources live on it rather
    ///         than on the node because which field a ray marches is a fact about the scene. The
    ///         node is on now — it publishes its target into the frame's namespace, so the combine
    ///         names it — and the branch fired the frame it first built, with no wiring change
    ///         owed, which was the point of writing it against the slot rather than the schedule.
    ///     </para>
    /// </remarks>
    public void Feed(SceneRenderHost host) {
        ArgumentNullException.ThrowIfNull(host);
        ObjectDisposedException.ThrowIf(disposed, this);

        GlobalDistanceFieldRenderer? clipmap = null;
        IrradianceFieldRenderer? probes = null;
        SurfaceCacheRenderer? cache = null;
        ReflectionRenderer? reflections = null;

        foreach (var node in host.Builder.Nodes.Values) {
            switch (node) {
                case GlobalDistanceFieldRenderer distances:
                    clipmap = distances;
                    break;

                case IrradianceFieldRenderer bricks:
                    probes = bricks;
                    break;

                case SurfaceCacheRenderer atlas:
                    cache = atlas;
                    break;

                case ReflectionRenderer mirrors:
                    reflections = mirrors;
                    break;
            }
        }

        // !DistanceFieldAo's set 0 and unprojection used to be re-asserted here; the engine owns
        // both now — PostEffectFactory hands over SceneConstants and the asset's `view:` knob.

        // The clipmap's volumes, under each dispatch's own qualified prefix — a composed slot's
        // bindings are named <shader>.<source>.<binding>, so one texture is three sets of names.
        if (clipmap?.Texture is { } field) {
            field.Apply(CacheLight.Parameters, $"{SurfaceCacheLightFill.ShaderName}.GlobalDistanceField");
            field.Apply(CacheBounce.Parameters, $"{SurfaceCacheGatherFill.ShaderName}.GlobalDistanceField");
            field.Apply(ProbeTracer.Parameters, $"{ScreenProbeTraceFill.ShaderName}.GlobalDistanceField");
        }

        if (probes?.Texture is { } irradiance) {
            irradiance.Apply(
                ProbeTracer.Parameters,
                $"{ScreenProbeTraceFill.ShaderName}.{MaterialCompiler.IrradianceFieldShader}"
            );
        }

        if (cache?.Texture is { } cards) {
            cards.Apply(
                ProbeTracer.Parameters,
                $"{ScreenProbeTraceFill.ShaderName}.{MaterialCompiler.SurfaceCacheShader}"
            );
        }

        if (reflections?.Trace is { } trace) {
            trace.Source = "GlobalDistanceField";
            trace.CacheSource = MaterialCompiler.SurfaceCacheShader;
            trace.RoughSource = MaterialCompiler.IrradianceFieldShader;
            trace.MissSource = MaterialCompiler.SkyReflectionMissShader;

            trace.Parameters.Set(
                ParameterKeys.New<Vector3>(
                    $"{ReflectionTraceFill.ShaderName}.{MaterialCompiler.SkyReflectionMissShader}.missSkyColor"
                ),
                AverageSky
            );

            if (clipmap?.Texture is { } marched) {
                marched.Apply(trace.Parameters, $"{ReflectionTraceFill.ShaderName}.GlobalDistanceField");
            }

            if (probes?.Texture is { } far) {
                far.Apply(
                    trace.Parameters,
                    $"{ReflectionTraceFill.ShaderName}.{MaterialCompiler.IrradianceFieldShader}"
                );
            }

            if (cache?.Texture is { } hit) {
                hit.Apply(
                    trace.Parameters,
                    $"{ReflectionTraceFill.ShaderName}.{MaterialCompiler.SurfaceCacheShader}"
                );
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        ProbeFilter.Dispose();
        ProbeAccumulator.Dispose();
        ProbeResolver.Dispose();
        ProbeTracer.Dispose();
        CacheBounce.Dispose();
        CacheLight.Dispose();
        TraceChain.Dispose();
        Occluders.Dispose();
        Visibility.Dispose();
    }

    /// <summary>Six cards for one authored box, over its world-space bounds.</summary>
    /// <remarks>
    ///     A card is world-axis aligned and a ramp is not, so a rotated box gets cards over its
    ///     rotated extents — the capture marches the real field inside them, so what the card holds
    ///     is the surface as it stands, not the box as it was authored. All six faces, including the
    ///     ones nobody sees: the atlas can afford them at this resolution, and a face the camera
    ///     never sees still bounces light at faces it does.
    /// </remarks>
    void AddCards(in ArenaBox box) {
        var half = Vector3.Max(box.Half, new(0.05f));
        var centre = box.Position + Quaternion.Transform(box.Centre, box.Rotation);

        // The rotated box's world extents: the absolute sum of its three rotated axes.
        var x = Quaternion.Transform(new Vector3(half.X, 0f, 0f), box.Rotation);
        var y = Quaternion.Transform(new Vector3(0f, half.Y, 0f), box.Rotation);
        var z = Quaternion.Transform(new Vector3(0f, 0f, half.Z), box.Rotation);

        var extents = Vector3.Abs(x) + Vector3.Abs(y) + Vector3.Abs(z) + new Vector3(CardMargin);

        for (var axis = 0; axis < 6; axis++) {
            var u = ((axis / 2) + 1) % 3;
            var v = ((axis / 2) + 2) % 3;

            var card = new SurfaceCard(
                axis,
                centre,
                extents,
                new(TexelsFor(Component(extents, u)), TexelsFor(Component(extents, v)))
            );

            if (Cache.AddCard(card) < 0) {
                CardsDropped++;
            } else {
                CardCount++;
            }
        }
    }

    static int TexelsFor(float halfExtent) =>
        Math.Clamp((int)MathF.Ceiling(halfExtent * 2f * CardTexelsPerUnit), 2, CardMaxResolution);

    static float Component(Vector3 value, int index) =>
        index switch { 0 => value.X, 1 => value.Y, _ => value.Z };

    /// <summary>The Preetham sky's radiance, averaged over the sphere.</summary>
    /// <remarks>
    ///     For the knobs that take one colour where the CPU paths take the model — the gather
    ///     dispatch's escape and the probe trace's miss. Sixty-four Fibonacci directions with no
    ///     seed, so two runs agree to the bit; the model itself answers the lower hemisphere with
    ///     the ground albedo, so no hemisphere weighting is needed here.
    /// </remarks>
    static Vector3 AverageSkyRadiance(ArenaFrame frame) {
        const int Samples = 64;
        const float GoldenAngle = 2.399963f;

        var sum = Vector3.Zero;

        for (var index = 0; index < Samples; index++) {
            var height = 1f - (2f * (index + 0.5f) / Samples);
            var radius = MathF.Sqrt(MathF.Max(0f, 1f - (height * height)));
            var angle = index * GoldenAngle;

            sum += PhysicalSky.Radiance(
                new(radius * MathF.Cos(angle), height, radius * MathF.Sin(angle)),
                frame.Atmosphere
            );
        }

        return sum / Samples;
    }

    /// <summary>What the cards are made of, for the reference capture to ask.</summary>
    /// <remarks>
    ///     One albedo for the whole level, and it is the same grey the fallback material carries:
    ///     the nine .vxmat files differ by a few percent of tint, which at bounce order one is under
    ///     the ray noise. A level with painted walls passes its materials in here.
    /// </remarks>
    sealed class Concrete : ISurfaceMaterial {
        public Vector3 Albedo(Vector3 position, Vector3 normal) => new(0.55f);

        public Vector3 Emissive(Vector3 position, Vector3 normal) => Vector3.Zero;
    }

    /// <summary>The baked sky as a radiance source: misses see Preetham, hits see nothing.</summary>
    /// <remarks>
    ///     The <c>Surface</c> half answers black because this type is always composed <em>under</em>
    ///     <see cref="SurfaceCacheRadiance" /> — a hit the cache does not cover is a hit nothing has
    ///     an answer for, and black is the tracers' honest pre-§ L4 reading of that.
    /// </remarks>
    sealed class PreethamRadiance(ArenaFrame frame) : IRadianceSource {
        public Vector3 Sky(Vector3 direction) => PhysicalSky.Radiance(direction, frame.Atmosphere);

        public Vector3 Surface(Vector3 position, Vector3 normal, Vector3 direction) => Vector3.Zero;
    }
}
