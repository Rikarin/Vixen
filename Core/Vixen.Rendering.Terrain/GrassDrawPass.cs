// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Foliage;
using Vixen.Graphics;
using Vixen.Shaders.Generated;

namespace Vixen.Rendering.Terrain;

/// <summary>The blade every grass instance is drawn from.</summary>
/// <param name="Vertices">Its vertex buffer: position, normal, texcoord.</param>
/// <param name="Indices">Its index buffer, 32-bit.</param>
/// <param name="IndexCount">How many indices one blade is.</param>
/// <remarks>
///     ⚠ <b>One mesh for every blade in the world, which is what makes the field one draw per
///     cell.</b> A blade differs from its neighbour by its instance record — position, rotation,
///     height, tint — and by nothing in its geometry, so uploading a mesh per blade would be
///     uploading the same eight vertices fifty thousand times.
/// </remarks>
public readonly record struct GrassBladeMesh(BufferHandle Vertices, BufferHandle Indices, int IndexCount);

/// <summary>Binds the pipeline, the blade and the material a grass field is drawn with.</summary>
/// <remarks>
///     <para>
///         <b>[docs/plan/31 § T6]'s draw, and the half <see cref="GrassDispatch" /> deliberately does
///         not do.</b> That class owns the scatter, the cull and the argument buffer, and its
///         <see cref="GrassDispatch.RecordDraws" /> binds nothing on purpose — a compute dispatch has
///         no business knowing what a blade looks like. This is what knows: the pipeline built from
///         <c>Grass.rvn</c>, the blade mesh, the albedo, and set 2.
///     </para>
///     <para>
///         ⚠ <b>The blades buffer is rebound every frame rather than once.</b>
///         <see cref="GrassDispatch" /> writes into a ring and the buffer handle is stable, but the
///         descriptor set is per frame-in-flight for the same reason the terrain's is: writing a set
///         a frame in flight is still reading is a race the validation layers catch and the driver
///         does not. The constants ring with the sets — a set per slot pointing at one block would
///         be the same race one indirection later.
///     </para>
///     <para>
///         ⚠ <b>Two-sided, and that is not a default worth inheriting.</b> A blade is a flat quad seen
///         from both sides as its instance rotates; culling its back faces makes half a field vanish
///         depending on where a person stands, which reads as the scatter being wrong rather than as
///         the raster state being wrong.
///     </para>
/// </remarks>
public sealed class GrassDrawPass : IDisposable {
    /// <summary>What each ring slot's constant block is aligned to. <c>GrassDispatch</c>'s 256.</summary>
    const int SlotAlignment = 256;

    readonly IGraphicsDevice device;
    readonly int slots;
    readonly long constantStride;

    // Which shader the set is written for — TerrainRenderer's arrangement, for its reason: the
    // preview and the lit shader bind the same resources under different indices.
    readonly bool lit;
    readonly int blockSize;
    readonly uint constantBinding;
    readonly uint albedoMapBinding;
    readonly uint albedoSamplerBinding;
    readonly uint bladesBinding;
    readonly SamplerHandle shadowSampler;

    readonly BufferHandle constants;
    readonly BufferHandle defaultStaging;
    readonly SamplerHandle albedoSampler;
    readonly DescriptorSetLayoutHandle setLayout;
    readonly PipelineLayoutHandle layout;
    readonly PipelineHandle pipeline;
    readonly DescriptorSetHandle[] descriptors;

    readonly TextureHandle defaultAlbedo;
    readonly TextureViewHandle defaultAlbedoView;

    readonly BufferHandle bladeVertices;
    readonly BufferHandle bladeIndices;

    int slot;
    bool defaultUploaded;
    bool disposed;

    /// <summary>Creates the pass.</summary>
    /// <param name="device">The device.</param>
    /// <param name="shaders">The two stages <c>Grass.rvn</c> compiles to.</param>
    /// <param name="output">What it renders into.</param>
    /// <exception cref="ArgumentNullException"><paramref name="device" /> is null.</exception>
    /// <exception cref="ArgumentException">The shaders are not both real.</exception>
    public GrassDrawPass(IGraphicsDevice device, TerrainShaders shaders, RenderOutput output)
        : this(device, shaders, output, lit: false) { }

