// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.SceneView;
using Vixen.Engine.Renderer;
using Vixen.Graphics;
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
///         ⚠ <b><c>Mount</c> is not called, and the mesh source is set by hand instead.</b> Mounting
///         wants an <c>AssetManager</c>, which resolves addresses through a catalog a *content build*
///         wrote — and <c>ProjectMeshSource</c>'s own remarks give the argument against making the
///         viewport wait for one: "waiting for a build to look at a level would make the viewport a
///         function of the build rather than of the files". So geometry comes from the same import
///         cache the tool renderer already reads, and the two cannot disagree about what a mesh is.
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
    /// <summary>What the frame document calls the view a pane looks through.</summary>
    /// <remarks>
    ///     <c>GraphicsCompositorAsset.Default</c>'s single stage names <c>Camera</c>, and a
    ///     <c>view:</c> is bound by name as the builder creates the node — so a view registered after
    ///     the build is one nothing refers to, which is a frame that collects no views and culls
    ///     everything.
    /// </remarks>
    public const string CameraView = "Camera";

    readonly LightExtractionSystem lights;
    readonly IGraphicsDevice device;

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
        // The editor runs no system graph at all — `TransformSystem` is resolved by hand for the same
        // reason, and that decision is `EditorApplication.ResolveTransforms`' own remarks — so what a
        // loop would give is a scheduler for two calls whose order is already decided by this file.
        Meshes = new(Renderer.Host.System, Renderer.Meshes, Renderer.Transforms, Renderer.Materials, Renderer.Residency) {
            Meshes = meshes,

            // ⚠ And *not* `Materials`, which stays null. See the type's remarks: with no source, a
            // drawable that names a material still draws — in this one — because a host that cannot
            // resolve one should show geometry rather than nothing.
            Material = Fallback
        };

        lights = new(Renderer.Lighting);

        // ⚠ The view before the build, the build before the mask, and the mask before anything
        // extracts. The first is the builder's rule — a `view:` is bound by name as each node is
        // created — and the second is `Stages`' own: a stage's index does not exist until a document
        // declares it, and a mask assigned after the first extraction reaches nothing that is
        // already in the frame. Both are why the document is built in a constructor rather than by
        // whichever pane happens to open first.
        Renderer.Host.Builder.Views[CameraView] = View;

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
        Compositor = Renderer.Host.Builder.Build(Document());

        // ⚠ Everything below is what a rebuild has to do again, which is why it is one call rather
        // than a run of statements in a constructor. See `Reload`.
        Adopt();

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

    /// <summary>What the shaded subtree is called, and what <see cref="Trees" /> looks it up by.</summary>
    const string ShadedTree = "Shaded";

    /// <summary>And the wireframe one.</summary>
    const string WireframeTree = "Wireframe view";

    /// <summary>The linear target the scene is shaded into, before the curve.</summary>
    /// <remarks>
    ///     ⚠ <b>Transient, and it is the reason the pane's own colour is the frame's <em>last</em>
    ///     target rather than its first.</b> A shading pass writes luminance in cd/m² — an overcast
    ///     sky is thousands of them — so an 8-bit target here is every surface clipped to white,
    ///     which reads as "the lighting broke" rather than as "there is no exposure in this frame".
    /// </remarks>
    const string HdrTarget = "SceneHdr";

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
    static GraphicsCompositorAsset Document() =>
        new() {
            Version = CompositorBuilder.SupportedVersion,

            // ⚠ Both stages declared by the one document, so both indices exist before anything
            // extracts and `Stages` can be their union. A stage added later is a bit no object in the
            // store carries — see that property's remarks.
            Stages = [new() { Name = OpaqueStage }, new() { Name = WireframeStage, Cull = CullMode.None }],
            Resources = [
                new() { Name = HdrTarget, Format = PixelFormat.Rgba16Float },
                new() {
                    Name = FramePresenter.DepthTarget,
                    Format = FramePresenter.DepthFormat,
                    Usage = TextureUsage.DepthStencilTarget
                }
            ],

            // ⚠ One build, two subtrees, and only one of them is ever `Compositor.Game`. Building a
            // second document on the same builder would work — `AddStage` reuses by name for exactly
            // that case — but `Build` clears `Nodes`, `Uploads` and `Readbacks` first, so the tree
            // built before it would be one whose sky node nothing can reach and whose uploads nothing
            // copies. A node that is in no tree costs nothing: `Build`, `Collect` and `Degradations`
            // all walk from `Game` down.
            Game = new SequenceAsset {
                Name = "Editor frame",
                Children = [
                    new SequenceAsset {
                        Name = ShadedTree,
                        Children = [
                            new SkyAsset { Name = "Background", Output = HdrTarget, View = CameraView },
                            new RenderPassAsset {
                                Name = "Main",
                                ColourTargets = [HdrTarget],
                                Load = LoadAction.Load,
                                DepthTarget = FramePresenter.DepthTarget,
                                ClearDepth = 0f,
                                Children = [
                                    new SingleStageAsset {
                                        Name = "Opaque draw",
                                        View = CameraView,
                                        Stage = OpaqueStage
                                    }
                                ]
                            },
                            new TonemapAsset {
                                Name = "Grade",
                                Source = HdrTarget,
                                Output = FramePresenter.ColourTarget,
                                Format = FramePresenter.ColourFormat,
                                Ev100 = Ev100,

                                // ⚠ Encoded here rather than by the target's format, because the
                                // interface samples this rather than presenting it — see
                                // `EditorHost.Presenter` for the same decision on the tool pane. A
                                // UNorm-sRGB target would be decoded on the way into the interface's
                                // shader and encoded again on the way out.
                                EncodeSrgb = true
                            }
                        ]
                    },

                    // ⚠ No sky and no grade, and both absences are the mode rather than an economy.
                    // A wireframe view is asked "where is the geometry", so a background that is a
                    // gradient is edges lost against it — and a curve that maps an overcast sky onto
                    // 0.89 would grade a line drawn at a few thousand cd/m² to the same grey as the
                    // sky it was meant to stand out from. Clipped white on near-black is the picture.
                    new SequenceAsset {
                        Name = WireframeTree,
                        Children = [
                            new RenderPassAsset {
                                Name = "Wires",
                                ColourTargets = [FramePresenter.ColourTarget],
                                ClearColour = new(0.04f, 0.045f, 0.06f),
                                DepthTarget = FramePresenter.DepthTarget,
                                ClearDepth = 0f,
                                Children = [
                                    new SingleStageAsset {
                                        Name = "Wireframe draw",
                                        View = CameraView,
                                        Stage = WireframeStage
                                    }
                                ]
                            }
                        ]
                    }
                ]
            }
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

    /// <summary>The compositor tree for each view mode this host can honestly draw.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>What <see cref="ViewModes.Register" /> is fed, and the whole of "a mode is a
    ///         compositor".</b> Each entry is a subtree of the one built document, and switching mode
    ///         is <see cref="Compositor" />'s <c>Game</c> being pointed at a different one — no
    ///         rebuild, no second render system, and no branch inside a renderer.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A mode is absent rather than present-and-wrong.</b> Wireframe is here only when
    ///         the device reports <c>HasWireframe</c>; <c>FillMode.Wireframe</c> needs
    ///         <c>fillModeNonSolid</c>, which is optional in Vulkan, and a pipeline built without it
    ///         is silently filled solid — a wireframe view drawing the shaded picture. Absent, the
    ///         pane keeps the tool renderer's wireframe, which is drawn as segments and therefore
    ///         works everywhere. <see cref="ViewModes.Registered" /> is what a host reads to choose.
    ///     </para>
    /// </remarks>
    public IReadOnlyDictionary<ViewMode, SceneRenderer> Trees => trees;

    readonly Dictionary<ViewMode, SceneRenderer> trees = [];

    /// <summary>The frame, its features, its descriptor pools and its compositor builder.</summary>
    public WorldRenderer Renderer { get; }

    /// <summary>What turns the world's drawables into the frame's objects.</summary>
    public MeshExtractionSystem Meshes { get; }

    /// <summary>What the viewport looks through.</summary>
    /// <remarks>
    ///     Held here rather than made per frame because <see cref="RenderView.PreviousViewProjection" />
    ///     is the one piece of a view that has to outlive a frame — a motion vector is measured against
    ///     it, and a view rebuilt every frame reports no history for ever.
    /// </remarks>
    public RenderView View { get; } = new("Editor");

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
    void Adopt() {
        var builder = Renderer.Host.Builder;

        Opaque = builder.Stages[OpaqueStage];

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

        trees.Clear();

        if (builder.Nodes.TryGetValue(ShadedTree, out var shaded)) {
            trees[ViewMode.Shaded] = shaded;
        }

        // ⚠ Registered only where the device can draw it — see `Trees`. A tree that filled solid
        // would be a menu line that changes the picture into a worse copy of the one above it.
        if (device.Features.HasWireframe
            && builder.Stages.TryGetValue(WireframeStage, out var wires)
            && builder.Nodes.TryGetValue(WireframeTree, out var wireTree)) {
            // The one legitimate call: a stage dedicated to the mode, configured before it has drawn.
            new ViewModes { Current = ViewMode.Wireframe }.ApplyTo(wires);

            trees[ViewMode.Wireframe] = wireTree;
        }
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
    public void Aim(EditorCamera camera, float aspectRatio) {
        ArgumentNullException.ThrowIfNull(camera);
        ObjectDisposedException.ThrowIf(disposed, this);

        View.Advance();

        // A pane one pixel wide during a splitter drag, or measured before the layout pass has run.
        // A zero or negative aspect makes a projection full of infinities and a frustum of NaN planes,
        // which culls the entire scene rather than failing.
        var aspect = aspectRatio > 0f && float.IsFinite(aspectRatio) ? aspectRatio : 1f;

        if (camera.IsOrthographic) {
            View.Camera = null;
            View.Position = camera.Position;
            View.ViewProjection = camera.ViewProjection(aspect);

            return;
        }

        View.Camera = new RenderCamera(
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

        Compositor = Renderer.Host.Builder.Build(asset ?? Document());

        Adopt();
        Resettle(world);

        // The mask is right and the objects are gone, so this is what puts them back under it.
        Extract(world);
    }

    /// <summary>Drops every extracted object, so the next <see cref="Extract" /> makes them again.</summary>
    /// <remarks>
    ///     ⚠ <b>The <c>RenderHandle</c> is what has to go, not the object.</b> An entity is "settled"
    ///     precisely by carrying one — <c>MeshExtractionSystem</c>'s appeared query is
    ///     <c>.WithNone&lt;RenderHandle&gt;()</c> — so removing the object alone would leave the entity
    ///     matching nothing, holding a handle to a slot that has been reused.
    /// </remarks>
    void Resettle(World world) {
        var settled = new QueryDescription().WithAll<RenderHandle>();
        var pending = new List<Vixen.Core.Entity>();

        // ⚠ Collected before anything is removed. Removing a component moves the entity to another
        // archetype, which is a structural change under the chunk being walked.
        foreach (var chunk in world.Chunks(settled)) {
            foreach (var entity in chunk.Entities[..chunk.Count]) {
                pending.Add(entity);
            }
        }

        foreach (var entity in pending) {
            var handle = world.Read<RenderHandle>(entity);

            Renderer.Host.System.Objects.Remove(handle.Object);

            // Releases the residency claim under the entity's name, which is the same claim the
            // handle records — one release, not two.
            Meshes.Forget(entity);

            world.Remove<RenderHandle>(entity);
        }
    }

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
