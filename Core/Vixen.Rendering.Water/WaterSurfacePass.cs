// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Rendering.Terrain;
using Vixen.Shaders.Generated;
using Vixen.Terrain;
using Vixen.Water;

namespace Vixen.Rendering.Water;

/// <summary>One patch of water a frame draws, as the vertex stage reads it.</summary>
/// <param name="Origin">Where the patch's low corner is, on the ground plane, in world metres.</param>
/// <param name="Step">How many metres one step of the grid patch spans.</param>
/// <param name="Morph">How far the patch has morphed towards its parent's grid, 0…1.</param>
/// <remarks>
///     <para>
///         <b>The C# half of <c>WaterNode</c> in <c>WaterMesh.rvn</c>, and the two agree by
///         construction or not at all.</b> The host packs these bytes and the shader reads them as a
///         struct — <see cref="TerrainNodeRecord" />'s bargain, and the same reason the duplication is
///         not a smell to be refactored away.
///     </para>
///     <para>
///         ⚠ <b>Sixteen bytes exactly, which is the one thing that has to be checked rather than
///         assumed.</b> A <c>float2</c> aligns to eight, so a struct containing one aligns to eight and
///         rounds its size up to a multiple of eight — two floats after it land at 8 and 12 and the
///         total is 16 with nothing left over. Adding a fifth field takes it to 24 in <c>std430</c> and
///         20 in C#, and a host writing a twenty-byte stride into an array read at twenty-four draws
///         patch one out of the middle of patch zero.
///     </para>
///     <para>
///         ⚠ <b>The origin is world metres and not texels</b>, which is the one place this differs
///         from the terrain's record — see <c>WaterNode</c> for why.
///     </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct WaterNodeRecord(Vector2 Origin, float Step, float Morph) {
    /// <summary>How many bytes one of these is.</summary>
    public static int SizeInBytes => Marshal.SizeOf<WaterNodeRecord>();

    /// <summary>The record for a node the selector chose.</summary>
    /// <param name="node">The node, in quads from the window's own origin.</param>
    /// <param name="windowOrigin">Where the window's low corner is, on the ground plane.</param>
    /// <param name="metresPerQuad">How many metres one quad of the finest level covers.</param>
    /// <param name="gridQuads">How many quads the shared patch spans.</param>
    /// <returns>The record.</returns>
    public static WaterNodeRecord Of(
        in TerrainLodNode node,
        Vector2 windowOrigin,
        float metresPerQuad,
        int gridQuads
    ) =>
        new(
            windowOrigin + new Vector2(node.X * metresPerQuad, node.Z * metresPerQuad),
            node.Quads * metresPerQuad / gridQuads,
            node.Morph
        );
}

/// <summary>The two stages the surface mesh is drawn with.</summary>
/// <param name="Vertex">The vertex stage.</param>
/// <param name="Fragment">The fragment stage.</param>
public readonly record struct WaterMeshShaders(ShaderHandle Vertex, ShaderHandle Fragment) {
    /// <summary>Whether both are there.</summary>
    public bool IsValid => Vertex.IsValid && Fragment.IsValid;
}