    /// <summary>Creates a pass whose set is written for <c>GrassLit</c> rather than the preview.</summary>
    /// <remarks>
    ///     Internal for <see cref="TerrainRenderer" />'s reason: the lit contract is
    ///     <see cref="TerrainSceneRenderer" />'s, and the frame's values arrive through
    ///     <see cref="Frame" /> before every <see cref="Prepare" />.
    /// </remarks>
    internal GrassDrawPass(IGraphicsDevice device, TerrainShaders shaders, RenderOutput output, bool lit) {
        ArgumentNullException.ThrowIfNull(device);

        if (!shaders.IsValid) {
            throw new ArgumentException("Grass needs both a vertex and a fragment stage.", nameof(shaders));
        }

        this.device = device;
        this.lit = lit;
        slots = Math.Max(1, device.FramesInFlight);

        blockSize = lit ? GrassLitKeys.ConstantBufferSize : GrassKeys.ConstantBufferSize;
        constantBinding = lit ? GrassLitKeys.ConstantBufferBinding : GrassKeys.ConstantBufferBinding;
        albedoMapBinding = lit ? GrassLitKeys.AlbedoMapBinding : GrassKeys.AlbedoMapBinding;
        albedoSamplerBinding = lit ? GrassLitKeys.AlbedoSamplerBinding : GrassKeys.AlbedoSamplerBinding;
        bladesBinding = lit ? GrassLitKeys.BladesBinding : GrassKeys.BladesBinding;

        // ⚠ One block per frame in flight, because `device.Write` is an immediate memcpy: a single
        // block rewritten each Prepare is the constants a frame still in flight is reading. The
        // stride is aligned up because a uniform binding's offset has a device-imposed granularity.
        constantStride = ((blockSize + SlotAlignment - 1) / SlotAlignment) * SlotAlignment;

        constants = device.CreateBuffer(
            new(constantStride * slots, BufferUsage.Uniform, MemoryAccess.HostUpload, "grass constants")
        );

        albedoSampler = device.CreateSampler(new());

        // ⚠ White rather than absent, for the reason the terrain's layer default is white: a field
        // with no albedo assigned draws as its tint, which reads as "no texture yet". A black default
        // reads as grass that is broken, and an unbound one is a set that binds partially or not at
        // all — which on most backends is a refused draw rather than a dark pixel.
        defaultAlbedo = device.CreateTexture(
            new(PixelFormat.Rgba8UNorm, 1, 1, TextureUsage.Sampled | TextureUsage.CopyDestination, Name: "grass default")
        );

        defaultAlbedoView = device.CreateTextureView(defaultAlbedo);

        // ⚠ A copy and not a write, because a sampled texture cannot be host-written. It needs a
        // command list the constructor does not have, so the texels are staged here and copied on
        // the first Prepare — until that copy lands the texture's contents are undefined, which
        // samples as garbage rather than as white.
        defaultStaging = device.CreateBuffer(
            new(4, BufferUsage.CopySource, MemoryAccess.HostUpload, "grass default staging")
        );

        device.Write(defaultStaging, 0, [255, 255, 255, 255]);

        var bindings = new List<DescriptorBinding> {
            new(constantBinding, DescriptorKind.UniformBuffer, ShaderStage.Vertex | ShaderStage.Fragment),
            new(albedoMapBinding, DescriptorKind.SampledTexture, ShaderStage.Vertex | ShaderStage.Fragment),
            new(albedoSamplerBinding, DescriptorKind.Sampler, ShaderStage.Vertex | ShaderStage.Fragment),
            new(bladesBinding, DescriptorKind.StorageBuffer, ShaderStage.Vertex | ShaderStage.Fragment)
        };

        if (lit) {
            // The cascade atlas, on TerrainRenderer's terms: unfiltered and clamped, because the
            // tap compares after sampling and the PCF supplies the softness itself.
            bindings.Add(new(GrassLitKeys.ShadowMapBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment));
            bindings.Add(new(GrassLitKeys.ShadowSamplerBinding, DescriptorKind.Sampler, ShaderStage.Fragment));

            shadowSampler = device.CreateSampler(SamplerDescription.PointClamp);
        }

        setLayout = device.CreateDescriptorSetLayout(
            new(DescriptorSetSlot.PerMaterial, [.. bindings], "grass")
        );

        layout = device.CreatePipelineLayout(new([setLayout], [], "grass"));

        // Every colour format the output declares — TerrainRenderer's reason: a split frame hands
        // the lit shader three targets, and a pipeline declaring fewer is refused at the draw.
        var attachments = new ColourTargetState[Math.Max(output.ColourCount, 1)];

        for (var target = 0; target < attachments.Length; target++) {
            attachments[target] = new(
                output.ColourCount > 0 ? output.ColourFormats[target] : PixelFormat.Rgba8UNorm,
                BlendState.Opaque
            );
        }

        pipeline = device.CreateGraphicsPipeline(
            new(
                shaders.Vertex,
                shaders.Fragment,
                layout,
                attachments,
                [
                    new(
                        BladeVertex.SizeInBytes,
                        [
                            // The lit shader's extra streams shift its input locations — a location
                            // comes from declaration order, the same way a binding index does.
                            new(lit ? GrassLitKeys.PositionLocation : GrassKeys.PositionLocation, VertexFormat.Float32X3, 0),
                            new(lit ? GrassLitKeys.NormalLocation : GrassKeys.NormalLocation, VertexFormat.Float32X3, 12),
                            new(lit ? GrassLitKeys.TexcoordLocation : GrassKeys.TexcoordLocation, VertexFormat.Float32X2, 24)
                        ]
                    )
                ],
                PrimitiveTopology.TriangleList,
                RasterizerState.Default with { Cull = CullMode.None },
                DepthStencilState.Default,
                output.DepthFormat,
                output.SampleCount,
                "grass"
            )
        );

        descriptors = new DescriptorSetHandle[slots];

        for (var index = 0; index < slots; index++) {
            descriptors[index] = device.CreateDescriptorSet(setLayout);
        }

        (bladeVertices, bladeIndices, DefaultBladeIndices) = BuildBlade(device);
        Blade = new(bladeVertices, bladeIndices, DefaultBladeIndices);
    }

