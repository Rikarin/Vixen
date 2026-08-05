// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Shaders.Generated;
using Vixen.Terrain;

// ⚠ The kernel's type and this namespace share a name, and an alias called `Terrain` cannot win:
// inside `namespace Vixen.Rendering.Terrain`, namespace-member lookup finds the namespace before it
// looks at using-aliases. So the alias takes a different name and the *property* keeps the good one.
using TerrainMap = Vixen.Terrain.Terrain;

namespace Vixen.Rendering.Terrain;

/// <summary>The two shader stages a terrain draws with.</summary>
/// <param name="Vertex">The vertex stage.</param>
/// <param name="Fragment">The fragment stage.</param>
public readonly record struct TerrainShaders(ShaderHandle Vertex, ShaderHandle Fragment) {
    /// <summary>Whether both stages are real.</summary>
    public bool IsValid => Vertex.IsValid && Fragment.IsValid;
}

/// <summary>What a view needs to draw a terrain.</summary>
/// <param name="ViewProjection">The camera's combined matrix.</param>
/// <param name="Position">Where the camera is, in the terrain's own space.</param>
/// <param name="Frustum">What it can see.</param>
public readonly record struct TerrainView(Matrix4x4 ViewProjection, Vector3 Position, BoundingFrustum Frustum);

/// <summary>
///     Draws a terrain: one indexed instanced call over the patches a quadtree chose.
/// </summary>
/// <remarks>
///     <para>
///         <b>[docs/plan/31 § T2].</b> The selection and the morph are arithmetic in
///         <c>Vixen.Terrain</c> and are tested without a device; this is the part that needs one —
///         the textures, the records, the descriptor set and the draw.
///     </para>
///     <para>
///         ⚠ <b>An atlas of per-tile blocks, which is the split of the <em>layout</em> the plan asked
///         for without the split of the texture it did not.</b> A tile is the unit of load ([§ D13]);
///         a CDLOD patch straddles a tile boundary except by luck, and a texture per tile would make
///         every straddling patch either two draws or a shader sampling two textures. One texture
///         holding a <c>TileSamples²</c> block per tile is both: one thing to bind, and a block to
///         upload, evict and mip on its own. See <see cref="TerrainAtlas" /> for why the blocks
///         duplicate their boundary samples rather than sharing them.
///     </para>
///     <para>
///         ⚠ <b>And it is what makes the mip chain legal at all.</b> A 2×2 reduction of the atlas
///         never crosses a block boundary, because every block is a power of two starting at a
///         multiple of one. Reducing the packed grid instead would mix two tiles at every level —
///         [§ D2]'s seam arriving through the mip chain.
///     </para>
///     <para>
///         <b>Only what changed is re-uploaded.</b> <see cref="Upload" /> asks the terrain which tiles
///         <c>Resolve</c> dirtied and copies those rows; a stroke on one tile of a hundred moves one
///         hundredth of the bytes. Uploading the whole heightmap per frame would make a 33 MB
///         transfer the cost of moving the brush.
///     </para>
/// </remarks>
public sealed class TerrainRenderer : IDisposable {
    readonly IGraphicsDevice device;
    readonly int gridQuads;
    readonly int indexCount;
    readonly int slots;

    // Which shader the set is written for. The preview and the lit shader bind the same resources
    // under different indices — a binding index comes from declaration order within a set, and the
    // lit shader declares the frame's resources first — so every write below goes through these
    // rather than through either shader's keys directly.
    readonly bool lit;
    readonly int blockSize;
    readonly uint constantBinding;
    readonly uint heightMapBinding;
    readonly uint weightMapsBinding;
    readonly uint layerMapsBinding;
    readonly uint surfaceMapsBinding;
    readonly uint holeMapBinding;
    readonly uint holeSamplerBinding;
    readonly uint heightSamplerBinding;
    readonly uint weightSamplerBinding;
    readonly uint layerSamplerBinding;
    readonly uint nodesBinding;
    readonly uint layerScalesBinding;
    readonly uint layerBlendsBinding;

    // The lit shader's own resources: the cascade atlas's sampler, and a spare storage buffer for
    // the cluster bindings on a frame that culls no lights — a binding is in the plan because it is
    // declared, and a set is written wholly or not at all.
    readonly SamplerHandle shadowSampler;
    readonly BufferHandle spareBuffer;

    readonly BufferHandle indices;
    readonly BufferHandle[] constants;
    readonly BufferHandle layerScales;
    readonly BufferHandle layerBlends;
    readonly SamplerHandle heightSampler;
    readonly SamplerHandle weightSampler;
    readonly SamplerHandle layerSampler;
    readonly DescriptorSetLayoutHandle setLayout;
    readonly DescriptorSetLayoutHandle emptySetLayout;
    readonly PipelineLayoutHandle layout;
    readonly PipelineHandle pipeline;

    readonly TerrainAtlas atlas;
    readonly TextureHandle heightMap;
    readonly TextureViewHandle heightView;
    readonly BufferHandle[] staging;
    readonly long[] stagingCapacity;
    readonly long stagingGrain;
    long stagingAt;
    readonly ushort[] chain;
    readonly byte[] weightChain;
    readonly TextureHandle[] weightMaps = new TextureHandle[MaxWeightMaps];
    readonly TextureViewHandle[] weightViews = new TextureViewHandle[MaxWeightMaps];

    readonly TextureHandle holeMap;
    readonly TextureViewHandle holeView;
    readonly SamplerHandle holeSampler;
    readonly byte[] holeBlock;

    readonly TextureHandle defaultAlbedo;
    readonly TextureViewHandle defaultAlbedoView;
    readonly TextureHandle defaultSurface;
    readonly TextureViewHandle defaultSurfaceView;
    readonly BufferHandle defaultStaging;
    readonly TextureViewHandle[] layerViews = new TextureViewHandle[MaxLayers];
    readonly TextureViewHandle[] surfaceViews = new TextureViewHandle[MaxLayers];

    readonly DescriptorSetHandle[] descriptors;
    readonly List<TerrainLodNode> selected = [];

    TerrainSplat splat;
    bool paintUploaded;

    BufferHandle nodes;
    long nodeCapacity;
    int slot;
    bool uploaded;
    bool disposed;

    /// <summary>Creates a renderer for one terrain.</summary>
    /// <param name="device">The device.</param>
    /// <param name="terrain">The terrain it draws.</param>
    /// <param name="shaders">Its two stages.</param>
    /// <param name="output">What it renders into.</param>
    /// <param name="ranges">Where each level of detail takes over.</param>
    /// <param name="gridQuads">How many quads the shared patch spans.</param>
    /// <exception cref="ArgumentException">The shaders are not both real.</exception>
    public TerrainRenderer(
        IGraphicsDevice device,
        TerrainMap terrain,
        TerrainShaders shaders,
        RenderOutput output,
        TerrainLodRanges ranges,
        int gridQuads = TerrainLodTree.DefaultGridQuads
    ) : this(device, terrain, shaders, output, ranges, lit: false, gridQuads) { }

