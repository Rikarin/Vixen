// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Shaders.Generated;

namespace Vixen.Rendering.Terrain;

/// <summary>
///     Draws one terrain into the sun's cascade atlas: the same patch lattice, depth only.
/// </summary>
/// <remarks>
///     <para>
///         <b>The caster half of a <see cref="TerrainRenderer" />, not a second terrain.</b> The
///         heightmap, the hole mask, the samplers and the index buffer are all borrowed from the
///         surface renderer — a caster with its own copies would be a second 33 MB upload and a
///         second thing to invalidate per stroke. What is this pass's own is exactly what differs
///         per cascade: the constant block holding the cascade's matrix, and the descriptor set
///         that points at it.
///     </para>
///     <para>
///         <b>A coarse fixed tiling, not the camera's selection.</b> The camera's node set is culled
///         to the camera's frustum, and a hill behind the camera still throws its shadow across the
///         view — so the caster covers the <em>whole</em> terrain, at one uniform level chosen so
///         the patch count stays bounded. Uniform also means no morph and no cracks by
///         construction; a cascade's texels are metres across, and the morph exists for a boundary
///         no cascade can resolve. With a streamer the level is floored to the pinned coarse tail,
///         so every patch reads mips that are resident by construction.
///     </para>
///     <para>
///         ⚠ <b>No per-cascade culling, deliberately.</b> The tiling is at most
///         <see cref="MaxPatchesPerSide" />² patches and every cascade draws all of them under its
///         own scissor; culling those few instances per cascade would cost more bookkeeping than
///         the vertices it saves. The scissor is what keeps a patch out of a tile it does not
///         belong to — <c>ShadowMapRenderer.Pass</c>'s own argument.
///     </para>
/// </remarks>
sealed class TerrainCasterPass : IDisposable {
    /// <summary>The most patches the coarse tiling spans along one axis.</summary>
    /// <remarks>
    ///     Eight — sixty-four patches, ≈130 k triangles per cascade at the default grid — which is
    ///     the budget of a modest mesh caster. The level rises with the terrain, so a four-kilometre
    ///     world casts from the same patch count a meadow does, one mip coarser per doubling.
    /// </remarks>
    public const int MaxPatchesPerSide = 8;

    readonly IGraphicsDevice device;
    readonly TerrainRenderer surface;
    readonly int slots;
    readonly long nodeCapacity;
    readonly byte[] block = new byte[TerrainCasterKeys.ConstantBufferSize];
    readonly List<TerrainNodeRecord> records = [];

    readonly BufferHandle nodes;
    readonly BufferHandle[] constants;
    readonly DescriptorSetHandle[] descriptors;
    readonly DescriptorSetLayoutHandle setLayout;
    readonly DescriptorSetLayoutHandle emptySetLayout;
    readonly PipelineLayoutHandle layout;
    readonly PipelineHandle pipeline;

    int slot;
    bool disposed;

    /// <summary>The depth state the caster rasterises with — reverse-Z, exactly the surface's.</summary>
    /// <remarks>
    ///     <see cref="DepthStencilState.Default" />, which is <see cref="CompareFunction.Greater" />
    ///     with depth writes on: near is 1, far is 0, the atlas clears to 0 and a caster wins by
    ///     being nearer the light. A property rather than a literal at the pipeline so a test can
    ///     hold the convention without a device that renders.
    /// </remarks>
    internal static DepthStencilState DepthState => DepthStencilState.Default;

    /// <summary>And the raster state: back faces culled, zero raster bias, depth clamped.</summary>
    /// <remarks>
    ///     The caster stage's own convention, spelt out where <c>!StandardFrame</c> emits the
    ///     <c>Shadow</c> stage: front-culling records the far side of a wide caster, a slope-scaled
    ///     raster bias explodes edge-on to a low sun, and the biases that replace it live in the
    ///     sampling — <c>FrameShadow.Term</c> adds them in metres. Depth clamp so a hill in front
    ///     of the light's near plane still casts.
    /// </remarks>
    internal static RasterizerState Raster => new(CullMode.Back, DepthClamp: true);