    /// <summary>How many indices the built-in blade is.</summary>
    public int DefaultBladeIndices { get; }

    /// <summary>What every instance is drawn from.</summary>
    /// <remarks>
    ///     ⚠ <b>Settable, and the built-in one is a fallback rather than the design.</b> A project's
    ///     grass is a mesh an artist made; what the built-in blade is for is that a field scattered
    ///     before anybody has assigned one draws something, which is the difference between "no mesh
    ///     yet" and "the grass system does not work".
    /// </remarks>
    public GrassBladeMesh Blade { get; set; }

    /// <summary>What the blades are textured with, or default for the built-in white.</summary>
    public TextureViewHandle Albedo { get; set; }

    /// <summary>How many draws the last <see cref="Record" /> issued.</summary>
    public int Draws { get; private set; }

    /// <summary>Whether the last <see cref="Record" /> had a mesh to draw with.</summary>
    /// <remarks>
    ///     ⚠ <b>A separate answer from <see cref="Draws" /> being zero.</b> A field with no cells and
    ///     a field whose blade mesh is empty both record nothing, and only one of them is a bug.
    /// </remarks>
    public bool HasBlade => Blade is { IndexCount: > 0 } blade && blade.Vertices.IsValid && blade.Indices.IsValid;

    /// <summary>The indirect template <see cref="Blade" /> is drawn with, for the dispatch.</summary>
    /// <remarks>
    ///     ⚠ <b>Computed from the blade at the moment it is read, which is what makes the template
    ///     coherent whatever order a frame calls the two passes in.</b> The old shape — this pass
    ///     patching a template stored on the dispatch — held the right number only if the patch
    ///     happened before the dispatch's <see cref="GrassDispatch.Prepare" /> baked it, and an
    ///     indirect command whose index count is zero draws nothing however many instances survived.
    /// </remarks>
    public DrawCommand MeshTemplate => new() { IndexCount = (uint)Math.Max(0, Blade.IndexCount) };

