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

    readonly BufferHandle indices;
    readonly BufferHandle constants;
    readonly BufferHandle layerScales;
    readonly BufferHandle layerBlends;
    readonly SamplerHandle heightSampler;
    readonly SamplerHandle weightSampler;
    readonly SamplerHandle layerSampler;
    readonly DescriptorSetLayoutHandle setLayout;
    readonly PipelineLayoutHandle layout;
    readonly PipelineHandle pipeline;

    readonly TerrainAtlas atlas;
    readonly TextureHandle heightMap;
    readonly TextureViewHandle heightView;
    readonly BufferHandle staging;
    readonly ushort[] chain;
    readonly byte[] weightChain;
    readonly TextureHandle[] weightMaps = new TextureHandle[MaxWeightMaps];
    readonly TextureViewHandle[] weightViews = new TextureViewHandle[MaxWeightMaps];
    readonly BufferHandle weightStaging;

    readonly TextureHandle holeMap;
    readonly TextureViewHandle holeView;
    readonly SamplerHandle holeSampler;
    readonly BufferHandle holeStaging;
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
    ) {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(terrain);

        if (!shaders.IsValid) {
            throw new ArgumentException("A terrain needs both a vertex and a fragment stage.", nameof(shaders));
        }

        this.device = device;
        this.gridQuads = gridQuads;

        Terrain = terrain;
        Tree = new(terrain.Description, ranges, gridQuads);
        slots = Math.Max(1, device.FramesInFlight);

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
        // on one tile of a hundred moves one hundredth of the bytes, and a staging buffer sized to
        // the whole atlas would be the megabytes that saves being allocated to avoid allocating them.
        chain = new ushort[TerrainMips.ChainSamples(description.TileSamples)];
        weightChain = new byte[chain.Length * TerrainSplat.LayersPerWeightMap];

        staging = device.CreateBuffer(
            new(
                (long)chain.Length * sizeof(ushort),
                BufferUsage.CopySource,
                MemoryAccess.HostUpload,
                "terrain height staging"
            )
        );

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

        constants = device.CreateBuffer(
            new(TerrainKeys.ConstantBufferSize, BufferUsage.Uniform, MemoryAccess.HostUpload, "terrain constants")
        );

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

        weightStaging = device.CreateBuffer(
            new(
                (long)weightChain.Length,
                BufferUsage.CopySource,
                MemoryAccess.HostUpload,
                "terrain weight staging"
            )
        );

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

        holeStaging = device.CreateBuffer(
            new(holeBlock.Length, BufferUsage.CopySource, MemoryAccess.HostUpload, "terrain hole staging")
        );

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

        setLayout = device.CreateDescriptorSetLayout(
            new(
                DescriptorSetSlot.PerMaterial,
                [
                    new(TerrainKeys.ConstantBufferBinding, DescriptorKind.UniformBuffer, ShaderStage.Vertex | ShaderStage.Fragment),
                    new(TerrainKeys.HeightMapBinding, DescriptorKind.SampledTexture, ShaderStage.Vertex | ShaderStage.Fragment),
                    new(TerrainKeys.WeightMapsBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment, MaxWeightMaps),
                    new(TerrainKeys.LayerMapsBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment, MaxLayers),
                    new(TerrainKeys.SurfaceMapsBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment, MaxLayers),
                    new(TerrainKeys.HoleMapBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment),
                    new(TerrainKeys.HoleSamplerBinding, DescriptorKind.Sampler, ShaderStage.Fragment),
                    new(TerrainKeys.HeightSamplerBinding, DescriptorKind.Sampler, ShaderStage.Vertex | ShaderStage.Fragment),
                    new(TerrainKeys.WeightSamplerBinding, DescriptorKind.Sampler, ShaderStage.Fragment),
                    new(TerrainKeys.LayerSamplerBinding, DescriptorKind.Sampler, ShaderStage.Fragment),
                    new(TerrainKeys.NodesBinding, DescriptorKind.StorageBuffer, ShaderStage.Vertex),
                    new(TerrainKeys.LayerScalesBinding, DescriptorKind.StorageBuffer, ShaderStage.Fragment),
                    new(TerrainKeys.LayerBlendsBinding, DescriptorKind.StorageBuffer, ShaderStage.Fragment)
                ],
                "terrain"
            )
        );

        layout = device.CreatePipelineLayout(new([setLayout], [], "terrain"));

        // No vertex buffers, which is the shader's own design: a lattice's positions are two
        // divisions of SV_VertexID, and Terrain.reflect.json's empty VertexInputs is the evidence.
        pipeline = device.CreateGraphicsPipeline(
            new(
                shaders.Vertex,
                shaders.Fragment,
                layout,
                [
                    new(
                        output.ColourCount > 0 ? output.ColourFormats[0] : PixelFormat.Rgba8UNorm,
                        BlendState.Opaque
                    )
                ],
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
        UploadedBytes = 0;

        // Any tile a stroke dirtied is recomposited before it is read, so the texture and the
        // composite cannot disagree — and the tiles it dirtied are the blocks worth copying.
        //
        // ⚠ The first frame copies everything whatever the dirty set says. A terrain built and then
        // resolved has no dirty tiles at all, so a renderer that only copied dirty tiles would draw a
        // heightmap of zeros until somebody happened to sculpt — which reads as a flat terrain rather
        // than as a missing upload.
        var dirty = DirtyTiles();

        if (!uploaded) {
            CopyDefaults(commands);
        }

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
        Streaming?.Pages.Drain((tileX, tileZ, chain) => CopyChain(commands, tileX, tileZ, chain, 0, floor));

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
        var block = new byte[TerrainKeys.ConstantBufferSize];

        new TerrainConstants {
            ViewProjection = view.ViewProjection,
            HeightMapSize = new(atlas.Width, atlas.Height),
            TileSamples = atlas.TileSamples,
            TileQuads = atlas.TileQuads,
            AtlasTiles = new(atlas.TilesX, atlas.TilesZ),
            HeightRange = new(description.MinHeight, description.MaxHeight),
            MetresPerQuad = description.MetresPerQuad
        }.Write(block);

        device.Write(constants, 0, block);

        return PatchCount;
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

        device.Write(staging, 0, chainBytes);

        var at = 0L;
        var copied = 0L;

        for (var level = 0; level < atlas.LevelCount; level++) {
            var block = atlas.BlockOf(tileX, tileZ, level);
            var texels = (long)block.Width * block.Height;

            if (level >= from && level < to) {
                commands.CopyBufferToTexture(
                    staging,
                    at * sizeof(ushort),
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

        device.Write(holeStaging, 0, holeBlock);

        var block = atlas.BlockOf(tileX, tileZ);

        commands.CopyBufferToTexture(
            holeStaging,
            0,
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

            device.Write(weightStaging, 0, weightChain.AsSpan(0, (int)at * channels));

            var offset = 0L;

            for (var level = 0; level < atlas.LevelCount; level++) {
                var block = atlas.BlockOf(tileX, tileZ, level);

                commands.CopyBufferToTexture(
                    weightStaging,
                    offset * channels,
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
                        .Select(slot => DescriptorWrite.Texture(TerrainKeys.LayerMapsBinding, layerViews[slot], slot)),
                    .. Enumerable.Range(0, MaxLayers)
                        .Select(slot => DescriptorWrite.Texture(TerrainKeys.SurfaceMapsBinding, surfaceViews[slot], slot))
                ]
            );
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

            device.UpdateDescriptorSet(
                descriptors[index],
                [
                    DescriptorWrite.Uniform(TerrainKeys.ConstantBufferBinding, constants),
                    DescriptorWrite.Texture(TerrainKeys.HeightMapBinding, heightView),
                    DescriptorWrite.Texture(TerrainKeys.HoleMapBinding, holeView),
                    DescriptorWrite.SamplerAt(TerrainKeys.HoleSamplerBinding, holeSampler),
                    DescriptorWrite.SamplerAt(TerrainKeys.HeightSamplerBinding, heightSampler),
                    DescriptorWrite.SamplerAt(TerrainKeys.WeightSamplerBinding, weightSampler),
                    DescriptorWrite.SamplerAt(TerrainKeys.LayerSamplerBinding, layerSampler),
                    DescriptorWrite.Storage(TerrainKeys.NodesBinding, nodes, index * nodeCapacity, nodeCapacity),
                    DescriptorWrite.Storage(TerrainKeys.LayerScalesBinding, layerScales),
                    DescriptorWrite.Storage(TerrainKeys.LayerBlendsBinding, layerBlends),
                    .. Enumerable.Range(0, MaxWeightMaps)
                        .Select(map => DescriptorWrite.Texture(TerrainKeys.WeightMapsBinding, weightViews[map], map))
                ]
            );
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

        device.Destroy(pipeline);
        device.Destroy(layout);
        device.Destroy(setLayout);
        device.Destroy(layerSampler);
        device.Destroy(weightSampler);
        device.Destroy(heightSampler);
        device.Destroy(heightView);
        device.Destroy(heightMap);
        device.Destroy(staging);
        device.Destroy(weightStaging);
        device.Destroy(holeStaging);
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
        device.Destroy(constants);
        device.Destroy(nodes);
        device.Destroy(indices);
    }

}