    /// <summary>Creates a renderer whose set is written for <c>TerrainLit</c> rather than the preview.</summary>
    /// <remarks>
    ///     Internal because the lit contract is <see cref="TerrainSceneRenderer" />'s: the frame's
    ///     values arrive through <see cref="Frame" /> before every <see cref="Upload" />, and a
    ///     caller that constructs lit without filling it draws ground under a black sun. Decided at
    ///     construction because the descriptor layout and the block size are the shader's.
    /// </remarks>
    internal TerrainRenderer(
        IGraphicsDevice device,
        TerrainMap terrain,
        TerrainShaders shaders,
        RenderOutput output,
        TerrainLodRanges ranges,
        bool lit,
        int gridQuads = TerrainLodTree.DefaultGridQuads
    ) {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(terrain);

        if (!shaders.IsValid) {
            throw new ArgumentException("A terrain needs both a vertex and a fragment stage.", nameof(shaders));
        }

        this.device = device;
        this.gridQuads = gridQuads;
        this.lit = lit;

        blockSize = lit ? TerrainLitKeys.ConstantBufferSize : TerrainKeys.ConstantBufferSize;
        constantBinding = lit ? TerrainLitKeys.ConstantBufferBinding : TerrainKeys.ConstantBufferBinding;
        heightMapBinding = lit ? TerrainLitKeys.HeightMapBinding : TerrainKeys.HeightMapBinding;
        weightMapsBinding = lit ? TerrainLitKeys.WeightMapsBinding : TerrainKeys.WeightMapsBinding;
        layerMapsBinding = lit ? TerrainLitKeys.LayerMapsBinding : TerrainKeys.LayerMapsBinding;
        surfaceMapsBinding = lit ? TerrainLitKeys.SurfaceMapsBinding : TerrainKeys.SurfaceMapsBinding;
        holeMapBinding = lit ? TerrainLitKeys.HoleMapBinding : TerrainKeys.HoleMapBinding;
        holeSamplerBinding = lit ? TerrainLitKeys.HoleSamplerBinding : TerrainKeys.HoleSamplerBinding;
        heightSamplerBinding = lit ? TerrainLitKeys.HeightSamplerBinding : TerrainKeys.HeightSamplerBinding;
        weightSamplerBinding = lit ? TerrainLitKeys.WeightSamplerBinding : TerrainKeys.WeightSamplerBinding;
        layerSamplerBinding = lit ? TerrainLitKeys.LayerSamplerBinding : TerrainKeys.LayerSamplerBinding;
        nodesBinding = lit ? TerrainLitKeys.NodesBinding : TerrainKeys.NodesBinding;
        layerScalesBinding = lit ? TerrainLitKeys.LayerScalesBinding : TerrainKeys.LayerScalesBinding;
        layerBlendsBinding = lit ? TerrainLitKeys.LayerBlendsBinding : TerrainKeys.LayerBlendsBinding;

        Terrain = terrain;
        Tree = new(terrain.Description, ranges, gridQuads);

        // ⚠ Deeper than the swapchain, because a frame is not one Upload: an editor uploads once per
        // pane, and a ring exactly FramesInFlight deep would come back to a slot the GPU is still
        // reading on the frame a fourth pane opened. The depth buys UploadsPerFrame uploads a frame
        // before that happens, and everything rung by slot — the records, the constants, the staging
        // — shares the bound.
        slots = Math.Max(1, device.FramesInFlight) * UploadsPerFrame;

        // The index buffer, once. It is the same lattice for every patch of every terrain, which is
        // the whole point of drawing them all from one mesh.
        indexCount = TerrainGridPatch.IndexCount(gridQuads);
        var patch = new uint[indexCount];
        TerrainGridPatch.FillIndices(gridQuads, patch);

        // ⚠ Host-upload rather than device-local, and the null device is what said so: a
        // device-local buffer cannot be host-written and has to be staged through a copy, which needs
        // a command list the constructor does not have. For the records and the constants that is the
        // right memory class anyway — they are rewritten every frame, so staging them would be a copy
        // per frame to save a read. For the index buffer it is a simplification: 24 KB written once,
        // read every draw. Staging it belongs with the first frame's command list if it ever measures.
        indices = device.CreateBuffer(
            new(
                (long)indexCount * sizeof(uint),
                BufferUsage.Index,
                MemoryAccess.HostUpload,
                "terrain indices"
            )
        );

        device.Write(indices, 0, MemoryMarshal.AsBytes<uint>(patch));

        var description = terrain.Description;

        atlas = new(in description);

        heightMap = device.CreateTexture(
            new(
                PixelFormat.R16UNorm,
                atlas.Width,
                atlas.Height,
                TextureUsage.Sampled | TextureUsage.CopyDestination,
                MipLevels: atlas.LevelCount,
                Name: "terrain heights"
            )
        );

        heightView = device.CreateTextureView(heightMap);

        // ⚠ One tile's chain, not the whole terrain's. The upload is per tile precisely so a stroke
        // on one tile of a hundred moves one hundredth of the bytes.
        chain = new ushort[TerrainMips.ChainSamples(description.TileSamples)];
        weightChain = new byte[chain.Length * TerrainSplat.LayersPerWeightMap];

        // ⚠ A recorded copy reads its source at submit, not at record, so every copy a frame records
        // needs its own bytes until that frame retires: one buffer rewritten at offset zero per tile
        // would hand every recorded copy the last tile's data. Staged writes are packed at distinct
        // offsets instead, and the buffer is rung by slot so the next frame's writes cannot land
        // under an in-flight frame's reads. The grain is one fully stale tile — chain, holes and
        // every weightmap — and a frame that needs more grows its slot; see Stage.
        staging = new BufferHandle[slots];
        stagingCapacity = new long[slots];
        stagingGrain =
            ((long)chain.Length * sizeof(ushort))
            + ((long)description.TileSamples * description.TileSamples)
            + ((long)weightChain.Length * MaxWeightMaps)
            + (4 * StagingAlignment);

        // Clamped for the heightmap and the weights, because a terrain's edge is its edge and a
        // repeat would wrap the far side of the world into the near one. The layer textures repeat,
        // because that is what a tiling ground texture is.
        var edge = new SamplerDescription(
            AddressU: AddressMode.ClampToEdge,
            AddressV: AddressMode.ClampToEdge,
            AddressW: AddressMode.ClampToEdge
        );

        heightSampler = device.CreateSampler(edge);
        weightSampler = device.CreateSampler(edge);
        layerSampler = device.CreateSampler(new());

        // ⚠ Rung by slot like the node records, because the block is rewritten every Upload and read
        // by draws up to FramesInFlight frames behind. One buffer would hand every pane of an editor
        // frame the last pane's ViewProjection — and hand an in-flight frame this one's.
        constants = new BufferHandle[slots];

        for (var index = 0; index < slots; index++) {
            constants[index] = device.CreateBuffer(
                new(
                    blockSize,
                    BufferUsage.Uniform,
                    MemoryAccess.HostUpload,
                    $"terrain constants {index}"
                )
            );
        }

        layerScales = device.CreateBuffer(
            new(MaxLayers * sizeof(float), BufferUsage.Storage, MemoryAccess.HostUpload, "terrain layer scales")
        );

        // ⚠ Two floats per layer — the mode and the contrast — because the fragment stage reads them
        // together and always. A separate integer buffer for the mode would be a second binding, a
        // second upload and a second thing to get out of step with the layer list.
        layerBlends = device.CreateBuffer(
            new(MaxLayers * 2 * sizeof(float), BufferUsage.Storage, MemoryAccess.HostUpload, "terrain layer blends")
        );

        // ⚠ Every weightmap slot the layout declares is created, whatever the terrain has today.
        // A descriptor array with a hole in it is undefined behaviour on most drivers, and a terrain
        // gains and loses layers while the renderer is alive — so the textures exist and the ones
        // with no layer read zero, which the loop's early-out then skips.
        for (var map = 0; map < MaxWeightMaps; map++) {
            weightMaps[map] = device.CreateTexture(
                new(
                    PixelFormat.Rgba8UNorm,
                    atlas.Width,
                    atlas.Height,
                    TextureUsage.Sampled | TextureUsage.CopyDestination,
                    MipLevels: atlas.LevelCount,
                    Name: $"terrain weights {map}"
                )
            );

            weightViews[map] = device.CreateTextureView(weightMaps[map]);
        }

        // ⚠ One level and never filtered towards a neighbour, which is why the shader's test is
        // `> 0.5`. A hole's edge is a decision, not a gradient — a mip of it would make a distant
        // cave mouth fade rather than end, and the ground it faded into would be ground the collider
        // says is not there.
        holeMap = device.CreateTexture(
            new(
                PixelFormat.R8UNorm,
                atlas.Width,
                atlas.Height,
                TextureUsage.Sampled | TextureUsage.CopyDestination,
                MipLevels: 1,
                Name: "terrain holes"
            )
        );

        holeView = device.CreateTextureView(holeMap);
        holeSampler = device.CreateSampler(edge);
        holeBlock = new byte[description.TileSamples * description.TileSamples];

        // ⚠ A texture per layer slot is bound whatever the terrain has today, and these are what a
        // slot with no texture gets. A descriptor array with a hole in it is undefined behaviour on
        // most drivers, and a terrain gains and loses layers while the renderer is alive.
        //
        // ⚠ Their contents are not arbitrary. White albedo makes an unassigned layer draw as its
        // weight rather than as black — a layer somebody just created reads as "no texture yet"
        // instead of as a hole in the world. And the surface default's alpha is 0.5, a flat blend
        // height, which is what makes a height blend degrade to a weight blend rather than to a hard
        // edge; `Terrain.rvn` says so at the binding and this is the other half of that sentence.
        defaultAlbedo = device.CreateTexture(
            new(
                PixelFormat.Rgba8UNorm,
                1,
                1,
                TextureUsage.Sampled | TextureUsage.CopyDestination,
                Name: "terrain layer default"
            )
        );

        defaultSurface = device.CreateTexture(
            new(
                PixelFormat.Rgba8UNorm,
                1,
                1,
                TextureUsage.Sampled | TextureUsage.CopyDestination,
                Name: "terrain surface default"
            )
        );

        defaultAlbedoView = device.CreateTextureView(defaultAlbedo);
        defaultSurfaceView = device.CreateTextureView(defaultSurface);

        defaultStaging = device.CreateBuffer(
            new(8, BufferUsage.CopySource, MemoryAccess.HostUpload, "terrain default staging")
        );

        device.Write(defaultStaging, 0, new byte[] { 255, 255, 255, 255, 255, 255, 255, 128 });

        for (var slot = 0; slot < MaxLayers; slot++) {
            layerViews[slot] = defaultAlbedoView;
            surfaceViews[slot] = defaultSurfaceView;
        }

        var bindings = new List<DescriptorBinding> {
            new(constantBinding, DescriptorKind.UniformBuffer, ShaderStage.Vertex | ShaderStage.Fragment),
            new(heightMapBinding, DescriptorKind.SampledTexture, ShaderStage.Vertex | ShaderStage.Fragment),
            new(weightMapsBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment, MaxWeightMaps),
            new(layerMapsBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment, MaxLayers),
            new(surfaceMapsBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment, MaxLayers),
            new(holeMapBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment),
            new(holeSamplerBinding, DescriptorKind.Sampler, ShaderStage.Fragment),
            new(heightSamplerBinding, DescriptorKind.Sampler, ShaderStage.Vertex | ShaderStage.Fragment),
            new(weightSamplerBinding, DescriptorKind.Sampler, ShaderStage.Fragment),
            new(layerSamplerBinding, DescriptorKind.Sampler, ShaderStage.Fragment),
            new(nodesBinding, DescriptorKind.StorageBuffer, ShaderStage.Vertex),
            new(layerScalesBinding, DescriptorKind.StorageBuffer, ShaderStage.Fragment),
            new(layerBlendsBinding, DescriptorKind.StorageBuffer, ShaderStage.Fragment)
        };

        if (lit) {
            // The frame's resources, at the lit shader's own indices. The sampler is this
            // renderer's — unfiltered and clamped, `ShadowMapRenderer.Publish`'s reasoning: the tap
            // compares after sampling, so a linear read blends four depths into a value no surface
            // has. The spare buffer stands in for the cluster bindings on a frame that culls no
            // lights, whose variant reads neither.
            bindings.Add(new(TerrainLitKeys.ShadowMapBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment));
            bindings.Add(new(TerrainLitKeys.ShadowSamplerBinding, DescriptorKind.Sampler, ShaderStage.Fragment));
            bindings.Add(new(TerrainLitKeys.LightBufferBinding, DescriptorKind.StorageBuffer, ShaderStage.Fragment));
            bindings.Add(new(TerrainLitKeys.ClustersBinding, DescriptorKind.StorageBuffer, ShaderStage.Fragment));

            shadowSampler = device.CreateSampler(SamplerDescription.PointClamp);

            spareBuffer = device.CreateBuffer(
                new(64, BufferUsage.Storage, MemoryAccess.HostUpload, "terrain lit spare")
            );
        }

        setLayout = device.CreateDescriptorSetLayout(
            new(DescriptorSetSlot.PerMaterial, [.. bindings], "terrain")
        );

        // ⚠ Padded below the material set, because a pipeline layout is positional and the shader's
        // bindings are set 2 — DescriptorSetSlot.PerMaterial, where Raven puts every unmarked
        // binding. A layout holding this set alone puts it at index 0, and BindDescriptorSet binds
        // it at index 2: undefined behaviour some desktop drivers absorb and MoltenVK refuses from
        // inside pipeline creation. One empty layout stands in for both unused slots — repeating a
        // handle in a pipeline layout is legal everywhere.
        emptySetLayout = device.CreateDescriptorSetLayout(new(DescriptorSetSlot.PerFrame, [], "terrain empty"));
        layout = device.CreatePipelineLayout(new([emptySetLayout, emptySetLayout, setLayout], [], "terrain"));

        // Every colour format the output declares, not the first: a split frame hands the lit
        // shader three targets — the scene, the albedo plane and the normal plane — and a pipeline
        // declaring fewer attachments than the pass would be a validation error at the draw.
        var attachments = new ColourTargetState[Math.Max(output.ColourCount, 1)];

        for (var target = 0; target < attachments.Length; target++) {
            attachments[target] = new(
                output.ColourCount > 0 ? output.ColourFormats[target] : PixelFormat.Rgba8UNorm,
                BlendState.Opaque
            );
        }

        // No vertex buffers, which is the shader's own design: a lattice's positions are two
        // divisions of SV_VertexID, and Terrain.reflect.json's empty VertexInputs is the evidence.
        pipeline = device.CreateGraphicsPipeline(
            new(
                shaders.Vertex,
                shaders.Fragment,
                layout,
                attachments,
                [],
                PrimitiveTopology.TriangleList,
                RasterizerState.Default,
                DepthStencilState.Default,
                output.DepthFormat,
                output.SampleCount,
                "terrain"
            )
        );

        descriptors = new DescriptorSetHandle[slots];
        Resize(1024);
    }

