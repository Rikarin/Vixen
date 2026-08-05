// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Shaders.Generated;

namespace Vixen.Rendering.Terrain;

/// <summary>The device geometry one foliage type's instances are drawn from.</summary>
/// <param name="Vertices">Its vertex buffer: position, normal, texcoord — 32 bytes a vertex.</param>
/// <param name="Indices">Its index buffer, 32-bit.</param>
/// <param name="IndexCount">How many indices the whole buffer holds.</param>
/// <remarks>
///     ⚠ <b>One mesh for every instance of the type, which is what makes a forest a few draws.</b> A
///     tree differs from its neighbour by its thirty-two-byte cull record and by nothing in its
///     geometry — <see cref="GrassBladeMesh" />'s argument, one order of magnitude up. The LOD
///     levels are index <em>ranges</em> into this one buffer, carried by
///     <see cref="FoliageDraw.Levels" />' first-index and index-count fields, so a level switch is a
///     different indirect command rather than a different binding.
/// </remarks>
public readonly record struct FoliageMesh(BufferHandle Vertices, BufferHandle Indices, int IndexCount) {
    /// <summary>Whether there is anything here to draw.</summary>
    public bool IsValid => IndexCount > 0 && Vertices.IsValid && Indices.IsValid;
}

/// <summary>Binds the pipeline and the per-frame set a volume's foliage is drawn with.</summary>
/// <remarks>
///     <para>
///         <b>[docs/plan/31 § T5]'s draw, and the half <see cref="FoliageCullPass" /> deliberately
///         does not do.</b> That class owns the instance table, the two dispatches and the patched
///         argument buffer, and exposes the three buffers a draw needs without binding any of them —
///         a compute dispatch has no business knowing what a pine looks like. This is what knows:
///         the pipeline built from <c>Foliage.rvn</c>, the constants, and set 2.
///     </para>
///     <para>
///         ⚠ <b>The instance id is the survivor slot, and that is the whole indirection.</b> The
///         cull patched each command's <c>firstInstance</c> to its batch's run, so the vertex stage
///         reads <c>survivors[SV_InstanceID]</c> and then the instance it names — no transform is
///         compacted, copied or re-uploaded between the cull and the draw.
///     </para>
///     <para>
///         ⚠ <b>The set is rung by frames in flight</b>, for <see cref="GrassDrawPass" />'s reason:
///         <see cref="Prepare" /> is an immediate host memcpy of the constants and a descriptor
///         rewrite, and the frame still in flight is reading both. The storage bindings also move
///         per frame — <see cref="FoliageCullPass.Commands" /> is one slot of a ring.
///     </para>
///     <para>
///         ⚠ <b>One albedo for the pass, white by default, and that is this increment's honest
///         ceiling.</b> A <c>.vxfoliage</c> names a mesh and nothing about its material; resolving a
///         model's materials into per-type textures is the impostor bake's problem too, and it is
///         owed to the same increment. Until then a forest draws flat-shaded in its tint — which
///         reads as "no material yet" rather than as foliage that is broken.
///     </para>
/// </remarks>
public sealed class FoliageDrawPass : IDisposable {
    /// <summary>What each ring slot's constant block is aligned to. <c>GrassDispatch</c>'s 256.</summary>
    const int SlotAlignment = 256;

    /// <summary>How many bytes one vertex is: position, normal, texcoord.</summary>
    public const int VertexBytes = 32;

    readonly IGraphicsDevice device;
    readonly int slots;
    readonly long constantStride;

    // Which shader the set is written for — GrassDrawPass's arrangement, for its reason: the
    // preview and the lit shader bind the same resources under different indices.
    readonly bool lit;
    readonly int blockSize;
    readonly uint constantBinding;
    readonly uint albedoMapBinding;
    readonly uint albedoSamplerBinding;
    readonly uint instancesBinding;
    readonly uint survivorsBinding;
    readonly uint parametersBinding;
    readonly SamplerHandle shadowSampler;

    readonly BufferHandle constants;
    readonly BufferHandle defaultStaging;
    readonly SamplerHandle albedoSampler;
    readonly DescriptorSetLayoutHandle setLayout;
    readonly DescriptorSetLayoutHandle emptySetLayout;
    readonly PipelineLayoutHandle layout;
    readonly PipelineHandle pipeline;
    readonly DescriptorSetHandle[] descriptors;

