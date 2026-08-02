// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Assets;
using Vixen.Engine.Frames;
using Vixen.Graphics;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Ecs;
using Vixen.Rendering.Features;
using Vixen.Rendering.Lighting;
using Vixen.Rendering.Materials;
using Vixen.Rendering.Vfx;
using Vixen.Shaders;

namespace Vixen.Engine.Renderer;

/// <summary>
///     Everything between a world and a drawn frame, assembled.
/// </summary>
/// <remarks>
///     <para>
///         <b>The join an application was missing.</b> The scene half was finished — components,
///         extraction systems, a residency cache — and so was the frame half, and nothing put them
///         together: a game had to construct four features, a geometry buffer, a residency, two
///         extraction systems and a host, in an order, and get every reference between them right. The
///         samples opened a device and issued draws instead, which is why none of them is a game.
///     </para>
///     <para>
///         <b>What it is not is a policy.</b> It creates the standard features and the standard
///         extraction, and it decides nothing about the frame: the compositor comes from a document,
///         the stages come with it, and the content comes from wherever the application mounted it. A
///         project that wants a different set adds to <see cref="Host" /> and skips
///         <see cref="Register" />.
///     </para>
///     <para>
///         <b>The order it enforces is the one worth enforcing.</b> Extraction runs in
///         <c>SystemPhase.PreRender</c> after the transforms are written, so an object is culled against
///         where it is this frame rather than where it was last one; the compositor then collects the
///         views and runs the render system's own phases. Both of those are decisions somebody has
///         already made correctly, and this is where a second host stops being able to make them again
///         differently.
///     </para>
/// </remarks>
public sealed class WorldRenderer : IDisposable {
    /// <summary>The source <see cref="Mount" /> built, for the frame work only it can do.</summary>
    /// <remarks>
    ///     Separate from <see cref="Painter" /> because that one is the interface an extraction asks
    ///     through and this one is the implementation that owns a device: a project supplying its own
    ///     <see cref="IMaterialSource" /> sets the first and leaves this null, and <see cref="Draw" />
    ///     then has nothing to flush, which is right.
    /// </remarks>
    AssetMaterialSource? painting;

    /// <summary>The source <see cref="Mount" /> built for the virtualized path, if there is one.</summary>
    AssetVirtualGeometrySource? clustering;

    bool disposed;