    /// <summary>The most weight-blended layers a terrain can carry.</summary>
    /// <remarks>
    ///     Sixteen, four to a weightmap, which is [docs/plan/31 § D6]'s ceiling. Above it the answer
    ///     is a virtual texture or two terrains.
    /// </remarks>
    public const int MaxLayers = 16;

    /// <summary>How many weightmap textures that is.</summary>
    public const int MaxWeightMaps = MaxLayers / 4;

    /// <summary>How many times one frame may call <see cref="Upload" /> — once per pane, in an editor.</summary>
    /// <remarks>
    ///     ⚠ The per-frame rings are FramesInFlight × this deep and <see cref="Upload" /> advances
    ///     them once per call, so a slot comes back around after FramesInFlight frames only while a
    ///     frame stays within this many uploads. A fifth pane in one frame would rewrite buffers a
    ///     frame still in flight is reading.
    /// </remarks>
    const int UploadsPerFrame = 4;

    /// <summary>What every staged offset is rounded up to.</summary>
    /// <remarks>
    ///     A buffer-to-texture copy's source offset must be a multiple of its texel size, and
    ///     sixteen covers every format staged here.
    /// </remarks>
    const long StagingAlignment = 16;

    /// <summary>The terrain being drawn.</summary>
    public TerrainMap Terrain { get; }

    /// <summary>The hole mask, for anything that has to stop where the ground does.</summary>
    /// <remarks>
    ///     ⚠ <b>Named rather than re-created, for the reason <see cref="GrassTerrainSource" /> names
    ///     the heightmap.</b> A scatter with its own copy would be a second upload of the same texels
    ///     and a second thing to invalidate when somebody punches a hole.
    /// </remarks>
    public TextureViewHandle Holes => holeView;

    /// <summary>And how it is read — clamped, unfiltered, at level 0.</summary>
    public SamplerHandle HoleSampler => holeSampler;

    /// <summary>What a grass scatter samples this terrain's ground from.</summary>
    /// <param name="layer">
    ///     The weight layer the grass is bound to, or negative for a type bound to no layer — the
    ///     scatter then reads channel zero of a map it never weights by.
    /// </param>
    /// <param name="origin">Where the terrain's low corner is, in world space.</param>
    /// <returns>The source, over this renderer's own textures.</returns>
    /// <remarks>
    ///     ⚠ <b>Here rather than assembled by the caller, because half of it is the atlas.</b>
    ///     <c>GrassScatter.rvn</c> routes every read through the tile-block transform, so a source
    ///     assembled from the description alone hands it a monolithic geometry the texture does not
    ///     have — the exact bug the tiled fields exist to close. The atlas is this class's private
    ///     arithmetic, and the one place that can fill both halves consistently is here.
    /// </remarks>
    public GrassTerrainSource GrassSource(int layer, Vector3 origin) {
        ObjectDisposedException.ThrowIf(disposed, this);

        var description = Terrain.Description;
        var map = layer < 0 ? 0 : Math.Clamp(layer / TerrainSplat.LayersPerWeightMap, 0, MaxWeightMaps - 1);

        return new(
            heightView,
            weightViews[map],
            holeView,
            heightSampler,
            weightSampler,
            holeSampler,
            layer < 0 ? 0 : layer % TerrainSplat.LayersPerWeightMap,
            new(atlas.Width, atlas.Height),
            new(description.MinHeight, description.MaxHeight),
            description.MetresPerQuad,
            origin,
            atlas.TileSamples,
            atlas.TileQuads,
            new(atlas.TilesX, atlas.TilesZ)
        );
    }

    /// <summary>Which tiles are loaded, or null while every tile always is.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Null is the whole terrain, and for anything a person sculpts that is the right
    ///         answer.</b> A sixteen-tile terrain fits in the atlas by construction, and the machinery
    ///         would be a decision with one outcome. What a streamer changes is the first frame of a
    ///         large one: sixteen thousand block copies become a few dozen, because the rest are drawn
    ///         from a pinned coarse tail rather than uploaded in full.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Set it before the first <see cref="Upload" />, not after.</b> The pinned tail is
    ///         written on the frame that first uploads, and a streamer attached later would find every
    ///         tile already fully copied — which is not wrong, but it is a streamer that saves nothing
    ///         and reports numbers saying it did.
    ///     </para>
    /// </remarks>
    public TerrainStreamer? Streaming { get; set; }

    /// <summary>Where the world has to be loaded around, in the terrain's own space.</summary>
    /// <remarks>
    ///     ⚠ <b>Empty means the camera, which is the common case and not the only one.</b> A listen
    ///     server's second player, a spawn point being prepared and a cinematic about to cut are all
    ///     sources — <see cref="StreamingSource" />'s own remarks — and a terrain that assumed the view
    ///     would be the one every engine has had to retrofit.
    /// </remarks>
    public List<StreamingSource> StreamingSources { get; } = [];