    /// <summary>Builds the caster over one surface renderer's resources.</summary>
    /// <param name="device">The device.</param>
    /// <param name="surface">Whose heightmap, holes and index buffer this draws from.</param>
    /// <param name="shaders">The <c>TerrainCaster</c> stages.</param>
    /// <param name="depthFormat">The atlas's format.</param>
    public TerrainCasterPass(IGraphicsDevice device, TerrainRenderer surface, TerrainShaders shaders, PixelFormat depthFormat) {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(surface);

        if (!shaders.IsValid) {
            throw new ArgumentException("A terrain caster needs both a vertex and a fragment stage.", nameof(shaders));
        }

        this.device = device;
        this.surface = surface;

        // One upload per frame — the caster node runs once, not once per pane — so the ring is
        // exactly as deep as the frames that can be in flight.
        slots = Math.Max(1, device.FramesInFlight);
        nodeCapacity = (long)MaxPatchesPerSide * MaxPatchesPerSide * TerrainNodeRecord.SizeInBytes;

        nodes = device.CreateBuffer(
            new(
                nodeCapacity * slots,
                BufferUsage.Storage,
                MemoryAccess.HostUpload,
                "terrain caster patches"
            )
        );

        // A block per cascade per slot, because the block is the one thing that differs between
        // the tiles: the same records under four matrices.
        constants = new BufferHandle[slots * ShadowCascades.MaxCascades];

        for (var index = 0; index < constants.Length; index++) {
            constants[index] = device.CreateBuffer(
                new(
                    TerrainCasterKeys.ConstantBufferSize,
                    BufferUsage.Uniform,
                    MemoryAccess.HostUpload,
                    $"terrain caster constants {index}"
                )
            );
        }

        // The whole of TerrainBase's set, because TerrainCaster inherits every binding the base
        // declares — the splat's textures included, defaults behind the ones the caster never
        // samples. The indices are the caster's own generated keys; today they agree with the
        // preview's because the caster declares nothing of its own, and the day it does, these
        // move and the preview's must not.
        setLayout = device.CreateDescriptorSetLayout(
            new(
                DescriptorSetSlot.PerMaterial,
                [
                    new(TerrainCasterKeys.ConstantBufferBinding, DescriptorKind.UniformBuffer, ShaderStage.Vertex | ShaderStage.Fragment),
                    new(TerrainCasterKeys.HeightMapBinding, DescriptorKind.SampledTexture, ShaderStage.Vertex | ShaderStage.Fragment),
                    new(TerrainCasterKeys.WeightMapsBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment, TerrainRenderer.MaxWeightMaps),
                    new(TerrainCasterKeys.LayerMapsBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment, TerrainRenderer.MaxLayers),
                    new(TerrainCasterKeys.SurfaceMapsBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment, TerrainRenderer.MaxLayers),
                    new(TerrainCasterKeys.HoleMapBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment),
                    new(TerrainCasterKeys.HoleSamplerBinding, DescriptorKind.Sampler, ShaderStage.Fragment),
                    new(TerrainCasterKeys.HeightSamplerBinding, DescriptorKind.Sampler, ShaderStage.Vertex | ShaderStage.Fragment),
                    new(TerrainCasterKeys.WeightSamplerBinding, DescriptorKind.Sampler, ShaderStage.Fragment),
                    new(TerrainCasterKeys.LayerSamplerBinding, DescriptorKind.Sampler, ShaderStage.Fragment),
                    new(TerrainCasterKeys.NodesBinding, DescriptorKind.StorageBuffer, ShaderStage.Vertex),
                    new(TerrainCasterKeys.LayerScalesBinding, DescriptorKind.StorageBuffer, ShaderStage.Fragment),
                    new(TerrainCasterKeys.LayerBlendsBinding, DescriptorKind.StorageBuffer, ShaderStage.Fragment)
                ],
                "terrain caster"
            )
        );

        // Padded below the material set — TerrainRenderer's own layout argument, verbatim.
        emptySetLayout = device.CreateDescriptorSetLayout(new(DescriptorSetSlot.PerFrame, [], "terrain caster empty"));
        layout = device.CreatePipelineLayout(new([emptySetLayout, emptySetLayout, setLayout], [], "terrain caster"));

        // No colour targets at all: the pass writes depth and nothing else, and a bound colour
        // attachment would be bandwidth spent on a value nobody reads.
        pipeline = device.CreateGraphicsPipeline(
            new(
                shaders.Vertex,
                shaders.Fragment,
                layout,
                [],
                [],
                PrimitiveTopology.TriangleList,
                Raster with { DepthClamp = device.Features.HasDepthClamp },
                DepthState,
                depthFormat,
                1,
                "terrain caster"
            )
        );

        descriptors = new DescriptorSetHandle[slots * ShadowCascades.MaxCascades];

        for (var index = 0; index < descriptors.Length; index++) {
            descriptors[index] = device.CreateDescriptorSet(setLayout, $"terrain caster {index}");
        }
    }