    /// <summary>Builds the standard renderer for a world.</summary>
    /// <param name="device">The device everything lives on.</param>
    /// <param name="effects">Where variants are compiled.</param>
    /// <param name="vertexCapacity">How many vertices the shared geometry buffer holds.</param>
    /// <param name="indexCapacity">How many indices it holds.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    ///     The two capacities are the scene's geometry budget and are the caller's because they are a
    ///     project's decision — <see cref="MeshExtractionSystem.Dropped" /> is what says a level asked
    ///     for more than was reserved, which otherwise looks like meshes that stop appearing past a
    ///     certain number.
    /// </remarks>
    public WorldRenderer(
        IGraphicsDevice device,
        EffectSystem effects,
        int vertexCapacity = 1 << 20,
        int indexCapacity = 1 << 21
    ) {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(effects);

        Device = device;
        Host = new(device, effects);

        // SurfaceVertex's own size rather than a number: it is what SurfaceGeometry.Packed produces and
        // what GeometryResidency puts in the buffer, so a stride written down here would be a second
        // opinion about a layout that already has one.
        Geometry = new(device, SurfaceVertex.SizeInBytes, vertexCapacity, indexCapacity, name: "Scene");
        Residency = new(Geometry);

        // ⚠ Device and Descriptors together, and without them nothing draws either.
        //
        // A material's own descriptor set is set 3 of the ForwardPlus layout — its uniform block, its
        // textures, its samplers. MaterialRenderFeature writes one when it has both of these and
        // falls back to Material.Descriptors when it does not; a material compiled by
        // MaterialCompiler has no set of its own, so the fallback is invalid, so nothing is bound and
        // every draw is refused for a layout incompatibility.
        //
        // Same shape as the vertex layout above: every device test passes these, and the arrangement
        // a game gets was the one that never had them.
        MaterialDescriptors = new(device, "Materials");

        // ⚠ Sets 0 and 1, and without them every draw is refused for the same reason set 3 was.
        //
        // A shading pass declares four sets and a host has to fill three of them; nothing filled any.
        // SceneConstants is the frame's — the lighting environment, the probes — and takes its layout
        // off the effect at bind time, so it needs only an allocator. ViewConstants is the camera's,
        // and writes the view-projection and the eye position itself from the RenderView, so a host
        // that supplies one cannot draw a frame with last frame's camera.
        //
        // Both go on the builder because the *document* decides which nodes bind them:
        // SingleStageRenderer hands the view block to the stage it draws, and RenderPassRenderer hands
        // the frame block to the pass.
        SceneBlock = new(device, "Scene") { Descriptors = MaterialDescriptors };
        ViewBlock = new(device, "View") { Descriptors = MaterialDescriptors };
        Materials = new() { Effects = effects, Device = device, Descriptors = MaterialDescriptors };
        Transforms = new() { Device = device };

        // ⚠ Its own contributor rather than two more fields on Transforms, because that feature stops
        // pushing entirely once the object records are on — see MotionVectorRenderFeature's remarks.
        // It contributes to exactly one pass, and decides which by asking the shader whether it has a
        // push range reaching byte 64; nothing here has to name the velocity pass.
        Motion = new() { Transforms = Transforms };
        // Where the lighting block's set layout comes from once a shader has resolved — see
        // ForwardLightingRenderFeature.Materials. Unset, the first frame binds no set 3 and the
        // driver refuses every draw in it, which on Metal is a fault rather than a dark frame.
        Lighting = new() { Device = device, Materials = Materials };

        // ⚠ What turns the frame's objects into the frame's set, and nothing built one.
        //
        // SceneConstants resolves set 0 through EffectSetWriter, which fills a binding only from a
        // name somebody set — so with no SceneLighting the sun is unwritten, the environment is
        // unwritten and the probe array is empty. ForwardPlus declares environment, probes and their
        // samplers whatever the permutations say, and a set writes every binding or none, so "no
        // environment" is not an unlit frame: it is a set 0 that never binds and a driver that
        // refuses every draw in the pass.
        //
        // One probe selector, shared with the lighting feature on purpose. That feature writes an
        // *index* into each object's block and this fills the array the index refers to; two equal
        // lists would agree until one of them was reordered.
        SceneEnvironment = new() { Sun = Lighting, Probes = new() };
        Lighting.Probes = SceneEnvironment.Probes;
        SceneBlock.Lighting = SceneEnvironment;

        // ⚠ And the two buffers, which are also bindings of set 0 and were also published nowhere.
        //
        // Both features already knew how to write their names and both had nobody to write them to.
        // The transform buffer is what a vertex shader reads a world matrix out of when the object
        // records are on, and the light buffer is what the clustered path indexes — so neither is
        // optional in the sense of "the picture is worse without it": the set is short a binding, and
        // a set short a binding is not bound at all.
        //
        // From Prepare rather than once, which is why they take a collection instead of a handle:
        // both buffers are recreated when the scene outgrows them, and a handle read once would name
        // a buffer the device has retired.
        Transforms.Scene = SceneBlock.Parameters;
        Lighting.Scene = SceneBlock.Parameters;

        var describer = new EffectPipelineDescriber(device);

        // ⚠ Layout zero, and without it nothing this renderer draws ever appears.
        //
        // EffectPipelineDescriber.VertexLayouts is a table indexed by MeshDraw.VertexLayout, and
        // GeometryBuffer.Apply leaves that index at zero — so every mesh in every scene names entry
        // zero of a table that started empty. Describe then passes a null vertex input state, the
        // pipeline is created declaring no attributes, and the driver refuses it because ForwardPlus'
        // vertex stage reads locations 6 to 9.
        //
        // What made it survive so long is where the failure lands: a validation error and a
        // VK_ERROR_INITIALIZATION_FAILED from inside the first draw, in a host that had already
        // reported the scene loaded and the camera placed. Every device test builds this table by
        // hand — see the golden tests — so the one arrangement that never had it was the one a game
        // uses.
        //
        // ⚠ The schema, not a layout, and the difference is whether a second pass can draw.
        //
        // A layout has the locations already in it, which makes it an answer for one shader.
        // SurfaceVertex.Locations is ForwardPlus's — 6 to 9, because that stage declares six
        // streams — and ShadowCaster declares one, so its position is location 1 and its attributes
        // are three rather than four. Describing a mesh once, by name, is what lets the same
        // geometry go through a forward pass and a caster pass without the renderer knowing there
        // are two.
        describer.VertexSchemas.Add(SurfaceVertex.Schema);

        // ⚠ Entry one, and the index is load-bearing: `ParticleRenderFeature.VertexLayout` defaults to
        // it, so a table holding only the surface schema describes a particle pipeline against a
        // layout that does not exist and the driver refuses every quad. Registered here, beside the
        // schema it sits next to, rather than by whoever happens to create the feature — the table is
        // this renderer's and an index into it means nothing away from it.
        describer.VertexSchemas.Add(ParticleVertices.Schema);

        Meshes = new() {
            Pipelines = new(device),
            Describer = describer
        };

        Host.Builder.SceneConstants = SceneBlock;
        Host.Builder.ViewConstants = ViewBlock;

        // ⚠ The four a document cannot carry, and nothing was passing any of them.
        //
        // Every full-screen node in the engine — the tonemap, the bloom chain, the AO march, the
        // indirect-diffuse resolve — is a FullScreenRenderer, and FullScreenRenderer.Build returns
        // before it asks for a variant when it has no device or no describer. Silently: the return is
        // above the Resolve, so the effect system counts no miss, and a frame whose whole post chain
        // did nothing reported one variant, zero misses and no warning of any kind.
        //
        // What that costs is the picture. The tonemap is the node that writes the swapchain, so a
        // document ending in one produced a frame in which nothing was ever written to the imported
        // output — a black window behind a log saying the scene had loaded and drawn.
        //
        // The same describer and the same allocator the mesh path uses, deliberately: one pipeline
        // cache across the frame, and one per-frame descriptor pool, rather than a second of each
        // that would double the pipelines and never be reset in step.
        Samplers = new(device);

        Host.Builder.Device = device;
        Host.Builder.Modules = describer;
        Host.Builder.Descriptors = MaterialDescriptors;
        Host.Builder.Samplers = Samplers;

        // The same feature the shading pass reads its sun from, so a shadow node fits its cascades
        // along the light the frame is actually lit by rather than along a constant.
        Host.Builder.Sun = Lighting;

        // And the same feature's light list, for the node that packs the punctual shadow atlas —
        // shared rather than copied, because that node writes each light's tile index back into the
        // entry it came from and this feature reads it a phase later. Two lists is two sets of
        // indices, one of which addresses an atlas packed from the other.
        Host.Builder.Lights = Lighting;

        Meshes.Add(Materials);
        Meshes.Add(Transforms);
        Meshes.Add(Motion);
        Meshes.Add(Lighting);

        Host.System.AddFeature(Meshes);

        // ⚠ A material feature of its own, and not by preference: a sub-feature belongs to exactly one
        // root feature — `RootRenderFeature.Add` refuses a second owner outright — and
        // `ParticleRenderFeature.Draw` asks *its* material sub-feature which variant each object
        // resolves to, skipping any object with no answer. So a particle feature with no material
        // feature of its own is a renderer that expands geometry every frame and draws none of it.
        //
        // What that costs is that a particle material has to be assigned through `ParticleMaterials`
        // rather than through `Materials`. The two hold separate variant caches and separate uniform
        // blocks, which is the honest consequence of one-owner sub-features and is small: a project
        // has a handful of particle materials against a scene's worth of surface ones.
        ParticleMaterials = new() { Effects = effects, Device = device, Descriptors = MaterialDescriptors };

        Particles = new() {
            Device = device,
            Pipelines = new(device),
            Describer = describer
        };

        Particles.Add(ParticleMaterials);
        Host.System.AddFeature(Particles);

        if (device.Features.HasBindless) {
            Table = new(device);
            Materials.Textures = Table;

            Paired(Materials, "ForwardPlus");
        }
    }