/// <summary>
///     Draws one zone's water: the selected patches, the far skirt, and the two planes § D8 reads.
/// </summary>
/// <remarks>
///     <para>
///         <b>[35 § D4](../../docs/plan/35-water.md#d4-the-surface-is-the-terrains-quadtree-with-a-different-height-source).</b>
///         All the water in a zone is <em>one draw call</em>, and the far mesh is a second — the same
///         instanced grid patch, placed by one <see cref="WaterNodeRecord" /> each, sharing the
///         terrain's index buffer because the lattice is the same lattice.
///     </para>
///     <para>
///         ⚠ <b>Depth is tested and not written, and that is what makes § D8's pass possible at
///         all.</b> The composite unprojects the <em>scene</em> depth to find what is behind the water;
///         a surface that wrote depth would put itself there and the water would be integrated against
///         itself. Testing is still necessary — a lake behind a hill must not draw over it — so the
///         state is reverse-Z <see cref="CompareFunction.Greater" /> with writes off.
///     </para>
///     <para>
///         ⚠ <b>The far mesh is drawn first, and the order is load-bearing.</b> With depth writes off
///         nothing arbitrates between two fragments at one pixel except which came last, and the near
///         mesh is the one with the field under it. Drawing the skirt afterwards would overwrite the
///         window with a flat plane wherever the two overlap at the horizon.
///     </para>
///     <para>
///         <b>Two pipelines for one shader, because the far mesh is a permutation.</b> § D4's own
///         wording: "drawn with a permutation that skips the wave sum and the flow". It is one shader
///         and one layout, so the second pipeline costs a compile and nothing per frame.
///     </para>
/// </remarks>
public sealed class WaterSurfacePass : IDisposable {
    readonly IGraphicsDevice device;
    readonly int gridQuads;
    readonly int slots;
    readonly long nodeCapacity;
    readonly byte[] block = new byte[WaterMeshKeys.PerMaterialBlockSize];
    readonly byte[] frameBlock = new byte[WaterMeshKeys.PerFrameBlockSize];

    readonly List<TerrainLodNode> selected = [];
    readonly List<WaterNodeRecord> records = [];

    readonly BufferHandle nodes;
    readonly BufferHandle waves;
    readonly BufferHandle[] constants;
    readonly BufferHandle[] frameConstants;
    readonly BufferHandle indices;

    readonly DescriptorSetLayoutHandle materialLayout;
    readonly DescriptorSetLayoutHandle frameLayout;
    readonly DescriptorSetLayoutHandle emptyLayout;
    readonly PipelineLayoutHandle layout;
    readonly PipelineHandle nearPipeline;
    readonly PipelineHandle farPipeline;
    readonly DescriptorSetHandle[] materialSets;
    readonly DescriptorSetHandle[] frameSets;

    int slot;
    int nearCount;
    int farCount;
    bool disposed;

    /// <summary>The most patches a zone may select before the rest are dropped.</summary>
    /// <remarks>
    ///     ⚠ <b>A stated cap rather than a growing buffer, and the overflow is a reading rather than
    ///     silence.</b> § Part 4's budget assertions want a number that regresses visibly; a buffer
    ///     that reallocated would turn a level-design mistake — a zone whose LOD scale descends to
    ///     level zero across two kilometres — into a frame-time cliff nobody can attribute.
    ///     <see cref="DroppedPatches" /> is what says it happened.
    /// </remarks>
    public const int MaxPatches = 2048;

    /// <summary>How many waves the buffer is sized for.</summary>
    /// <remarks>The largest quantised count — see <see cref="WaterWaveCount.ThirtyTwo" />.</remarks>
    public const int MaxWaves = (int)WaterWaveCount.ThirtyTwo;

    /// <summary>Reverse-Z, tested and not written. See the class remarks.</summary>
    public static DepthStencilState DepthState =>
        DepthStencilState.Default with { DepthWrite = false, DepthCompare = CompareFunction.Greater };

    /// <summary>Back faces culled, which is what the patch's winding is generated for.</summary>
    /// <remarks>
    ///     The lattice is counter-clockwise seen from above, exactly as the terrain's is — which is
    ///     also why the two share <see cref="TerrainGridPatch" /> rather than each generating one.
    /// </remarks>
    public static RasterizerState Raster => new(CullMode.Back);