    /// <summary>Writes the frame's constants and points the set at this frame's blades.</summary>
    /// <param name="commands">Where the first frame's default-albedo upload is recorded.</param>
    /// <param name="dispatch">The scatter whose instance buffer the draw reads.</param>
    /// <param name="view">The camera's combined matrix and position.</param>
    /// <param name="wind">How the blades move — the grass type's own, not a second declaration of one.</param>
    /// <param name="time">
    ///     The clock the waves are sampled at, in seconds.
    ///     ⚠ A parameter rather than read from a clock here: two views of one frame must sample the
    ///     same instant, and a pass that read its own would make a split-screen's two halves disagree
    ///     about where the wind is — visible exactly at the seam, which is where a person is looking.
    /// </param>
    /// <param name="tint">The darkest and lightest a blade may be tinted. Default is a mild spread.</param>
    /// <param name="fade">Where the field starts and finishes fading out, in metres.</param>
    /// <exception cref="ArgumentNullException">There is no dispatch.</exception>
    /// <exception cref="ObjectDisposedException">The pass is gone.</exception>
    /// <remarks>
    ///     ⚠ <b>The mesh's index count is not written into the dispatch here.</b>
    ///     <see cref="MeshTemplate" /> is what carries it, as a parameter of
    ///     <see cref="GrassDispatch.Prepare" /> — a template patched from one pass and baked by
    ///     another held the right number only in one calling order, and the wrong order drew nothing
    ///     with every counter reading healthy.
    /// </remarks>
    public void Prepare(
        ICommandList commands,
        GrassDispatch dispatch,
        in TerrainView view,
        in GrassWind wind = default,
        float time = 0f,
        Vector2 tint = default,
        Vector2 fade = default
    ) {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(dispatch);
        ObjectDisposedException.ThrowIf(disposed, this);

        slot = (slot + 1) % slots;

        // ⚠ Once, on the first frame that records anything. The texture is created Undefined, so
        // the copy needs a layout transition on both sides — sampling without them is undefined
        // contents on one driver and a validation error on another.
        if (!defaultUploaded) {
            defaultUploaded = true;

            commands.Barrier(
                new([], [new TextureBarrier(defaultAlbedo, ResourceState.Undefined, ResourceState.CopyDestination)])
            );

            commands.CopyBufferToTexture(defaultStaging, 0, new(defaultAlbedo), new(1, 1, 1));

            commands.Barrier(
                new([], [new TextureBarrier(defaultAlbedo, ResourceState.CopyDestination, ResourceState.ShaderRead)])
            );
        }

        var block = new byte[blockSize];

        if (lit) {
            WriteLitBlock(block, view, wind, time, tint, fade);
        } else {
            new GrassConstants {
                ViewProjection = view.ViewProjection,
                ViewPosition = view.Position,
                Time = time,
                WindDirection = wind.Direction,
                WindStrength = wind.Strength,
                WindFrequency = wind.Frequency,
                WindSpeed = wind.Speed,
                WindFlutter = wind.Flutter,
                WindHeight = wind.Height,
                TintRange = tint == default ? new(0.85f, 1.1f) : tint,
                FadeRange = fade == default ? new(60f, 80f) : fade
            }.Write(block);
        }

        device.Write(constants, slot * constantStride, block);

        List<DescriptorWrite> writes = [
            DescriptorWrite.Uniform(constantBinding, constants, slot * constantStride, blockSize),
            DescriptorWrite.Texture(albedoMapBinding, Albedo.IsValid ? Albedo : defaultAlbedoView),
            DescriptorWrite.SamplerAt(albedoSamplerBinding, albedoSampler),
            DescriptorWrite.Storage(bladesBinding, dispatch.Instances)
        ];

        if (lit) {
            // The frame's atlas, or the white default while nothing has resolved it — the same
            // whole-set argument TerrainRenderer.BindFrameResources makes.
            var atlas = Frame is { ShadowAtlas.IsValid: true } present ? present.ShadowAtlas : defaultAlbedoView;

            writes.Add(DescriptorWrite.Texture(GrassLitKeys.ShadowMapBinding, atlas));
            writes.Add(DescriptorWrite.SamplerAt(GrassLitKeys.ShadowSamplerBinding, shadowSampler));
        }

        device.UpdateDescriptorSet(descriptors[slot], CollectionsMarshal.AsSpan(writes));
    }

    /// <summary>The frame's lighting, filled by <see cref="TerrainSceneRenderer" /> before every prepare.</summary>
    /// <remarks>The same instance the terrains read — see <see cref="TerrainRenderer.Frame" />.</remarks>
    internal TerrainFrameLighting? Frame { get; set; }

    /// <summary>Fills the lit shader's block: the frame's lighting, then the preview's own values.</summary>
    void WriteLitBlock(
        byte[] block,
        in TerrainView view,
        in GrassWind wind,
        float time,
        Vector2 tint,
        Vector2 fade
    ) {
        var frame = Frame;

        new GrassLitConstants {
            LightDirection = frame?.LightDirection ?? Vector3.Zero,
            ShadowConstantBias = frame?.ShadowConstantBias ?? 0f,
            LightColor = frame?.LightColor ?? Vector3.Zero,
            ShadowSlopeBias = frame?.ShadowSlopeBias ?? 0f,
            ShadowFadeRange = frame?.ShadowFadeRange ?? 1f,
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
            ViewProjection = view.ViewProjection,
            ViewPosition = view.Position,
            Time = time,
            WindDirection = wind.Direction,
            WindStrength = wind.Strength,
            WindFrequency = wind.Frequency,
            WindSpeed = wind.Speed,
            WindFlutter = wind.Flutter,
            WindHeight = wind.Height,
            TintRange = tint == default ? new(0.85f, 1.1f) : tint,
            FadeRange = fade == default ? new(60f, 80f) : fade
        }.Write(block);

        if (frame is null) {
            return;
        }

        for (var cascade = 0; cascade < GrassLitCascadesElement.Count; cascade++) {
            // The lit terrain and the lit grass carry the same four records; the two generated
            // element types differ only in the block offset each bakes.
            new GrassLitCascadesElement {
                ViewProjection = frame.Cascades[cascade].ViewProjection,
                Split = frame.Cascades[cascade].Split,
                DepthScale = frame.Cascades[cascade].DepthScale
            }.Write(block, cascade);
        }
    }