    /// <summary>The device everything here lives on.</summary>
    public IGraphicsDevice Device { get; }

    /// <summary>The frame: a compositor, a graph and the render system.</summary>
    public SceneRenderHost Host { get; }

    /// <summary>Where a material's own descriptor set is allocated from, frame by frame.</summary>
    public DescriptorAllocator MaterialDescriptors { get; }

    /// <summary>How many bytes of geometry have been copied to the device.</summary>
    /// <remarks>
    ///     Zero after a frame that drew is the failure this counts: the meshes are staged, the slices
    ///     exist and the draws are recorded, and the buffer the GPU reads was never written.
    /// </remarks>
    public int UploadedBytes { get; private set; }

    /// <summary>The samplers the frame's nodes share.</summary>
    /// <remarks>
    ///     Shared rather than per node, for the reason a cache exists at all: a sampler is pure state,
    ///     a device caps how many there may be, and a post chain of nine passes each creating its own
    ///     linear-clamp is nine of a budget that is not large.
    /// </remarks>
    public SamplerCache Samplers { get; }

    /// <summary>The frame's set 0: the lighting environment every shading pass reads.</summary>
    public SceneConstants SceneBlock { get; }

    /// <summary>
    ///     What the scene contributes to it: the sky, the probes, the sun.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Public because two of the three are a project's to supply.
    ///         <see cref="SceneLighting.Sun" /> is already the lighting feature's, so a level with a
    ///         directional light needs nothing; <see cref="SceneLighting.Environment" /> and the
    ///         probes are content, and content is loaded rather than constructed.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A frame with no environment does not draw.</b> Set 0's <c>environment</c> and
    ///         <c>probes</c> bindings exist whatever a material's permutations say, the probe slots
    ///         fall back to the environment's own cube, and a set is written whole or not at all. So
    ///         one baked sky is not a quality setting here — it is four of the set's bindings, and
    ///         without it the pass binds nothing and the driver refuses every draw in it.
    ///         <see cref="EnvironmentTexture" /> is what turns a bake into the handle this wants.
    ///     </para>
    /// </remarks>
    public SceneLighting SceneEnvironment { get; }