    /// <summary>Builds the pass.</summary>
    /// <param name="device">The device.</param>
    /// <param name="shaders">The <c>WaterMesh</c> stages, near and far.</param>
    /// <param name="formats">The two colour target formats, in attachment order.</param>
    /// <param name="depthFormat">The scene depth's format.</param>
    /// <param name="gridQuads">How many quads the shared patch spans.</param>
    /// <exception cref="ArgumentNullException"><paramref name="device" /> is null.</exception>
    /// <exception cref="ArgumentException">A stage is missing, or the patch is not one that can be drawn.</exception>
    public WaterSurfacePass(
        IGraphicsDevice device,
        (WaterMeshShaders Near, WaterMeshShaders Far) shaders,
        ReadOnlySpan<PixelFormat> formats,
        PixelFormat depthFormat,
        int gridQuads = PatchSelector.DefaultGridQuads
    ) {
        ArgumentNullException.ThrowIfNull(device);

        if (!shaders.Near.IsValid || !shaders.Far.IsValid) {
            throw new ArgumentException(
                "A water surface needs a vertex and a fragment stage for both the window and the far "
                + "mesh. The two are one shader and a permutation — see WaterMesh.FarMesh.",
                nameof(shaders)
            );
        }

        this.device = device;
        this.gridQuads = gridQuads;

        slots = Math.Max(1, device.FramesInFlight);
        nodeCapacity = (long)MaxPatches * WaterNodeRecord.SizeInBytes;

        nodes = device.CreateBuffer(
            new(nodeCapacity * slots, BufferUsage.Storage, MemoryAccess.HostUpload, "water patches")
        );

        waves = device.CreateBuffer(
            new(
                (long)MaxWaves * Marshal.SizeOf<GerstnerWave>() * slots,
                BufferUsage.Storage,
                MemoryAccess.HostUpload,
                "water waves"
            )
        );

        constants = new BufferHandle[slots];
        frameConstants = new BufferHandle[slots];

        for (var index = 0; index < slots; index++) {
            constants[index] = device.CreateBuffer(
                new(
                    WaterMeshKeys.PerMaterialBlockSize,
                    BufferUsage.Uniform,
                    MemoryAccess.HostUpload,
                    $"water surface constants {index}"
                )
            );

            frameConstants[index] = device.CreateBuffer(
                new(
                    WaterMeshKeys.PerFrameBlockSize,
                    BufferUsage.Uniform,
                    MemoryAccess.HostUpload,
                    $"water surface frame constants {index}"
                )
            );
        }

        // The index buffer is the terrain's lattice — § D4's "sharing the terrain's patch vertex
        // buffer". There are no vertices at all: a regular grid's positions are two divisions of
        // SV_VertexID, so uploading them would be sending the shader something it can count.
        var indexData = new uint[TerrainGridPatch.IndexCount(gridQuads)];
        IndexCount = TerrainGridPatch.FillIndices(gridQuads, indexData);

        indices = device.CreateBuffer(
            new(indexData.Length * sizeof(uint), BufferUsage.Index, MemoryAccess.HostUpload, "water patch indices")
        );

        device.Write(indices, 0, MemoryMarshal.AsBytes(indexData.AsSpan()));

        frameLayout = device.CreateDescriptorSetLayout(
            new(
                DescriptorSetSlot.PerFrame,
                [
                    new(
                        WaterMeshKeys.PerFrameBlockBinding,
                        DescriptorKind.UniformBuffer,
                        ShaderStage.Vertex | ShaderStage.Fragment
                    ),
                    new(WaterMeshKeys.BufferedWaterWavesWavesBinding, DescriptorKind.StorageBuffer, ShaderStage.Vertex)
                ],
                "water surface frame"
            )
        );

        materialLayout = device.CreateDescriptorSetLayout(
            new(
                DescriptorSetSlot.PerMaterial,
                [
                    new(
                        WaterMeshKeys.PerMaterialBlockBinding,
                        DescriptorKind.UniformBuffer,
                        ShaderStage.Vertex | ShaderStage.Fragment
                    ),
                    new(WaterMeshKeys.WaterInfoBinding, DescriptorKind.SampledTexture, ShaderStage.Vertex),
                    new(WaterMeshKeys.InfoSamplerBinding, DescriptorKind.Sampler, ShaderStage.Vertex),
                    new(WaterMeshKeys.NodesBinding, DescriptorKind.StorageBuffer, ShaderStage.Vertex)
                ],
                "water surface"
            )
        );

        // Set 1 is nobody's here and is padded, exactly as the terrain's layout pads below its
        // material set: a pipeline layout is positional, and a gap is a set index that means
        // something else.
        emptyLayout = device.CreateDescriptorSetLayout(new(DescriptorSetSlot.PerView, [], "water surface empty"));
        layout = device.CreatePipelineLayout(new([frameLayout, emptyLayout, materialLayout], [], "water surface"));

        nearPipeline = Pipeline(shaders.Near, formats, depthFormat, "water surface");
        farPipeline = Pipeline(shaders.Far, formats, depthFormat, "water far mesh");

        materialSets = new DescriptorSetHandle[slots];
        frameSets = new DescriptorSetHandle[slots];

        for (var index = 0; index < slots; index++) {
            materialSets[index] = device.CreateDescriptorSet(materialLayout, $"water surface {index}");
            frameSets[index] = device.CreateDescriptorSet(frameLayout, $"water surface frame {index}");
        }
    }

