// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Shaders.Generated;

namespace Vixen.Rendering.Terrain;

/// <summary>
///     Draws one terrain into the frame's motion target: the same patch lattice, under this frame's
///     matrix and last frame's.
/// </summary>
/// <remarks>
///     <para>
///         <b>The velocity half of a <see cref="TerrainRenderer" />, on <see cref="TerrainCasterPass" />'s
///         terms.</b> The heightmap, the hole mask, the samplers, the index buffer <em>and the frame's
///         node records</em> are all borrowed from the surface renderer — the records especially, because
///         the depth test is what makes a velocity pass honest: it draws the exact patches the surface
///         drew, at the same morph, so its fragments land on their own depths and nothing else's. What is
///         this pass's own is the constant block holding the two matrices and the set that points at it.
///     </para>
///     <para>
///         ⚠ <b><see cref="CompareFunction.GreaterEqual" />, not the frame stages' strict
///         <see cref="CompareFunction.Greater" />.</b> This pass re-rasterises geometry that is already
///         the nearest thing in the depth buffer, so its fragments arrive <em>at</em> the stored depth —
///         a strict test rejects every one of them and the pass silently writes nothing. Equal passes,
///         anything behind still fails, which is all the test is here for.
///     </para>
/// </remarks>
sealed class TerrainVelocityPass : IDisposable {
    readonly IGraphicsDevice device;
    readonly TerrainRenderer surface;
    readonly int slots;
    readonly byte[] block = new byte[TerrainVelocityKeys.ConstantBufferSize];

    readonly BufferHandle[] constants;
    readonly DescriptorSetHandle[] descriptors;
    readonly DescriptorSetLayoutHandle setLayout;
    readonly DescriptorSetLayoutHandle emptySetLayout;
    readonly PipelineLayoutHandle layout;
    readonly PipelineHandle pipeline;

    int slot;
    bool disposed;

    /// <summary>The depth state the velocity pass rasterises with — tested, never written, equal wins.</summary>
    /// <remarks>See the class remarks for why the comparison is not the stages' strict one. A property
    ///     rather than a literal at the pipeline so a test can hold the convention without a device
    ///     that renders.</remarks>
    internal static DepthStencilState DepthState =>
        DepthStencilState.TestOnly with { DepthCompare = CompareFunction.GreaterEqual };

