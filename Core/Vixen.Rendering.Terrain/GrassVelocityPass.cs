// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Foliage;
using Vixen.Graphics;
using Vixen.Shaders.Generated;

namespace Vixen.Rendering.Terrain;

/// <summary>
///     Draws one grass field into the frame's motion target: the same blades, the wind evaluated at
///     two clocks.
/// </summary>
/// <remarks>
///     <para>
///         <b>The velocity half of a <see cref="GrassDrawPass" />, on
///         <see cref="TerrainVelocityPass" />'s terms.</b> The blade mesh, the albedo binding and the
///         scatter's instance buffer are all the colour pass's; what is this pass's own is the block
///         holding both matrices and both clocks, and the pipeline that writes one signed channel
///         pair instead of shaded colour. Grass is the one thing in the ground stack whose velocity
///         is not the camera's alone — the sway moves the tip between frames, and a resolve that is
///         not told about it ghosts every gust.
///     </para>
///     <para>
///         ⚠ <b>The indirect commands are re-issued, not re-counted.</b>
///         <see cref="GrassDispatch.RecordDraws" /> reads the argument ring slot this frame's scatter
///         patched, so this pass draws exactly the blades the colour pass drew — which the depth
///         test then holds to their own pixels, <see cref="TerrainVelocityPass" />'s equal-wins
///         comparison saying why the test is not the stages' strict one.
///     </para>
/// </remarks>
sealed class GrassVelocityPass : IDisposable {
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

    /// <summary>Builds the velocity pass beside one field's colour pass.</summary>
    /// <param name="device">The device.</param>
    /// <param name="shaders">The <c>GrassVelocity</c> stages.</param>
    /// <param name="motionFormat">The frame's motion target format.</param>
    /// <param name="depthFormat">The scene depth it tests against.</param>
    public GrassVelocityPass(IGraphicsDevice device, TerrainShaders shaders, PixelFormat motionFormat, PixelFormat depthFormat) {
        ArgumentNullException.ThrowIfNull(device);

        if (!shaders.IsValid) {
            throw new ArgumentException("A grass velocity pass needs both a vertex and a fragment stage.", nameof(shaders));
        }

        this.device = device;
        slots = Math.Max(1, device.FramesInFlight);

        // One block per frame in flight — GrassDrawPass's ring, for its immediate-memcpy reason.
        constantStride = ((GrassVelocityKeys.ConstantBufferSize + SlotAlignment - 1) / SlotAlignment) * SlotAlignment;

        constants = device.CreateBuffer(
            new(constantStride * slots, BufferUsage.Uniform, MemoryAccess.HostUpload, "grass velocity constants")
        );

        setLayout = device.CreateDescriptorSetLayout(
            new(
                DescriptorSetSlot.PerMaterial,
                [
                    new(GrassVelocityKeys.ConstantBufferBinding, DescriptorKind.UniformBuffer, ShaderStage.Vertex | ShaderStage.Fragment),
                    new(GrassVelocityKeys.AlbedoMapBinding, DescriptorKind.SampledTexture, ShaderStage.Vertex | ShaderStage.Fragment),
                    new(GrassVelocityKeys.AlbedoSamplerBinding, DescriptorKind.Sampler, ShaderStage.Vertex | ShaderStage.Fragment),
                    new(GrassVelocityKeys.BladesBinding, DescriptorKind.StorageBuffer, ShaderStage.Vertex | ShaderStage.Fragment)
                ],
                "grass velocity"
            )
        );

        // Padded below the material set on TerrainRenderer's terms.
        emptySetLayout = device.CreateDescriptorSetLayout(new(DescriptorSetSlot.PerFrame, [], "grass velocity empty"));
        layout = device.CreatePipelineLayout(new([emptySetLayout, emptySetLayout, setLayout], [], "grass velocity"));

        pipeline = device.CreateGraphicsPipeline(
            new(
                shaders.Vertex,
                shaders.Fragment,
                layout,
                [new(motionFormat, BlendState.Opaque)],
                [
                    new(
                        32,
                        [
                            new(GrassVelocityKeys.PositionLocation, VertexFormat.Float32X3, 0),
                            new(GrassVelocityKeys.NormalLocation, VertexFormat.Float32X3, 12),
                            new(GrassVelocityKeys.TexcoordLocation, VertexFormat.Float32X2, 24)
                        ]
                    )
                ],
                PrimitiveTopology.TriangleList,

                // Two-sided, exactly as the colour pass draws — a blade culled here and drawn there
                // is a pixel whose depth exists and whose velocity does not.
                RasterizerState.Default with { Cull = CullMode.None },
                TerrainVelocityPass.DepthState,
                depthFormat,
                1,
                "grass velocity"
            )
        );

        descriptors = new DescriptorSetHandle[slots];

        for (var index = 0; index < slots; index++) {
            descriptors[index] = device.CreateDescriptorSet(setLayout);
        }
    }

