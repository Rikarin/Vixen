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

        setLayout = device.CreateDescriptorSetLayout(
            new(
                DescriptorSetSlot.PerMaterial,
                [
                    new(TerrainKeys.ConstantBufferBinding, DescriptorKind.UniformBuffer, ShaderStage.Vertex | ShaderStage.Fragment),
                    new(TerrainKeys.HeightMapBinding, DescriptorKind.SampledTexture, ShaderStage.Vertex | ShaderStage.Fragment),
                    new(TerrainKeys.WeightMapsBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment, MaxWeightMaps),
                    new(TerrainKeys.LayerMapsBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment, MaxLayers),
                    new(TerrainKeys.SurfaceMapsBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment, MaxLayers),
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

        var description = Terrain.Description;

        for (var tileZ = 0; tileZ < description.TilesZ; tileZ++) {
            for (var tileX = 0; tileX < description.TilesX; tileX++) {
                var stale = !uploaded || dirty.Contains((tileX, tileZ));

                if (stale) {
                    CopyTileHeights(commands, tileX, tileZ);
                }

                if (stale || repaint) {
                    CopyTileWeights(commands, tileX, tileZ);
                }
            }
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
                records[index] = TerrainNodeRecord.Of(selected[index], gridQuads, atlas.LevelCount - 1);
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
    void CopyTileHeights(ICommandList commands, int tileX, int tileZ) {
        var written = TerrainMips.Build(Terrain, tileX, tileZ, chain);

        device.Write(staging, 0, MemoryMarshal.AsBytes(chain.AsSpan(0, (int)written)));

        var at = 0L;

        for (var level = 0; level < atlas.LevelCount; level++) {
            var block = atlas.BlockOf(tileX, tileZ, level);

            commands.CopyBufferToTexture(
                staging,
                at * sizeof(ushort),
                new(heightMap, level, Origin: new(block.X, block.Z, 0)),
                new(block.Width, block.Height, 1)
            );

            at += (long)block.Width * block.Height;
        }

        UploadedBytes += written * sizeof(ushort);
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