    /// <summary>How many indices one patch is drawn from.</summary>
    public int IndexCount { get; }

    /// <summary>How many patches the last <see cref="Upload" /> selected inside the window.</summary>
    public int NearPatches => nearCount;

    /// <summary>And how many the far skirt contributed.</summary>
    public int FarPatches => farCount;

    /// <summary>How many patches the last selection had to drop, over <see cref="MaxPatches" />.</summary>
    /// <remarks>
    ///     ⚠ Reported rather than silently absorbed. A zone that drops patches has a hole in it, and a
    ///     hole attributed to the wrong thing is a week — see <see cref="MaxPatches" />.
    /// </remarks>
    public int DroppedPatches { get; private set; }

    /// <summary>How many draws the last <see cref="Record" /> made. Zero, one or two.</summary>
    public int Draws { get; private set; }

    /// <summary>The patches the last <see cref="Upload" /> chose — the skirt's first, then the window's.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The order is the draw order and it is load bearing</b>, which is why this is one
    ///         span and not two: the far skirt is uploaded and drawn first because depth writes are
    ///         off, so at a pixel where both cover the last fragment wins and the near mesh is the one
    ///         with a field under it. The first <see cref="FarPatches" /> entries are the skirt's.
    ///     </para>
    ///     <para>
    ///         Published for <c>water.showTiles</c> and <c>water.showLod</c>. A debug draw that
    ///         selected the patches again would be drawing <em>its own</em> descent — which would
    ///         agree with the frame's until the moment it stopped, and that moment is exactly the bug
    ///         somebody would be turning the overlay on to find.
    ///     </para>
    /// </remarks>
    public ReadOnlySpan<TerrainLodNode> Selected => CollectionsMarshal.AsSpan(selected);

    /// <summary>Selects this frame's patches and stages everything the draw reads.</summary>
    /// <param name="mesh">The zone's mesh, already pointed at a rasterised field.</param>
    /// <param name="view">The camera's placement and what it can see.</param>
    /// <param name="sea">The sea state, already summed from its spectrum.</param>
    /// <param name="info">The zone's info texture.</param>
    /// <param name="sampler">How the vertex stage reads it. Linear and clamped.</param>
    /// <param name="settings">The numbers the shader's own block carries.</param>
    /// <exception cref="ArgumentNullException"><paramref name="mesh" /> is null.</exception>
    /// <remarks>
    ///     Host writes only — no copies, no barriers — so this can run in the graph's upload pass
    ///     beside the info texture's own, which is where a copy into a texture has to be anyway.
    /// </remarks>
    public void Upload(
        WaterSurfaceMesh mesh,
        in WaterMeshView view,
        ReadOnlySpan<GerstnerWave> sea,
        TextureViewHandle info,
        SamplerHandle sampler,
        in WaterMeshSettings settings
    ) {
        ArgumentNullException.ThrowIfNull(mesh);
        ObjectDisposedException.ThrowIf(disposed, this);

        slot = (slot + 1) % slots;

        selected.Clear();
        records.Clear();
        DroppedPatches = 0;

        // ⚠ The far skirt first, because with depth writes off the last fragment at a pixel wins and
        // the near mesh is the one with a field under it. See the class remarks.
        farCount = Take(mesh.SelectFar(view.Frustum, settings.RestHeight, selected), mesh);
        nearCount = Take(mesh.Select(view.Position, view.Frustum, selected), mesh);

        if (records.Count > 0) {
            device.Write(
                nodes,
                slot * nodeCapacity,
                MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(records))
            );
        }