    /// <summary>How far around a source a terrain's fine levels are kept, in metres.</summary>
    public float StreamingRadius { get; set; } = 512f;

    /// <summary>What turns a layer's texture reference into something to sample.</summary>
    /// <remarks>
    ///     ⚠ <b>Null is a working renderer, not a broken one.</b> Every layer slot is bound a default
    ///     at construction, so a terrain with no texture source draws its weights in white — which is
    ///     what a freshly created layer should look like, and what a headless test sees.
    /// </remarks>
    public ITerrainTextures? Textures { get; set; }

    /// <summary>How many layer textures the last <see cref="Upload" /> resolved.</summary>
    /// <remarks>
    ///     ⚠ <b>Counting the ones that came back, not the ones that were asked for.</b> A source that
    ///     answers nothing for a reference it has not loaded yet is the normal case on the frame a
    ///     layer is assigned, and the number a person needs is how many arrived.
    /// </remarks>
    public int ResolvedTextures { get; private set; }

    /// <summary>The quadtree the selection descends.</summary>
    public TerrainLodTree Tree { get; }

    /// <summary>How many patches the last <see cref="Upload" /> chose.</summary>
    public int PatchCount { get; private set; }

    /// <summary>What the generated splat material compiles as, from the terrain's layer list.</summary>
    /// <remarks>
    ///     Recomputed by <see cref="Upload" /> and compared: a layer added or removed changes the
    ///     permutation, and that is what makes the whole weightmap set stale rather than a rectangle
    ///     of it.
    /// </remarks>
    public TerrainSplat Splat => splat;

    /// <summary>How many draws the last <see cref="Record" /> made.</summary>
    /// <remarks>
    ///     One, for any number of patches, or zero when nothing was selected. It is a property rather
    ///     than a constant because "the terrain is one draw call" is the claim [§ D3] makes, and a
    ///     claim worth making is worth a test reading it back.
    /// </remarks>
    public int Draws { get; private set; }

    /// <summary>How many triangles the last <see cref="Record" /> submitted.</summary>
    public int Triangles => PatchCount * gridQuads * gridQuads * 2;

    /// <summary>How many patch records the buffer can hold.</summary>
    public int Capacity => (int)(nodeCapacity / TerrainNodeRecord.SizeInBytes);

    /// <summary>How many bytes the last <see cref="Upload" /> copied into the heightmap.</summary>
    /// <remarks>
    ///     Zero for a frame in which nothing was sculpted, which is almost all of them — and the
    ///     number a profiler reads to see that a stroke on one tile does not move the whole terrain.
    /// </remarks>
    public long UploadedBytes { get; private set; }

    /// <summary>Chooses this frame's patches and stages what the device needs.</summary>
    /// <param name="commands">Where to record the copies.</param>
    /// <param name="view">The camera.</param>
    /// <returns>How many patches were chosen.</returns>
    public int Upload(ICommandList commands, in TerrainView view) {
        ArgumentNullException.ThrowIfNull(commands);
        ObjectDisposedException.ThrowIf(disposed, this);

        slot = (slot + 1) % slots;
        stagingAt = 0;
        UploadedBytes = 0;

        // Any tile a stroke dirtied is recomposited before it is read, so the texture and the
        // composite cannot disagree — and the tiles it dirtied are the blocks worth copying.
        //
        // ⚠ The first frame copies everything whatever the dirty set says. A terrain built and then
        // resolved has no dirty tiles at all, so a renderer that only copied dirty tiles would draw a
        // heightmap of zeros until somebody happened to sculpt — which reads as a flat terrain rather
        // than as a missing upload.
        var dirty = DirtyTiles();

        Terrain.Resolve();

        // ⚠ The weights ride the same dirty set as the heights and are re-uploaded on their own
        // trigger as well: a paint stroke moves no height, so a renderer that only watched the
        // composite would show yesterday's ground until somebody sculpted. `Splat` changing is the
        // other trigger — a layer added or removed changes what every texel means.
        var wanted = TerrainSplat.Of(Terrain.Weights);
        var repaint = !paintUploaded || wanted != splat;

        if (repaint) {
            paintUploaded = true;
            splat = wanted;

            WriteLayerConstants();
        }

        // ⚠ Asked every frame rather than only when the layer list changes, and the difference is a
        // texture that finished loading. A source answers `default` for something not yet resident,
        // so a layer assigned this frame resolves next frame — and a renderer that only asked when
        // the *list* changed would show the default for ever, because assigning a texture to an
        // existing layer does not change the list.
        ResolveLayerTextures();

        var description = Terrain.Description;

        // ⚠ Serviced before the copies rather than after, so a tile that arrives this frame is
        // uploaded this frame. Servicing afterwards costs every arrival a frame of latency, which is
        // a tile that pops in one frame late for ever rather than once.
        Stream(view);

        var floor = Streaming?.CoarseLevel ?? 0;

        // Decided before anything is recorded, so the state transitions can be one barrier group in
        // and one out rather than a stall per tile. Pending is stable here: pages are placed inside
        // Stream, on this thread, and drained below.
        var anyStale = !uploaded || dirty.Count > 0;
        var copyHeights = anyStale || Streaming?.Pages.Pending > 0;
        var copyWeights = (anyStale || repaint) && Terrain.Weights.LayerCount > 0;

        BeginCopies(commands, copyHeights, anyStale, copyWeights);

        if (!uploaded) {
            CopyDefaults(commands);
        }

        for (var tileZ = 0; tileZ < description.TilesZ; tileZ++) {
            for (var tileX = 0; tileX < description.TilesX; tileX++) {
                var stale = !uploaded || dirty.Contains((tileX, tileZ));

                if (stale) {
                    // ⚠ From the coarse floor down, which is everything when nothing is streaming.
                    // With a streamer the fine levels of a tile nobody is near are bytes copied for a
                    // patch that will be drawn from the tail anyway.
                    CopyTileHeights(commands, tileX, tileZ, Streaming?.IsResident(tileX, tileZ) == true ? 0 : floor);
                    CopyTileHoles(commands, tileX, tileZ);
                }

                if (stale || repaint) {
                    CopyTileWeights(commands, tileX, tileZ);
                }
            }
        }

        // And the fine levels of whatever the pool handed over, which is the streaming half proper.
        //
        // ⚠ After the dirty tiles' own copies, and that is safe only because the two sets cannot
        // overlap in a frame with anything to disagree about. `Terrain.Resolve` above bumps every
        // dirty tile's revision before `Stream` places anything, so a chain read before this frame is
        // refused by the pool and never gets here — which means a tile in `dirty` has no arrival, and
        // a tile with an arrival was not dirtied. What is left is the first frame, where every tile is
        // stale and an arrival may cover levels a full copy also covers: same revision, so the same
        // bytes twice, and which copy the device runs last does not matter. Move the resolve after the
        // stream and both of those stop being true.
        Streaming?.Pages.Drain((tileX, tileZ, chain) => CopyChain(commands, tileX, tileZ, chain, 0, floor));

        EndCopies(commands, copyHeights, anyStale, copyWeights);

        // The first frame stages the whole atlas and no later frame should keep paying for it: a
        // slot grown well past the grain is handed back — destruction waits out the frames that
        // could still read it — and the next frame round re-allocates at the grain.
        if (stagingCapacity[slot] > stagingGrain * 8) {
            device.Destroy(staging[slot]);

            staging[slot] = default;
            stagingCapacity[slot] = 0;
        }

        uploaded = true;

        selected.Clear();

        // A local, because a positional record's members are property calls rather than fields and an
        // `in` parameter's call result has nothing to take a reference to.
        var frustum = view.Frustum;
        Tree.Select(view.Position, in frustum, Terrain, selected);
        PatchCount = selected.Count;

        if (PatchCount > Capacity) {
            Resize(PatchCount);
        }

        if (PatchCount > 0) {
            var records = new TerrainNodeRecord[PatchCount];

            for (var index = 0; index < PatchCount; index++) {
                var record = TerrainNodeRecord.Of(selected[index], gridQuads, atlas.LevelCount - 1);

                // ⚠ The level is floored rather than the patch dropped, and that is the whole
                // degradation story: a node over a tile whose fine levels have not arrived is drawn
                // from the pinned tail, in the right place and coarser than it asked for. Dropping it
                // would put a hole in the distance on the frame a camera turned.
                if (Streaming is { } streaming) {
                    var node = selected[index];
                    var quads = description.TileQuads;
                    var tile = (
                        X: Math.Clamp(node.X / Math.Max(1, quads), 0, description.TilesX - 1),
                        Z: Math.Clamp(node.Z / Math.Max(1, quads), 0, description.TilesZ - 1)
                    );

                    record = record with { Level = streaming.LevelOf(tile.X, tile.Z, record.Level) };
                }

                records[index] = record;
            }

            device.Write(nodes, slot * nodeCapacity, MemoryMarshal.AsBytes<TerrainNodeRecord>(records));
        }

        // ⚠ Through the generated block rather than by writing the fields in order. Std140 pads a
        // float2 to eight bytes and a mat4 is sixty-four, so a block written in declaration order
        // puts every field after the first vector at the wrong address — and the symptom is a terrain
        // whose tile count is somebody else's height range.
        var block = new byte[blockSize];

        if (lit) {
            WriteLitBlock(block, view);
        } else {
            new TerrainConstants {
                ViewProjection = view.ViewProjection,
                HeightMapSize = new(atlas.Width, atlas.Height),
                TileSamples = atlas.TileSamples,
                TileQuads = atlas.TileQuads,
                AtlasTiles = new(atlas.TilesX, atlas.TilesZ),
                HeightRange = new(description.MinHeight, description.MaxHeight),
                MetresPerQuad = description.MetresPerQuad
            }.Write(block);
        }

        device.Write(constants[slot], 0, block);

        if (lit) {
            BindFrameResources();
        }

        return PatchCount;
    }

