// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Shaders.Generated;

namespace Vixen.Rendering.Terrain;

/// <summary>
///     Draws one volume's foliage into the frame's motion target: the same survivors, reprojected.
/// </summary>
/// <remarks>
///     <para>
///         <b>The velocity half of a <see cref="FoliageDrawPass" />, on
///         <see cref="GrassVelocityPass" />'s terms.</b> The meshes, the cull's three buffers and the
///         patched indirect commands are all the colour pass's frame; what is this pass's own is the
///         block holding both matrices. A placed tree does not move, so unlike the grass there is no
///         second clock — the camera term is the whole motion, which is exactly what a static object
///         owes the resolve: without it, unwritten texels read as "did not move on screen", and every
///         pan smears the forest.
///     </para>
/// </remarks>
sealed class FoliageVelocityPass : IDisposable {
    /// <summary>What each ring slot's constant block is aligned to. <c>GrassDispatch</c>'s 256.</summary>
    const int SlotAlignment = 256;

    readonly IGraphicsDevice device;
    readonly int slots;
    readonly long constantStride;

    readonly BufferHandle constants;
    readonly DescriptorSetLayoutHandle setLayout;
    readonly DescriptorSetLayoutHandle emptySetLayout;
    readonly PipelineLayoutHandle layout;
    readonly PipelineHandle pipeline;
    readonly DescriptorSetHandle[] descriptors;

    int slot;
    bool disposed;

    /// <summary>Builds the velocity pass beside one volume's colour pass.</summary>
    /// <param name="device">The device.</param>
    /// <param name="shaders">The <c>FoliageVelocity</c> stages.</param>
    /// <param name="motionFormat">The frame's motion target format.</param>
    /// <param name="depthFormat">The scene depth it tests against.</param>
    public FoliageVelocityPass(IGraphicsDevice device, TerrainShaders shaders, PixelFormat motionFormat, PixelFormat depthFormat) {
        ArgumentNullException.ThrowIfNull(device);

        if (!shaders.IsValid) {
            throw new ArgumentException("A foliage velocity pass needs both a vertex and a fragment stage.", nameof(shaders));
        }

        this.device = device;
        slots = Math.Max(1, device.FramesInFlight);

        // One block per frame in flight — FoliageDrawPass's ring, for its immediate-memcpy reason.
        constantStride = ((FoliageVelocityKeys.ConstantBufferSize + SlotAlignment - 1) / SlotAlignment) * SlotAlignment;

        constants = device.CreateBuffer(
            new(constantStride * slots, BufferUsage.Uniform, MemoryAccess.HostUpload, "foliage velocity constants")
        );

        setLayout = device.CreateDescriptorSetLayout(
            new(
                DescriptorSetSlot.PerMaterial,
                [
                    new(FoliageVelocityKeys.ConstantBufferBinding, DescriptorKind.UniformBuffer, ShaderStage.Vertex | ShaderStage.Fragment),
                    new(FoliageVelocityKeys.AlbedoMapBinding, DescriptorKind.SampledTexture, ShaderStage.Vertex | ShaderStage.Fragment),
                    new(FoliageVelocityKeys.AlbedoSamplerBinding, DescriptorKind.Sampler, ShaderStage.Vertex | ShaderStage.Fragment),
                    new(FoliageVelocityKeys.InstancesBinding, DescriptorKind.StorageBuffer, ShaderStage.Vertex | ShaderStage.Fragment),
                    new(FoliageVelocityKeys.SurvivorsBinding, DescriptorKind.StorageBuffer, ShaderStage.Vertex | ShaderStage.Fragment),
                    new(FoliageVelocityKeys.ParametersBinding, DescriptorKind.StorageBuffer, ShaderStage.Vertex | ShaderStage.Fragment)
                ],
                "foliage velocity"
            )
        );

        // Padded below the material set on TerrainRenderer's terms.
        emptySetLayout = device.CreateDescriptorSetLayout(new(DescriptorSetSlot.PerFrame, [], "foliage velocity empty"));
        layout = device.CreatePipelineLayout(new([emptySetLayout, emptySetLayout, setLayout], [], "foliage velocity"));

        pipeline = device.CreateGraphicsPipeline(
            new(
                shaders.Vertex,
                shaders.Fragment,
                layout,
                [new(motionFormat, BlendState.Opaque)],
                [
                    new(
                        FoliageDrawPass.VertexBytes,
                        [
                            new(FoliageVelocityKeys.PositionLocation, VertexFormat.Float32X3, 0),
                            new(FoliageVelocityKeys.NormalLocation, VertexFormat.Float32X3, 12),
                            new(FoliageVelocityKeys.TexcoordLocation, VertexFormat.Float32X2, 24)
                        ]
                    )
                ],
                PrimitiveTopology.TriangleList,

                // Back-face culled, exactly as the colour pass draws — the two must rasterise the
                // same fragments or a pixel gains a depth with no velocity beside it.
                RasterizerState.Default,
                TerrainVelocityPass.DepthState,
                depthFormat,
                1,
                "foliage velocity"
            )
        );

        descriptors = new DescriptorSetHandle[slots];

        for (var index = 0; index < slots; index++) {
            descriptors[index] = device.CreateDescriptorSet(setLayout);
        }
    }