        var waveCount = Math.Min(sea.Length, MaxWaves);
        var waveStride = (long)MaxWaves * Marshal.SizeOf<GerstnerWave>();

        if (waveCount > 0) {
            device.Write(waves, slot * waveStride, MemoryMarshal.AsBytes(sea[..waveCount]));
        }

        new WaterMeshPerFrameConstants { BufferedWaterWavesWaveCount = waveCount }.Write(frameBlock);
        device.Write(frameConstants[slot], 0, frameBlock);

        new WaterMeshPerMaterialConstants {
            ViewProjection = view.ViewProjection,
            WindowOrigin = mesh.Window.Origin,
            WindowExtent = mesh.Window.Extent,
            AttenuationDepth = settings.AttenuationDepth,
            WaterTime = settings.WaterTime,
            EdgeFade = mesh.EdgeFade,
            RestHeight = settings.RestHeight,
            ShoreDepth = settings.ShoreDepth,
            FoamDepth = settings.FoamDepth,
            FoamCrest = settings.FoamCrest
        }.Write(block);

        device.Write(constants[slot], 0, block);

        device.UpdateDescriptorSet(
            frameSets[slot],
            [
                new(WaterMeshKeys.PerFrameBlockBinding, DescriptorKind.UniformBuffer, Buffer: frameConstants[slot]),
                new(
                    WaterMeshKeys.BufferedWaterWavesWavesBinding,
                    DescriptorKind.StorageBuffer,
                    Buffer: waves,
                    Offset: slot * waveStride,
                    Size: waveStride
                )
            ]
        );