    /// <summary>The frame's lighting, filled by <see cref="TerrainSceneRenderer" /> before every upload.</summary>
    /// <remarks>
    ///     One stable instance per node rather than a parameter, because the same values reach every
    ///     terrain the frame draws and the grass pass beside them — the scene renderer fills it once
    ///     per frame and every consumer reads it. Null on a lit renderer draws under a black sun,
    ///     which is the loud version of "nobody filled the frame".
    /// </remarks>
    internal TerrainFrameLighting? Frame { get; set; }

    /// <summary>Where this terrain's low corner is in world space — the lit block's <c>originWS</c>.</summary>
    /// <remarks>
    ///     Beside <see cref="Frame" /> rather than inside it, because the lighting is the frame's
    ///     and the placement is this terrain's: one shared instance, one origin per renderer.
    /// </remarks>
    internal Vector3 FrameOrigin { get; set; }

    /// <summary>Whether the atlas has been copied at least once, which is when a caster may sample it.</summary>
    /// <remarks>
    ///     The caster node's guard: its pass records before this renderer's own Upload pass runs,
    ///     so on the frame a renderer is built the heightmap is still <c>Undefined</c> — a caster
    ///     that drew from it would be a validation error and a flat shadow at once. One frame of
    ///     caster latency after construction is the price of the ordering, and it is paid once.
    /// </remarks>
    internal bool Uploaded => uploaded;

    /// <summary>How many quads the shared grid patch spans — the caster's record arithmetic needs it.</summary>
    internal int GridQuadCount => gridQuads;

    /// <summary>How deep the atlas's mip chain is, which clamps the caster's coarse level.</summary>
    internal int AtlasLevels => atlas.LevelCount;

    /// <summary>The shared patch's index buffer, which the caster draws from too.</summary>
    internal BufferHandle SharedIndices => indices;

    /// <summary>And how many indices one patch is.</summary>
    internal int SharedIndexCount => indexCount;

    /// <summary>Fills a <c>TerrainCaster</c> block: a cascade's matrix over this terrain's geometry.</summary>
    /// <remarks>
    ///     Here rather than in <see cref="TerrainCasterPass" /> because every scalar below the
    ///     matrix is this renderer's private atlas arithmetic — the caster samples the same
    ///     heightmap through the same tile-block transform, and two derivations of that transform
    ///     is how a shadow reads a block its surface was not drawn from.
    /// </remarks>
    internal void WriteCasterConstants(byte[] block, in Matrix4x4 viewProjection) {
        var description = Terrain.Description;

        new TerrainCasterConstants {
            ViewProjection = viewProjection,
            HeightMapSize = new(atlas.Width, atlas.Height),
            TileSamples = atlas.TileSamples,
            TileQuads = atlas.TileQuads,
            AtlasTiles = new(atlas.TilesX, atlas.TilesZ),
            HeightRange = new(description.MinHeight, description.MaxHeight),
            MetresPerQuad = description.MetresPerQuad
        }.Write(block);
    }

    /// <summary>Writes a caster descriptor set: the caster's own buffers over this renderer's textures.</summary>
    /// <remarks>
    ///     The whole set, not the two textures the caster reads — <c>TerrainCaster</c> inherits
    ///     every binding <c>TerrainBase</c> declares, and a set is written wholly or not at all.
    ///     The splat's slots get the same defaults <see cref="Resize" /> writes, because a binding
    ///     the shader never samples still has to hold something a driver can validate.
    /// </remarks>
    internal void WriteCasterSet(DescriptorSetHandle set, BufferHandle block, BufferHandle casterNodes, long nodesOffset, long nodesSize) {
        device.UpdateDescriptorSet(
            set,
            [
                DescriptorWrite.Uniform(TerrainCasterKeys.ConstantBufferBinding, block),
                DescriptorWrite.Texture(TerrainCasterKeys.HeightMapBinding, heightView),
                DescriptorWrite.Texture(TerrainCasterKeys.HoleMapBinding, holeView),
                DescriptorWrite.SamplerAt(TerrainCasterKeys.HoleSamplerBinding, holeSampler),
                DescriptorWrite.SamplerAt(TerrainCasterKeys.HeightSamplerBinding, heightSampler),
                DescriptorWrite.SamplerAt(TerrainCasterKeys.WeightSamplerBinding, weightSampler),
                DescriptorWrite.SamplerAt(TerrainCasterKeys.LayerSamplerBinding, layerSampler),
                DescriptorWrite.Storage(TerrainCasterKeys.NodesBinding, casterNodes, nodesOffset, nodesSize),
                DescriptorWrite.Storage(TerrainCasterKeys.LayerScalesBinding, layerScales),
                DescriptorWrite.Storage(TerrainCasterKeys.LayerBlendsBinding, layerBlends),
                .. Enumerable.Range(0, MaxWeightMaps)
                    .Select(map => DescriptorWrite.Texture(TerrainCasterKeys.WeightMapsBinding, weightViews[map], map)),
                .. Enumerable.Range(0, MaxLayers)
                    .Select(slot => DescriptorWrite.Texture(TerrainCasterKeys.LayerMapsBinding, defaultAlbedoView, slot)),
                .. Enumerable.Range(0, MaxLayers)
                    .Select(slot => DescriptorWrite.Texture(TerrainCasterKeys.SurfaceMapsBinding, defaultSurfaceView, slot))
            ]
        );
    }

    /// <summary>Fills a <c>TerrainVelocity</c> block: this frame's placed matrix and last frame's.</summary>
    /// <remarks>
    ///     Here rather than in <see cref="TerrainVelocityPass" /> for <see cref="WriteCasterConstants" />'s
    ///     reason: every scalar below the matrices is this renderer's private atlas arithmetic, and a
    ///     second derivation of the tile-block transform is how a velocity pass reads a block its
    ///     surface was not drawn from.
    /// </remarks>
    internal void WriteVelocityConstants(byte[] block, in Matrix4x4 viewProjection, in Matrix4x4 previousViewProjection) {
        var description = Terrain.Description;

        new TerrainVelocityConstants {
            PreviousViewProjection = previousViewProjection,
            ViewProjection = viewProjection,
            HeightMapSize = new(atlas.Width, atlas.Height),
            TileSamples = atlas.TileSamples,
            TileQuads = atlas.TileQuads,
            AtlasTiles = new(atlas.TilesX, atlas.TilesZ),
            HeightRange = new(description.MinHeight, description.MaxHeight),
            MetresPerQuad = description.MetresPerQuad
        }.Write(block);
    }

    /// <summary>Writes a velocity descriptor set: the pass's block over this renderer's textures and
    ///     this frame's own node records.</summary>
    /// <remarks>
    ///     The whole set, on <see cref="WriteCasterSet" />'s terms — <c>TerrainVelocity</c> inherits
    ///     every binding <c>TerrainBase</c> declares, and a set is written wholly or not at all.
    ///     Unlike the caster's this binds the surface's <em>current</em> node slot: the velocity pass
    ///     runs after this renderer's own upload, so the records are this frame's camera selection —
    ///     the same patches, the same morph, and therefore the same depths the depth test compares.
    /// </remarks>
    internal void WriteVelocitySet(DescriptorSetHandle set, BufferHandle block) {
        device.UpdateDescriptorSet(
            set,
            [
                DescriptorWrite.Uniform(TerrainVelocityKeys.ConstantBufferBinding, block),
                DescriptorWrite.Texture(TerrainVelocityKeys.HeightMapBinding, heightView),
                DescriptorWrite.Texture(TerrainVelocityKeys.HoleMapBinding, holeView),
                DescriptorWrite.SamplerAt(TerrainVelocityKeys.HoleSamplerBinding, holeSampler),
                DescriptorWrite.SamplerAt(TerrainVelocityKeys.HeightSamplerBinding, heightSampler),
                DescriptorWrite.SamplerAt(TerrainVelocityKeys.WeightSamplerBinding, weightSampler),
                DescriptorWrite.SamplerAt(TerrainVelocityKeys.LayerSamplerBinding, layerSampler),
                DescriptorWrite.Storage(TerrainVelocityKeys.NodesBinding, nodes, slot * nodeCapacity, nodeCapacity),
                DescriptorWrite.Storage(TerrainVelocityKeys.LayerScalesBinding, layerScales),
                DescriptorWrite.Storage(TerrainVelocityKeys.LayerBlendsBinding, layerBlends),
                .. Enumerable.Range(0, MaxWeightMaps)
                    .Select(map => DescriptorWrite.Texture(TerrainVelocityKeys.WeightMapsBinding, weightViews[map], map)),
                .. Enumerable.Range(0, MaxLayers)
                    .Select(slot => DescriptorWrite.Texture(TerrainVelocityKeys.LayerMapsBinding, defaultAlbedoView, slot)),
                .. Enumerable.Range(0, MaxLayers)
                    .Select(slot => DescriptorWrite.Texture(TerrainVelocityKeys.SurfaceMapsBinding, defaultSurfaceView, slot))
            ]
        );
    }