    /// <summary>
    ///     The prefiltered sky to copy up before the frame that samples it, or null for a host that
    ///     uploads its own.
    /// </summary>
    /// <remarks>
    ///     Not owned — a host that baked it disposes it — but uploaded from here, because
    ///     <see cref="Draw" /> is the only point in a frame that is both before the passes and holding
    ///     a command list. <see cref="EnvironmentTexture.Upload" /> is a no-op after the first, so
    ///     this costs a null check per frame once the sky is up.
    /// </remarks>
    public EnvironmentTexture? Environment { get; set; }

    /// <summary>The camera's set 1, which fills itself from whichever view is being drawn.</summary>
    public ViewConstants ViewBlock { get; }

    /// <summary>The shared vertex and index memory every scene mesh is suballocated from.</summary>
    public GeometryBuffer Geometry { get; }

    /// <summary>What decides which meshes are in it, and shares one slice between every user.</summary>
    public GeometryResidency Residency { get; }

    /// <summary>The feature that draws them.</summary>
    public MeshRenderFeature Meshes { get; }

    /// <summary>The materials they are drawn with.</summary>
    public MaterialRenderFeature Materials { get; }

    /// <summary>Their world matrices.</summary>
    public TransformRenderFeature Transforms { get; }

    /// <summary>And where those matrices were last frame, for the velocity pass.</summary>
    /// <remarks>
    ///     Contributes to no pass but that one. A frame with no motion stage in it pays one array
    ///     copy per frame and nothing else — which is the price of the history being kept whether or
    ///     not this frame asked for it, and the alternative is a stage that produces garbage on the
    ///     first frame after somebody adds it.
    /// </remarks>
    public MotionVectorRenderFeature Motion { get; }

