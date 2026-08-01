// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Assets;
using Vixen.Engine.Frames;
using Vixen.Graphics;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Ecs;
using Vixen.Rendering.Features;
using Vixen.Rendering.Materials;
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
        // Where the lighting block's set layout comes from once a shader has resolved — see
        // ForwardLightingRenderFeature.Materials. Unset, the first frame binds no set 3 and the
        // driver refuses every draw in it, which on Metal is a fault rather than a dark frame.
        Lighting = new() { Device = device, Materials = Materials };

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
        // The stride and the locations are both SurfaceVertex's own, which is what makes this a
        // statement of the format rather than a second opinion about it.
        describer.VertexLayouts.Add([
            new VertexBufferLayout(
                SurfaceVertex.SizeInBytes,
                [
                    new(SurfaceVertex.Locations[0], VertexFormat.Float32X3, 0),
                    new(SurfaceVertex.Locations[1], VertexFormat.Float32X3, 12),
                    new(SurfaceVertex.Locations[2], VertexFormat.Float32X4, 24),
                    new(SurfaceVertex.Locations[3], VertexFormat.Float32X2, 40)
                ]
            )
        ]);

        Meshes = new() {
            Pipelines = new(device),
            Describer = describer
        };

        Host.Builder.SceneConstants = SceneBlock;
        Host.Builder.ViewConstants = ViewBlock;

        Meshes.Add(Materials);
        Meshes.Add(Transforms);
        Meshes.Add(Lighting);

        Host.System.AddFeature(Meshes);

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

    /// <summary>The frame's set 0: the lighting environment every shading pass reads.</summary>
    public SceneConstants SceneBlock { get; }

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

    /// <summary>The lights that reach them.</summary>
    public ForwardLightingRenderFeature Lighting { get; }

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

        if (Extraction is { } extraction) {
            extraction.Meshes = Source;
            extraction.Materials = Painter;
            extraction.Virtualized = Clusters?.Feature;
            extraction.Clusters = Hierarchies;
        }
    }

    /// <summary>
    ///     Adds the extraction systems to a loop, so the world's entities reach the render system.
    /// </summary>
    /// <param name="loop">The loop that runs them.</param>
    /// <param name="stages">Which stages the extracted objects are drawn in.</param>
    /// <exception cref="ArgumentNullException"><paramref name="loop" /> is null.</exception>
    /// <remarks>
    ///     The stage mask is the caller's because a stage's index is assigned by the render system when
    ///     the compositor's document declares it — so this is called after
    ///     <see cref="SceneRenderHost.Load" />, and a mask of none draws nothing at all.
    /// </remarks>
    public void Register(EngineLoop loop, RenderStageMask stages) {
        ArgumentNullException.ThrowIfNull(loop);
        ObjectDisposedException.ThrowIf(disposed, this);

        Extraction = new(Host.System, Meshes, Transforms, Materials, Residency) {
            Stages = stages,
            Meshes = Source,
            Materials = Painter,
            Virtualized = Clusters?.Feature,
            Clusters = Hierarchies
        };

        loop.Add(Extraction);
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
        SceneBlock.Dispose();
        ViewBlock.Dispose();
        Geometry.Dispose();
    }
}