    /// <summary>How many indirect draws the last <see cref="Record" /> issued.</summary>
    public int Draws { get; private set; }

    /// <summary>Writes the frame's constants and points the set at this frame's blades.</summary>
    /// <param name="draw">The colour pass whose albedo binding and defaults this borrows.</param>
    /// <param name="dispatch">The scatter whose instance buffer the draw reads.</param>
    /// <param name="view">The camera's matrix and position — the colour pass's own values, verbatim.</param>
    /// <param name="previousViewProjection">Last frame's matrix, unjittered like the current one.</param>
    /// <param name="wind">The field's wind — evaluated at both clocks by the shader.</param>
    /// <param name="time">This frame's clock, in seconds.</param>
    /// <param name="previousTime">Last frame's, so the sway's own motion is real to the resolve.</param>
    /// <param name="fade">Where the field fades, in metres — the colour pass's numbers exactly.</param>
    /// <remarks>
    ///     ⚠ <b>Every value the stipple reads must be the colour pass's own</b> — the position, the
    ///     fade band, the clock — because the two passes must discard the same pixels: a fragment
    ///     the colour pass dissolved shows the ground behind it, and a velocity surviving there
    ///     would overwrite the ground's answer with the blade's.
    /// </remarks>
    public void Prepare(
        GrassDrawPass draw,
        GrassDispatch dispatch,
        in TerrainView view,
        in Matrix4x4 previousViewProjection,
        in GrassWind wind,
        float time,
        float previousTime,
        Vector2 fade
    ) {
        ArgumentNullException.ThrowIfNull(draw);
        ArgumentNullException.ThrowIfNull(dispatch);
        ObjectDisposedException.ThrowIf(disposed, this);

        slot = (slot + 1) % slots;

        var block = new byte[GrassVelocityKeys.ConstantBufferSize];

        new GrassVelocityConstants {
            PreviousViewProjection = previousViewProjection,
            PreviousTime = previousTime,
            ViewProjection = view.ViewProjection,
            ViewPosition = view.Position,
            Time = time,
            WindDirection = wind.Direction,
            WindStrength = wind.Strength,
            WindFrequency = wind.Frequency,
            WindSpeed = wind.Speed,
            WindFlutter = wind.Flutter,
            WindHeight = wind.Height,

            // ⚠ The colour pass's own number, never a second one written here. `GrassBlade.Cutout`
            // is the predicate both fragments run, and it reads this: a velocity block holding a
            // different cutoff cuts a different silhouette out of the motion target than the
            // picture has, which is a rim of the ground's motion around every blade.
            AlphaCutoff = draw.AlphaCutoff,

            // Unread — the fragment computes a cutout and a delta, never a colour — but the block
            // is written wholly, and the colour pass's default keeps the two blocks comparable.
            TintRange = new(0.85f, 1.1f),
            FadeRange = fade == default ? new(60f, 80f) : fade
        }.Write(block);

        device.Write(constants, slot * constantStride, block);

        device.UpdateDescriptorSet(
            descriptors[slot],
            [
                DescriptorWrite.Uniform(GrassVelocityKeys.ConstantBufferBinding, constants, slot * constantStride, GrassVelocityKeys.ConstantBufferSize),
                DescriptorWrite.Texture(GrassVelocityKeys.AlbedoMapBinding, draw.AlbedoOrDefault),
                DescriptorWrite.SamplerAt(GrassVelocityKeys.AlbedoSamplerBinding, draw.AlbedoSampler),
                DescriptorWrite.Storage(GrassVelocityKeys.BladesBinding, dispatch.Instances)
            ]
        );
    }

    /// <summary>Binds everything the draws need and issues them — the colour pass's blade, indirect.</summary>
    public int Record(ICommandList commands, GrassDrawPass draw, GrassDispatch dispatch) {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(draw);
        ArgumentNullException.ThrowIfNull(dispatch);
        ObjectDisposedException.ThrowIf(disposed, this);

        Draws = 0;

        if (!draw.HasBlade) {
            return 0;
        }

        var blade = draw.Blade;

        commands.BindPipeline(pipeline);
        commands.BindDescriptorSet(DescriptorSetSlot.PerMaterial, descriptors[slot]);
        commands.BindVertexBuffer(0, blade.Vertices);
        commands.BindIndexBuffer(blade.Indices, IndexFormat.UInt32);

        Draws = dispatch.RecordDraws(commands);

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