    /// <summary>The lights that reach them.</summary>
    public ForwardLightingRenderFeature Lighting { get; }

    /// <summary>
    ///     The feature that draws <see cref="Vfx.VfxSystem" />s, for a frame with a stage to put them in.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Always constructed, and free in a frame with no effects in it.</b> Its
    ///         <c>Prepare</c> walks the frame's objects and finds none of its own, and its <c>Draw</c>
    ///         is only reached for a stage a document declared and a render object opted into. The
    ///         alternative — a host constructing one when it decides it wants particles — puts the
    ///         vertex schema, the pipeline cache and the material pairing in a game's hands, and every
    ///         one of those is a thing that fails silently when it is missed.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Nothing here attaches effects, and nothing extracts them from the world.</b> There
    ///         is no particle component: a caller adds a render object, hands it a
    ///         <see cref="Vfx.VfxSystem" /> through <c>SetSystem</c>, assigns it a material and steps
    ///         the system itself. That is the state <c>docs/overview.md</c> records for <c>.vxvfx</c>
    ///         — a graph is written in code and there is no runtime loader — and this is the half of
    ///         the path that does exist.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Set <see cref="ParticleRenderFeature.View" /></b>, or the quads face
    ///         whichever view happens to be first. A billboard is expanded once for the whole frame —
    ///         see that feature's remarks — so it must be expanded against the camera, and a frame
    ///         whose first view is a shadow cascade would otherwise turn every particle edge-on.
    ///     </para>
    /// </remarks>
    public ParticleRenderFeature Particles { get; }

    /// <summary>
    ///     The materials <see cref="Particles" /> is drawn with, which are not <see cref="Materials" />.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Assign a particle's material here, not through <see cref="Materials" />.</b> A
    ///     sub-feature has exactly one owner — <c>RootRenderFeature.Add</c> refuses a second — so the
    ///     mesh path and the particle path cannot share one, and a material assigned through the mesh
    ///     feature is a material the particle feature has never heard of: the object resolves to no
    ///     variant, and <c>ParticleRenderFeature.Draw</c> skips it. Which is an effect that expands its
    ///     quads every frame, uploads them, and draws nothing.
    /// </remarks>
    public MaterialRenderFeature ParticleMaterials { get; }

    /// <summary>Where the geometry a mesh reference names comes from.</summary>
    /// <remarks>
    ///     Null until <see cref="Mount" /> or a caller sets one. A world whose entities carry mesh
    ///     references and whose renderer has no source draws none of them and counts them in
    ///     <see cref="MeshExtractionSystem.Waiting" />.
    /// </remarks>
    public IMeshSource? Source { get; set; }

    /// <summary>Where the material a reference names comes from.</summary>
    /// <remarks>
    ///     Null until <see cref="Mount" />. A drawable naming a material this cannot supply is drawn in
    ///     <see cref="MeshExtractionSystem.Material" /> rather than not at all — see that property's
    ///     remarks for why "no source" and "not yet" are different answers.
    /// </remarks>
    public IMaterialSource? Painter { get; set; }

    /// <summary>The frame's texture table, on a device that has one.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Created only where the device can index it.</b> A table is what turns a material's
    ///         texture into a <c>uint</c> in its own uniform block, which is what lets a feature sample
    ///         one at all; without <see cref="GraphicsDeviceFeatures.HasBindless" /> there is nothing to
    ///         index and the pairing below would write slots into a shader that has no table to read
    ///         them from.
    ///     </para>
    ///     <para>
    ///         ⚠ Null is not a degraded frame. It is what runs on GL, on WebGL2 and on MoltenVK below
    ///         argument-buffer tier 2 (ADR-011), where a project uses the untextured workflow and tints
    ///         instead — the same fork the whole engine makes at that line.
    ///     </para>
    /// </remarks>
    public BindlessTable? Table { get; }

    /// <summary>Where a material's textures come from, once content is mounted.</summary>
    public AssetTextureSource? Painted { get; private set; }

