// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

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

        // ⚠ `Builder.Build` rather than `Host.Load`, and the difference is which graph draws.
        // `Host.Load` would put this compositor in `Host.Compositor`, and `WorldRenderer.Draw` would
        // then build it into the host's *own* graph and execute it there — a second graph, whose
        // resources the editor's per-window graph cannot order its interface pass against. Left null,
        // `Host.Draw` returns immediately and `WorldRenderer.Draw` is exactly the per-frame prologue
        // a pane needs: the descriptor pools' frame boundary, the geometry residency's flush, the
        // sky's upload and the set-1 layout. The pane builds this into the window's graph itself.
        Compositor = Renderer.Host.Builder.Build(GraphicsCompositorAsset.Default);
        Opaque = Renderer.Host.Builder.Stages[OpaqueStage];
        Stages = Opaque.Mask;

        sky = BakeSky(device);
        Renderer.Environment = sky;

        var ambient = new EnvironmentLight { MipCount = sky.MipCount, Intensity = 1f, Irradiance = SkyIrradiance };

        sky.Apply(ambient);
        Renderer.SceneEnvironment.Environment = ambient;

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

    /// <summary>What the default frame's one stage is called.</summary>
    const string OpaqueStage = "Opaque";

    /// <summary>The frame the pane draws, built from the document a project with none falls back to.</summary>
    /// <remarks>
    ///     ⚠ <b>Built into the caller's graph, not executed here.</b>
    ///     <c>GraphicsCompositor.Build</c> takes a <c>RenderGraph</c>, resets nothing and runs
    ///     nothing — so the editor's per-window graph is what a pane hands it, and the interface's
    ///     pass can then declare that it reads the frame's colour like any other resource.
    /// </remarks>
    public GraphicsCompositor Compositor { get; }

    /// <summary>The stage every extracted object is drawn in.</summary>
    public RenderStage Opaque { get; }

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
