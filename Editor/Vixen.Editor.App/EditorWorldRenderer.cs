// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.SceneView;
using Vixen.Engine.Renderer;
using Vixen.Graphics;
using Vixen.Graphics.RenderGraph;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Ecs;
using Vixen.Rendering.Lighting;
using Vixen.Rendering.Materials;
using Vixen.Rendering.PostFx;
using Vixen.Shaders;
using Vixen.Shaders.Generated;

namespace Vixen.Editor.App;

/// <summary>The standard renderer, over a scene document's world, in the editor.</summary>
/// <remarks>
///     <para>
///         <b>The three inputs a compositor-driven viewport needs, and nothing that draws.</b> A
///         <see cref="Engine.Renderer.WorldRenderer" />, the two extraction systems that fill it from a
///         world, and a <see cref="RenderView" /> aimed by an <see cref="EditorCamera" />. What is
///         missing is the frame document and the pane that presents it, which is deliberately somebody
///         else's: every one of these is assertable on its own, and a pane built over inputs nobody had
///         checked is a pane whose first failure is attributed to the pane.
///     </para>
///     <para>
///         ⚠ <b><c>Mount</c> is not called, and the mesh source is set by hand instead.</b> This
///         paragraph used to say mounting means a catalog "a *content build* wrote" and cited
///         <c>ProjectMeshSource</c>'s "waiting for a build" line. Both are wrong: <c>EditorContent</c>
///         mounts a <c>LooseContent</c> catalog, which needs no build and reads the same import cache.
///         The argument that survives is narrower and is about what a catalog omits — an excluded
///         asset gets no address, so the catalog silently resolves less than the import cache does.
///         So geometry comes from the same import cache the tool renderer already reads, the two
///         cannot disagree about what a mesh is, and nothing in a project stops being drawn because
///         it is not shipped.
///     </para>
///     <para>
///         ⚠ <b>Which is an argument about geometry only, and the paragraph below is the cost of
///         over-reading it.</b> <c>Mount</c> is also the only thing that builds an
///         <c>IMaterialSource</c>, a texture source, a vfx source and the terrain seams, and
///         <c>Source</c> and <c>Painter</c> are both settable — so mounting and then restoring
///         <c>ProjectMeshSource</c> as the geometry is what would close this without reopening that.
///     </para>
///     <para>
///         ⚠ <b>Which leaves <see cref="Engine.Renderer.WorldRenderer.Painter" /> null, and that is a
///         real degrade rather than a detail.</b> There is no editor-side <see cref="IMaterialSource" />
///         — <c>ProjectSurfaceSource</c> is the tool renderer's tint-and-style source and does not
///         satisfy that interface — so every drawable in the scene is painted with
///         <see cref="Fallback" /> whatever material it names. <see cref="Degraded" /> is the sentence
///         that says so, and it exists because the alternative is a viewport where assigning a material
///         appears to do nothing.
///     </para>
///     <para>
///         ⚠ <b>Not a <c>SceneRenderer.Degrade</c>, and it could not be.</b> That mechanism reports a
///         <em>node</em>'s degradation into <c>GraphicsCompositor.Degradations</c> — <c>Degrade</c> is
///         protected, a node calls it about itself, and the collection walks the frame's nodes. A
///         missing material source is a fact about the host before any document is loaded: there is no
///         node whose condition it is, and every node in the frame would have to repeat it. It belongs
///         where <see cref="EditorEffects.Refusal" /> is, which is a string a panel reads.
///     </para>
/// </remarks>
sealed class EditorWorldRenderer : IDisposable {
    /// <summary>What the frame document calls the view the first pane looks through.</summary>
    /// <remarks>
    ///     <c>GraphicsCompositorAsset.Default</c>'s single stage names <c>Camera</c>, and a
    ///     <c>view:</c> is bound by name as the builder creates the node — so a view registered after
    ///     the build is one nothing refers to, which is a frame that collects no views and culls
    ///     everything.
    /// </remarks>
    public const string CameraView = "Camera";

    /// <summary>How many panes the editor's own document declares a frame for.</summary>
    /// <remarks>
    ///     ⚠ <b>Four because <c>ViewportArrangement.Quad</c> is four</b>, and the document is built
    ///     once in a constructor — so the slots have to exist before anybody splits the panel. A pane
    ///     past this has no view bound by name and therefore no tree registered, which
    ///     <c>EditorApplication.RegisterViewModes</c> turns into a pane the tool renderer draws rather
    ///     than a pane that fails.
    /// </remarks>
    public const int MaxPanes = 4;

    /// <summary>What the document calls the view a given pane looks through.</summary>
    /// <remarks>
    ///     ⚠ <b>The first pane's name is unsuffixed, and that is load-bearing rather than tidy.</b> A
    ///     project's own <c>.vxcompositor</c> names <c>Camera</c> and knows nothing about panes — see
    ///     <see cref="Reload" /> — so the pane an authored document draws has to be the one whose view
    ///     is bound under that name.
    /// </remarks>
    public static string ViewName(int pane) => pane == 0 ? CameraView : CameraView + pane.ToString();

    readonly LightExtractionSystem lights;
    readonly IGraphicsDevice device;

    /// <summary>One view per pane, made before the build and never replaced.</summary>
    /// <inheritdoc cref="View" path="/remarks" />
    readonly RenderView[] views = new RenderView[MaxPanes];

    /// <summary>The panes' trees, joined into the one frame a build composes.</summary>
    /// <remarks>
    ///     ⚠ <b>One object, refilled, rather than a sequence per frame.</b> This is assigned to
    ///     <c>Compositor.Game</c> once a frame for as long as the editor is open, and a node allocated
    ///     per frame would be a per-frame allocation in the record loop for no gain at all.
    /// </remarks>
    readonly SceneRendererSequence composed = new() { Name = "Editor panes" };

    readonly List<(string Node, string Reason)> degradations = [];

    /// <summary>The sky the ambient term and the reflections come out of.</summary>
    readonly EnvironmentTexture sky;

    /// <summary>The two set-0 bindings the default frame declares and no node in it produces.</summary>
    readonly TextureHandle shadowStandIn;
    readonly TextureViewHandle shadowStandInView;
    readonly BufferHandle shadowStandInStaging;
    readonly BufferHandle clusterStandIn;

    bool standInsUploaded;
    bool disposed;