    /// <summary>How many indirect draws the last <see cref="Record" /> issued.</summary>
    public int Draws { get; private set; }

    /// <summary>Writes the frame's constants and points the set at the cull's buffers.</summary>
    /// <param name="draw">The colour pass whose albedo binding and defaults this borrows.</param>
    /// <param name="cull">The cull whose survivors, parameters and instances the draw reads.</param>
    /// <param name="view">The camera's combined matrix — the colour pass's own value.</param>
    /// <param name="previousViewProjection">Last frame's matrix, unjittered like the current one.</param>
    /// <remarks>Called after the cull's own prepare, for <see cref="FoliageDrawPass.Prepare" />'s
    ///     ring-slot reason — and after the colour pass's, whose first frame uploads the default
    ///     albedo this set points at.</remarks>
    public void Prepare(
        FoliageDrawPass draw,
        FoliageCullPass cull,
        in TerrainView view,
        in Matrix4x4 previousViewProjection
    ) {
        ArgumentNullException.ThrowIfNull(draw);
        ArgumentNullException.ThrowIfNull(cull);
        ObjectDisposedException.ThrowIf(disposed, this);

        slot = (slot + 1) % slots;

        var block = new byte[FoliageVelocityKeys.ConstantBufferSize];

        new FoliageVelocityConstants {
            PreviousViewProjection = previousViewProjection,
            ViewProjection = view.ViewProjection,

            // ⚠ The colour pass's own number, never a second one written here — the grass velocity
            // pass's note says what a disagreeing cutoff writes into the motion target.
            AlphaCutoff = draw.AlphaCutoff,

            // Unread — the fragment computes a cutout and a delta — but a block is written wholly.
            TintRange = new(1f, 1f)
        }.Write(block);

        device.Write(constants, slot * constantStride, block);

        device.UpdateDescriptorSet(
            descriptors[slot],
            [
                DescriptorWrite.Uniform(FoliageVelocityKeys.ConstantBufferBinding, constants, slot * constantStride, FoliageVelocityKeys.ConstantBufferSize),
                DescriptorWrite.Texture(FoliageVelocityKeys.AlbedoMapBinding, draw.AlbedoOrDefault),
                DescriptorWrite.SamplerAt(FoliageVelocityKeys.AlbedoSamplerBinding, draw.AlbedoSampler),
                DescriptorWrite.Storage(FoliageVelocityKeys.InstancesBinding, cull.Instances),
                DescriptorWrite.Storage(FoliageVelocityKeys.SurvivorsBinding, cull.Survivors),
                DescriptorWrite.Storage(FoliageVelocityKeys.ParametersBinding, cull.Parameters)
            ]
        );
    }

    /// <summary>Binds everything the draws need and issues them — the colour pass's loop, verbatim.</summary>
    /// <remarks>One draw per level per batch, empty levels included, meshless batches skipped —
    ///     <see cref="FoliageDrawPass.Record" />'s own contract, because the two passes must issue
    ///     the same commands to cover the same pixels.</remarks>
    public int Record(ICommandList commands, FoliageCullPass cull, IReadOnlyDictionary<int, FoliageMesh> meshes) {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(cull);
        ArgumentNullException.ThrowIfNull(meshes);
        ObjectDisposedException.ThrowIf(disposed, this);

        Draws = 0;

        if (cull.BatchCount == 0) {
            return 0;
        }

        commands.BindPipeline(pipeline);
        commands.BindDescriptorSet(DescriptorSetSlot.PerMaterial, descriptors[slot]);

        var bound = -1;

        for (var batch = 0; batch < cull.BatchCount; batch++) {
            var type = cull.TypeOf(batch);

            if (!meshes.TryGetValue(type, out var mesh) || !mesh.IsValid) {
                continue;
            }

            if (type != bound) {
                commands.BindVertexBuffer(0, mesh.Vertices);
                commands.BindIndexBuffer(mesh.Indices, IndexFormat.UInt32);
                bound = type;
            }

            var levels = (int)cull.BatchOf(batch).LevelCount;

            for (var level = 0; level < levels; level++) {
                commands.DrawIndexedIndirect(cull.Commands, cull.CommandOf(batch, level));
                Draws++;
            }
        }

        return Draws;
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

        device.Destroy(constants);
        device.Destroy(pipeline);
        device.Destroy(layout);
        device.Destroy(setLayout);
        device.Destroy(emptySetLayout);
    }
}