    /// <summary>How many patches the coarse tiling holds.</summary>
    public int PatchCount { get; private set; }

    /// <summary>How many draws the last <see cref="Record" /> made — one, or zero with no patches.</summary>
    public int Draws { get; private set; }

    /// <summary>Stages one frame's casting: the records once, and a block per cascade.</summary>
    /// <param name="origin">Where the terrain's low corner is, in world space.</param>
    /// <param name="cascades">
    ///     Each cascade's own view-projection — <em>unfolded</em>, because the tile is the
    ///     viewport's business. The placement is folded in here, exactly as the camera pass rides
    ///     it on the view matrix.
    /// </param>
    /// <remarks>
    ///     Host writes only — no copies, no barriers — which is what lets the caster pass be one
    ///     graph pass with no upload sibling. The buffers are rung by frame so an in-flight frame's
    ///     reads are never under this frame's writes.
    /// </remarks>
    public void Upload(Vector3 origin, ReadOnlySpan<Matrix4x4> cascades) {
        ObjectDisposedException.ThrowIf(disposed, this);

        slot = (slot + 1) % slots;

        FillRecords();
        PatchCount = records.Count;

        if (PatchCount > 0) {
            device.Write(nodes, slot * nodeCapacity, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(records)));
        }

        var placement = Matrix4x4.FromTranslation(origin);
        var count = Math.Min(cascades.Length, ShadowCascades.MaxCascades);

        for (var cascade = 0; cascade < count; cascade++) {
            var at = (slot * ShadowCascades.MaxCascades) + cascade;

            surface.WriteCasterConstants(block, placement * cascades[cascade]);
            device.Write(constants[at], 0, block);

            surface.WriteCasterSet(descriptors[at], constants[at], nodes, slot * nodeCapacity, nodeCapacity);
        }
    }

    /// <summary>Draws the tiling into one cascade — whose viewport and scissor are already set.</summary>
    /// <param name="commands">Where to record the draw.</param>
    /// <param name="cascade">Which cascade's block to bind.</param>
    public void Record(ICommandList commands, int cascade) {
        ArgumentNullException.ThrowIfNull(commands);
        ObjectDisposedException.ThrowIf(disposed, this);

        Draws = 0;

        if (PatchCount == 0) {
            return;
        }

        commands.BindPipeline(pipeline);
        commands.BindDescriptorSet(DescriptorSetSlot.PerMaterial, descriptors[(slot * ShadowCascades.MaxCascades) + cascade]);
        commands.BindIndexBuffer(surface.SharedIndices, IndexFormat.UInt32);
        commands.DrawIndexed(surface.SharedIndexCount, PatchCount);

        Draws = 1;
    }

    /// <summary>The uniform tiling: every patch of the terrain, at the one level the budget allows.</summary>
    /// <remarks>
    ///     Recomputed per frame rather than cached, because it is a few dozen records and the
    ///     streamer's floor is a property somebody can move; the quadtree's own edge rule is kept —
    ///     a patch wholly off a non-square terrain is skipped, not clamped.
    /// </remarks>
    void FillRecords() {
        var description = surface.Terrain.Description;
        var gridQuads = surface.GridQuadCount;
        var rootQuads = surface.Tree.RootQuads;

        var step = 1;

        while (rootQuads / (gridQuads * step) > MaxPatchesPerSide) {
            step *= 2;
        }

        if (surface.Streaming is { } streaming) {
            // The pinned tail is the one set of mips resident everywhere by construction; a finer
            // read over a tile nobody is near would sample levels the pool never uploaded.
            step = Math.Max(step, 1 << streaming.CoarseLevel);
        }

        var nodeQuads = gridQuads * step;

        records.Clear();

        for (var z = 0; z < rootQuads; z += nodeQuads) {
            for (var x = 0; x < rootQuads; x += nodeQuads) {
                if (x >= description.SamplesX - 1 || z >= description.SamplesZ - 1) {
                    continue;
                }

                // Morph zero: a uniform tiling has no level boundary to degenerate towards, and the
                // level rides the record so each vertex reads the mip whose texels are its own.
                records.Add(TerrainNodeRecord.Of(new(x, z, nodeQuads, 0, 0f), gridQuads, surface.AtlasLevels - 1));
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        foreach (var set in descriptors) {
            device.Destroy(set);
        }

        device.Destroy(pipeline);
        device.Destroy(layout);
        device.Destroy(setLayout);
        device.Destroy(emptySetLayout);

        foreach (var buffer in constants) {
            device.Destroy(buffer);
        }

        device.Destroy(nodes);
    }
}