    /// <summary>Builds the renderer and the bridges into it.</summary>
    /// <param name="device">The device everything lives on.</param>
    /// <param name="effects">
    ///     Where variants come from — <see cref="EditorEffects.System" />, which is one object for the
    ///     life of the editor precisely because this constructor keeps it.
    /// </param>
    /// <param name="meshes">
    ///     Where the geometry a <c>MeshRenderable</c> names comes from, or null to draw none of them.
    ///     Null is not an error: a project with no import cache yet has nothing to resolve, and the
    ///     entities wait rather than disappearing — see <see cref="Waiting" />.
    /// </param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public EditorWorldRenderer(IGraphicsDevice device, EffectSystem effects, IMeshSource? meshes = null) {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(effects);

        this.device = device;

        // ⚠ A tenth of the geometry budget a game's default reserves. A scene open in an editor is
        // one level, and the buffers are allocated for real on the device the moment this is built —
        // whereas a game sizes them for whatever it streams. `Meshes.Dropped` is what says a level
        // outgrew them, and it is a number rather than a silent stop.
        Renderer = new(device, effects, vertexCapacity: 1 << 18, indexCapacity: 1 << 19) {
            Source = meshes
        };

        Fallback = CompileFallback();

        // ⚠ Assembled here rather than through `WorldRenderer.Register`, which takes an `EngineLoop`.
        // An editing frame runs no system graph — `TransformSystem` is resolved by hand for the same
        // reason, and that decision is `EditorApplication.ResolveTransforms`' own remarks — so what a
        // loop would give is a scheduler for two calls whose order is already decided by this file.
        // ⚠ And a *play* session's loop, which does exist now, deliberately does not get them either:
        // `Register` would schedule this extraction beside the out-of-band `Extract` the editor
        // already calls every frame, and it writes `RenderHandle` structurally and claims residency
        // per entity — so it would be run twice over one world. See `EditorFrames`.
        Meshes = new(Renderer.Host.System, Renderer.Meshes, Renderer.Transforms, Renderer.Materials, Renderer.Residency) {
            Meshes = meshes,

            // ⚠ And *not* `Materials`, which stays null. See the type's remarks: with no source, a
            // drawable that names a material still draws — in this one — because a host that cannot
            // resolve one should show geometry rather than nothing.
            Material = Fallback
        };

        lights = new(Renderer.Lighting);

        // ⚠ The views before the build, the build before the mask, and the mask before anything
        // extracts. The first is the builder's rule — a `view:` is bound by name as each node is
        // created — and the second is `Stages`' own: a stage's index does not exist until a document
        // declares it, and a mask assigned after the first extraction reaches nothing that is
        // already in the frame. Both are why the document is built in a constructor rather than by
        // whichever pane happens to open first.
        // ⚠ All four, and not the count the panel happens to have. A pane splits mid-session and the
        // document is built once, so a view registered when the split happens is one no node refers
        // to — the frame would collect it, cull for it and draw nothing from it.
        for (var pane = 0; pane < MaxPanes; pane++) {
            views[pane] = new RenderView($"Editor {pane}");
            Renderer.Host.Builder.Views[ViewName(pane)] = views[pane];
        }

        // The sky before the build, because the ambient term is set 0's and the background node the
        // build creates has to be handed the cube afterwards.
        sky = BakeSky(device);
        Renderer.Environment = sky;

        var ambient = new EnvironmentLight { MipCount = sky.MipCount, Intensity = 1f, Irradiance = SkyIrradiance };

        sky.Apply(ambient);
        Renderer.SceneEnvironment.Environment = ambient;

        // ⚠ Registered before the build, because a node kind nothing has bound is not a warning —
        // it is a `CompositorBindingException` out of the middle of the build.
        Renderer.Host.Builder.Factories.Add(new PostEffectFactory());

        // ⚠ `Builder.Build` rather than `Host.Load`, and the difference is which graph draws.
        // `Host.Load` would put this compositor in `Host.Compositor`, and `WorldRenderer.Draw` would
        // then build it into the host's *own* graph and execute it there — a second graph, whose
        // resources the editor's per-window graph cannot order its interface pass against. Left null,
        // `Host.Draw` returns immediately and `WorldRenderer.Draw` is exactly the per-frame prologue
        // a pane needs: the descriptor pools' frame boundary, the geometry residency's flush, the
        // sky's upload and the set-1 layout. The pane builds this into the window's graph itself.
        // ⚠ Everything `Adopt` does is what a rebuild has to do again, which is why it is one call
        // rather than a run of statements in a constructor. See `Reload`.
        Compositor = Adopt(Renderer.Host.Builder.Build(Document()));

        (shadowStandIn, shadowStandInView, shadowStandInStaging) = CreateShadowStandIn(device);

        clusterStandIn = device.CreateBuffer(
            new(ClusterGrid.BufferSize, BufferUsage.Storage, MemoryAccess.HostUpload, "Editor clusters")
        );

        device.Write(clusterStandIn, 0, new byte[ClusterGrid.BufferSize]);

