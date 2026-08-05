// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Vixen.Assets;
using Vixen.Core.Mathematics;
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
    readonly bool ownsDevice;

    ISwapChain? swapChain;
    ICommandList? commands;

    /// <summary>The framebuffer size the swapchain was last built for.</summary>
    /// <remarks>
    ///     ⚠ <b>What was asked for, not what came back.</b> A surface decides its own extent — Vulkan's
    ///     <c>currentExtent</c> overrides the request — so comparing against <see cref="ISwapChain.Size" />
    ///     would find a difference that rebuilding cannot remove, which is a rebuild every frame for ever.
    /// </remarks>
    Int2 built;

    int reportedWarnings;
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

        Effects = new();

        // The one source a shipped build has. The code that could compile a variant instead lives in
        // Tools/Vixen.ShaderCompiler and is never linked into a game, so a build with no bundle
        // resolves every material to a miss — which EffectSystem counts, and which a development head
        // fixes by adding a provider of its own to Effects.
        if (shaders is not null) {
            Effects.AddProvider(new EffectSourceProvider(shaders, new(device)));
            HostLog.ShadersMounted(logger, shaders.Count);
        }

        Renderer = new(device, Effects, options.VertexCapacity, options.IndexCapacity);

        // Before Load, because the builder binds a document's `view:` by name as it builds the nodes,
        // and a view added afterwards is one nothing refers to.
        View = new(options.View);
        Renderer.Host.Builder.Views[options.View] = View;

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
        var quality = RenderQuality.Resolve(options.Quality, preset);

        // ⚠ Before the factories too, because a !WaterSurface node is handed this as it is created
        // and a node with no zones draws nothing at all. It is also the one water clock — see
        // WaterZoneSystem.WaterTime — so the surface, the underwater volume and a buoyancy solver all
        // read the same number rather than three that agree until the frame rate changes.
        Water = new(View);

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
        // the platform's pick. See GraphicsOptions.Quality.
        Renderer.Host.Builder.Quality = options.Quality;

        Renderer.Host.Load(Frame(assets));

        if (Renderer.Host.Builder.Stages.TryGetValue(options.Stage, out var stage)) {
            // The view draws the camera's stage alone; extraction covers that one and every caster
            // stage beside it. See GraphicsOptions.CasterStages for why the two masks differ.
            var extracted = stage.Mask;

            foreach (var caster in options.CasterStages) {
                if (Renderer.Host.Builder.Stages.TryGetValue(caster, out var casting)) {
                    extracted |= casting.Mask;
                } else {
                    HostLog.NoStage(logger, caster);
                }
            }

            Stages = extracted;
            View.Stages = stage.Mask;
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
            engine.Add(Volumes);
            Renderer.Register(engine, Stages, ParticleStages);
        }

        Resize();
    }

    /// <summary>The device everything here lives on.</summary>
    public IGraphicsDevice Device { get; }

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

    /// <summary>The post-process volumes the camera is inside, folded into one overlay.</summary>
    /// <remarks>
    ///     Its result reaches the frame in <see cref="Begin" />, between the engine's update and the
    ///     compositor's build — see there.
    /// </remarks>
    public PostProcessVolumeSystem Volumes { get; }

    /// <summary>Which stages the world's drawables are extracted into.</summary>
    public RenderStageMask Stages { get; }

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

        commands = Device.BeginCommandList(QueueKind.Graphics, "frame");

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

    /// <summary>Closes the frame: submits it and presents.</summary>
    public void End() {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (commands is not { } list) {
            return;
        }

        commands = null;

        list.Finish();
        Device.GraphicsQueue.Submit([list]);
        list.Dispose();

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

        return device.CreateSwapChain(new(surface, size, options.Format, options.PresentMode));
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

        Renderer.Dispose();

        if (ownsDevice) {
            Device.Dispose();
        }
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
    ///         ⚠ <b>What this cannot see is a <c>!StandardFrame</c>'s own <c>quality:</c> or inline
    ///         <c>preset:</c>.</b> Those are read inside the build, by an expansion that has already
    ///         replaced the node by the time anything holds the document — so a frame document that
    ///         names its own tier moves the post chain and not the ground. The <c>!Terrain</c> node's
    ///         own scalars are the document-level vote that does reach here, and they out-vote this
    ///         per field.
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

        Renderer.Host.Import(
            options.Output,
            new(swapChain.CurrentTexture, view, description, ResourceState.Undefined, ResourceState.Present)
        );
    }

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
    }
}