    /// <summary>Fills the lit shader's block: the frame's lighting, then the base surface values.</summary>
    void WriteLitBlock(byte[] block, in TerrainView view) {
        var description = Terrain.Description;
        var frame = Frame;

        new TerrainLitConstants {
            OriginWS = FrameOrigin,
            ShadowConstantBias = frame?.ShadowConstantBias ?? 0f,
            LightDirection = frame?.LightDirection ?? Vector3.Zero,
            ShadowSlopeBias = frame?.ShadowSlopeBias ?? 0f,
            LightColor = frame?.LightColor ?? Vector3.Zero,
            ShadowFadeRange = frame?.ShadowFadeRange ?? 1f,
            ViewPosition = frame?.ViewPosition ?? Vector3.Zero,
            AmbientIntensity = frame?.AmbientIntensity ?? 0f,
            View = frame?.View ?? Matrix4x4.Identity,
            EnvironmentShL00 = frame?.EnvironmentSh.L00 ?? Vector3.Zero,
            EnvironmentShL1m1 = frame?.EnvironmentSh.L1m1 ?? Vector3.Zero,
            EnvironmentShL10 = frame?.EnvironmentSh.L10 ?? Vector3.Zero,
            EnvironmentShL11 = frame?.EnvironmentSh.L11 ?? Vector3.Zero,
            EnvironmentShL2m2 = frame?.EnvironmentSh.L2m2 ?? Vector3.Zero,
            EnvironmentShL2m1 = frame?.EnvironmentSh.L2m1 ?? Vector3.Zero,
            EnvironmentShL20 = frame?.EnvironmentSh.L20 ?? Vector3.Zero,
            EnvironmentShL21 = frame?.EnvironmentSh.L21 ?? Vector3.Zero,
            EnvironmentShL22 = frame?.EnvironmentSh.L22 ?? Vector3.Zero,
            ShadowTexelSize = frame?.ShadowTexelSize ?? new Vector2(1f, 1f),
            TanHalfFov = frame?.TanHalfFov ?? new Vector2(1f, 0.5625f),
            NearPlane = frame?.NearPlane ?? 0.1f,
            FarPlane = frame?.FarPlane ?? 1000f,
            ViewProjection = view.ViewProjection,
            HeightMapSize = new(atlas.Width, atlas.Height),
            TileSamples = atlas.TileSamples,
            TileQuads = atlas.TileQuads,
            AtlasTiles = new(atlas.TilesX, atlas.TilesZ),
            HeightRange = new(description.MinHeight, description.MaxHeight),
            MetresPerQuad = description.MetresPerQuad
        }.Write(block);

        if (frame is null) {
            return;
        }

        for (var cascade = 0; cascade < TerrainLitCascadesElement.Count; cascade++) {
            frame.Cascades[cascade].Write(block, cascade);
        }
    }

    /// <summary>Points this slot's set at the frame's atlas and cluster buffers.</summary>
    /// <remarks>
    ///     Per upload rather than once, because the handles are the frame's: the atlas is a graph
    ///     texture whose placement can move, and the light buffer grows with the scene. The spare
    ///     buffer stands in wherever the frame published nothing — the unclustered variant reads
    ///     neither, and a set is written wholly or not at all.
    /// </remarks>
    void BindFrameResources() {
        var atlasView = Frame is { ShadowAtlas.IsValid: true } present ? present.ShadowAtlas : defaultAlbedoView;
        var lights = Frame is { LightBuffer.IsValid: true } scene ? scene.LightBuffer : spareBuffer;
        var lists = Frame is { Clusters.IsValid: true } culled ? culled.Clusters : spareBuffer;

        device.UpdateDescriptorSet(
            descriptors[slot],
            [
                DescriptorWrite.Texture(TerrainLitKeys.ShadowMapBinding, atlasView),
                DescriptorWrite.SamplerAt(TerrainLitKeys.ShadowSamplerBinding, shadowSampler),
                DescriptorWrite.Storage(TerrainLitKeys.LightBufferBinding, lights),
                DescriptorWrite.Storage(TerrainLitKeys.ClustersBinding, lists)
            ]
        );
    }

    /// <summary>Draws this frame's patches.</summary>
    /// <param name="commands">Where to record the draw.</param>
    /// <remarks>
    ///     <b>One indexed instanced call.</b> Every patch is the same lattice and differs only in its
    ///     record, which the vertex stage reaches through <c>SV_InstanceID</c> — so a terrain of four
    ///     hundred patches is one draw and one descriptor set.
    /// </remarks>
    public void Record(ICommandList commands) {
        ArgumentNullException.ThrowIfNull(commands);
        ObjectDisposedException.ThrowIf(disposed, this);

        Draws = 0;

        if (PatchCount == 0) {
            return;
        }

        commands.BindPipeline(pipeline);
        commands.BindDescriptorSet(DescriptorSetSlot.PerMaterial, descriptors[slot]);
        commands.BindIndexBuffer(indices, IndexFormat.UInt32);
        commands.DrawIndexed(indexCount, PatchCount);

        Draws = 1;
    }

    /// <summary>Tells the streamer where this frame is looking, if there is one.</summary>
    void Stream(in TerrainView view) {
        if (Streaming is not { } streaming) {
            return;
        }

        if (StreamingSources.Count > 0) {
            streaming.Update(CollectionsMarshal.AsSpan(StreamingSources));

            return;
        }

        Span<StreamingSource> one = [new(view.Position, StreamingRadius)];

        streaming.Update(one);
    }

    /// <summary>Which tiles a recomposite dirtied.</summary>
    HashSet<(int X, int Z)> DirtyTiles() {
        var description = Terrain.Description;
        var dirty = new HashSet<(int, int)>();

        for (var tileZ = 0; tileZ < description.TilesZ; tileZ++) {
            for (var tileX = 0; tileX < description.TilesX; tileX++) {
                if (Terrain.IsTileDirty(tileX, tileZ)) {
                    dirty.Add((tileX, tileZ));
                }
            }
        }

        return dirty;
    }

    /// <summary>Stages and copies one tile's whole height chain into its block of the atlas.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The block, not a band of rows — which is the whole of what the split buys.</b> The
    ///         packed layout made a rectangle's copy a full-width band, because a buffer-to-texture
    ///         copy reads its source with one row pitch and a narrow rectangle would need one copy per
    ///         row. A block is contiguous by construction, so a stroke on one tile of a hundred moves
    ///         one hundredth of the bytes instead of a band across the whole world.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every level, not only level 0.</b> A chain whose top is fresh and whose tail is a
    ///         frame old draws the sculpted tile correctly up close and as it was from a distance —
    ///         which reads as the edit not having taken, until you walk towards it.
    ///     </para>
    /// </remarks>
    /// <param name="commands">Where to record the copies.</param>
    /// <param name="tileX">The tile's X index.</param>
    /// <param name="tileZ">Its Z index.</param>
    /// <param name="from">
    ///     The finest level to copy. Zero is the whole chain; a streamer's coarse floor is the pinned
    ///     tail, which every tile in the world can afford.
    /// </param>
    void CopyTileHeights(ICommandList commands, int tileX, int tileZ, int from = 0) {
        var written = TerrainMips.Build(Terrain, tileX, tileZ, chain);

        CopyChain(commands, tileX, tileZ, MemoryMarshal.AsBytes(chain.AsSpan(0, (int)written)), from, atlas.LevelCount);
    }

    /// <summary>Writes bytes into this frame's staging slot and answers the buffer and offset a copy reads.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A recorded copy reads its source at submit, not at record.</b> The offsets are
    ///         what keep this frame's copies apart, and the slot ring is what keeps this frame's
    ///         writes off a previous frame's in-flight reads — <c>Write</c> is an immediate memcpy,
    ///         and overwriting bytes a recorded copy has not yet read is the bug both exist to rule
    ///         out.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Growth swaps the buffer rather than moving it.</b> The copies already recorded
    ///         hold the old handle, whose destruction the device defers until the frames that could
    ///         read it have retired — so the old bytes stay put and only new writes land in the
    ///         replacement.
    ///     </para>
    /// </remarks>
    BufferHandle Stage(ReadOnlySpan<byte> bytes, out long offset) {
        var at = (stagingAt + StagingAlignment - 1) / StagingAlignment * StagingAlignment;

        if (!staging[slot].IsValid || at + bytes.Length > stagingCapacity[slot]) {
            if (staging[slot].IsValid) {
                device.Destroy(staging[slot]);
            }

            stagingCapacity[slot] = Math.Max(stagingGrain, Math.Max(stagingCapacity[slot] * 2, bytes.Length));

            staging[slot] = device.CreateBuffer(
                new(stagingCapacity[slot], BufferUsage.CopySource, MemoryAccess.HostUpload, "terrain staging")
            );

            at = 0;
        }

        device.Write(staging[slot], at, bytes);

        offset = at;
        stagingAt = at + bytes.Length;

        return staging[slot];
    }