        device.UpdateDescriptorSet(
            materialSets[slot],
            [
                new(WaterMeshKeys.PerMaterialBlockBinding, DescriptorKind.UniformBuffer, Buffer: constants[slot]),
                new(WaterMeshKeys.WaterInfoBinding, DescriptorKind.SampledTexture, TextureView: info),
                new(WaterMeshKeys.InfoSamplerBinding, DescriptorKind.Sampler, Sampler: sampler),
                new(
                    WaterMeshKeys.NodesBinding,
                    DescriptorKind.StorageBuffer,
                    Buffer: nodes,
                    Offset: slot * nodeCapacity,
                    Size: nodeCapacity
                )
            ]
        );
    }

    /// <summary>Draws what the last <see cref="Upload" /> staged.</summary>
    /// <param name="commands">Where to record it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="commands" /> is null.</exception>
    public void Record(ICommandList commands) {
        ArgumentNullException.ThrowIfNull(commands);
        ObjectDisposedException.ThrowIf(disposed, this);

        Draws = 0;

        if (records.Count == 0) {
            return;
        }

        commands.BindDescriptorSet(DescriptorSetSlot.PerFrame, frameSets[slot]);
        commands.BindDescriptorSet(DescriptorSetSlot.PerMaterial, materialSets[slot]);
        commands.BindIndexBuffer(indices, IndexFormat.UInt32);

        // The skirt occupies the first records and the window the rest, which is what lets two
        // permutations share one instance buffer: an instanced draw's first instance is an offset.
        if (farCount > 0) {
            commands.BindPipeline(farPipeline);
            commands.DrawIndexed(IndexCount, farCount);
            Draws++;
        }

        if (nearCount > 0) {
            commands.BindPipeline(nearPipeline);
            commands.DrawIndexed(IndexCount, nearCount, firstInstance: farCount);
            Draws++;
        }
    }

    /// <summary>Turns however many nodes were just appended into records, up to the cap.</summary>
    int Take(int count, WaterSurfaceMesh mesh) {
        var taken = 0;

        for (var index = selected.Count - count; index < selected.Count; index++) {
            if (records.Count >= MaxPatches) {
                DroppedPatches += selected.Count - index;

                break;
            }

            records.Add(
                WaterNodeRecord.Of(selected[index], mesh.Window.Origin, mesh.MetresPerQuad, gridQuads)
            );

            taken++;
        }

        return taken;
    }

    /// <summary>The two planes, opaque.</summary>
    /// <remarks>
    ///     ⚠ <b>No blending, and that is not an omission.</b> The surface plane's <c>g</c> is a mask
    ///     and its <c>r</c> is a depth; blending either would average two surfaces into one that is
    ///     neither, and § D8's composite would unproject a pixel that is nowhere.
    /// </remarks>
    static ColourTargetState[] Targets(ReadOnlySpan<PixelFormat> formats) {
        var targets = new ColourTargetState[formats.Length];

        for (var index = 0; index < formats.Length; index++) {
            targets[index] = new(formats[index]);
        }

        return targets;
    }

    PipelineHandle Pipeline(
        in WaterMeshShaders shaders,
        ReadOnlySpan<PixelFormat> formats,
        PixelFormat depthFormat,
        string name
    ) =>
        device.CreateGraphicsPipeline(
            new(
                shaders.Vertex,
                shaders.Fragment,
                layout,
                Targets(formats),
                null,
                PrimitiveTopology.TriangleList,
                Raster,
                DepthState,
                depthFormat,
                1,
                name
            )
        );

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        foreach (var set in materialSets) {
            device.Destroy(set);
        }

        foreach (var set in frameSets) {
            device.Destroy(set);
        }

        device.Destroy(nearPipeline);
        device.Destroy(farPipeline);
        device.Destroy(layout);
        device.Destroy(materialLayout);
        device.Destroy(frameLayout);
        device.Destroy(emptyLayout);

        foreach (var buffer in constants) {
            device.Destroy(buffer);
        }

        foreach (var buffer in frameConstants) {
            device.Destroy(buffer);
        }

        device.Destroy(indices);
        device.Destroy(waves);
        device.Destroy(nodes);
    }
}

/// <summary>Where the camera is and what it can see, for one zone's selection.</summary>
/// <param name="Position">Where the view is, in world units.</param>
/// <param name="Frustum">What it can see.</param>
/// <param name="ViewProjection">What the vertex stage transforms by.</param>
public readonly record struct WaterMeshView(Vector3 Position, BoundingFrustum Frustum, Matrix4x4 ViewProjection);

/// <summary>The numbers the surface's own block carries, beyond what the window already says.</summary>
/// <remarks>
///     A record rather than ten parameters, because every one of them is a knob a zone panel shows and
///     the set grows — which is the argument <c>RenderQuality</c> makes for its own groups.
/// </remarks>
public readonly record struct WaterMeshSettings {
    /// <summary>The simulation's water time, in seconds. ⚠ Not a frame time — see § D2.</summary>
    public float WaterTime { get; init; }

    /// <summary>The depth at which waves reach their full height, in metres.</summary>
    public float AttenuationDepth { get; init; }

    /// <summary>Where the open surface sits, for the far mesh, in world units.</summary>
    public float RestHeight { get; init; }

    /// <summary>How deep the water has to be before it is fully opaque water, in metres.</summary>
    public float ShoreDepth { get; init; }

    /// <summary>How shallow it has to be before it foams, in metres.</summary>
    public float FoamDepth { get; init; }

    /// <summary>How far above the rest height a crest has to rise before it foams, in metres.</summary>
    public float FoamCrest { get; init; }

    /// <summary>The shader's own declared defaults, which is what a document that says nothing gets.</summary>
    public static WaterMeshSettings Default =>
        new() {
            WaterTime = 0f,
            AttenuationDepth = 2f,
            RestHeight = 0f,
            ShoreDepth = 0.25f,
            FoamDepth = 0.4f,
            FoamCrest = 0.5f
        };
}
