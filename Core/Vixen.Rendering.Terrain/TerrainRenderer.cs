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
///         ⚠ <b>One heightmap for the whole terrain, not one per tile — and the plan said per
///         tile.</b> Per-tile textures exist for <em>streaming</em>: a tile is the unit of load,
///         which is [§ D13]'s whole argument. Drawing wants the opposite, because a patch straddles
///         no tile boundary only by luck and a per-tile heightmap makes every straddling patch either
///         two draws or a shader that samples two textures. A 4 km² terrain at one metre is 4097²
///         samples, which is 33 MB in <c>R16UNorm</c> — a texture, not a problem. The per-tile split
///         belongs with the streaming that needs it, and is owed rather than done.
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
    readonly SamplerHandle heightSampler;
    readonly SamplerHandle weightSampler;
    readonly SamplerHandle layerSampler;
    readonly DescriptorSetLayoutHandle setLayout;
    readonly PipelineLayoutHandle layout;
    readonly PipelineHandle pipeline;

    readonly TextureHandle heightMap;
    readonly TextureViewHandle heightView;
    readonly BufferHandle staging;
    readonly DescriptorSetHandle[] descriptors;
    readonly List<TerrainLodNode> selected = [];

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

        heightMap = device.CreateTexture(
            new(
                PixelFormat.R16UNorm,
                description.SamplesX,
                description.SamplesZ,
                TextureUsage.Sampled | TextureUsage.CopyDestination,
                Name: "terrain heights"
            )
        );

        heightView = device.CreateTextureView(heightMap);

        staging = device.CreateBuffer(
            new(
                description.SampleCount * sizeof(ushort),
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

        setLayout = device.CreateDescriptorSetLayout(
            new(
                DescriptorSetSlot.PerMaterial,
                [
                    new(TerrainKeys.ConstantBufferBinding, DescriptorKind.UniformBuffer, ShaderStage.Vertex | ShaderStage.Fragment),
                    new(TerrainKeys.HeightMapBinding, DescriptorKind.SampledTexture, ShaderStage.Vertex | ShaderStage.Fragment),
                    new(TerrainKeys.WeightMapsBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment, MaxWeightMaps),
                    new(TerrainKeys.LayerMapsBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment, MaxLayers),
                    new(TerrainKeys.HeightSamplerBinding, DescriptorKind.Sampler, ShaderStage.Vertex | ShaderStage.Fragment),
                    new(TerrainKeys.WeightSamplerBinding, DescriptorKind.Sampler, ShaderStage.Fragment),
                    new(TerrainKeys.LayerSamplerBinding, DescriptorKind.Sampler, ShaderStage.Fragment),
                    new(TerrainKeys.NodesBinding, DescriptorKind.StorageBuffer, ShaderStage.Vertex),
                    new(TerrainKeys.LayerScalesBinding, DescriptorKind.StorageBuffer, ShaderStage.Fragment)
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
        // composite cannot disagree — and the tiles it dirtied are the rows worth copying.
        //
        // ⚠ The first frame copies everything whatever the dirty set says. A terrain built and then
        // resolved has no dirty tiles at all, so a renderer that only copied dirty rows would draw a
        // heightmap of zeros until somebody happened to sculpt — which reads as a flat terrain rather
        // than as a missing upload.
        var dirty = Rows();
        Terrain.Resolve();

        if (!uploaded) {
            uploaded = true;
            CopyHeights(commands, new(0, 0, Terrain.Description.SamplesX, Terrain.Description.SamplesZ));
        } else if (dirty.HasValue) {
            CopyHeights(commands, dirty.Value);
        }

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
                records[index] = TerrainNodeRecord.Of(selected[index], gridQuads);
            }

            device.Write(nodes, slot * nodeCapacity, MemoryMarshal.AsBytes<TerrainNodeRecord>(records));
        }

        var description = Terrain.Description;

        var block = new Constants(
            view.ViewProjection,
            new(description.SamplesX, description.SamplesZ),
            new(description.MinHeight, description.MaxHeight),
            description.MetresPerQuad,
            0f
        );

        device.Write(constants, 0, MemoryMarshal.AsBytes(new ReadOnlySpan<Constants>(in block)));

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

    /// <summary>The rows a recomposite dirtied, or null when every tile is clean.</summary>
    TerrainRect? Rows() {
        var description = Terrain.Description;
        var union = TerrainRect.Empty;

        for (var tileZ = 0; tileZ < description.TilesZ; tileZ++) {
            for (var tileX = 0; tileX < description.TilesX; tileX++) {
                if (Terrain.IsTileDirty(tileX, tileZ)) {
                    union = union.Union(description.SamplesOf(tileX, tileZ));
                }
            }
        }

        return union.IsEmpty ? null : union;
    }

    /// <summary>Stages and copies a rectangle of heights into the texture.</summary>
    /// <remarks>
    ///     ⚠ <b>Whole rows, even for a rectangle that is not.</b> A buffer-to-texture copy reads its
    ///     source with one row pitch, so a narrow rectangle would need either a repacked staging
    ///     buffer or one copy per row. Copying the full-width band the rectangle spans is one copy
    ///     over a few more bytes, and a stroke is a band of a few dozen rows out of thousands.
    /// </remarks>
    void CopyHeights(ICommandList commands, TerrainRect rect) {
        var description = Terrain.Description;
        var clipped = rect.Clip(new(0, 0, description.SamplesX, description.SamplesZ));

        if (clipped.IsEmpty) {
            return;
        }

        var width = description.SamplesX;
        var offset = (long)clipped.Z * width * sizeof(ushort);
        var bytes = (long)clipped.Height * width * sizeof(ushort);

        device.Write(
            staging,
            offset,
            MemoryMarshal.AsBytes(Terrain.Composite.Span.Slice(clipped.Z * width, clipped.Height * width))
        );

        commands.CopyBufferToTexture(
            staging,
            offset,
            new(heightMap, Origin: new(0, clipped.Z, 0)),
            new(width, clipped.Height, 1)
        );

        UploadedBytes = bytes;
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
                    DescriptorWrite.Storage(TerrainKeys.LayerScalesBinding, layerScales)
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
        device.Destroy(layerScales);
        device.Destroy(constants);
        device.Destroy(nodes);
        device.Destroy(indices);
    }

    /// <summary>What the shader's uniform block holds. The layout is the reflection's.</summary>
    [StructLayout(LayoutKind.Sequential)]
    readonly record struct Constants(
        Matrix4x4 ViewProjection,
        Vector2 HeightMapSize,
        Vector2 HeightRange,
        float MetresPerQuad,
        float Padding
    );
}