    /// <summary>Binds everything the draw needs and issues it.</summary>
    /// <param name="commands">Where to record.</param>
    /// <param name="dispatch">The scatter whose indirect commands are read.</param>
    /// <returns>How many draws were issued.</returns>
    /// <exception cref="ArgumentNullException">There is no command list or no dispatch.</exception>
    /// <exception cref="ObjectDisposedException">The pass is gone.</exception>
    public int Record(ICommandList commands, GrassDispatch dispatch) {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(dispatch);
        ObjectDisposedException.ThrowIf(disposed, this);

        Draws = 0;

        if (!HasBlade) {
            return 0;
        }

        var blade = Blade;

        commands.BindPipeline(pipeline);
        commands.BindDescriptorSet(DescriptorSetSlot.PerMaterial, descriptors[slot]);
        commands.BindVertexBuffer(0, blade.Vertices);
        commands.BindIndexBuffer(blade.Indices, IndexFormat.UInt32);

        Draws = dispatch.RecordDraws(commands);

        return Draws;
    }

    /// <summary>One vertex of the built-in blade.</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct BladeVertex {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector2 Texcoord;

        public const int SizeInBytes = 32;
    }

    /// <summary>Builds the tapered blade a field draws before anybody assigns a mesh.</summary>
    /// <remarks>
    ///     ⚠ <b>Three segments rather than one quad, because the wind bends it.</b>
    ///     <c>Grass.rvn</c>'s vertex stage displaces by height, so a two-triangle blade bends as a
    ///     rigid card leaning over — which at any strength above a breeze reads as the grass being
    ///     knocked flat rather than swaying.
    /// </remarks>
    static (BufferHandle Vertices, BufferHandle Indices, int IndexCount) BuildBlade(IGraphicsDevice device) {
        const int segments = 3;

        var vertices = new BladeVertex[(segments + 1) * 2];
        var indices = new uint[segments * 6];

        for (var step = 0; step <= segments; step++) {
            var v = step / (float)segments;

            // Tapered: full width at the root, a point at the tip.
            var halfWidth = 0.5f * (1f - v) * 0.06f;

            vertices[step * 2] = new() { Position = new(-halfWidth, v, 0f), Normal = new(0f, 0f, 1f), Texcoord = new(0f, v) };
            vertices[(step * 2) + 1] = new() { Position = new(halfWidth, v, 0f), Normal = new(0f, 0f, 1f), Texcoord = new(1f, v) };
        }

        var at = 0;

        for (var step = 0; step < segments; step++) {
            var lower = (uint)(step * 2);

            indices[at++] = lower;
            indices[at++] = lower + 2;
            indices[at++] = lower + 1;

            indices[at++] = lower + 1;
            indices[at++] = lower + 2;
            indices[at++] = lower + 3;
        }

        var vertexBuffer = device.CreateBuffer(
            new(
                (long)vertices.Length * BladeVertex.SizeInBytes,
                BufferUsage.Vertex,
                MemoryAccess.HostUpload,
                "grass blade vertices"
            )
        );

        var indexBuffer = device.CreateBuffer(
            new((long)indices.Length * sizeof(uint), BufferUsage.Index, MemoryAccess.HostUpload, "grass blade indices")
        );

        device.Write(vertexBuffer, 0, MemoryMarshal.AsBytes<BladeVertex>(vertices));
        device.Write(indexBuffer, 0, MemoryMarshal.AsBytes<uint>(indices));

        return (vertexBuffer, indexBuffer, indices.Length);
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        if (shadowSampler.IsValid) {
            device.Destroy(shadowSampler);
        }

        foreach (var set in descriptors) {
            device.Destroy(set);
        }

        device.Destroy(bladeVertices);
        device.Destroy(bladeIndices);
        device.Destroy(constants);
        device.Destroy(defaultStaging);
        device.Destroy(albedoSampler);
        device.Destroy(defaultAlbedoView);
        device.Destroy(defaultAlbedo);
        device.Destroy(pipeline);
        device.Destroy(layout);
        device.Destroy(setLayout);
    }
}