    /// <summary>The virtualized stack, if this renderer was given one.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Set by the application, not created here.</b> A <c>VirtualGeometrySystem</c> owns a page
    ///         pool whose size is a project's streaming budget, and it has to be handed to a compositor
    ///         builder before the document is loaded — both of which happen outside this class. What this
    ///         does with it is the one thing only it can: route the scene's clustered meshes to it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Null draws every model through its fallback mesh, and nothing looks wrong.</b> That is
    ///         the failure mode worth naming: a virtualized model's fallback is a correct picture of the
    ///         same object, so a project that meant to use the virtualized path and did not set this sees
    ///         a scene that draws, at a fraction of the detail, with no error anywhere.
    ///         <see cref="MeshExtractionSystem.VirtualizedCount" /> is what says so.
    ///     </para>
    /// </remarks>
    public VirtualGeometrySystem? Clusters { get; set; }

    /// <summary>Where the cluster hierarchy a mesh reference names comes from.</summary>
    public IVirtualGeometrySource? Hierarchies { get; set; }

    /// <summary>The extraction the last <see cref="Register" /> added, or null.</summary>
    public MeshExtractionSystem? Extraction { get; private set; }

    /// <summary>The emitter bridge the last <see cref="Register" /> added, or null.</summary>
    public VfxExtractionSystem? Emitters { get; private set; }

    /// <summary>Where the effect a <c>VfxEmitter</c> names comes from.</summary>
    /// <remarks>
    ///     Null until <see cref="Mount" />. An emitter naming an effect this cannot supply emits
    ///     nothing and is counted in <see cref="VfxExtractionSystem.Waiting" />, on the mesh path's
    ///     terms.
    /// </remarks>
    public IVfxEffectSource? VfxEffects { get; set; }

    /// <summary>What every emitter's particles are drawn with.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The project's decision rather than the effect's, and a working default.</b> A
    ///         <c>.vxvfx</c> says how particles <em>move</em> and nothing about which shader draws them
    ///         — the node library has no material node — so somebody has to choose, and choosing
    ///         <c>ParticleSprite</c> is what makes an effect dropped onto an entity appear rather than
    ///         simulate invisibly.
    ///     </para>
    ///     <para>
    ///         <b>Built by <see cref="ParticleSpriteMaterial.Default" /> rather than here</b>, so
    ///         that "what a particle material starts as" has one answer and a device test can draw
    ///         with the same one a game gets. That type says what a parameter left unset used to
    ///         cost.
    ///     </para>
    ///     <para>
    ///         A project wanting embers in cd/m² sets <c>emissive</c> on this, or replaces it outright
    ///         with a material of its own.
    ///     </para>
    /// </remarks>
    public Material ParticleMaterial { get; set; } = ParticleSpriteMaterial.Default();

    /// <summary>Points the renderer at a content manager, so mesh references resolve.</summary>
    /// <param name="assets">Where the meshes come from.</param>
    /// <exception cref="ArgumentNullException"><paramref name="assets" /> is null.</exception>
    public void Mount(AssetManager assets) {
        ArgumentNullException.ThrowIfNull(assets);
        ObjectDisposedException.ThrowIf(disposed, this);

        Source = new AssetMeshSource(assets);

        // Only where there is a table to put them in: an AssetTextureSource with nothing indexing its
        // views would upload every texture in the level and hand the slots to nobody.
        Painted = Table is null ? null : new AssetTextureSource(Device, assets);
        Painter = painting = new(assets, Painted);

        // Only where there is a stack to register them with. A source that loaded hierarchies and had
        // nowhere to put them would page a level's geometry in and draw none of it.
        Hierarchies = clustering = Clusters is null ? null : new(assets, Clusters);

        // Unconditionally, because there is no device resource behind it and nothing to hold: an
        // effect is a compiled graph, which is data. A project with no emitters asks for none.
        VfxEffects = new AssetVfxEffectSource(assets);

        if (Extraction is { } extraction) {
            extraction.Meshes = Source;
            extraction.Materials = Painter;
            extraction.Virtualized = Clusters?.Feature;
            extraction.Clusters = Hierarchies;
        }

        if (Emitters is { } emitters) {
            emitters.Effects = VfxEffects;
        }
    }