    readonly TextureHandle defaultAlbedo;
    readonly TextureViewHandle defaultAlbedoView;

    int slot;
    bool defaultUploaded;
    bool disposed;

    /// <summary>Creates the pass.</summary>
    /// <param name="device">The device.</param>
    /// <param name="shaders">The two stages <c>Foliage.rvn</c> compiles to.</param>
    /// <param name="output">What it renders into.</param>
    /// <exception cref="ArgumentNullException"><paramref name="device" /> is null.</exception>
    /// <exception cref="ArgumentException">The shaders are not both real.</exception>
    public FoliageDrawPass(IGraphicsDevice device, TerrainShaders shaders, RenderOutput output)
        : this(device, shaders, output, lit: false) { }

    /// <summary>Creates a pass whose set is written for <c>FoliageLit</c> rather than the preview.</summary>
    /// <remarks>
    ///     Internal for <see cref="GrassDrawPass" />'s reason: the lit contract is
    ///     <see cref="TerrainSceneRenderer" />'s, and the frame's values arrive through
    ///     <see cref="Frame" /> before every <see cref="Prepare" />.
    /// </remarks>
    internal FoliageDrawPass(IGraphicsDevice device, TerrainShaders shaders, RenderOutput output, bool lit) {
        ArgumentNullException.ThrowIfNull(device);

        if (!shaders.IsValid) {
            throw new ArgumentException("Foliage needs both a vertex and a fragment stage.", nameof(shaders));
        }

        this.device = device;
        this.lit = lit;
        slots = Math.Max(1, device.FramesInFlight);

        blockSize = lit ? FoliageLitKeys.ConstantBufferSize : FoliageKeys.ConstantBufferSize;
        constantBinding = lit ? FoliageLitKeys.ConstantBufferBinding : FoliageKeys.ConstantBufferBinding;
        albedoMapBinding = lit ? FoliageLitKeys.AlbedoMapBinding : FoliageKeys.AlbedoMapBinding;
        albedoSamplerBinding = lit ? FoliageLitKeys.AlbedoSamplerBinding : FoliageKeys.AlbedoSamplerBinding;
        instancesBinding = lit ? FoliageLitKeys.InstancesBinding : FoliageKeys.InstancesBinding;
        survivorsBinding = lit ? FoliageLitKeys.SurvivorsBinding : FoliageKeys.SurvivorsBinding;
        parametersBinding = lit ? FoliageLitKeys.ParametersBinding : FoliageKeys.ParametersBinding;

        // ⚠ One block per frame in flight, because `device.Write` is an immediate memcpy: a single
        // block rewritten each Prepare is the constants a frame still in flight is reading. The
        // stride is aligned up because a uniform binding's offset has a device-imposed granularity.
        constantStride = ((blockSize + SlotAlignment - 1) / SlotAlignment) * SlotAlignment;

        constants = device.CreateBuffer(
            new(constantStride * slots, BufferUsage.Uniform, MemoryAccess.HostUpload, "foliage constants")
        );

        albedoSampler = device.CreateSampler(new());

        // ⚠ White rather than absent, for the grass pass's reason: a forest with no material path
        // yet draws as its tint, which reads as "no texture yet"; black reads as broken, and an
        // unbound slot is a refused draw on most backends rather than a dark pixel.
        defaultAlbedo = device.CreateTexture(
            new(PixelFormat.Rgba8UNorm, 1, 1, TextureUsage.Sampled | TextureUsage.CopyDestination, Name: "foliage default")
        );

        defaultAlbedoView = device.CreateTextureView(defaultAlbedo);

        // A copy and not a write, because a sampled texture cannot be host-written — staged here and
        // copied on the first Prepare, GrassDrawPass's own arrangement.
        defaultStaging = device.CreateBuffer(
            new(4, BufferUsage.CopySource, MemoryAccess.HostUpload, "foliage default staging")
        );

        device.Write(defaultStaging, 0, [255, 255, 255, 255]);

        var bindings = new List<DescriptorBinding> {
            new(constantBinding, DescriptorKind.UniformBuffer, ShaderStage.Vertex | ShaderStage.Fragment),
            new(albedoMapBinding, DescriptorKind.SampledTexture, ShaderStage.Vertex | ShaderStage.Fragment),
            new(albedoSamplerBinding, DescriptorKind.Sampler, ShaderStage.Vertex | ShaderStage.Fragment),
            new(instancesBinding, DescriptorKind.StorageBuffer, ShaderStage.Vertex | ShaderStage.Fragment),
            new(survivorsBinding, DescriptorKind.StorageBuffer, ShaderStage.Vertex | ShaderStage.Fragment),
            new(parametersBinding, DescriptorKind.StorageBuffer, ShaderStage.Vertex | ShaderStage.Fragment)
        };

        if (lit) {
            // The cascade atlas, on TerrainRenderer's terms: unfiltered and clamped, because the
            // tap compares after sampling and the PCF supplies the softness itself.
            bindings.Add(new(FoliageLitKeys.ShadowMapBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment));
            bindings.Add(new(FoliageLitKeys.ShadowSamplerBinding, DescriptorKind.Sampler, ShaderStage.Fragment));

            shadowSampler = device.CreateSampler(SamplerDescription.PointClamp);
        }

        setLayout = device.CreateDescriptorSetLayout(
            new(DescriptorSetSlot.PerMaterial, [.. bindings], "foliage")
        );

        // Padded below the material set on TerrainRenderer's terms: the shader's bindings are set 2
        // and a pipeline layout is positional, so the two unused slots hold one empty layout twice.
        emptySetLayout = device.CreateDescriptorSetLayout(new(DescriptorSetSlot.PerFrame, [], "foliage empty"));
        layout = device.CreatePipelineLayout(new([emptySetLayout, emptySetLayout, setLayout], [], "foliage"));

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
                        VertexBytes,
                        [
                            // Both variants share one location set — the base shader's streams come
                            // first in both, unlike the grass pair's differing counts.
                            new(lit ? FoliageLitKeys.PositionLocation : FoliageKeys.PositionLocation, VertexFormat.Float32X3, 0),
                            new(lit ? FoliageLitKeys.NormalLocation : FoliageKeys.NormalLocation, VertexFormat.Float32X3, 12),
                            new(lit ? FoliageLitKeys.TexcoordLocation : FoliageKeys.TexcoordLocation, VertexFormat.Float32X2, 24)
                        ]
                    )
                ],
                PrimitiveTopology.TriangleList,

                // ⚠ Back-face culled, unlike the grass — a blade is a card seen from both sides and
                // a trunk is a closed mesh. A type whose canopy is authored as single-sided cards
                // will show it here, and the answer is the mesh's, not a pass that draws every
                // forest twice over.
                RasterizerState.Default,
                DepthStencilState.Default,
                output.DepthFormat,
                output.SampleCount,
                "foliage"
            )
        );

        descriptors = new DescriptorSetHandle[slots];

        for (var index = 0; index < slots; index++) {
            descriptors[index] = device.CreateDescriptorSet(setLayout);
        }
    }

    /// <summary>What the instances are textured with, or default for the built-in white.</summary>
    public TextureViewHandle Albedo { get; set; }

    /// <summary>How many indirect draws the last <see cref="Record" /> issued.</summary>
    public int Draws { get; private set; }

    /// <summary>How many batches the last <see cref="Record" /> skipped for want of a mesh.</summary>
    /// <remarks>
    ///     ⚠ <b>A separate answer from <see cref="Draws" /> being low.</b> A forest whose pines have
    ///     not loaded and a forest that culled to nothing record alike, and only one of them fixes
    ///     itself next frame.
    /// </remarks>
    public int MissingMeshes { get; private set; }

    /// <summary>The frame's lighting, filled by <see cref="TerrainSceneRenderer" /> before every prepare.</summary>
    /// <remarks>The same instance the terrains read — see <see cref="TerrainRenderer.Frame" />.</remarks>
    internal TerrainFrameLighting? Frame { get; set; }

    /// <summary>What the velocity pass binds where this pass binds its albedo — the grass pass's
    ///     borrow, for its reason: the default's first-frame upload is this pass's.</summary>
    internal TextureViewHandle AlbedoOrDefault => Albedo.IsValid ? Albedo : defaultAlbedoView;

    /// <summary>And the sampler beside it, on the same terms.</summary>
    internal SamplerHandle AlbedoSampler => albedoSampler;

    /// <summary>Writes the frame's constants and points the set at the cull's buffers.</summary>
    /// <param name="commands">Where the first frame's default-albedo upload is recorded.</param>
    /// <param name="cull">The cull whose survivors, parameters and instances the draw reads.</param>
    /// <param name="view">The camera's combined matrix and position.</param>
    /// <param name="tint">The darkest and lightest an instance may be tinted. Default is no tint.</param>
    /// <exception cref="ArgumentNullException">There is no command list or no cull.</exception>
    /// <exception cref="ObjectDisposedException">The pass is gone.</exception>
    /// <remarks>
    ///     ⚠ <b>Called after the cull's own <see cref="FoliageCullPass.Prepare" /></b>, because
    ///     <see cref="FoliageCullPass.Commands" /> answers with the ring slot that prepare claimed —
    ///     the draw and the dispatches have to be the same frame's.
    /// </remarks>
    public void Prepare(
        ICommandList commands,
        FoliageCullPass cull,
        in TerrainView view,
        Vector2 tint = default
    ) {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(cull);
        ObjectDisposedException.ThrowIf(disposed, this);

        slot = (slot + 1) % slots;

        // ⚠ Once, on the first frame that records anything — the texture is created Undefined, and
        // sampling without the transitions is garbage on one driver and a validation error on another.
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
        var range = tint == default ? new Vector2(1f, 1f) : tint;

        if (lit) {
            WriteLitBlock(block, view, range);
        } else {
            new FoliageConstants {
                ViewProjection = view.ViewProjection,
                TintRange = range
            }.Write(block);
        }

        device.Write(constants, slot * constantStride, block);

        List<DescriptorWrite> writes = [
            DescriptorWrite.Uniform(constantBinding, constants, slot * constantStride, blockSize),
            DescriptorWrite.Texture(albedoMapBinding, Albedo.IsValid ? Albedo : defaultAlbedoView),
            DescriptorWrite.SamplerAt(albedoSamplerBinding, albedoSampler),
            DescriptorWrite.Storage(instancesBinding, cull.Instances),
            DescriptorWrite.Storage(survivorsBinding, cull.Survivors),
            DescriptorWrite.Storage(parametersBinding, cull.Parameters)
        ];

        if (lit) {
            // The frame's atlas, or the white default while nothing has resolved it — the same
            // whole-set argument TerrainRenderer.BindFrameResources makes.
            var atlas = Frame is { ShadowAtlas.IsValid: true } present ? present.ShadowAtlas : defaultAlbedoView;

            writes.Add(DescriptorWrite.Texture(FoliageLitKeys.ShadowMapBinding, atlas));
            writes.Add(DescriptorWrite.SamplerAt(FoliageLitKeys.ShadowSamplerBinding, shadowSampler));
        }

        device.UpdateDescriptorSet(descriptors[slot], CollectionsMarshal.AsSpan(writes));
    }

    /// <summary>Fills the lit shader's block: the frame's lighting, then the preview's own values.</summary>
    void WriteLitBlock(byte[] block, in TerrainView view, Vector2 tint) {
        var frame = Frame;

        new FoliageLitConstants {
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
            TintRange = tint
        }.Write(block);

        if (frame is null) {
            return;
        }

        for (var cascade = 0; cascade < FoliageLitCascadesElement.Count; cascade++) {
            // The lit terrain, grass and foliage carry the same four records; the generated element
            // types differ only in the block offset each bakes.
            new FoliageLitCascadesElement {
                ViewProjection = frame.Cascades[cascade].ViewProjection,
                Split = frame.Cascades[cascade].Split,
                DepthScale = frame.Cascades[cascade].DepthScale
            }.Write(block, cascade);
        }
    }

    /// <summary>Binds everything the draws need and issues them.</summary>
    /// <param name="commands">Where to record.</param>
    /// <param name="cull">The cull whose indirect commands are read.</param>
    /// <param name="meshes">Each palette type's device mesh, by palette index.</param>
    /// <returns>How many indirect draws were issued.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ObjectDisposedException">The pass is gone.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>One draw per level per batch, empty levels included</b> — the cull's own
    ///         contract: a level with no survivors kept the host's zero instance count, and issuing
    ///         it is what lets the draw bind level N's range at slot N without reading anything back.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A batch whose type has no mesh yet is skipped and counted, not defaulted.</b>
    ///         There is no honest stand-in for a tree — the grass's built-in blade exists because
    ///         every blade is the same shape, and no such fact exists here.
    ///     </para>
    /// </remarks>
    public int Record(ICommandList commands, FoliageCullPass cull, IReadOnlyDictionary<int, FoliageMesh> meshes) {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(cull);
        ArgumentNullException.ThrowIfNull(meshes);
        ObjectDisposedException.ThrowIf(disposed, this);

        Draws = 0;
        MissingMeshes = 0;

        if (cull.BatchCount == 0) {
            return 0;
        }

        commands.BindPipeline(pipeline);
        commands.BindDescriptorSet(DescriptorSetSlot.PerMaterial, descriptors[slot]);

        var bound = -1;

        for (var batch = 0; batch < cull.BatchCount; batch++) {
            var type = cull.TypeOf(batch);

            if (!meshes.TryGetValue(type, out var mesh) || !mesh.IsValid) {
                MissingMeshes++;
                continue;
            }

            // Rebound only when the type changes — consecutive batches of one type are one forest.
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

    /// <summary>Interleaves a mesh's arrays into the layout the pipeline reads.</summary>
    /// <param name="device">The device the buffers live on.</param>
    /// <param name="positions">One position per vertex.</param>
    /// <param name="normals">One normal per vertex, or empty for straight up.</param>
    /// <param name="texcoords">One texcoord per vertex, or empty for zero.</param>
    /// <param name="indices">Three indices per triangle.</param>
    /// <param name="name">What the buffers are called in a capture.</param>
    /// <returns>The device mesh. The caller owns both buffers.</returns>
    /// <exception cref="ArgumentNullException">An array is null.</exception>
    /// <exception cref="ArgumentException">There is nothing to draw.</exception>
    /// <remarks>
    ///     Here rather than beside the asset system, because the 32-byte layout is this pass's
    ///     pipeline contract and nobody else's — the attribute offsets above and these writes are
    ///     the two halves of one stride, and a helper elsewhere would let them drift apart.
    /// </remarks>
    public static FoliageMesh UploadMesh(
        IGraphicsDevice device,
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<Vector3> normals,
        ReadOnlySpan<Vector2> texcoords,
        ReadOnlySpan<int> indices,
        string name = "foliage mesh"
    ) {
        ArgumentNullException.ThrowIfNull(device);

        if (positions.IsEmpty || indices.IsEmpty) {
            throw new ArgumentException(
                "A foliage mesh needs positions and indices; an empty one would draw nothing and "
                + "say nothing.",
                positions.IsEmpty ? nameof(positions) : nameof(indices)
            );
        }

        var vertices = new byte[positions.Length * VertexBytes];
        var span = vertices.AsSpan();

        for (var vertex = 0; vertex < positions.Length; vertex++) {
            var at = vertex * VertexBytes;

            MemoryMarshal.Write(span[at..], in positions[vertex]);

            // An attribute the mesh does not have is a usable default rather than garbage: straight
            // up shades as a canopy from above, which is the least wrong answer a missing normal has.
            var normal = vertex < normals.Length ? normals[vertex] : Vector3.UnitY;

            MemoryMarshal.Write(span[(at + 12)..], in normal);

            var texcoord = vertex < texcoords.Length ? texcoords[vertex] : Vector2.Zero;

            MemoryMarshal.Write(span[(at + 24)..], in texcoord);
        }

        var faces = new uint[indices.Length];

        for (var index = 0; index < indices.Length; index++) {
            faces[index] = (uint)indices[index];
        }

        var vertexBuffer = device.CreateBuffer(
            new(vertices.Length, BufferUsage.Vertex, MemoryAccess.HostUpload, $"{name} vertices")
        );

        var indexBuffer = device.CreateBuffer(
            new((long)faces.Length * sizeof(uint), BufferUsage.Index, MemoryAccess.HostUpload, $"{name} indices")
        );

        device.Write(vertexBuffer, 0, vertices);
        device.Write(indexBuffer, 0, MemoryMarshal.AsBytes<uint>(faces));

        return new(vertexBuffer, indexBuffer, faces.Length);
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

        device.Destroy(constants);
        device.Destroy(defaultStaging);
        device.Destroy(albedoSampler);
        device.Destroy(defaultAlbedoView);
        device.Destroy(defaultAlbedo);
        device.Destroy(pipeline);
        device.Destroy(layout);
        device.Destroy(setLayout);
        device.Destroy(emptySetLayout);
    }
}