        // ⚠ Three names, and every one of them is the difference between a picture and a black pane.
        // `ForwardPlus` declares `shadowMap`, `shadowSampler` and `clusters` whatever its
        // permutations say — a permutation folds code, not bindings — and `EffectSetWriter` writes a
        // set whole or not at all. The default frame has neither a shadow node nor a culling
        // dispatch, so nothing in it produces any of the three, and a set 0 short one binding is not
        // a frame without shadows: it is every draw in the pass refused.
        // The cascade matrices are zero, so `CascadeContaining` finds no cascade for any fragment and
        // the shader answers "fully lit" without ever sampling the map — which is why one white texel
        // is an honest stand-in rather than a shadow term somebody has to explain.
        Renderer.SceneBlock.Parameters.Set(ForwardPlusKeys.ShadowMap, shadowStandInView);
        Renderer.SceneBlock.Parameters.Set(ForwardPlusKeys.ShadowSampler, Renderer.Samplers.PointClamp);
        Renderer.SceneBlock.Parameters.Set(ForwardPlusKeys.Clusters, clusterStandIn);
    }

    /// <summary>What the editor frame's shaded stage is called.</summary>
    const string OpaqueStage = "Opaque";

    /// <summary>And the stage the wireframe mode draws the same geometry in.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A stage of its own rather than <see cref="ViewModes.ApplyTo" /> against the shaded
    ///         one, and the pipeline cache is why.</b> <c>PipelineKey</c> is
    ///         <c>(Effect, Stage.Index, VertexLayout, Output)</c> —
    ///         <c>Core/Vixen.Rendering/PipelineCache.cs:33</c> — so a stage's rasterizer, blend and
    ///         depth state are read exactly once, by <c>EffectPipelineDescriber.Describe</c> on the
    ///         first draw that misses the cache, and baked into a pipeline the key can no longer tell
    ///         apart from any other state on the same stage. The cache never evicts and nothing in the
    ///         tree calls <c>Clear</c>. Mutating a stage that has already drawn is therefore silent:
    ///         the mode changes and the picture does not.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Which makes a stage the mode's, not the pane's.</b> Two panes in wireframe share
    ///         this stage and both draw wireframe correctly; a pane in shaded is on a different index
    ///         and gets a different pipeline. <see cref="ViewModes.ApplyTo" />'s own remark that "a
    ///         four-pane layout with independent render modes needs a stage per pane" is true only of
    ///         the mutate-a-shared-stage arrangement the cache rules out.
    ///     </para>
    /// </remarks>
    const string WireframeStage = "Wireframe";

    /// <summary>What a pane's shaded subtree is called, and what <see cref="Trees" /> looks it up by.</summary>
    /// <remarks>
    ///     ⚠ <b>Unique per pane, because <c>CompositorBuilder.Nodes</c> is a dictionary by name and a
    ///     repeat silently overwrites</b> — <c>Core/Vixen.Rendering/Compositor/CompositorBuilder.cs:629</c>.
    ///     Four panes sharing a node name is three panes whose tree cannot be looked up, which is
    ///     three panes with no mode registered and therefore three panes the tool renderer draws.
    /// </remarks>
    static string ShadedTree(int pane) => pane == 0 ? "Shaded" : $"Shaded {pane}";

    /// <summary>And the wireframe one.</summary>
    static string WireframeTree(int pane) => pane == 0 ? "Wireframe view" : $"Wireframe view {pane}";

    /// <summary>What a pane's node is called, which is the same name with the pane number after it.</summary>
    static string Named(string name, int pane) => pane == 0 ? name : $"{name} {pane}";

    /// <summary>The linear target a pane's scene is shaded into, before the curve.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Transient, and it is the reason the pane's own colour is the frame's <em>last</em>
    ///         target rather than its first.</b> A shading pass writes luminance in cd/m² — an overcast
    ///         sky is thousands of them — so an 8-bit target here is every surface clipped to white,
    ///         which reads as "the lighting broke" rather than as "there is no exposure in this frame".
    ///     </para>
    ///     <para>
    ///         ⚠ <b>One per pane, and sized explicitly rather than from the frame's reference
    ///         size.</b> A declared resource with no size is <c>Scale</c> of <c>FrameSize</c> —
    ///         <c>RenderResourceAsset.Describe</c> — and a frame has one of those where four panes have
    ///         four extents. Left to the reference size, three panes would attach a colour of one size
    ///         beside a depth of another, which is a framebuffer the driver refuses rather than a
    ///         picture that is merely wrong. <see cref="Size" /> is what writes them.
    ///     </para>
    /// </remarks>
    static string HdrTarget(int pane) => pane == 0 ? "SceneHdr" : $"SceneHdr{pane}";

    /// <summary>What the frame is graded at, as an exposure value at ISO 100.</summary>
    /// <remarks>
    ///     ⚠ <b>Fixed rather than metered, which is an editor's decision and not a game's.</b> An
    ///     auto-exposure node eases towards its target over about a second, so a viewport driven by
    ///     one visibly re-grades itself whenever a designer orbits past a bright surface — and two
    ///     panes of one scene would settle at different exposures and disagree about the colour of
    ///     the same wall.
    /// </remarks>
    /// <remarks>
    ///     ⚠ <b>Measured off the picture rather than read off the table.</b> Twelve is the documented
    ///     value for overcast and it grades this frame to 0.89 — a pane whose sky and whose surfaces
    ///     are twenty sRGB units apart, which reads as fog rather than as a scene. The table names
    ///     the luminance a <em>subject</em> is at, and a subject lit by nothing but the dome over it
    ///     sits one albedo below the sky rather than several stops below a sun.
    /// </remarks>
    const float Ev100 = 13.5f;

    /// <summary>The frame the pane draws: a sky, one shading pass, and a curve.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><c>GraphicsCompositorAsset.Default</c> with the two things a picture needs added,
    ///         and nothing else.</b> That document is one opaque stage into a colour and a depth, and
    ///         it exists so that "a new project renders something" is true — it is not a frame
    ///         anybody looks at, because it has no exposure in it and no background.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The names the pane lends are still exactly two.</b> <c>SceneColour</c> is the
    ///         graded result and <c>SceneDepth</c> is what the shading pass wrote, which is what the
    ///         tool overlay tests against — the linear intermediate between them is the graph's, so
    ///         it can be sized, aliased and dropped.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The shading pass <em>loads</em> the colour the sky filled.</b> A node that
    ///         cleared it would draw the background and then wipe it, which is a black frame with
    ///         correctly lit objects in it — <c>SkyAsset</c>'s own remarks say so.
    ///     </para>
    ///     <para>
    ///         Zero is <em>far</em> under the engine's reversed-Z convention, which every depth state
    ///         in the engine agrees on.
    ///     </para>
    /// </remarks>
    static GraphicsCompositorAsset Document() {
        var resources = new List<RenderResourceAsset>();
        var panes = new List<ISceneRendererAsset>();

        for (var pane = 0; pane < MaxPanes; pane++) {
            resources.Add(new() { Name = HdrTarget(pane), Format = PixelFormat.Rgba16Float });

            resources.Add(
                new() {
                    Name = FramePresenter.Depth(pane),
                    Format = FramePresenter.DepthFormat,
                    Usage = TextureUsage.DepthStencilTarget
                }
            );

            panes.Add(Shaded(pane));
            panes.Add(Wireframe(pane));
        }

        return new() {
            Version = CompositorBuilder.SupportedVersion,

            // ⚠ Both stages declared by the one document, so both indices exist before anything
            // extracts and `Stages` can be their union. A stage added later is a bit no object in the
            // store carries — see that property's remarks.
            // ⚠ Two, not two per pane. A stage belongs to the mode: `PipelineKey` is
            // `(Effect, Stage.Index, VertexLayout, Output)`, so two panes in wireframe share this
            // index and get the same pipeline, which is the pipeline wireframe wants. The thing that
            // is per pane is the *view*, and that is what this document has four of.
            Stages = [new() { Name = OpaqueStage }, new() { Name = WireframeStage, Cull = CullMode.None }],
            Resources = [.. resources],

            // ⚠ One build, eight subtrees, and the ones a frame draws are chosen per frame. Building
            // a second document on the same builder would work — `AddStage` reuses by name for
            // exactly that case — but `Build` clears `Nodes`, `Uploads` and `Readbacks` first, so the
            // tree built before it would be one whose sky node nothing can reach and whose uploads
            // nothing copies. A node that is in no tree costs nothing: `Build`, `Collect` and
            // `Degradations` all walk from `Game` down.
            Game = new SequenceAsset { Name = "Editor frame", Children = [.. panes] }
        };
    }

    /// <summary>One pane's shaded frame: a sky, one shading pass, and a curve.</summary>
    static SequenceAsset Shaded(int pane) =>
        new() {
            Name = ShadedTree(pane),
            Children = [
                new SkyAsset { Name = Named("Background", pane), Output = HdrTarget(pane), View = ViewName(pane) },
                new RenderPassAsset {
                    Name = Named("Main", pane),
                    ColourTargets = [HdrTarget(pane)],
                    Load = LoadAction.Load,
                    DepthTarget = FramePresenter.Depth(pane),
                    ClearDepth = 0f,
                    Children = [
                        new SingleStageAsset {
                            Name = Named("Opaque draw", pane),
                            View = ViewName(pane),
                            Stage = OpaqueStage
                        }
                    ]
                },
                new TonemapAsset {
                    Name = Named("Grade", pane),
                    Source = HdrTarget(pane),
                    Output = FramePresenter.Colour(pane),
                    Format = FramePresenter.ColourFormat,
                    Ev100 = Ev100,

                    // ⚠ Encoded here rather than by the target's format, because the interface
                    // samples this rather than presenting it — see `EditorHost.Presenter` for the
                    // same decision on the tool pane. A UNorm-sRGB target would be decoded on the way
                    // into the interface's shader and encoded again on the way out.
                    EncodeSrgb = true
                }
            ]
        };

    /// <summary>And its wireframe one.</summary>
    /// <remarks>
    ///     ⚠ No sky and no grade, and both absences are the mode rather than an economy. A wireframe
    ///     view is asked "where is the geometry", so a background that is a gradient is edges lost
    ///     against it — and a curve that maps an overcast sky onto 0.89 would grade a line drawn at a
    ///     few thousand cd/m² to the same grey as the sky it was meant to stand out from. Clipped
    ///     white on near-black is the picture.
    /// </remarks>
    static SequenceAsset Wireframe(int pane) =>
        new() {
            Name = WireframeTree(pane),
            Children = [
                new RenderPassAsset {
                    Name = Named("Wires", pane),
                    ColourTargets = [FramePresenter.Colour(pane)],
                    ClearColour = new(0.04f, 0.045f, 0.06f),
                    DepthTarget = FramePresenter.Depth(pane),
                    ClearDepth = 0f,
                    Children = [
                        new SingleStageAsset {
                            Name = Named("Wireframe draw", pane),
                            View = ViewName(pane),
                            Stage = WireframeStage
                        }
                    ]
                }
            ]
        };

    /// <summary>The frame the pane draws, built from the document a project with none falls back to.</summary>
    /// <remarks>
    ///     ⚠ <b>Built into the caller's graph, not executed here.</b>
    ///     <c>GraphicsCompositor.Build</c> takes a <c>RenderGraph</c>, resets nothing and runs
    ///     nothing — so the editor's per-window graph is what a pane hands it, and the interface's
    ///     pass can then declare that it reads the frame's colour like any other resource.
    /// </remarks>
    public GraphicsCompositor Compositor { get; private set; }

    /// <summary>The stage a shaded pane's objects are drawn in.</summary>
    public RenderStage Opaque { get; private set; }

    /// <summary>The compositor tree for each view mode a given pane can honestly draw.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>What <see cref="ViewModes.Register" /> is fed, and the whole of "a mode is a
    ///         compositor".</b> Each entry is a subtree of the one built document, and switching mode
    ///         is the pane contributing a different one to the frame — no rebuild, no second render
    ///         system, and no branch inside a renderer.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Per pane, because a tree names a view and a target and both are the pane's.</b>
    ///         Two panes registered against one tree would be two panes drawing one camera into one
    ///         texture, which is the arrangement this replaced.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A mode is absent rather than present-and-wrong.</b> Wireframe is here only when
    ///         the device reports <c>HasWireframe</c>; <c>FillMode.Wireframe</c> needs
    ///         <c>fillModeNonSolid</c>, which is optional in Vulkan, and a pipeline built without it
    ///         is silently filled solid — a wireframe view drawing the shaded picture. Absent, the
    ///         pane keeps the tool renderer's wireframe, which is drawn as segments and therefore
    ///         works everywhere. <see cref="ViewModes.Registered" /> is what a host reads to choose.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Empty for a pane past <see cref="MaxPanes" />, and for every pane but the first
    ///         when a project's own document is installed.</b> A <c>.vxcompositor</c> declares one
    ///         frame naming one view and one colour; replicating it per pane would mean rewriting
    ///         every target name inside a tree of node kinds this assembly does not know. So an
    ///         authored frame composes the first pane and the rest keep the tool renderer, which
    ///         draws — see <see cref="Reload" />.
    ///     </para>
    /// </remarks>
    /// <param name="pane">Which pane, in the scene panel's reading order.</param>
    /// <returns>Its modes, which is empty for a pane no tree was built for.</returns>
    public IReadOnlyDictionary<ViewMode, SceneRenderer> Trees(int pane) =>
        pane >= 0 && pane < MaxPanes ? trees[pane] : Empty;

    static readonly Dictionary<ViewMode, SceneRenderer> Empty = [];

    readonly Dictionary<ViewMode, SceneRenderer>[] trees =
        [.. Enumerable.Range(0, MaxPanes).Select(_ => new Dictionary<ViewMode, SceneRenderer>())];

    /// <summary>The frame, its features, its descriptor pools and its compositor builder.</summary>
    public WorldRenderer Renderer { get; }

    /// <summary>What turns the world's drawables into the frame's objects.</summary>
    public MeshExtractionSystem Meshes { get; }

    /// <summary>What the first pane looks through.</summary>
    /// <inheritdoc cref="ViewOf" path="/remarks" />
    public RenderView View => views[0];

    /// <summary>What a given pane looks through.</summary>
    /// <param name="pane">Which pane.</param>
    /// <returns>Its view.</returns>
    /// <exception cref="ArgumentOutOfRangeException">There is no such pane.</exception>
    /// <remarks>
    ///     <para>
    ///         Held here rather than made per frame because
    ///         <see cref="RenderView.PreviousViewProjection" /> is the one piece of a view that has to
    ///         outlive a frame — a motion vector is measured against it, and a view rebuilt every
    ///         frame reports no history for ever.
    ///     </para>
    ///     <para>
    ///         <b>One per pane over one extracted store, and nothing is copied per view.</b>
    ///         <c>RenderSystem</c> is already an N-view machine: extraction fills one
    ///         <c>RenderObjectStore</c>, <c>Cull</c> writes a bitset per view index, <c>Sort</c> keys
    ///         its work lists by <c>(view, stage)</c> and <c>ViewConstants</c> keeps a uniform block
    ///         per <c>RenderView</c>. What is per view is a frustum, a bitset, a sorted list and 208
    ///         bytes — everything expensive is shared.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Which is why every pane is composed by one <see cref="Compose" />.</b> A view's
    ///         <c>Index</c> is assigned by <c>RenderSystem.SetViews</c>, which runs once per
    ///         <c>GraphicsCompositor.Collect</c> and clears the list — and the node lists a pass draws
    ///         are looked up by that index at <em>execute</em> time, which is after every pane has
    ///         built. A build per pane would therefore leave all four panes recording whichever view
    ///         took index 0 in the last collect: four cameras, one visible set, and every counter in
    ///         the frame healthy.
    ///     </para>
    /// </remarks>
    public RenderView ViewOf(int pane) {
        ArgumentOutOfRangeException.ThrowIfNegative(pane);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pane, MaxPanes);

        return views[pane];
    }

    /// <summary>What a drawable is painted with, or null when nothing would compile one.</summary>
    /// <remarks>
    ///     ⚠ <b>Null here is a frame in which nothing is drawn at all</b>, not a frame drawn untinted:
    ///     <c>MeshExtractionSystem</c> assigns a material only when it has one, and an object with none
    ///     resolves to no variant and is skipped. It is why <see cref="Degraded" /> distinguishes the
    ///     two cases rather than saying "fallback" in both.
    /// </remarks>
    public Material? Fallback { get; }

    /// <summary>Which stages an extracted object appears in.</summary>
    /// <remarks>
    ///     ⚠ <b>Set it before the first <see cref="Extract" />, not after.</b> A stage mask is copied
    ///     into each render object as it is created and a settled entity is never re-extracted, so a
    ///     mask assigned later reaches the next entity somebody adds and none of the ones already
    ///     there. Zero draws nothing, which is the state a host that has not loaded a frame document
    ///     is honestly in — a stage's index is assigned by the render system when the document
    ///     declares it.
    /// </remarks>
    public RenderStageMask Stages {
        get => Meshes.Stages;
        set => Meshes.Stages = value;
    }

    /// <summary>How many of the world's entities are in the frame's object list.</summary>
    public int ObjectCount => Meshes.ObjectCount;

    /// <summary>How many are waiting for geometry that has not been imported yet.</summary>
    public int Waiting => Meshes.Waiting;

    /// <summary>How many lights the last extraction put in the frame's list.</summary>
    public int LightCount => lights.LightCount;

    /// <summary>Why the picture is not what a game's would be, or null when it is.</summary>
    /// <inheritdoc cref="EditorWorldRenderer" path="/remarks/para[3]" />
    public string? Degraded => Fallback is null
        ? "No material would compile, so nothing in the scene is drawn at all."
        : Renderer.Painter is null
            ? "The editor has no material source, so every mesh is drawn in the fallback material "
            + "rather than the one it names."
            : null;

    /// <summary>Takes over everything a freshly built <see cref="Compositor" /> leaves for its host.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Every line of this is a thing that fails silently when a rebuild forgets it</b>,
    ///         which is why it is a method rather than a run of statements at the end of a
    ///         constructor. A sky node that never got its cube draws black — indistinguishable from a
    ///         frame with no background node at all. A stage mask left at the old document's indices
    ///         is a frame that reports its objects and draws none of them.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The stage state is set here, before the stage has ever drawn, and that is the
    ///         only moment it can be.</b> See <see cref="WireframeStage" />: the pipeline cache reads
    ///         it once, on the first draw that misses.
    ///     </para>
    /// </remarks>
    [MemberNotNull(nameof(Opaque))]
    GraphicsCompositor Adopt(GraphicsCompositor built) {
        var builder = Renderer.Host.Builder;

        // ⚠ By name, then whatever the document called its first stage, then a refusal. A frame that
        // declares no stage at all draws its background and none of the scene — which is a picture,
        // and therefore the kind of failure that gets attributed to the lighting.
        Opaque = builder.Stages.TryGetValue(OpaqueStage, out var opaque)
            ? opaque
            : builder.Stages.Values.FirstOrDefault()
            ?? throw new InvalidOperationException(
                "This frame document declares no render stage, so nothing in the scene would be drawn."
            );

        // ⚠ The union of every stage a mode might draw in, not the current mode's. A mask is copied
        // into a render object as it is created and a settled entity is never extracted again — so a
        // pane switched to wireframe after the scene loaded would find every object carrying the
        // shaded bit alone, and draw nothing. Set once, before the first `Extract`, covering all of
        // them; which stage actually runs is decided by the tree that is installed, and a stage no
        // node asked for collects nothing.
        var mask = Opaque.Mask;

        foreach (var stage in builder.Stages.Values) {
            mask |= stage.Mask;
        }

        Stages = mask;

        // ⚠ After the build and not once, because the nodes are made by the builder — a sky node
        // that never got its cube draws black, which is exactly what a missing background looks like.
        foreach (var node in builder.Nodes.Values) {
            if (node is SkyRenderer background) {
                background.Environment = sky.View;
                background.EnvironmentSampler = sky.Sampler;
                background.MipCount = sky.MipCount;
            }
        }

        foreach (var pane in trees) {
            pane.Clear();
        }

        // ⚠ Configured once and not per pane, because a stage belongs to the mode: two panes in
        // wireframe are two views collecting the same stage index, and `PipelineCache` reads this
        // state on the first miss and bakes it in. See `WireframeStage`.
        var wireframe = device.Features.HasWireframe
            && builder.Stages.TryGetValue(WireframeStage, out var wires);

        if (wireframe) {
            // The one legitimate call: a stage dedicated to the mode, configured before it has drawn.
            new ViewModes { Current = ViewMode.Wireframe }.ApplyTo(builder.Stages[WireframeStage]);
        }

        for (var pane = 0; pane < MaxPanes; pane++) {
            // ⚠ The named subtree when this is the editor's own document, and the whole frame when it
            // is a project's. A `.vxcompositor` knows nothing about the editor's mode names, and
            // refusing to register anything for it would be a pane that falls back to the tool
            // renderer the moment somebody opens the frame they are authoring — which is the one
            // moment they want to see it.
            // ⚠ And the whole frame only for the first pane, because it names one view and one
            // colour. See `Trees`.
            if (builder.Nodes.TryGetValue(ShadedTree(pane), out var shaded)) {
                trees[pane][ViewMode.Shaded] = shaded;
            } else if (pane == 0 && built.Game is { } whole) {
                trees[pane][ViewMode.Shaded] = whole;
            }

            // ⚠ Registered only where the device can draw it — see `Trees`. A tree that filled solid
            // would be a menu line that changes the picture into a worse copy of the one above it.
            if (wireframe && builder.Nodes.TryGetValue(WireframeTree(pane), out var wireTree)) {
                trees[pane][ViewMode.Wireframe] = wireTree;
            }
        }

        return built;
    }

    /// <summary>Points the view where an editor camera is looking.</summary>
    /// <param name="camera">The pane's camera.</param>
    /// <param name="aspectRatio">Width over height, in the pane's own pixels.</param>
    /// <exception cref="ArgumentNullException"><paramref name="camera" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Two paths, because <see cref="RenderCamera" /> is perspective and an editor camera
    ///         is not always.</b> Assigning <see cref="RenderView.Camera" /> is the better one — it
    ///         sets the position, the matrix and therefore the frustum from one description, and it is
    ///         what a shadow cascade fit needs, since slicing a cone wants the field of view a matrix
    ///         alone cannot give back. An orthographic pane has no cone, so it sets the matrix
    ///         directly and leaves the camera null, which is the same answer a shadow cascade's own
    ///         view gives.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see cref="RenderView.Advance" /> first, before the new matrix.</b> That is what
    ///         makes a motion vector measure this frame against last frame rather than against
    ///         itself — and it has to be called by whoever owns the per-frame update, which here is
    ///         this.
    ///     </para>
    /// </remarks>
    public void Aim(EditorCamera camera, float aspectRatio) => Aim(0, camera, aspectRatio);

    /// <summary>Points one pane's view where its editor camera is looking.</summary>
    /// <param name="pane">Which pane.</param>
    /// <param name="camera">The pane's camera.</param>
    /// <param name="aspectRatio">Width over height, in the pane's own pixels.</param>
    /// <exception cref="ArgumentNullException"><paramref name="camera" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">There is no such pane.</exception>
    /// <inheritdoc cref="Aim(EditorCamera, float)" path="/remarks" />
    public void Aim(int pane, EditorCamera camera, float aspectRatio) {
        ArgumentNullException.ThrowIfNull(camera);
        ObjectDisposedException.ThrowIf(disposed, this);

        var view = ViewOf(pane);

        view.Advance();

        // A pane one pixel wide during a splitter drag, or measured before the layout pass has run.
        // A zero or negative aspect makes a projection full of infinities and a frustum of NaN planes,
        // which culls the entire scene rather than failing.
        var aspect = aspectRatio > 0f && float.IsFinite(aspectRatio) ? aspectRatio : 1f;

        if (camera.IsOrthographic) {
            view.Camera = null;
            view.Position = camera.Position;
            view.ViewProjection = camera.ViewProjection(aspect);

            return;
        }

        view.Camera = new RenderCamera(
            camera.Position,
            camera.Forward,
            Vector3.UnitY,
            camera.FieldOfView,
            aspect,
            camera.NearPlane,
            camera.FarPlane
        );
    }

    /// <summary>Rebuilds the frame from a document, without a restart and without a new device.</summary>
    /// <param name="asset">
    ///     The frame to build, or <see langword="null" /> for the editor's own — which is what a
    ///     <c>StandardFrameDocument</c> being closed goes back to.
    /// </param>
    /// <param name="world">The scene's world, so the objects can be re-extracted under the new stages.</param>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>Builder.Build</c> and not <c>Host.Load</c>, for the reason the constructor
    ///         gives at length</b>: <c>Load</c> puts the compositor in <c>Host.Compositor</c>, and
    ///         <c>WorldRenderer.Draw</c> then builds it into the host's <em>own</em> graph and
    ///         executes it there — a second graph, whose resources the editor's per-window graph
    ///         cannot order its interface pass against. Left null, <c>Draw</c> is exactly the
    ///         per-frame prologue a pane needs and declares nothing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the objects are resettled, which is the half that has no symptom of its own.</b>
    ///         A document naming a stage the old one did not gets a fresh index, and every object
    ///         already in the store carries a mask without that bit — a pane that reports its two
    ///         objects, its one light, nothing waiting and nothing dropped, and draws an empty frame.
    ///     </para>
    /// </remarks>
    public void Reload(GraphicsCompositorAsset? asset, World world) {
        ArgumentNullException.ThrowIfNull(world);
        ObjectDisposedException.ThrowIf(disposed, this);

        // ⚠ Built and adopted before anything is assigned, so a document that would leave the pane
        // unable to draw — no stage at all — throws with the frame that works still installed. A
        // half-swapped renderer is a viewport whose failure outlives the edit that caused it.
        Compositor = Adopt(Renderer.Host.Builder.Build(asset ?? Document()));

        Resettle(world);

        // The mask is right and the objects are gone, so this is what puts them back under it.
        Extract(world);
    }

    /// <summary>Drops every extracted object, so the next <see cref="Extract" /> makes them again.</summary>
    /// <remarks>
    ///     ⚠ <b>The <c>RenderHandle</c> is what has to go, not the object</b> — an entity is "settled"
    ///     precisely by carrying one. That is <c>MeshExtractionSystem.Resettle</c>'s whole job, and it
    ///     owns the store and the residency claim this would otherwise have to reach through, so this
    ///     is a forward rather than a copy.
    /// </remarks>
    void Resettle(World world) => Meshes.Resettle(world);

    /// <summary>Brings the frame's objects and lights up to date with a world.</summary>
    /// <param name="world">The scene document's world.</param>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>After the transforms have been resolved, not before.</b> Both queries want
    ///     <c>WorldTransform</c> and neither computes one: in a game the phase and the declared access
    ///     put them after <c>TransformSystem</c>, and the editor has no graph to do that — so the order
    ///     is <see cref="EditorApplication.ResolveTransforms" /> and then this, and an extraction that
    ///     ran first would place every object where it was last frame.
    /// </remarks>
    public void Extract(World world) {
        ArgumentNullException.ThrowIfNull(world);
        ObjectDisposedException.ThrowIf(disposed, this);

        Meshes.Extract(world);
        lights.Extract(world);
    }

    /// <summary>Puts the one-off copies the frame's stand-ins need on the list.</summary>
    /// <param name="commands">The frame's list, open and outside a render pass.</param>
    /// <exception cref="ArgumentNullException"><paramref name="commands" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>The texel is written, not only the layout, and both are needed.</b> A descriptor
    ///     written against a sampled image promises the image is in <c>ShaderRead</c> when the draw
    ///     executes, and the validation layers check that promise whether or not any instruction
    ///     reads it — so a texture created and never transitioned is a validation error every frame
    ///     about a resource the shader ignores. <c>WorldRenderer.UploadMissingMap</c> is the same
    ///     shape for the same reason.
    /// </remarks>
    public void Upload(ICommandList commands) {
        ArgumentNullException.ThrowIfNull(commands);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (standInsUploaded) {
            return;
        }

        standInsUploaded = true;

        commands.Barrier(
            new([], [new TextureBarrier(shadowStandIn, ResourceState.Undefined, ResourceState.CopyDestination)])
        );

        commands.CopyBufferToTexture(shadowStandInStaging, 0, new(shadowStandIn), new(1, 1, 1));

        commands.Barrier(
            new([], [new TextureBarrier(shadowStandIn, ResourceState.CopyDestination, ResourceState.ShaderRead)])
        );
    }

    /// <summary>Runs the frame's prologue: the stand-ins, the pools' boundary, the geometry's flush.</summary>
    /// <param name="commands">The frame's list, open and outside a render pass.</param>
    /// <exception cref="ArgumentNullException"><paramref name="commands" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Once a frame, whatever the panes number, and that is a correctness rule rather
    ///         than an economy.</b> <c>WorldRenderer.Draw</c> opens with
    ///         <c>MaterialDescriptors.BeginFrame</c>, which is the per-frame descriptor pool's
    ///         boundary — it recycles every set handed out since the last call. Called again between
    ///         two panes it would hand the second pane sets the first pane's passes are still going to
    ///         bind at execute time, which is a frame that draws with another pane's textures rather
    ///         than a frame that fails.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And it is what makes the frame's own inputs arrive at all.</b>
    ///         <c>GeometryResidency.Flush</c> is what copies the vertices and indices themselves, so a
    ///         frame without it draws the right counts at the right offsets out of memory nothing
    ///         wrote; <see cref="Upload" /> is the shadow stand-in's one texel.
    ///     </para>
    /// </remarks>
    public void Begin(ICommandList commands) {
        ArgumentNullException.ThrowIfNull(commands);
        ObjectDisposedException.ThrowIf(disposed, this);

        Upload(commands);
        Renderer.Draw(commands);
    }

    /// <summary>Tells the frame how large one pane's targets are.</summary>
    /// <param name="pane">Which pane.</param>
    /// <param name="size">Its extent in render pixels.</param>
    /// <remarks>
    ///     ⚠ <b>Every frame rather than on a change, because a rebuild puts the document's own
    ///     declarations back.</b> <c>CompositorBuilder.Build</c> refills <c>Compositor.Resources</c>
    ///     from the asset, whose sizes are zero — meaning "a fraction of the frame" — so a reload
    ///     between two sizings would leave a pane's linear target at the reference size while its
    ///     colour and depth are the pane's. Two attachments of different extents in one pass is a
    ///     framebuffer the driver refuses.
    /// </remarks>
    public void Size(int pane, Int2 size) {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (size.X <= 0 || size.Y <= 0) {
            return;
        }

        var hdr = HdrTarget(pane);
        var depth = FramePresenter.Depth(pane);

        for (var index = 0; index < Compositor.Resources.Count; index++) {
            var declared = Compositor.Resources[index];

            if (declared.Name == hdr || declared.Name == depth) {
                Compositor.Resources[index] = declared with { Width = size.X, Height = size.Y };
            }
        }
    }

    /// <summary>Builds every composed pane's frame into one graph, in one collect.</summary>
    /// <param name="graph">The window's graph.</param>
    /// <param name="panes">Each pane's tree, already carrying that pane's tool pass.</param>
    /// <param name="reference">
    ///     The frame's reference size, which a resource an authored document declares as a fraction of
    ///     the frame is sized from. The largest composed pane, because a fraction of the largest is
    ///     the only choice that is never smaller than what a pane attaches.
    /// </param>
    /// <param name="idle">Waits for the device, for the nodes a reference-size change makes re-lay.</param>
    /// <returns>The built frame, which is what a pane takes its texture out of.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>One build for every pane, and the reason is <c>RenderView.Index</c>.</b> See
    ///         <see cref="ViewOf" />: a build per pane would give each pane's view index 0 in turn,
    ///         and every pass records at execute time against whichever view held that index last.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>One pane is still one tree, not a sequence of one.</b> A wrapper would change what
    ///         <c>Compositor.Game</c> is for the arrangement the editor is in most of the time, which
    ///         is a difference in the frame that nothing about the frame asked for.
    ///     </para>
    /// </remarks>
    public CompositorFrame Compose(
        RenderGraph graph,
        IReadOnlyList<SceneRenderer> panes,
        Int2 reference,
        Action idle
    ) {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(panes);
        ArgumentNullException.ThrowIfNull(idle);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (panes.Count == 0) {
            throw new ArgumentException("A frame with no panes in it is a build nobody would read.", nameof(panes));
        }

        if (panes.Count == 1) {
            Compositor.Game = panes[0];
        } else {
            composed.Children.Clear();

            foreach (var pane in panes) {
                composed.Children.Add(pane);
            }

            Compositor.Game = composed;
        }

        // ⚠ Between frames by contract and this is the closest thing the editor has to one: it is
        // called before the graph executes and after the last submission, and it no-ops on an
        // unchanged size — which is every frame that is not a resize.
        // ⚠ After the panes are installed and not before, because `Resize` walks `Game` to find the
        // nodes that had laid device state out against the old size. Run first, it would walk *last*
        // frame's panes — and the frame a pane joins the composition on is exactly the frame whose
        // reference size changes, so the one node with state to lay again is the one it would miss.
        if (reference.X > 0 && reference.Y > 0) {
            Compositor.Resize(reference, idle);
        }

        var frame = Compositor.Build(graph, Renderer.Host.Effects, device);

        degradations.Clear();
        Compositor.Degradations(degradations);

        return frame;
    }

    /// <summary>What the last <see cref="Compose" /> drew differently than it was asked to.</summary>
    /// <remarks>
    ///     ⚠ <b>The frame's rather than a pane's, because one build covers every pane.</b> A node that
    ///     declined names itself, and the pane it belongs to is in that name — which is why the
    ///     document's node names carry the pane number.
    /// </remarks>
    public IReadOnlyList<(string Node, string Reason)> Degradations => degradations;

    /// <summary>Whether the frame's set 0 found everything it needed on its last bind.</summary>
    /// <remarks>
    ///     ⚠ <b>The one fact a black pane is otherwise missing</b>, and the reason it is surfaced
    ///     rather than only asserted: a set written short of one binding is never bound at all, so
    ///     the pass draws with whatever set 0 held before — which on most drivers is a refused draw
    ///     and on some is a fault. <see cref="MissingBinding" /> says which name had nobody to fill
    ///     it.
    /// </remarks>
    public bool IsComplete => Renderer.SceneBlock.IsComplete;

    /// <summary>Which of set 0's bindings nothing filled, or null when the set is whole.</summary>
    public string? MissingBinding => Renderer.SceneBlock.MissingBinding;

    /// <summary>One side of the baked sky, before prefiltering.</summary>
    /// <remarks>
    ///     Small deliberately, and for <c>ShowcaseFrame</c>'s reason: the convolution is on the CPU at
    ///     eight importance samples per texel per face per level, and this runs on the frame the
    ///     editor acquires a device. A gradient has no detail a larger cube would preserve.
    /// </remarks>
    const int SkySize = 16;

    /// <summary>How many roughness levels the chain holds.</summary>
    const int SkyLevels = 4;

    /// <summary>The diffuse half of <see cref="sky" />, projected off the same gradient.</summary>
    /// <remarks>
    ///     Off the source rather than off level zero of the chain, which is already convolved with
    ///     the narrowest lobe — projecting that would give a surface whose ambient and whose
    ///     reflection disagree, which reads as the wrong roughness rather than as two bakes that do
    ///     not match.
    /// </remarks>
    static ShCoefficients SkyIrradiance { get; set; }

    /// <summary>Bakes the studio sky the editor lights an unlit scene by.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Photometric, in cd/m², because everything downstream is.</b> The shading pass
    ///         works in real units and a sky authored as a 0–1 tint is a scene a dozen stops under
    ///         anything the exposure machinery was built for — a pass lit by one is pixel-identical
    ///         to a pass that never ran.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Overcast rather than a clear sky, and that is an editor's decision rather than a
    ///         game's.</b> A scene being built has no sun until somebody places one, and a viewport
    ///         whose default light is a hard key throws a black side on every face of every block a
    ///         designer is placing. A bright even dome is the light a model is judged under.
    ///     </para>
    /// </remarks>
    static EnvironmentTexture BakeSky(IGraphicsDevice device) {
        var source = new CubeImage(SkySize);

        for (var face = 0; face < 6; face++) {
            var image = (CubeFace)face;

            for (var y = 0; y < source.Size; y++) {
                for (var x = 0; x < source.Size; x++) {
                    var height = Math.Clamp((source.DirectionOf(image, x, y).Y * 0.5f) + 0.5f, 0f, 1f);

                    // Warm ground and cool zenith, with unequal channels, so a swizzle anywhere in
                    // the upload is a colour change rather than a shade change. An overcast noon
                    // zenith is a few thousand cd/m² and the ground bounce is a fraction of it.
                    source.At(image, x, y) = Vector3.Lerp(
                        new(900f, 860f, 780f),
                        new(2_600f, 2_900f, 3_400f),
                        height
                    );
                }
            }
        }

        SkyIrradiance = SphericalHarmonics.Project(source);

        return EnvironmentTexture.Bake(device, source, SkyLevels, samples: 8);
    }

    /// <summary>The one opaque texel the shading pass's <c>shadowMap</c> binding points at.</summary>
    static (TextureHandle Texture, TextureViewHandle View, BufferHandle Staging) CreateShadowStandIn(
        IGraphicsDevice device
    ) {
        var texture = device.CreateTexture(
            new(
                PixelFormat.Rgba8UNorm,
                1,
                1,
                TextureUsage.Sampled | TextureUsage.CopyDestination,
                Name: "Editor shadow stand-in"
            )
        );

        var staging = device.CreateBuffer(
            new(4, BufferUsage.CopySource, MemoryAccess.HostUpload, "Editor shadow staging")
        );

        device.Write(staging, 0, [byte.MaxValue, byte.MaxValue, byte.MaxValue, byte.MaxValue]);

        return (texture, device.CreateTextureView(texture), staging);
    }

    /// <summary>One grey metal-roughness surface, for everything this cannot paint properly.</summary>
    /// <remarks>
    ///     ⚠ <b>Not a placeholder for a missing material — it is the material every mesh in the editor
    ///     is drawn in</b>, because there is no editor-side <see cref="IMaterialSource" />. A game
    ///     compiles this too and draws it approximately never; here it is the whole picture, which is
    ///     what <see cref="Degraded" /> says out loud.
    /// </remarks>
    static Material? CompileFallback() {
        var compilation = MaterialCompiler.Compile(
            new() {
                ShaderName = "ForwardPlus",
                Features = [
                    new MetalRoughnessFeature {
                        BaseColor = new Vector3(0.62f, 0.63f, 0.66f),
                        Metalness = 0f,
                        Roughness = 0.7f
                    }
                ]
            }
        );

        return compilation.Failed ? null : compilation.Material;
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        // ⚠ The claims before the renderer, because a claim is a slice of the geometry buffer the
        // renderer owns and releasing one afterwards would be a release against a disposed pool.
        Meshes.Clear();
        Renderer.Dispose();

        // ⚠ And the sky after it. `WorldRenderer.Environment` is a reference the renderer uploads
        // from and does not own — see that property's own remarks — so destroying the cube while the
        // renderer still holds it would be a use-after-free on the frame that is going down.
        sky.Dispose();

        device.Destroy(shadowStandInView);
        device.Destroy(shadowStandIn);
        device.Destroy(shadowStandInStaging);
        device.Destroy(clusterStandIn);
    }
}