    /// <summary>
    ///     Adds the extraction systems to a loop, so the world's entities reach the render system.
    /// </summary>
    /// <param name="loop">The loop that runs them.</param>
    /// <param name="stages">Which stages the extracted objects are drawn in.</param>
    /// <param name="particleStages">
    ///     Which stage the emitters are drawn in — a transparent one, separate from
    ///     <paramref name="stages" /> and never a shadow one. None leaves them simulating and undrawn.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="loop" /> is null.</exception>
    /// <remarks>
    ///     The stage mask is the caller's because a stage's index is assigned by the render system when
    ///     the compositor's document declares it — so this is called after
    ///     <see cref="SceneRenderHost.Load" />, and a mask of none draws nothing at all.
    /// </remarks>
    public void Register(EngineLoop loop, RenderStageMask stages, RenderStageMask particleStages = default) {
        ArgumentNullException.ThrowIfNull(loop);
        ObjectDisposedException.ThrowIf(disposed, this);

        Extraction = new(Host.System, Meshes, Transforms, Materials, Residency) {
            Stages = stages,
            Meshes = Source,
            Materials = Painter,
            Virtualized = Clusters?.Feature,
            Clusters = Hierarchies
        };

        // ⚠ A mask of its own, and defaulting to none. Particles want a transparent stage that tests
        // depth and does not write it, which is not the stage a mesh is drawn in — and they must not
        // be in a shadow one at all, because a billboard is expanded once for the whole frame and a
        // cascade would draw the camera's quads edge-on to its own light. A project whose document
        // declares no such stage passes nothing and its emitters simulate without appearing, which is
        // the same "a host has not finished wiring" state `MeshExtractionSystem.Stages` describes.
        Emitters = new(Host.System, Particles, ParticleMaterials) {
            Stages = particleStages,
            Effects = VfxEffects,
            Material = ParticleMaterial
        };

        loop.Add(Extraction);
        loop.Add(Emitters);
        loop.Add(new LightExtractionSystem(Lighting));
    }

    /// <summary>Draws the frame, having first put the content work the frame needs on the list.</summary>
    /// <param name="commands">The frame's list.</param>
    /// <exception cref="ArgumentNullException"><paramref name="commands" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>The texture copies go on the list before anything samples them</b>, which is the whole
    ///     reason this exists rather than callers reaching for <see cref="SceneRenderHost.Draw" />
    ///     directly: a host that draws without this leaves every textured material sampling the table's
    ///     fallback for ever, which is a picture rather than a failure and reads as "all my materials
    ///     are the same flat colour".
    /// </remarks>
    public void Draw(ICommandList commands) {
        ArgumentNullException.ThrowIfNull(commands);
        ObjectDisposedException.ThrowIf(disposed, this);

        painting?.Update(commands);

        // ⚠ The vertices and indices themselves, and nothing was copying them.
        //
        // GeometryResidency stages a mesh into the shared buffer's upload region when it becomes
        // resident, and Flush is what records the copy to device memory. Nobody called it, so every
        // draw in every frame read whatever the allocator's memory happened to hold — which is not a
        // missing mesh but a *wrong* one: the positions are noise, so the triangles are enormous and
        // land at depths the test rejects, and the index buffer is noise too.
        //
        // What made it survive is what it looks like. Every counter says the frame is healthy — the
        // meshes resolved, the slices exist, the draws were recorded with the right counts and
        // offsets — because all of that is true of geometry whose bytes never arrived. The picture is
        // a window of clear colour, and with the depth test off it is a window covered by one
        // triangle of noise.
        //
        // Before the frame and outside any pass, which is both what a copy needs and what the draws
        // reading the buffer need: Flush leaves it in ResourceState.VertexInput.
        UploadedBytes += Residency.Flush(commands);

        // ⚠ Here rather than from a host's own OnRender, because of when that runs: the application
        // records the scene first and offers the list to the game afterwards, so a cube uploaded
        // there is a cube the frame that needed it had not got. That is not one dim frame — set 0
        // binds whole or not at all, so it is one frame in which nothing draws, and a refused draw
        // is a fault rather than a dark pixel on some backends.
        Environment?.Upload(commands);

        AdoptViewLayout();

        // The scene's virtualized materials to the pass that dispatches for them. Read every frame
        // rather than pushed once, because an entity appearing is what adds one — see
        // MeshExtractionSystem.ResolveMaterials.
        if (Clusters is { } clusters && Extraction is { ResolveMaterials.Count: > 0 } extraction) {
            clusters.Materials = extraction.ResolveMaterials;
        }

        Host.Draw(commands);
    }