    /// <summary>Copies a range of one tile's chain into its blocks of the atlas.</summary>
    /// <remarks>
    ///     ⚠ <b>The staged bytes are the whole chain and the copies are a slice of it, which is why
    ///     the offset is accumulated from level zero rather than from <paramref name="from" />.</b> A
    ///     chain packs level after level, so the byte address of level 2 depends on the sizes of 0 and
    ///     1 whether or not they are being copied — and starting the running offset at the first
    ///     copied level puts every level's texels at the address of a coarser one.
    /// </remarks>
    void CopyChain(ICommandList commands, int tileX, int tileZ, ReadOnlySpan<byte> chainBytes, int from, int to) {
        if (from >= to) {
            return;
        }

        var source = Stage(chainBytes, out var staged);

        var at = 0L;
        var copied = 0L;

        for (var level = 0; level < atlas.LevelCount; level++) {
            var block = atlas.BlockOf(tileX, tileZ, level);
            var texels = (long)block.Width * block.Height;

            if (level >= from && level < to) {
                commands.CopyBufferToTexture(
                    source,
                    staged + (at * sizeof(ushort)),
                    new(heightMap, level, Origin: new(block.X, block.Z, 0)),
                    new(block.Width, block.Height, 1)
                );

                copied += texels;
            }

            at += texels;
        }

        UploadedBytes += copied * sizeof(ushort);
    }

    /// <summary>And its hole mask, one texel per sample.</summary>
    /// <remarks>
    ///     ⚠ <b>The <em>sample</em> mask rather than the quad mask, and the two are different
    ///     questions.</b> <see cref="TerrainHoles.IsQuadMissing" /> asks whether the quad between four
    ///     samples has no surface, which is what the collider and an index buffer want; a fragment is
    ///     inside a quad and asks about the sample it is nearest. Uploading the quad answer would make
    ///     every hole one sample too small on two of its four sides.
    /// </remarks>
    void CopyTileHoles(ICommandList commands, int tileX, int tileZ) {
        var rect = Terrain.Description.SamplesOf(tileX, tileZ);
        var samples = Terrain.Description.TileSamples;
        var at = 0;

        for (var z = 0; z < samples; z++) {
            for (var x = 0; x < samples; x++) {
                holeBlock[at++] = Terrain.Holes.IsHole(rect.X + x, rect.Z + z) ? (byte)255 : (byte)0;
            }
        }

        var source = Stage(holeBlock, out var staged);
        var block = atlas.BlockOf(tileX, tileZ);

        commands.CopyBufferToTexture(
            source,
            staged,
            new(holeMap, Origin: new(block.X, block.Z, 0)),
            new(block.Width, block.Height, 1)
        );

        UploadedBytes += holeBlock.Length;
    }

    /// <summary>And its packed layer weights, into every weightmap the layout declares.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Every weightmap, whatever the terrain has today.</b> A terrain with five layers
    ///         has two weightmaps and fourteen empty channels; leaving the second unwritten leaves
    ///         whatever the driver allocated in the channels the splat loop skips — which is fine
    ///         right up until a sixth layer makes one of them live.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Weights reduce by the <em>average</em> and heights by the <em>maximum</em>, and
    ///         this is the one file where both appear.</b> A maximum on a weight makes every layer
    ///         cover everything one level up, so a distant terrain is every texture at once; an
    ///         average on a height sinks a ridge, because four samples of which one is a peak average
    ///         to a quarter of it. The two quantities want opposite reductions and neither is a
    ///         default.
    ///     </para>
    /// </remarks>
    void CopyTileWeights(ICommandList commands, int tileX, int tileZ) {
        if (Terrain.Weights.LayerCount == 0) {
            return;
        }

        var samples = Terrain.Description.TileSamples;
        var rect = Terrain.Description.SamplesOf(tileX, tileZ);
        var channels = TerrainSplat.LayersPerWeightMap;

        for (var map = 0; map < MaxWeightMaps; map++) {
            TerrainSplat.Pack(Terrain.Weights, map, rect, weightChain);

            var at = (long)samples * samples;
            var parentAt = 0L;
            var parentSize = samples;

            for (var level = 1; level < atlas.LevelCount; level++) {
                var childSize = atlas.BlockSizeAt(level);

                Average(weightChain, (int)parentAt, parentSize, (int)at, childSize, channels);

                parentAt = at;
                parentSize = childSize;
                at += (long)childSize * childSize;
            }

            var source = Stage(weightChain.AsSpan(0, (int)at * channels), out var staged);
            var offset = 0L;

            for (var level = 0; level < atlas.LevelCount; level++) {
                var block = atlas.BlockOf(tileX, tileZ, level);

                commands.CopyBufferToTexture(
                    source,
                    staged + (offset * channels),
                    new(weightMaps[map], level, Origin: new(block.X, block.Z, 0)),
                    new(block.Width, block.Height, 1)
                );

                offset += (long)block.Width * block.Height;
            }

            UploadedBytes += at * channels;
        }
    }

    /// <summary>Reduces one level of packed weights onto the next, by average over four texels.</summary>
    /// <remarks>
    ///     ⚠ <b>The window is clamped rather than assumed to be two by two</b>, for
    ///     <see cref="TerrainMips" />'s reason: a level of an odd size has a last row whose parent is
    ///     one texel, and reading past it takes the first texel of the next row — which puts the far
    ///     edge of a block into its near one.
    /// </remarks>
    static void Average(byte[] chain, int parentAt, int parentSize, int childAt, int childSize, int channels) {
        for (var z = 0; z < childSize; z++) {
            for (var x = 0; x < childSize; x++) {
                var x0 = Math.Min(x * 2, parentSize - 1);
                var z0 = Math.Min(z * 2, parentSize - 1);
                var x1 = Math.Min(x0 + 1, parentSize - 1);
                var z1 = Math.Min(z0 + 1, parentSize - 1);

                for (var channel = 0; channel < channels; channel++) {
                    var total =
                        chain[((parentAt + (z0 * parentSize) + x0) * channels) + channel]
                        + chain[((parentAt + (z0 * parentSize) + x1) * channels) + channel]
                        + chain[((parentAt + (z1 * parentSize) + x0) * channels) + channel]
                        + chain[((parentAt + (z1 * parentSize) + x1) * channels) + channel];

                    chain[((childAt + (z * childSize) + x) * channels) + channel] = (byte)(total / 4);
                }
            }
        }
    }

    /// <summary>Resolves every layer slot's textures, rebinding the sets only when one changed.</summary>
    /// <remarks>
    ///     ⚠ <b>Rebinding is what costs, so the comparison is what avoids it.</b> Updating a
    ///     descriptor set is a driver call per write and there are thirty-two of them here; doing it
    ///     every frame for a set of textures that changes when somebody assigns one in a panel would
    ///     be sixty of those a second to answer a question whose answer is almost always "the same".
    /// </remarks>
    void ResolveLayerTextures() {
        ResolvedTextures = 0;

        var changed = false;

        for (var slot = 0; slot < MaxLayers; slot++) {
            var albedo = defaultAlbedoView;
            var surface = defaultSurfaceView;

            if (slot < Terrain.Weights.LayerCount && Textures is { } source) {
                var layer = Terrain.Weights.LayerOf(slot);

                if (layer.Albedo.Length > 0 && source.Resolve(layer.Albedo) is { IsValid: true } resolved) {
                    albedo = resolved;
                    ResolvedTextures++;
                }

                if (layer.Surface.Length > 0 && source.Resolve(layer.Surface) is { IsValid: true } packed) {
                    surface = packed;
                    ResolvedTextures++;
                }
            }

            changed |= layerViews[slot] != albedo || surfaceViews[slot] != surface;

            layerViews[slot] = albedo;
            surfaceViews[slot] = surface;
        }

        if (changed) {
            BindLayerTextures();
        }
    }

    /// <summary>Writes the layer and surface arrays into every frame's descriptor set.</summary>
    void BindLayerTextures() {
        foreach (var set in descriptors) {
            if (!set.IsValid) {
                continue;
            }

            device.UpdateDescriptorSet(
                set,
                [
                    .. Enumerable.Range(0, MaxLayers)
                        .Select(slot => DescriptorWrite.Texture(layerMapsBinding, layerViews[slot], slot)),
                    .. Enumerable.Range(0, MaxLayers)
                        .Select(slot => DescriptorWrite.Texture(surfaceMapsBinding, surfaceViews[slot], slot))
                ]
            );
        }
    }

