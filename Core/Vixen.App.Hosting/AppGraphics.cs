// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Vixen.Assets;
using Vixen.Core.Diagnostics;
using Vixen.Core.Mathematics;
using Vixen.Engine.Diagnostics;
using Vixen.Engine.Diagnostics.Overlays;
using Vixen.Engine.Frames;
using Vixen.Engine.Renderer;
using Vixen.Graphics;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Ecs;
using Vixen.Rendering.PostFx;
using Vixen.Rendering.Terrain;
using Vixen.Shaders;

namespace Vixen.App;

/// <summary>The device, the swapchain and the world's frame, owned by the host.</summary>
/// <remarks>
///     <para>
///         <b>The step the host stopped one short of.</b> <c>WorldRenderer</c> was the whole join
///         between a world and a drawn frame — the standard features, the shared geometry, the
///         extraction systems, a <c>SceneRenderHost</c> — and nothing outside a test project
///         constructed one, so every sample opened a device and issued draws by hand and none of them
///         was a game. This is that object put in <c>VixenApp.Run</c>'s way, together with the two
///         things it deliberately does not own: a device, and a swapchain to present it through.
///     </para>
///     <para>
///         <b>What it decides is an order, which is the part a host gets wrong silently.</b> The
///         swapchain image is lent to the frame under a name <em>before</em> the compositor builds,
///         because a graph culls a pass whose target nobody outside it reads; the camera's aspect
///         ratio is set when the swapchain is sized rather than when the frame is drawn, because
///         extraction has already run by then; and the world is drawn before
///         <see cref="Game.OnRender" /> is offered the same command list, so an application's own
///         passes land on top of the scene rather than under it.
///     </para>
///     <para>
///         <b>Every step is a public method on a public object.</b> <c>docs/plan/17</c>'s rule that
///         nothing in the boot path is inaccessible applies here as much as to the loop: a head that
///         wants two windows, a different swapchain, or a frame recorded into somebody else's command
///         list builds this itself and calls <see cref="Begin" /> and <see cref="End" /> where it
///         likes — or skips it and uses <c>WorldRenderer</c> directly, which is all this does.
///     </para>
/// </remarks>
public sealed class AppGraphics : IDisposable {
    // What an untouched TerrainFactory.Vegetation equals, so a game that filled it by hand can be
    // told from one that never mentioned it. A record, so this is member-for-member equality — the
    // nullable-slot trick Scene uses is not available for a property whose default is not null.
    static readonly TerrainVegetationQuality DefaultVegetation = new();

    readonly GraphicsOptions options;
    readonly Platform.IWindow? window;
    readonly ILogger logger;

    // The render system's own category, deliberately apart from the host's: "which node stopped
    // lighting itself" is then one filter in the Console panel rather than a search through the
    // host's startup lines. What speaks on it are the frames that drew and quietly drew something
    // else — see CompositorBuilder.Logger and SceneLighting.Logger, the two seams it reaches.
    readonly ILogger renderLogger;
    readonly bool ownsDevice;

    ISwapChain? swapChain;
    ICommandList? commands;
    GpuProfiler? gpu;
    FrameCapture? capture;

    DebugOverlayRenderer? debugNode;
    DiagnosticOverlaySystem? overlaySystem;
    FrameStatsOverlay? frameStats;
    GpuOverlay? gpuOverlay;
    Vixen.Rendering.Water.WaterOverlay? waterOverlay;
    Vixen.Rendering.Water.WaterMeshOverlay? waterMeshOverlay;
    LineShaders lineShaders;
    bool ownsLineShaders;

    // What MeshRenderFeature's two counters read at the end of the previous frame. They count for the
    // life of the renderer rather than for a frame — see MeshRenderFeature.DrawCount — so the panel's
    // "this frame" numbers are a difference, and taking it here is the only place both readings exist.
    int lastDraws;
    long lastIndices;

    /// <summary>The framebuffer size the swapchain was last built for.</summary>
    /// <remarks>
    ///     ⚠ <b>What was asked for, not what came back.</b> A surface decides its own extent — Vulkan's
    ///     <c>currentExtent</c> overrides the request — so comparing against <see cref="ISwapChain.Size" />
    ///     would find a difference that rebuilding cannot remove, which is a rebuild every frame for ever.
    /// </remarks>
    Int2 built;

    int reportedWarnings;
    string captureName = "frame";
    bool disposed;