    /// <summary>Gives the view block the set-1 layout only a resolved shader knows.</summary>
    /// <remarks>
    ///     <c>ForwardLightingRenderFeature.Materials</c> makes the same argument about set 3 and makes
    ///     it at length: a set is allocated against a layout that must match the pipeline's, only the
    ///     shader has one, and the first shader resolves inside the first frame. <c>SceneConstants</c>
    ///     needs no equivalent — it takes the effect itself at bind time and reads set 0 off that.
    /// </remarks>
    void AdoptViewLayout() {
        if (ViewBlock.Layout.IsValid || Materials.AnyResolved is not { } effect) {
            return;
        }

        var slot = (int)ViewBlock.Slot;

        if (effect.SetLayouts.Length > slot && effect.SetLayouts[slot].IsValid) {
            ViewBlock.Layout = effect.SetLayouts[slot];
        }
    }

    /// <summary>
    ///     Says which of a shader's <c>uint</c> parameters is filled from which of a material's
    ///     textures.
    /// </summary>
    /// <param name="materials">The feature that holds the pairing.</param>
    /// <param name="shader">The shading pass its materials are authored against.</param>
    /// <remarks>
    ///     <para>
    ///         <b>Explicit, because the two names belong to different things.</b> The shader's is the
    ///         composed parameter — the path of types the feature was reached through, then
    ///         <c>baseColorIndex</c> — and the material's is what an artist called the map. A convention
    ///         that stripped <c>Index</c> and matched the rest would guess, and would guess silently: an
    ///         unmatched pair leaves the index at zero, which is a valid slot holding some other
    ///         material's texture.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The material-side name is the feature's default and not whatever the material
    ///         chose.</b> <see cref="TexturedMetalRoughnessFeature.BaseColorMap" /> is authorable and
    ///         this pairing is one entry, so a material that renamed its map to <c>bark</c> samples the
    ///         fallback. That is the shape of the pairing rather than an oversight — a table keyed by
    ///         one name cannot hold two — and closing it means keying the pairing per material, which is
    ///         a cost every material would pay for the few that rename.
    ///     </para>
    ///     <para>
    ///         The composition path carries no slot, so the same feature in the first chain slot and the
    ///         eighth is one parameter and one entry here. That is why
    ///         <see cref="TexturedMetalRoughnessFeature.BaseColorIndexParameter" /> takes a path rather
    ///         than a slot.
    ///     </para>
    /// </remarks>
    static void Paired(MaterialRenderFeature materials, string shader) {
        var path = $"{shader}.{MaterialCompiler.ChainShader}.{new TexturedMetalRoughnessFeature().ShaderName}.";

        materials.TextureIndices[
                ParameterKeys.New<uint>(TexturedMetalRoughnessFeature.BaseColorIndexParameter(path))
            ] =
            ParameterKeys.New<TextureViewHandle>(new TexturedMetalRoughnessFeature().BaseColorMap);
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        // ⚠ The host first, and the table last. MaterialRenderFeature gives its table slots back when
        // it is disposed — the table outlives a feature, being the frame's rather than a material's —
        // so a table destroyed first is an ObjectDisposedException thrown out of a tear-down, from a
        // line whose whole purpose is to avoid a leak.
        Host.Dispose();
        clustering?.Dispose();
        painting?.Dispose();
        Painted?.Dispose();
        Table?.Dispose();
        MaterialDescriptors.Dispose();
        Samplers.Dispose();
        SceneBlock.Dispose();
        ViewBlock.Dispose();
        Geometry.Dispose();
    }
}