    /// <summary>Moves what this frame copies into the copy state, in one barrier.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ These textures are named into a descriptor set rather than read through the render
    ///         graph, so nothing else in the frame transitions them and this has to — the same
    ///         obligation <c>IrradianceFieldTexture</c> records. A texture copied into and
    ///         left in the copy state is a validation error at the draw that samples it, and one
    ///         never transitioned at all is one at the copy.
    ///     </para>
    ///     <para>
    ///         ⚠ The first frame moves <em>every</em> texture out of Undefined, copied into or not:
    ///         a terrain with no layers records no weight copies, but its weightmaps are still bound
    ///         and sampled, and a sampled texture left Undefined fails at the draw rather than here.
    ///         After that first frame everything rests in ShaderRead between Uploads.
    ///     </para>
    /// </remarks>
    void BeginCopies(ICommandList commands, bool heights, bool holes, bool weights) {
        var settled = uploaded ? ResourceState.ShaderRead : ResourceState.Undefined;

        Span<TextureBarrier> barriers = stackalloc TextureBarrier[4 + MaxWeightMaps];
        var count = 0;

        if (heights) {
            barriers[count++] = new(heightMap, settled, ResourceState.CopyDestination);
        }

        if (holes) {
            barriers[count++] = new(holeMap, settled, ResourceState.CopyDestination);
        }

        for (var map = 0; map < MaxWeightMaps; map++) {
            if (weights) {
                barriers[count++] = new(weightMaps[map], settled, ResourceState.CopyDestination);
            } else if (!uploaded) {
                barriers[count++] = new(weightMaps[map], ResourceState.Undefined, ResourceState.ShaderRead);
            }
        }

        if (!uploaded) {
            barriers[count++] = new(defaultAlbedo, ResourceState.Undefined, ResourceState.CopyDestination);
            barriers[count++] = new(defaultSurface, ResourceState.Undefined, ResourceState.CopyDestination);
        }

        if (count > 0) {
            commands.Barrier(new([], barriers[..count]));
        }
    }

    /// <summary>And hands them back to the samplers once the frame's copies are recorded.</summary>
    void EndCopies(ICommandList commands, bool heights, bool holes, bool weights) {
        Span<TextureBarrier> barriers = stackalloc TextureBarrier[4 + MaxWeightMaps];
        var count = 0;

        if (heights) {
            barriers[count++] = new(heightMap, ResourceState.CopyDestination, ResourceState.ShaderRead);
        }

        if (holes) {
            barriers[count++] = new(holeMap, ResourceState.CopyDestination, ResourceState.ShaderRead);
        }

        if (weights) {
            for (var map = 0; map < MaxWeightMaps; map++) {
                barriers[count++] = new(weightMaps[map], ResourceState.CopyDestination, ResourceState.ShaderRead);
            }
        }

        if (!uploaded) {
            barriers[count++] = new(defaultAlbedo, ResourceState.CopyDestination, ResourceState.ShaderRead);
            barriers[count++] = new(defaultSurface, ResourceState.CopyDestination, ResourceState.ShaderRead);
        }

        if (count > 0) {
            commands.Barrier(new([], barriers[..count]));
        }
    }

    /// <summary>Fills the two default textures, once, on the first frame that records anything.</summary>
    /// <remarks>
    ///     ⚠ <b>A copy and not a write, because a sampled texture cannot be host-written.</b> It needs
    ///     a command list, which the constructor does not have — the same reason the index buffer is
    ///     host-upload memory rather than staged.
    /// </remarks>
    void CopyDefaults(ICommandList commands) {
        commands.CopyBufferToTexture(defaultStaging, 0, new(defaultAlbedo), new(1, 1, 1));
        commands.CopyBufferToTexture(defaultStaging, 4, new(defaultSurface), new(1, 1, 1));
    }

    /// <summary>Writes the per-layer tiling and blend the fragment stage reads.</summary>
    /// <remarks>
    ///     Once per change of the layer list rather than per frame: a tiling is edited in a panel and
    ///     a blend mode is a dropdown, so uploading them every frame would be a copy per frame to
    ///     save a comparison.
    /// </remarks>
    void WriteLayerConstants() {
        var scales = new float[MaxLayers];
        var blends = new Vector2[MaxLayers];

        splat.FillScales(Terrain.Weights, scales);
        splat.FillBlends(Terrain.Weights, blends);

        // Past the slots the material loops over, so a buffer read out of an unrolled branch finds a
        // number rather than whatever was there.
        for (var slot = splat.LayerSlots; slot < MaxLayers; slot++) {
            scales[slot] = 1f;
            blends[slot] = new(0f, 1f);
        }

        device.Write(layerScales, 0, MemoryMarshal.AsBytes<float>(scales));
        device.Write(layerBlends, 0, MemoryMarshal.AsBytes<Vector2>(blends));
    }

    void Resize(int patches) {
        if (nodes.IsValid) {
            device.Destroy(nodes);
        }

        nodeCapacity = (long)Math.Max(patches, 256) * TerrainNodeRecord.SizeInBytes;

        nodes = device.CreateBuffer(
            new(
                nodeCapacity * slots,
                BufferUsage.Storage,
                MemoryAccess.HostUpload,
                "terrain patches"
            )
        );

        for (var index = 0; index < slots; index++) {
            if (!descriptors[index].IsValid) {
                descriptors[index] = device.CreateDescriptorSet(setLayout, $"terrain {index}");
            }

            List<DescriptorWrite> writes = [
                DescriptorWrite.Uniform(constantBinding, constants[index]),
                DescriptorWrite.Texture(heightMapBinding, heightView),
                DescriptorWrite.Texture(holeMapBinding, holeView),
                DescriptorWrite.SamplerAt(holeSamplerBinding, holeSampler),
                DescriptorWrite.SamplerAt(heightSamplerBinding, heightSampler),
                DescriptorWrite.SamplerAt(weightSamplerBinding, weightSampler),
                DescriptorWrite.SamplerAt(layerSamplerBinding, layerSampler),
                DescriptorWrite.Storage(nodesBinding, nodes, index * nodeCapacity, nodeCapacity),
                DescriptorWrite.Storage(layerScalesBinding, layerScales),
                DescriptorWrite.Storage(layerBlendsBinding, layerBlends),
                .. Enumerable.Range(0, MaxWeightMaps)
                    .Select(map => DescriptorWrite.Texture(weightMapsBinding, weightViews[map], map))
            ];

            if (lit) {
                // Written valid rather than left for the first Upload: a freshly grown set is bound
                // the same frame, and a set with an unwritten binding the pipeline statically uses
                // is undefined on one driver and a validation error on another. Upload's
                // BindFrameResources overwrites these with the frame's handles before any draw.
                writes.Add(DescriptorWrite.Texture(TerrainLitKeys.ShadowMapBinding, defaultAlbedoView));
                writes.Add(DescriptorWrite.SamplerAt(TerrainLitKeys.ShadowSamplerBinding, shadowSampler));
                writes.Add(DescriptorWrite.Storage(TerrainLitKeys.LightBufferBinding, spareBuffer));
                writes.Add(DescriptorWrite.Storage(TerrainLitKeys.ClustersBinding, spareBuffer));
            }

            device.UpdateDescriptorSet(descriptors[index], CollectionsMarshal.AsSpan(writes));
        }

        // A resized set is a new set, so whatever the layers resolved to has to be written into it —
        // otherwise growing the patch buffer silently reverts every layer to its default.
        BindLayerTextures();
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        foreach (var set in descriptors) {
            if (set.IsValid) {
                device.Destroy(set);
            }
        }

        if (shadowSampler.IsValid) {
            device.Destroy(shadowSampler);
        }

        if (spareBuffer.IsValid) {
            device.Destroy(spareBuffer);
        }

        device.Destroy(pipeline);
        device.Destroy(layout);
        device.Destroy(setLayout);
        device.Destroy(emptySetLayout);
        device.Destroy(layerSampler);
        device.Destroy(weightSampler);
        device.Destroy(heightSampler);
        device.Destroy(heightView);
        device.Destroy(heightMap);

        foreach (var buffer in staging) {
            if (buffer.IsValid) {
                device.Destroy(buffer);
            }
        }

        device.Destroy(holeSampler);
        device.Destroy(holeView);
        device.Destroy(holeMap);
        device.Destroy(defaultStaging);
        device.Destroy(defaultSurfaceView);
        device.Destroy(defaultSurface);
        device.Destroy(defaultAlbedoView);
        device.Destroy(defaultAlbedo);

        for (var map = 0; map < MaxWeightMaps; map++) {
            device.Destroy(weightViews[map]);
            device.Destroy(weightMaps[map]);
        }

        device.Destroy(layerBlends);
        device.Destroy(layerScales);

        foreach (var buffer in constants) {
            device.Destroy(buffer);
        }

        device.Destroy(nodes);
        device.Destroy(indices);
    }

}