    /// <summary>Builds the frame a world is drawn through.</summary>
    /// <param name="device">The device everything lives on.</param>
    /// <param name="options">What the application asked for.</param>
    /// <param name="window">The window to present to, or <see langword="null" />.</param>
    /// <param name="assets">Where meshes, materials and the compositor come from, or null.</param>
    /// <param name="shaders">The baked variants, or null for a head that supplies its own provider.</param>
    /// <param name="engine">The loop the extraction systems run in, or null.</param>
    /// <param name="logs">Where this logs.</param>
    /// <param name="ownsDevice">Whether disposing this disposes the device.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public AppGraphics(
        IGraphicsDevice device,
        GraphicsOptions options,
        Platform.IWindow? window,
        AssetManager? assets,
        EffectStore? shaders,
        EngineLoop? engine,
        ILoggerFactory logs,
        bool ownsDevice = true
    ) {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logs);

        Device = device;
        this.options = options;
        this.window = window;
        this.ownsDevice = ownsDevice;

        logger = logs.CreateLogger("Vixen.App.Graphics");
        renderLogger = logs.CreateLogger("Vixen.Rendering");

        Effects = new();

        // The one source a shipped build has. The code that could compile a variant instead lives in
        // Tools/Vixen.ShaderCompiler and is never linked into a game, so a build with no bundle
        // resolves every material to a miss — which EffectSystem counts, and which a development head
        // fixes by adding a provider of its own to Effects.
        if (shaders is not null) {
            Effects.AddProvider(new EffectSourceProvider(shaders, new(device)));
            HostLog.ShadersMounted(logger, shaders.Count);
        }

        Renderer = new(device, Effects, options.VertexCapacity, options.IndexCapacity) {
            // Under `Vixen.Rendering` rather than this class's own category, because the ids that
            // arrive here are that assembly's — the 4 000 range in docs/manual/log-events.md — and a
            // filter written against the register has to be able to name them. Set before Mount below
            // as a matter of taste only; WorldRenderer.Logger forwards either way round.
            Logger = logs.CreateLogger("Vixen.Rendering")
        };

        // ⚠ Attached once, here, and never per frame. The graph reads its sink at Execute and the
        // sink's pools are sized at construction, so a profiler swapped mid-run would be one whose
        // pools the frames in flight are still writing into. Off unless asked for, and refused
        // rather than faked on a device with no clock — see GraphicsOptions.GpuProfiling.
        if (options.GpuProfiling) {
            if (device.Features.HasTimestampQueries) {
                gpu = new(device);
                Renderer.Host.Graph.Profiler = gpu;
            }

            HostLog.GpuProfiling(logger, gpu is not null, device.Adapter.Name);
        }

        // Before Load, because the builder binds a document's `view:` by name as it builds the nodes,
        // and a view added afterwards is one nothing refers to.
        View = new(options.View);
        Renderer.Host.Builder.Views[options.View] = View;

        // ⚠ Also before Load, and for a stricter reason than the view's: the node is appended by
        // SceneRenderHost.Load itself, so one assigned afterwards would sit in a field until the
        // *next* build — and for a project that never reloads its compositor, for ever. See
        // GraphicsOptions.Overlays for what this switch is and why it is off.
        if (options.Overlays) {
            BuildOverlays();
        }

        // ⚠ Loaded here rather than at Load below, because the document is the top vote in the
        // quality waterfall and the two consumers of a resolved tier are wired before the build
        // finishes. Nothing between here and Load reads it, so the only thing this ordering costs
        // is that the "which compositor" log line comes earlier.
        var document = Frame(assets);

        // The project's quality preset is the waterfall's middle layer, and the object holding it is
        // in the factory list: PostEffectFactory takes the loaded asset rather than an address, so
        // the game that loaded it put it there. Found in a pass of its own rather than in the loop
        // below — the terrain factory may be registered first, and the tier it is handed must not
        // depend on the order a game happened to register two factories in.
        var preset = default(RenderQualityAsset);

        foreach (var factory in options.Factories) {
            if (factory is PostEffectFactory { Preset: { } project }) {
                preset = project;

                break;
            }
        }

        // One fold for every consumer below. Resolving twice would be free of consequence only
        // while both calls agree on the arguments — and they did not: the texture pool was sized
        // from the tier alone, so a project's .vxpreset moved its vegetation budgets and silently
        // did not move its texture budget.
        //
        // Through the document rather than from options.Quality alone, and that is the whole of
        // what a !StandardFrame's own `quality:` and inline `preset:` used to be unable to move: the
        // expansion replaces the node during the build, so a host asking afterwards would be asking
        // a document that no longer says anything. This is the same fold the expansion performs, on
        // the same two layers — see PostEffectFactory.QualityOf.
        var quality = PostEffectFactory.QualityOf(document, options.Quality, preset);

        // ⚠ The light budget, which the tiers have carried since they were written and nothing read.
        //
        // LightQuality said "carried, not yet consumed" and it was exactly true: Low asks for four
        // lights an object and every tier drew eight, because eight is this feature's constructor
        // default and no line of the waterfall reached it. The same shape as the vegetation budgets
        // below — a resolved number, handed to the object that owns the behaviour, before anything
        // is built from it — and the same shape as the defect one array along, where a tier's
        // cascade count reached no shader either.
        //
        // Before Load, necessarily and for two reasons: the block's size is fixed at the first frame
        // that prepares, and CompositorBuilder reads this number to compile the shading passes
        // against a `lights[]` of the same length. A game that wants its own budget sets it after
        // this and reloads the document, which is what sample 13 does.
        Renderer.Lighting.MaxLightsPerObject = quality.MaxLightsPerObject;

        // ⚠ Before the factories too, because a !WaterSurface node is handed this as it is created
        // and a node with no zones draws nothing at all. It is also the one water clock — see
        // WaterZoneSystem.WaterTime — so the surface, the underwater volume and a buoyancy solver all
        // read the same number rather than three that agree until the frame rate changes.
        Water = new(View);
        WaterClock = new(Water);

        // ⚠ And the builder gets a voice, before any node is built from it. A node that degrades on
        // purpose — the terrain's preview shading is the case that taught us — used to do it in
        // total silence, and a renderer that quietly draws something else is worse than one that
        // refuses: every counter stays healthy and the picture looks like a different bug. Its own
        // category, so "which node stopped lighting itself" is one filter in the Console panel
        // rather than a search through the host's own lines.
        Renderer.Host.Builder.Logger = renderLogger;

        // Also before Load, and for a stricter version of the same reason: a node kind nothing has
        // bound is not a warning, it is a CompositorBindingException from inside the build. This is
        // where a project's own node packages get their say — see GraphicsOptions.Factories.
        foreach (var factory in options.Factories) {
            Renderer.Host.Builder.Factories.Add(factory);

            // The terrain factory is recognised rather than configured, because what its nodes read
            // is the world renderer's own frame list — an object that does not exist when
            // OnConfigure registers the factory. Registering it is the whole installation; a factory
            // whose Scene was assigned by the game already is left alone.
            if (factory is TerrainFactory terrain) {
                terrain.Scene ??= Renderer.TerrainScene;

                // ⚠ And the same recognition is where the quality tier crosses an assembly boundary
                // the waterfall cannot: Vixen.Rendering.Terrain must not reference
                // Vixen.Rendering.PostFx, so the resolved numbers travel as a plain-numbered copy
                // and this is the hand-off. Without it a shipped game runs the terrain stack's
                // constructor defaults whatever tier it selected, and every vegetation budget is
                // carried the whole length of the waterfall and dropped at the last step.
                //
                // Before Load, necessarily: the factory's Create reads these while the frame builds.
                if (terrain.Vegetation == DefaultVegetation) {
                    terrain.Vegetation = VegetationOf(quality);
                }
            }

            // And water's, on exactly the same terms and for the same reason: what a !WaterSurface
            // node draws is the zones an ECS system folded out of the scene this frame, and that
            // system does not exist when a game's OnConfigure hands the factory over. A factory whose
            // Zones the game already assigned is left alone.
            if (factory is Vixen.Rendering.Water.WaterRendererFactory water) {
                water.Zones ??= Water;
            }
        }

        // Also before Load: the tier is read by the document transform, which runs inside the
        // build. A preset frame that names its own quality out-votes this; one that does not gets
        // the platform's pick. See GraphicsOptions.Quality. The same fallback `quality` above was
        // folded from, so the expansion and the budgets cannot land on two different tiers.
        Renderer.Host.Builder.Quality = options.Quality;

        Renderer.Host.Load(document);

        if (Renderer.Host.Builder.Stages.TryGetValue(options.Stage, out var stage)) {
            // The view draws the camera's stage alone; extraction covers that one and every caster
            // stage beside it. See GraphicsOptions.CasterStages for why the two masks differ.
            var extracted = stage.Mask;

            // ⚠ Accumulated across *both* caster lists below, because it is what a drawable saying it
            // casts no shadow is taken out of and either list may be the one it would have been
            // stamped from — see MeshExtractionSystem.CasterStages.
            var casters = RenderStageMask.None;

            foreach (var caster in options.CasterStages) {
                if (Renderer.Host.Builder.Stages.TryGetValue(caster, out var casting)) {
                    extracted |= casting.Mask;
                    casters |= casting.Mask;
                } else {
                    HostLog.NoStage(logger, caster);
                }
            }

            Stages = extracted;
            View.Stages = stage.Mask;

            // ⚠ The camera's stage and the *static* caster stages — a replacement for `extracted`
            // rather than an addition to it. An entity in both caster stages is drawn into the cached
            // atlas and then again on top of it every frame, which is the level rasterised twice per
            // cascade and a cache that costs instead of paying. Left empty when the project names no
            // static stage, which is what makes the claim ignored rather than obeyed with a mask of
            // none — see MeshExtractionSystem.StaticStages.
            if (options.StaticCasterStages.Count > 0) {
                var still = stage.Mask;

                foreach (var caster in options.StaticCasterStages) {
                    if (Renderer.Host.Builder.Stages.TryGetValue(caster, out var casting)) {
                        still |= casting.Mask;
                        casters |= casting.Mask;
                    } else {
                        HostLog.NoStage(logger, caster);
                    }
                }

                StaticStages = still;
            }

            CasterStages = casters;
        } else {
            HostLog.NoStage(logger, options.Stage);
        }

        // ⚠ A mask of its own, and *not* folded into `Stages` or into the view's. Folding it into the
        // extraction mask would draw every mesh in the level into the transparent stage as well;
        // folding it into the view's would be right and is unnecessary, because the view already
        // draws whichever stage a `!SingleStage` node names. What this decides is only which stage the
        // emitters are stamped with.
        if (options.ParticleStage is { Length: > 0 } emitting) {
            if (Renderer.Host.Builder.Stages.TryGetValue(emitting, out var particles)) {
                ParticleStages = particles.Mask;
            } else {
                HostLog.NoStage(logger, emitting);
            }
        }

        if (assets is not null) {
            // Before Mount, necessarily: the pool is sized when the texture source is built, and a
            // pool that could be resized afterwards would not be a budget. This is where
            // `textures.streamingPoolMegabytes` stops being a number nobody reads.
            Renderer.Textures = new() {
                PoolMegabytes = quality.StreamingPoolMegabytes,
                MipBias = quality.MipBias
            };

            Renderer.Mount(assets);
        }

        Camera = new(View);
        Volumes = new(View);

        // After Load, necessarily: the frame document's own look is deposited on the builder by the
        // !StandardFrame transform, which runs inside the build. The look never touches the built
        // graph — it is the volume fold's base layer, which is what lets editing it relight the
        // same document with nothing rebuilt.
        Volumes.Look = LookFor(assets);

        // ⚠ The seam doc 35 § B2 generalised doc 32's box for, wired here rather than left to a game.
        // An underwater volume is a PostProcessVolume with Shape: Custom on a zone entity, and
        // without a source it reaches *nothing* — deliberately, because falling back to the box would
        // grade a rectangle around the lake while the inspector looked correct. A game that supplies
        // its own source out-votes this.
        Volumes.Shapes ??= Water;

        if (engine is not null) {
            // The order the three are added in does not decide the order they run in — SystemPhase
            // and the declared access do — but all are PreRender readers of WorldTransform, so all
            // land after the transforms are written and a camera moved this frame renders from where
            // it is.
            engine.Add(Camera);

            // ⚠ Before the volumes, and the order does matter here even though the three above it are
            // order-free. The volume fold asks this system for the underwater shape, and a shape
            // built from a field that has not been rasterised this frame is one testing against where
            // the water was — which at a shoreline is the grade coming on a frame early. The phase
            // and the declared access are what actually order them; adding it here says why.
            engine.Add(Water);

            // ⚠ The one thing that advances the water clock, and it is a second system rather than a
            // line in the one above because of a phase. The fold has to be in PreRender — a body is
            // rasterised where TransformSystem has just put it — and FixedUpdate, where a buoyancy
            // solver runs, is *earlier in the same frame*. A clock advanced during the fold reaches a
            // solver a frame late, which is a boat exactly one frame of swell behind the water drawn
            // under it: constant, small, and invisible until the frame rate changes. EarlyUpdate is
            // before everything that reads it.
            engine.Add(WaterClock);

            engine.Add(Volumes);
            Renderer.Register(engine, Stages, ParticleStages, StaticStages, CasterStages);

            // ⚠ The overlay system and *not* DebugDrawSystem beside it. This one is a PreRender
            // reader that draws panels into the accumulator, which is exactly where doc 13 puts it:
            // everything it reports on has run and nothing has drained yet. The ageing half cannot
            // be a system in this loop at all — see AdvanceDebug.
            if (overlaySystem is not null) {
                engine.Add(overlaySystem);
            }
        }

        Resize();
    }

    /// <summary>The device everything here lives on.</summary>
    public IGraphicsDevice Device { get; }

    /// <summary>
    ///     The one accumulator every subsystem's debug geometry goes into, or null when
    ///     <see cref="GraphicsOptions.Overlays" /> is off.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>One instance, and everything that draws or drains reads it off here.</b> The
    ///         overlay system pushes into it, the compositor node drains it, and the host ages it —
    ///         all three from this property, because the failure mode of the alternative is already
    ///         on the record: the editor built two <c>DiagnosticsModule</c>s and the half that was
    ///         fed was not the half that drew, which reads as a platform limitation rather than as a
    ///         second object.
    ///     </para>
    ///     <para>
    ///         A game draws into it directly — <c>services.Graphics?.Debug?.Line(a, b, colour)</c> —
    ///         and so does anything it hands the object to. Null is a build that did not ask for the
    ///         overlays; a caller that wants its own accumulator regardless makes one and drains it
    ///         itself.
    ///     </para>
    /// </remarks>
    public DebugDraw? Debug { get; private set; }

    /// <summary>The registry of diagnostic panels, or null when the overlays are off.</summary>
    /// <remarks>
    ///     Frame stats, the mini flame chart and the console are added here by the host; the log tail
    ///     is added by <c>AppBuilder</c>, which is what owns the ring it reads. A subsystem's own
    ///     panel — <c>AudioOverlay</c>, water's <c>stat water</c> — is added by whoever has the
    ///     numbers, which is the seam <c>IDiagnosticOverlay</c> exists to be.
    /// </remarks>
    public DiagnosticOverlays? Overlays { get; private set; }

    /// <summary>What the console runs, or null when the overlays are off.</summary>
    /// <remarks>
    ///     Already carries every verb any loaded subsystem contributed through
    ///     <see cref="ConsoleCommands.Contribute" /> — water's six arrive that way — plus
    ///     <c>overlay</c> and <c>overlays</c>. A game adds its own with
    ///     <see cref="ConsoleCommands.Register(string, string, Action{ConsoleContext})" />.
    /// </remarks>
    public ConsoleCommands? Console { get; private set; }

    /// <summary>Where variants are compiled or looked up.</summary>
    /// <remarks>
    ///     Public and mutable-by-addition on purpose: a development head adds a compiling provider on
    ///     top of the baked one, which is the tiering <c>IEffectSource</c> was drawn for.
    /// </remarks>
    public EffectSystem Effects { get; }

    /// <summary>The world's frame: the features, the geometry, the extraction and the host.</summary>
    public WorldRenderer Renderer { get; }

    /// <summary>The view the scene's camera fills.</summary>
    public RenderView View { get; }

    /// <summary>What fills it from the world.</summary>
    public CameraExtractionSystem Camera { get; }

    /// <summary>The scene's water: the zones, their fields, and the one water clock.</summary>
    /// <remarks>
    ///     <para>
    ///         Held by the host rather than by a game, because three unrelated things need the same
    ///         one — the <c>!WaterSurface</c> node draws its zones, the volume fold asks it for the
    ///         underwater shape, and a buoyancy solver reads its clock.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A game still has to point it at its splines, its sea states and its ground.</b>
    ///         <see cref="Vixen.Rendering.Water.WaterZoneSystem.Splines" /> is null until something
    ///         supplies one, and every body then counts into <c>UnresolvedBodies</c>;
    ///         <see cref="Vixen.Rendering.Water.WaterZoneSystem.Waves" /> is null until something
    ///         supplies one, and every zone naming a <c>.vxwaves</c> falls back to its inline
    ///         spectrum and counts into <c>UnresolvedWaves</c>; <c>Ground</c> defaults to a flat plane
    ///         at zero, which is right for an open ocean and visibly wrong for a lake in a valley.
    ///     </para>
    ///     <para>
    ///         <c>Vixen.Engine.Renderer</c>'s <c>AssetWaterSource</c> is the implementation of the
    ///         first two for a game with a content build, and it is not wired here for
    ///         <c>AssetTerrainSource</c>'s reason: the host owns a device and a world, and an asset
    ///         manager is the application's.
    ///     </para>
    /// </remarks>
    public Vixen.Rendering.Water.WaterZoneSystem Water { get; }

    /// <summary>What advances the water clock, in <c>EarlyUpdate</c>, before anything reads it.</summary>
    /// <remarks>
    ///     ⚠ <b>Separate from <see cref="Water" /> because of a phase, not because of tidiness.</b>
    ///     The fold runs in <c>PreRender</c> and a buoyancy solver runs in <c>FixedUpdate</c>, which is
    ///     earlier in the same frame — so a clock advanced during the fold hands the solver last
    ///     frame's time while the vertex stage draws this frame's. Exposed so a cinematic can slow the
    ///     sea with <c>Rate</c>, or a test pin <c>Water.WaterTime</c> and simply not add this.
    /// </remarks>
    public Vixen.Rendering.Water.WaterClockSystem WaterClock { get; }

    /// <summary>The post-process volumes the camera is inside, folded into one overlay.</summary>
    /// <remarks>
    ///     Its result reaches the frame in <see cref="Begin" />, between the engine's update and the
    ///     compositor's build — see there.
    /// </remarks>
    public PostProcessVolumeSystem Volumes { get; }

    /// <summary>Which stages the world's drawables are extracted into.</summary>
    public RenderStageMask Stages { get; }

    /// <summary>Which stages a drawable claiming to be static goes into instead, or none.</summary>
    /// <remarks>
    ///     <c>GraphicsOptions.StaticCasterStages</c> resolved against the document's stages. None is a
    ///     project that named none, and leaves <c>StaticShadowCaster</c> ignored — which is the frame
    ///     every project that has not opted into a cached sun shadow already has.
    /// </remarks>
    public RenderStageMask StaticStages { get; }

    /// <summary>Which of the extracted stages draw shadows, or none.</summary>
    /// <remarks>
    ///     Both of <c>GraphicsOptions.CasterStages</c> and <c>StaticCasterStages</c> resolved and
    ///     unioned, which is what <c>MeshExtractionSystem.CasterStages</c> takes out of a drawable
    ///     whose <c>MeshRenderable.CastsShadows</c> is clear. Both lists, because either may be the
    ///     set the drawable was going to be stamped from. None is a project that named no caster
    ///     stage, and leaves the flag ignored.
    /// </remarks>
    public RenderStageMask CasterStages { get; }

    /// <summary>Which stage the scene's particle emitters are drawn in, or none.</summary>
    /// <remarks>
    ///     Separate from <see cref="Stages" /> for the reason <c>GraphicsOptions.ParticleStage</c>
    ///     gives: a transparent stage is not the one a mesh is drawn in, and a shadow stage is one a
    ///     billboard must never be in. Zero is a document that declares no such stage, and leaves
    ///     every emitter simulating and undrawn.
    /// </remarks>
    public RenderStageMask ParticleStages { get; }

    /// <summary>The swapchain, once one has been built.</summary>
    public ISwapChain? SwapChain => swapChain;

    /// <summary>
    ///     The frame's command list, open only between <see cref="Begin" /> and <see cref="End" />.
    /// </summary>
    /// <remarks>
    ///     What <see cref="Game.OnRender" /> records its own work into. Null outside a frame, and null
    ///     during one whose image could not be acquired — a window being resized, a device that has
    ///     gone — which is why an application checks rather than assumes.
    /// </remarks>
    public ICommandList? Commands => commands;

    /// <summary>Whether the device has gone and nothing more will be drawn.</summary>
    /// <remarks>
    ///     A latch rather than a recovery, deliberately. Rebuilding every device resource a game holds
    ///     after a driver reset is a whole feature — one this records honestly as absent rather than
    ///     pretending at with a half-measure that leaves handles dangling.
    /// </remarks>
    public bool IsLost { get; private set; }

    /// <summary>How many frames have been recorded.</summary>
    public int FrameCount => Renderer.Host.FrameCount;

    /// <summary>The most recent frame whose passes the GPU has finished timing.</summary>
    /// <remarks>
    ///     <para>
    ///         <see cref="GpuFrame.Empty" /> unless <see cref="GraphicsOptions.GpuProfiling" /> is
    ///         on, and for the first few frames after it is — the readings are a few submissions
    ///         behind, which is a property of the measurement rather than of this property.
    ///     </para>
    ///     <para>
    ///         One scope per surviving render-graph pass, named after the pass and in the order the
    ///         document declared them. This is what a game reads to draw its own overlay, and what
    ///         the editor's timeline panel is handed when the editor is the host.
    ///     </para>
    /// </remarks>
    public GpuFrame GpuFrame { get; private set; } = GpuFrame.Empty;

    /// <summary>
    ///     The frame a project gets before it authors one: one lit pass, into the window.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Built as objects rather than parsed from a string, because a document is a record graph
    ///         and the YAML is the editor's format for writing one — a host that parsed at start-up
    ///         would pay a parser to reach a value it could have named.
    ///     </para>
    ///     <para>
    ///         <b>Deliberately not a renderer.</b> No shadows, no post, no transparent stage: it is
    ///         the smallest frame in which a scene is visible, so that "my game draws nothing" is
    ///         never the host's fault, and small enough that nobody mistakes it for what they should
    ///         ship. A project's own compositor is content — <see cref="GraphicsOptions.Compositor" />
    ///         names it — and the moment it exists this is not used.
    ///     </para>
    ///     <para>
    ///         <c>SceneColour</c> is declared <em>and</em> imported: declared so the document is a
    ///         whole thing that a test or the editor can build against a scratch texture, imported at
    ///         run time so the pass writes the window. An import wins over a declaration of the same
    ///         name, which is the mechanism that makes one document run in both places.
    ///     </para>
    /// </remarks>
    /// <remarks>
    ///     ⚠ <b>The asset's own default rather than a copy of one</b>, and it moved there when the
    ///     editor became the second head that needed it: a game falling back to one frame and an
    ///     editor to another would make the viewport disagree with the build for every project that
    ///     had not authored a compositor — which is exactly the projects looking at the viewport to
    ///     find out what their scene looks like.
    /// </remarks>
    public static GraphicsCompositorAsset DefaultFrame => GraphicsCompositorAsset.Default;

    /// <summary>Opens the frame: acquires an image, lends it to the document, draws the world.</summary>
    /// <returns>Whether there is a frame to finish. <see langword="false" /> leaves nothing open.</returns>
    /// <remarks>
    ///     ⚠ <b>Every path that opened the device's frame closes it.</b> <c>BeginFrame</c> waits on a
    ///     slot's fence and resets it, and <c>EndFrame</c> is what submits the signal that makes the
    ///     next wait return — so a frame abandoned between them is not a dropped frame, it is a hang
    ///     on the frame after it.
    /// </remarks>
    public bool Begin() {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (IsLost) {
            return false;
        }

        EnsureSwapChain();

        Device.BeginFrame();

        var status = swapChain!.AcquireNextImage(out var view);

        // Out of date arrives on the first acquire after every resize, and returning here would
        // present nothing that frame — which during a window drag is the window blinking.
        if (status is SwapChainStatus.OutOfDate) {
            Recreate(force: true);
            status = swapChain.AcquireNextImage(out view);
        }

        if (status is SwapChainStatus.DeviceLost) {
            IsLost = true;
            HostLog.DeviceLost(logger);
            Device.EndFrame();

            return false;
        }

        if (status is not (SwapChainStatus.Ready or SwapChainStatus.Suboptimal)) {
            Device.EndFrame();
            return false;
        }

        Lend(view);

        // ⚠ Between the engine's update and the compositor's build, and it has to be both. The fold
        // needs the camera position that PreRender just produced, and the nodes read their parameters
        // when they build — which is the next thing that happens. Applying it after the build would
        // be a frame late, which is the shape of bug that looks like input lag on the grade.
        //
        // Applied even when nothing contributes: a node lays the overlay over its authored values
        // rather than accumulating, so "no volumes reach the camera" has to be delivered for the
        // frame to go back to what the document said.
        Renderer.Host.Compositor?.Apply(Volumes.Overlay);

        // ⚠ **The frame's lighting is told which camera it is being lit for, and nothing was telling
        // it.** `SceneLighting.Camera` had exactly two writers in the whole tree and both were unit
        // tests, so in every running game it was null — a finished consumer nothing fed, on
        // `IWaterGround`'s precedent. Two things read it and both failed silently:
        //
        //  - `SceneLighting.Extract` only calls `ClusterGrid.Apply` when it is set, so the froxel
        //    grid's half-tangents and planes were never written into set 0. A frame running clustered
        //    lights binned them with one set of numbers and looked them up with zeros.
        //  - `TerrainSceneRenderer.DetectMode` gates the *entire* frame-lit ground on it, so terrain
        //    and grass could never leave the preview shaders — whose output is a reflectance in
        //    [0, 1] rather than a radiance in cd/m². Against this level's daylight sky that is about
        //    one nit in a frame metered for thousands, which draws as **black ground under a correct
        //    sky** at every viewpoint and every hour. Both counters said the ground was drawn,
        //    because it was.
        //
        // Here for the reason the volume fold above is here: `PreRender` has just written
        // `View.Camera` and the nodes read their parameters when they build, which is the next thing
        // that happens. A null camera is still the honest answer for a view that has none — an
        // orthographic one leaves it null by design — and both readers already treat that as "not
        // this frame" rather than as zero.
        if (Renderer.SceneEnvironment is { } lighting) {
            lighting.Camera = View.Camera;

            // ⚠ And it is now allowed to say so when it is not. The bug above was invisible for the
            // whole life of the engine because nothing that consumed this member ever complained
            // about its absence — assigned every frame rather than once because SceneEnvironment is
            // the renderer's to replace, and a reference compare is what a re-assert costs.
            lighting.Logger = renderLogger;
        }

        AimTheJitter();

        commands = Device.BeginCommandList(QueueKind.Graphics, "frame");

        // ⚠ Before anything is recorded, because what it records is the pool reset — which Vulkan
        // will not allow inside a render pass, and which has to be on the same list as the writes it
        // is resetting for.
        gpu?.BeginFrame(commands, FrameCount);

        // Renderer.Draw rather than Host.Draw: the texture copies a material's maps need go on the
        // list before anything samples them, and a host that skips them leaves every textured
        // material sampling the table's fallback for ever — which reads as "all my materials are the
        // same flat colour" rather than as a failure.
        Renderer.Draw(commands);

        // The graph's lint, surfaced once per distinct finding. These are frames that draw and
        // quietly waste or discard work — the class of wrongness no exception ever reaches — and
        // a warning that repeated every frame would be muted by the reader it exists for.
        var warnings = Renderer.Host.Graph.Warnings;

        for (; reportedWarnings < warnings.Count; reportedWarnings++) {
            HostLog.FrameLint(logger, warnings[reportedWarnings]);
        }

        return true;
    }

    /// <summary>
    ///     Tells the camera whether this frame's tree resolves temporally, and at what size.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The last thing nobody was doing.</b> <c>TemporalAntialiasingRenderer.Jitter</c>
    ///         has existed since the pass was written, documented as "a host adds it to the
    ///         projection", and no host did — so every temporally resolved frame in this engine was
    ///         averaging the same sub-pixel sample over and over. That is the case the pass's own
    ///         remarks name: a frame that gets blurrier and no sharper, which is exactly what sample
    ///         13's <c>!Sharpen</c> node was put in to undo.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Only when a tree actually has one, because a jittered camera nothing accumulates
    ///         is strictly worse than a still one</b> — the frame shakes by half a pixel and no pass
    ///         averages the shake out. Hence the walk rather than an option: what decides is the
    ///         document, and a document is loaded, rebuilt and switched at run time.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It reaches the next frame's extraction, not this one's.</b> The camera is
    ///         extracted in <c>SystemPhase.PreRender</c>, which has already run by the time
    ///         <see cref="Begin" /> is called — see the note on the lighting assignment above, which
    ///         is here for the mirror-image reason. A Halton sequence has no meaningful phase, so
    ///         starting it one frame late costs nothing; what would cost something is the geometry
    ///         and the camera description disagreeing about which offset this frame took, and those
    ///         are read together inside one <c>Extract</c>.
    ///     </para>
    /// </remarks>
    void AimTheJitter() =>
        Camera.JitterTarget = ResolvesTemporally(Renderer.Host.Compositor?.Game) ? Renderer.Host.FrameSize : default;

    /// <summary>Whether any node under this one is a temporal resolve.</summary>
    /// <remarks>
    ///     ⚠ <b>Disabled nodes count, on <c>GraphicsCompositor.Apply</c>'s precedent.</b> A resolve
    ///     switched off for a frame and on again the next would otherwise get one frame of history
    ///     accumulated from an unjittered camera, which is a frame of ghosting at the moment somebody
    ///     toggled the setting to look at it.
    /// </remarks>
    static bool ResolvesTemporally(SceneRenderer? node) {
        if (node is null) {
            return false;
        }

        if (node is TemporalAntialiasingRenderer) {
            return true;
        }

        foreach (var child in node.Nested) {
            if (ResolvesTemporally(child)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Asks for the next frame that finishes to be written as a picture.</summary>
    /// <param name="name">The file's name without an extension.</param>
    /// <returns>
    ///     Whether the request was taken. <see langword="false" /> when no capture directory was
    ///     configured, or when the frame has a window to present to — a presented image is created
    ///     without the transfer-source flag, so nothing can copy out of it.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         The picture is the resource named by <see cref="GraphicsOptions.Output" /> as the
    ///         frame's last pass left it — after tonemapping, antialiasing and every post effect, and
    ///         before a present that here does not happen.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A headless picture is not a screenshot of the window, and should not be read as
    ///         one.</b> It reproduces, which a screenshot does not; it is the right artefact for "is
    ///         the grass black", "did the shadow appear", "is it washed out". It is the wrong one for
    ///         "what does a player see", because there is no present, the format is what was asked
    ///         for rather than what a surface offered, and no display's backing scale is involved.
    ///         See <a href="../../docs/guide/rendering/capturing-a-frame.md">capturing a frame</a>.
    ///     </para>
    /// </remarks>
    public bool RequestCapture(string name) {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (options.CapturePath is not { Length: > 0 } directory || !IsOffscreen) {
            return false;
        }

        capture = new(Device, directory);
        captureName = name;

        return true;
    }

    /// <summary>Where the last capture was written, or <see langword="null" /> if there was none.</summary>
    public string? LastCapturePath { get; private set; }

    /// <summary>Closes the frame: submits it and presents.</summary>
    public void End() {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (commands is not { } list) {
            return;
        }

        commands = null;

        // ⚠ Before Finish, because a copy recorded after it is a copy on no list; and the read half
        // is below the submit, because device.Read is immediate while this executes at submit. The
        // two cannot be the same call, and putting them together is what produces a black PNG.
        capture?.Record(list, swapChain!.CurrentTexture, swapChain.Size, swapChain.Format);

        list.Finish();
        Device.GraphicsQueue.Submit([list]);
        list.Dispose();

        // ⚠ After the submit, and what it reads is a frame from several submissions ago rather than
        // the one just sent. `TryResolveQueries` never waits — a resolve that blocked would stall
        // the frame thread once per frame and halve the rate it is reporting — so the first frames
        // after a run starts yield nothing, and `GpuFrame` stays empty until the GPU has retired one.
        if (gpu is not null && gpu.Resolve()) {
            GpuFrame = gpu.Latest;
        }

        // After the submit and before the next loop pass, which is the only moment both readings of
        // the frame exist: the panels are drawn in PreRender, which for the frame just recorded has
        // already happened and for the next one has not.
        ReadStatistics();

        Device.EndFrame();

        switch (swapChain!.Present()) {
            // The one status that says the swapchain may not be used again at all.
            case SwapChainStatus.OutOfDate:
                Recreate(force: true);
                break;

            // ⚠ A hint, and rebuilding on it unconditionally is the flicker: it means "this still
            // presents, and the surface would prefer other parameters", which a scaled display keeps
            // saying — so it goes through the size check rather than round it.
            case SwapChainStatus.Suboptimal:
                Recreate();
                break;

            case SwapChainStatus.DeviceLost:
                IsLost = true;
                HostLog.DeviceLost(logger);
                break;

            default:
                break;
        }

        // Last, because it waits for the queue: everything above is what the frame owes the next
        // one, and a capture is what the run owes whoever asked for it.
        if (capture is { } pending) {
            capture = null;
            LastCapturePath = pending.Write(captureName);

            if (LastCapturePath is { } written) {
                HostLog.FrameCaptured(logger, written);
            }
        }
    }

    /// <summary>Rebuilds the swapchain for the window's current size, if it has changed.</summary>
    /// <param name="force">
    ///     Whether to rebuild at the same size too. True only for <see cref="SwapChainStatus.OutOfDate" />.
    /// </param>
    public void Recreate(bool force = false) {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (swapChain is null) {
            return;
        }

        var target = Target;

        if (!force && target == built) {
            return;
        }

        Device.WaitIdle();
        swapChain.Resize(target);

        built = target;
        Resize();
    }

    /// <summary>The size the swapchain should be, which is the window's or the configured one.</summary>
    Int2 Target => window is null ? options.WindowlessSize : FramebufferOf(window);

    /// <summary>The window's framebuffer size, never zero in either axis.</summary>
    /// <param name="window">The window.</param>
    /// <returns>The size to build a swapchain at.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="window" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>FramebufferSize</c> rather than <c>ClientSize</c></b>, because the framebuffer
    ///         is what a swapchain image is measured in and the two disagree by the display's scale
    ///         factor. A swapchain built from the client size on a 2× display is a quarter of the
    ///         window, and what it looks like is a game rendered into the top-left corner.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Clamped to one.</b> A minimised window reports zero and every backend refuses a
    ///         zero-sized swapchain, so passing it straight through would turn "the user minimised
    ///         the window" into a crash on the resize that follows.
    ///     </para>
    /// </remarks>
    public static Int2 FramebufferOf(Platform.IWindow window) {
        ArgumentNullException.ThrowIfNull(window);

        return new(Math.Max(window.FramebufferSize.X, 1), Math.Max(window.FramebufferSize.Y, 1));
    }

    /// <summary>Creates the swapchain a device presents through.</summary>
    /// <param name="device">The device.</param>
    /// <param name="window">The window, or <see langword="null" /> for an offscreen frame.</param>
    /// <param name="options">What the application asked for.</param>
    /// <returns>The swapchain.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    ///     ⚠ <b>Not behind <see cref="IGraphicsBackend" />, and deliberately.</b> Every backend
    ///     implements <see cref="IGraphicsDevice.CreateSwapChain" />, so there is nothing here for a
    ///     backend to answer differently — only a surface handle, a size and two format choices that
    ///     came off <see cref="GraphicsOptions" />. Putting it behind the seam would have been an
    ///     indirection with exactly one possible implementation.
    /// </remarks>
    public static ISwapChain SwapChainFor(IGraphicsDevice device, Platform.IWindow? window, GraphicsOptions options) {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(options);

        var surface = window?.Surface.Handle ?? Core.SurfaceHandle.None;
        var size = window is null ? options.WindowlessSize : FramebufferOf(window);

        return device.CreateSwapChain(
            new(surface, size, options.Format, options.PresentMode, Gamut: options.Gamut)
        );
    }

    /// <summary>
    ///     Drops the swapchain, for a platform that has taken the surface away.
    /// </summary>
    /// <remarks>
    ///     What <see cref="Platform.PlatformEventKind.Suspending" /> means on Android and iOS: the
    ///     native window is destroyed and the surface with it, while the device — and every buffer,
    ///     texture and pipeline on it — survives. So this releases exactly the thing that became
    ///     invalid, and the next <see cref="Begin" /> builds a new one from the surface handle the
    ///     resumed window now has.
    /// </remarks>
    public void Suspend() {
        if (disposed || swapChain is null) {
            return;
        }

        Device.WaitIdle();
        swapChain.Dispose();

        swapChain = null;
        built = default;
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        // Idle first: everything below this line frees memory the GPU may still be reading from the
        // last submitted frame, and a device that is torn down under a frame in flight faults inside
        // the driver, where the stack says nothing about which resource it was.
        if (!IsLost) {
            Device.WaitIdle();
        }

        commands?.Dispose();
        commands = null;

        swapChain?.Dispose();
        swapChain = null;

        // Detached before it is destroyed: the graph outlives this only in a test that kept a
        // reference, and a sink whose pools have been returned is a use-after-free on the next frame.
        Renderer.Host.Graph.Profiler = null;
        gpu?.Dispose();
        gpu = null;

        // Detached for the same reason: Load appends whatever is in this field, and a disposed node
        // in it would be appended again by a reload during shutdown.
        Renderer.Host.Debug = null;
        debugNode?.Dispose();
        debugNode = null;

        if (ownsLineShaders) {
            Device.Destroy(lineShaders.Vertex);
            Device.Destroy(lineShaders.Fragment);
            ownsLineShaders = false;
        }

        Renderer.Dispose();

        if (ownsDevice) {
            Device.Dispose();
        }
    }

    /// <summary>
    ///     Ages the frame's debug geometry, which a host does itself and after the frame is recorded.
    /// </summary>
    /// <param name="seconds">How much time passed.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Not <c>DebugDrawSystem</c>, and the difference is the whole feature working or
    ///         not.</b> That system ages in <c>SystemPhase.PostRender</c>, which is after the drain
    ///         only for a host whose renderer is a system in the same graph. <c>VixenApplication</c>
    ///         runs <c>EngineLoop.Frame</c> to completion and records the GPU frame afterwards, so
    ///         <c>PostRender</c> lands between the overlay that drew a panel and the node that would
    ///         have drawn it — every one-frame primitive deleted, every counter still reading
    ///         correct, and nothing on the screen.
    ///     </para>
    ///     <para>
    ///         Safe to call when the overlays are off; it does nothing.
    ///     </para>
    /// </remarks>
    public void AdvanceDebug(float seconds) => Debug?.Advance(seconds);

    /// <summary>Builds the one accumulator, the one registry and the one console, and the node.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Everything here is built once and exposed, rather than made where it is used.</b>
    ///         Three consumers need the same <c>DebugDraw</c> — the overlay system that draws panels
    ///         into it, the compositor node that drains it, and <see cref="AdvanceDebug" /> — and the
    ///         way this feature fails when they are not the same object is not an exception, it is an
    ///         empty screen beside counters that all read as if it worked.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The line stages come out of <c>Vixen.Rendering</c>'s own resources.</b> Until they
    ///         were published there, the only line shader a build could reach was
    ///         <c>Editor/Vixen.Editor.Host/Shaders/Line.rvn</c> — which is why the accumulator had a
    ///         renderer, a golden image and no game that could construct one.
    ///     </para>
    /// </remarks>
    void BuildOverlays() {
        Debug = new();
        Overlays = new();
        Console = new();

        // The two that report on the frame itself. The log tail is AppBuilder's, because the ring it
        // reads is; a subsystem's own panel is added by whoever holds its numbers.
        frameStats = new() { Enabled = true };

        Overlays.Add(frameStats);
        Overlays.Add(new FrameGraphOverlay());

        // ⚠ The three this host can honestly feed, and no more. An overlay is registered here only
        // when this class holds the object its numbers come from: the GPU frame is this class's
        // property, and the water zones are the system it constructs. `AudioOverlay` is deliberately
        // absent — nothing in the host owns an `AudioEngine`, and a panel wired to an engine nobody
        // updates draws a full set of zeroes that reads as "audio is idle and fine".
        //
        // Registered switched off. A panel that appears the moment somebody passes --vixen-overlays
        // is a panel in the way; `overlay gpu`, `overlay water` and `overlay watermesh` are how each
        // is asked for, and `overlays` lists them.
        gpuOverlay = new() { Available = gpu is not null };
        waterOverlay = new();
        waterMeshOverlay = new();

        Overlays.Add(gpuOverlay);
        Overlays.Add(waterOverlay);
        Overlays.Add(waterMeshOverlay);

        Overlays.Add(new ConsoleOverlay(Console));
        Overlays.RegisterCommands(Console);

        // ⚠ After the host's own and *not* last, because the request outlives this call: a name for a
        // panel nobody has registered yet is kept and honoured when it arrives, which is what makes
        // `--vixen-overlay audio` work on a game that adds its audio panel from OnInitialise.
        Overlays.Request(options.EnabledOverlays);

        lineShaders = LineShaders.Default(Device);
        ownsLineShaders = true;

        debugNode = new(Device, lineShaders, Debug, View) { Target = options.Output };
        Renderer.Host.Debug = debugNode;

        overlaySystem = new(Overlays, Debug) { Viewport = new(Target.X, Target.Y) };
    }

    /// <summary>Hands the panels this frame's numbers, before the loop draws them.</summary>
    /// <remarks>
    ///     ⚠ <b>Last frame's, and honestly so.</b> The overlays are drawn in <c>PreRender</c> and the
    ///     frame is recorded afterwards, so the newest draw count in existence when a panel is drawn
    ///     is the previous frame's — which is what every frame-stats panel in every engine shows and
    ///     what makes it stable enough to read. The GPU figure is older still and says by how much.
    /// </remarks>
    void ReadStatistics() {
        if (frameStats is null) {
            return;
        }

        if (gpuOverlay is not null) {
            gpuOverlay.Frame = GpuFrame;
            gpuOverlay.Latency = GpuFrame.Scopes.Count > 0 ? Math.Max(0, FrameCount - GpuFrame.FrameIndex) : 0;

            // ⚠ The frame being recorded's overflow, not the resolved frame's — `GpuProfiler.Dropped`
            // is cleared at each BeginFrame. It is still the right number to show: a document that
            // overflows the pool overflows it every frame, and the alternative is a breakdown that
            // stops halfway down the frame with nothing on the panel saying so.
            gpuOverlay.Dropped = gpu?.Dropped ?? 0;
        }

        // ⚠ Found by walking the built tree rather than captured once, because `SceneRenderHost.Load`
        // is callable again — a project that swaps its quality preset swaps its compositor, and
        // Samples/13 reloads its document once the shader library is attached. A node captured at
        // wire time is a node in a frame that stopped drawing, which is the failure the debug node's
        // own placement exists to avoid. A dozen entries, no allocation, once a frame.
        var surface = default(Vixen.Rendering.Water.WaterMeshRenderer);

        if (waterOverlay is not null || waterMeshOverlay is not null) {
            foreach (var node in Renderer.Host.Builder.Nodes.Values) {
                if (node is Vixen.Rendering.Water.WaterMeshRenderer found) {
                    surface = found;

                    break;
                }
            }
        }

        waterOverlay?.Read(Water, surface);

        if (surface is not null) {
            waterMeshOverlay?.Read(surface);
        }

        var draws = Renderer.Meshes.DrawCount;
        var indices = Renderer.Meshes.IndexCount;

        frameStats.Statistics = new() {
            GpuMilliseconds = GpuFrame.Milliseconds,

            // How many frames back the reading is. Zero scopes is nobody measuring, which
            // FrameStatsOverlay draws as a dash rather than as a very fast frame.
            GpuLatency = GpuFrame.Scopes.Count > 0 ? Math.Max(0, FrameCount - GpuFrame.FrameIndex) : 0,
            DrawCalls = Math.Max(0, draws - lastDraws),
            Triangles = Math.Max(0L, indices - lastIndices) / 3L,
            Visible = Renderer.Extraction?.ObjectCount ?? 0
        };

        lastDraws = draws;
        lastIndices = indices;
    }

    /// <summary>The document the frame is built from: the project's, or the built-in one.</summary>
    /// <remarks>
    ///     A compositor that will not load falls back rather than throwing. A frame is the one asset
    ///     whose absence stops anything else being visible — including the message saying what went
    ///     wrong — so a build with a broken document draws its scene through the default frame and
    ///     says so, which is the same trade the catalog and the shader bundle make one layer down.
    /// </remarks>
    GraphicsCompositorAsset Frame(AssetManager? assets) {
        if (options.Compositor is not { Length: > 0 } address) {
            return DefaultFrame;
        }

        if (assets is null) {
            HostLog.NoCompositor(logger, address, "this build shipped no content.");
            return DefaultFrame;
        }

        try {
            if (assets.Load<GraphicsCompositorAsset>(address).Result is { } asset) {
                HostLog.CompositorLoaded(logger, address);
                return asset;
            }

            HostLog.NoCompositor(logger, address, "nothing was published under that address.");
        } catch (Exception failure) when (failure is not (OutOfMemoryException or StackOverflowException)) {
            HostLog.NoCompositor(logger, address, failure.Message);
        }

        return DefaultFrame;
    }

    /// <summary>The project look's payload: the document's inline one, or the host-addressed asset.</summary>
    /// <remarks>
    ///     The precedence is <c>GraphicsOptions.Look</c>'s: a frame document that wrote its look
    ///     inline decided, and the host's address covers the project that left it out. Failure keeps
    ///     the neutral frame and says so — a look is the one asset whose absence looks exactly like
    ///     an asset nobody wired, so the log line is most of the diagnostic.
    /// </remarks>
    PostProcessSettings LookFor(AssetManager? assets) {
        if (Renderer.Host.Builder.Look is { } inline) {
            HostLog.LookApplied(logger, "from the frame document");
            return inline.Settings;
        }

        if (options.Look is not { Length: > 0 } address) {
            return PostProcessSettings.None;
        }

        if (assets is null) {
            HostLog.NoLook(logger, address, "this build shipped no content.");
            return PostProcessSettings.None;
        }

        try {
            if (assets.Load<LookAsset>(address).Result is { } look) {
                HostLog.LookApplied(logger, address);
                return look.Settings;
            }

            HostLog.NoLook(logger, address, "nothing was published under that address.");
        } catch (Exception failure) when (failure is not (OutOfMemoryException or StackOverflowException)) {
            HostLog.NoLook(logger, address, failure.Message);
        }

        return PostProcessSettings.None;
    }

    /// <summary>One resolved tier's vegetation budgets, in the terrain stack's own vocabulary.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The one place the two vocabularies are matched up, and adding a knob to
    ///         <c>VegetationQuality</c> without adding it here carries the number the whole length of
    ///         the waterfall and drops it.</b> The records are deliberately not the same type: the
    ///         waterfall lives in <c>Vixen.Rendering.PostFx</c> and the consumer in
    ///         <c>Vixen.Rendering.Terrain</c>, which must not reference it — see
    ///         <see cref="TerrainVegetationQuality" />. Two of the names differ across the seam
    ///         (<c>TerrainLodNearRange</c> is the terrain stack's <c>TerrainNearRange</c>), which is
    ///         the other reason this is written out rather than reflected over.
    ///     </para>
    ///     <para>
    ///         <c>GrassBladesPerCell</c> is absent because no tier decides it: it is the scatter
    ///         dispatch's shape rather than a budget, so it keeps the record's own default and a
    ///         document or the game says otherwise.
    ///     </para>
    ///     <para>
    ///         What arrives here is folded from the frame document as well as the host — a
    ///         <c>!StandardFrame</c>'s own <c>quality:</c> and inline <c>preset:</c> included, which
    ///         for a while it was not. The expansion replaces the node during the build, so the vote
    ///         is read off the document before it (<c>PostEffectFactory.QualityOf</c>) rather than
    ///         out of the built frame, where it no longer exists. The <c>!Terrain</c> node's own
    ///         scalars are a further document-level vote and out-vote this per field, in
    ///         <c>TerrainFactory.Create</c>.
    ///     </para>
    /// </remarks>
    static TerrainVegetationQuality VegetationOf(ResolvedQuality quality) =>
        new() {
            GrassDensityScale = quality.GrassDensityScale,
            GrassCullDistanceScale = quality.GrassCullDistanceScale,
            GrassResidentCells = quality.GrassResidentCells,
            FoliageDensityScale = quality.FoliageDensityScale,
            FoliageCullDistanceScale = quality.FoliageCullDistanceScale,
            FoliageCellBudget = quality.FoliageCellBudget,
            TerrainNearRange = quality.TerrainLodNearRange,
            TerrainStreamingMegabytes = quality.TerrainStreamingMegabytes
        };

    /// <summary>Builds the swapchain if there is not one, and says what the frame is now sized to.</summary>
    void EnsureSwapChain() {
        if (swapChain is not null) {
            return;
        }

        swapChain = SwapChainFor(Device, window, options);

        // ⚠ What was asked for, not swapChain.Size — see the field. The two differ wherever the
        // surface overrides the extent, and recording what came back means every later Recreate finds
        // a difference that rebuilding cannot remove: a device-wide wait and a fresh set of undefined
        // images, every frame, for ever.
        built = Target;

        Resize();
        HostLog.GraphicsStarted(logger, Device.Adapter.Name, Device.Adapter.Kind, built.X, built.Y);
        ReportScaling();
    }

    /// <summary>Says so when the frame is not the size the window was asked for.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The point of this method is that the difference is invisible otherwise.</b> A
    ///         window is requested in logical points and a swapchain is built in physical pixels
    ///         (<see cref="FramebufferOf" />), so on a retina display the frame has four times the
    ///         pixels the application named and nothing before this line said which of the two numbers
    ///         <see cref="HostLog.GraphicsStarted" /> is reporting. Quadrupling a number somebody chose
    ///         is a decision the engine is entitled to make and not one it may make quietly.
    ///     </para>
    ///     <para>
    ///         Read off the window rather than off <c>WindowOptions</c>, and deliberately: the window
    ///         manager is entitled to have ignored the requested size, so the honest comparison is
    ///         between the client area the window actually has and the framebuffer it actually got.
    ///     </para>
    /// </remarks>
    void ReportScaling() {
        if (window is null) {
            return;
        }

        var points = window.ClientSize;

        if (points.X <= 0 || points.Y <= 0 || (points.X == built.X && points.Y == built.Y)) {
            return;
        }

        HostLog.FramebufferScaled(
            logger,
            points.X,
            points.Y,
            built.X / (float)points.X,
            built.X,
            built.Y,
            built.X * (float)built.Y / (points.X * (float)points.Y)
        );
    }

    /// <summary>Lends the acquired image to the document, under the name the frame writes.</summary>
    /// <remarks>
    ///     ⚠ <b>Every frame, not once.</b> A swapchain hands out a different image each acquire, and
    ///     an import bound once names whichever one happened to be first — so two frames in three are
    ///     drawn into an image that is not being presented, which on a triple-buffered surface reads
    ///     as a picture that judders rather than as nothing at all.
    /// </remarks>
    void Lend(TextureViewHandle view) {
        var description = new TextureDescription(
            swapChain!.Format,
            swapChain.Size.X,
            swapChain.Size.Y,
            TextureUsage.ColourTarget,
            Name: options.Output
        );

        // ⚠ The exit state is what the image is *for* after the frame, and the two answers are not
        // interchangeable. A presented image ends in Present; an offscreen one has no presentation
        // engine to hand it to, and PRESENT_SRC is a layout that only means anything where the
        // swapchain extension is loaded — so an offscreen frame that asked for it would be a
        // validation error on a device that never enabled the thing the layout belongs to.
        //
        // CopySource is also exactly where a capture wants it, which is why the readback needs no
        // barrier of its own. That is the golden suite's arrangement, and its Fixture explains at
        // length why an *imported* target is the thing that makes it work: a transient the graph
        // created would be stored DontCare and read back as undefined memory.
        Renderer.Host.Import(
            options.Output,
            new(
                swapChain.CurrentTexture,
                view,
                description,
                ResourceState.Undefined,
                IsOffscreen ? ResourceState.CopySource : ResourceState.Present
            )
        );
    }

    /// <summary>Whether the frame has nothing to present to, and is therefore readable.</summary>
    /// <remarks>
    ///     The window's own answer rather than a setting, because it is the surface that decides: a
    ///     headless window is a real window with a real size whose surface is
    ///     <see cref="Core.SurfaceKind.None" />, and a head with no window at all is the same case
    ///     with less of it.
    /// </remarks>
    bool IsOffscreen => !(window?.Surface.Handle ?? Core.SurfaceHandle.None).CanPresent;

    /// <summary>Tells the frame and the camera how big the target is.</summary>
    /// <remarks>
    ///     Where the size is decided rather than where the frame is drawn, because the camera's
    ///     extraction runs in the engine's <c>PreRender</c> — which is over by the time
    ///     <see cref="Begin" /> is called. Setting the aspect ratio there would render every frame
    ///     through the previous frame's shape, which nobody sees until the window is dragged.
    /// </remarks>
    void Resize() {
        // The swapchain's own size here rather than what was asked for: this is what every resource
        // the document declares is a fraction of, and the frame has to match the images it is drawn
        // into even where the surface overrode the request.
        var size = swapChain?.Size ?? Target;

        Renderer.Host.FrameSize = size;
        Camera.AspectRatio = size.X / (float)Math.Max(size.Y, 1);

        // ⚠ Render-target pixels, not window points, and kept current here because nothing in
        // Vixen.Engine can know either. A stale value draws the panels for the size the window used
        // to be, which after a resize is a corner panel hanging off the edge — and on a 2× display
        // with the logical size it is every panel at half size in the top-left quarter.
        if (overlaySystem is not null) {
            overlaySystem.Viewport = new(size.X, size.Y);
        }
    }
}