    /// <summary>Builds the velocity pass over one surface renderer's resources.</summary>
    /// <param name="device">The device.</param>
    /// <param name="surface">Whose heightmap, holes, node records and index buffer this draws from.</param>
    /// <param name="shaders">The <c>TerrainVelocity</c> stages.</param>
    /// <param name="motionFormat">The frame's motion target format.</param>
    /// <param name="depthFormat">The scene depth it tests against.</param>
    public TerrainVelocityPass(
        IGraphicsDevice device,
        TerrainRenderer surface,
        TerrainShaders shaders,
        PixelFormat motionFormat,
        PixelFormat depthFormat
    ) {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(surface);

        if (!shaders.IsValid) {
            throw new ArgumentException("A terrain velocity pass needs both a vertex and a fragment stage.", nameof(shaders));
        }

        this.device = device;
        this.surface = surface;

        // One upload per frame — the velocity node runs once — so the ring is exactly as deep as
        // the frames that can be in flight.
        slots = Math.Max(1, device.FramesInFlight);

        constants = new BufferHandle[slots];

        for (var index = 0; index < constants.Length; index++) {
            constants[index] = device.CreateBuffer(
                new(
                    TerrainVelocityKeys.ConstantBufferSize,
                    BufferUsage.Uniform,
                    MemoryAccess.HostUpload,
                    $"terrain velocity constants {index}"
                )
            );
        }

        // The whole of TerrainBase's set, because TerrainVelocity inherits every binding the base
        // declares — TerrainCasterPass's layout argument, under this shader's own generated keys.
        setLayout = device.CreateDescriptorSetLayout(
            new(
                DescriptorSetSlot.PerMaterial,
                [
                    new(TerrainVelocityKeys.ConstantBufferBinding, DescriptorKind.UniformBuffer, ShaderStage.Vertex | ShaderStage.Fragment),
                    new(TerrainVelocityKeys.HeightMapBinding, DescriptorKind.SampledTexture, ShaderStage.Vertex | ShaderStage.Fragment),
                    new(TerrainVelocityKeys.WeightMapsBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment, TerrainRenderer.MaxWeightMaps),
                    new(TerrainVelocityKeys.LayerMapsBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment, TerrainRenderer.MaxLayers),
                    new(TerrainVelocityKeys.HoleMapBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment),
                    new(TerrainVelocityKeys.SurfaceMapsBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment, TerrainRenderer.MaxLayers),
                    new(TerrainVelocityKeys.HeightSamplerBinding, DescriptorKind.Sampler, ShaderStage.Vertex | ShaderStage.Fragment),
                    new(TerrainVelocityKeys.WeightSamplerBinding, DescriptorKind.Sampler, ShaderStage.Fragment),
                    new(TerrainVelocityKeys.LayerSamplerBinding, DescriptorKind.Sampler, ShaderStage.Fragment),
                    new(TerrainVelocityKeys.HoleSamplerBinding, DescriptorKind.Sampler, ShaderStage.Fragment),
                    new(TerrainVelocityKeys.NodesBinding, DescriptorKind.StorageBuffer, ShaderStage.Vertex),
                    new(TerrainVelocityKeys.LayerScalesBinding, DescriptorKind.StorageBuffer, ShaderStage.Fragment),
                    new(TerrainVelocityKeys.LayerBlendsBinding, DescriptorKind.StorageBuffer, ShaderStage.Fragment)
                ],
                "terrain velocity"
            )
        );

        // Padded below the material set — TerrainRenderer's own layout argument, verbatim.
        emptySetLayout = device.CreateDescriptorSetLayout(new(DescriptorSetSlot.PerFrame, [], "terrain velocity empty"));
        layout = device.CreatePipelineLayout(new([emptySetLayout, emptySetLayout, setLayout], [], "terrain velocity"));

        pipeline = device.CreateGraphicsPipeline(
            new(
                shaders.Vertex,
                shaders.Fragment,
                layout,
                [new(motionFormat, BlendState.Opaque)],
                [],
                PrimitiveTopology.TriangleList,
                RasterizerState.Default,
                DepthState,
                depthFormat,
                1,
                "terrain velocity"
            )
        );

        descriptors = new DescriptorSetHandle[slots];

        for (var index = 0; index < descriptors.Length; index++) {
            descriptors[index] = device.CreateDescriptorSet(setLayout, $"terrain velocity {index}");
        }
    }

    /// <summary>How many draws the last <see cref="Record" /> made — one, or zero with no patches.</summary>
    public int Draws { get; private set; }

    /// <summary>Stages one frame's reprojection: the two placed matrices, over the surface's records.</summary>
    /// <param name="viewProjection">This frame's view-projection with the placement folded in.</param>
    /// <param name="previousViewProjection">Last frame's, placed the same way.</param>
    /// <remarks>
    ///     Host writes only — no copies, no barriers — <see cref="TerrainCasterPass.Upload" />'s
    ///     arrangement, and rung by frame for its reason. The node records are not staged here at
    ///     all: the set points at the slot the surface's own upload wrote this frame.
    /// </remarks>
    public void Upload(in Matrix4x4 viewProjection, in Matrix4x4 previousViewProjection) {
        ObjectDisposedException.ThrowIf(disposed, this);

        slot = (slot + 1) % slots;

        surface.WriteVelocityConstants(block, viewProjection, previousViewProjection);
        device.Write(constants[slot], 0, block);

        surface.WriteVelocitySet(descriptors[slot], constants[slot]);
    }

    /// <summary>Draws this frame's patches into the motion target.</summary>
    public void Record(ICommandList commands) {
        ArgumentNullException.ThrowIfNull(commands);
        ObjectDisposedException.ThrowIf(disposed, this);

        Draws = 0;

        if (surface.PatchCount == 0) {
            return;
        }

        commands.BindPipeline(pipeline);
        commands.BindDescriptorSet(DescriptorSetSlot.PerMaterial, descriptors[slot]);
        commands.BindIndexBuffer(surface.SharedIndices, IndexFormat.UInt32);
        commands.DrawIndexed(surface.SharedIndexCount, surface.PatchCount);

        Draws = 1;
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
    }
}
