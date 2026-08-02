// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics;

namespace Vixen.Rendering.Lighting;

/// <summary>
///     A prefiltered environment on the device: the cube, its view and how it is sampled.
/// </summary>
/// <remarks>
///     <para>
///         The step between <see cref="EnvironmentBaker" /> and <see cref="EnvironmentLight" />, and
///         the one that was missing. <c>Prefilter</c> answers with <see cref="CubeImage" />s — arrays
///         of <see cref="Vector3" /> in managed memory, which is what makes the convolution checkable
///         against its own integral — and <see cref="EnvironmentLight.Prefiltered" /> wants a
///         <see cref="TextureViewHandle" />. Nothing turned one into the other, so a project that
///         baked a sky had no way to bind it, and set 0's <c>environment</c> binding had nothing to
///         fill it with.
///     </para>
///     <para>
///         <b>That is not a cosmetic gap.</b> <c>ForwardPlus</c> declares <c>environment</c>,
///         <c>probes</c> and their two samplers whatever the permutations say, and
///         <see cref="EffectSetWriter" /> writes every binding of a set or none — so a frame with no
///         environment does not lose its reflections, it loses <em>every draw in the pass</em>. The
///         probe slots fall back to this same cube (see <see cref="SceneLighting" />), which is why
///         one baked environment closes four of the set's thirteen bindings.
///     </para>
///     <para>
///         <b>The same shape as <see cref="GlobalDistanceFieldTexture" /> and
///         <see cref="IrradianceFieldTexture" />, deliberately.</b> Allocate on the first upload,
///         copy through one staging buffer, transition into <see cref="ResourceState.ShaderRead" />
///         and stay there. These are not graph resources — they are named into a descriptor set — so
///         nothing else in the frame transitions them and this has to.
///     </para>
///     <para>
///         <b>Uploaded once, unless something re-bakes.</b> An environment is a property of a level
///         rather than of a frame, so <see cref="Upload" /> after the first is a no-op until
///         <see cref="Invalidate" /> says the chain changed. A sky that animates wants a device-side
///         convolution rather than this.
///     </para>
/// </remarks>
public sealed class EnvironmentTexture : IDisposable {
    readonly CubeImage[] chain;
    readonly IGraphicsDevice device;

    TextureHandle texture;
    TextureViewHandle view;
    SamplerHandle sampler;
    BufferHandle staging;
    bool uploaded;
    bool disposed;

    /// <summary>Mirrors one prefiltered chain.</summary>
    /// <param name="graphics">The device the cube lives on.</param>
    /// <param name="prefiltered">
    ///     The chain, coarsest level last, as <see cref="EnvironmentBaker.Prefilter" /> returns it.
    /// </param>
    /// <param name="format">What the levels are stored as.</param>
    /// <exception cref="ArgumentNullException">There is no device or no chain.</exception>
    /// <exception cref="ArgumentException">
    ///     The chain is empty, or a level is not half the one above it.
    /// </exception>
    /// <remarks>
    ///     ⚠ <b>The cube, its view and its sampler are created here rather than on the first upload,
    ///     and the difference is not tidiness.</b> <see cref="EnvironmentLight" /> holds a
    ///     <see cref="TextureViewHandle" /> rather than a reference to this, so whatever
    ///     <see cref="Apply" /> hands it is what the frame's set is filled with for ever. Creating the
    ///     view lazily meant a light configured at load time held an invalid handle, the writer found
    ///     nothing for <c>environment</c>, and set 0 never bound — a black screen produced by an
    ///     allocation that had merely not happened yet. The contents still wait for a command list;
    ///     the handles do not.
    /// </remarks>
    public EnvironmentTexture(
        IGraphicsDevice graphics,
        IReadOnlyList<CubeImage> prefiltered,
        PixelFormat format = PixelFormat.Rgba16Float
    ) {
        ArgumentNullException.ThrowIfNull(graphics);
        ArgumentNullException.ThrowIfNull(prefiltered);

        if (prefiltered.Count == 0) {
            throw new ArgumentException(
                "A prefiltered chain needs at least one level. An environment with no levels is one a "
                + "shader would sample at a mip that does not exist.",
                nameof(prefiltered)
            );
        }

        chain = [.. prefiltered];

        // ⚠ Checked rather than assumed, because the failure is silent: a mip chain whose levels are
        // not successive halves is one the hardware indexes with its own arithmetic, so a rough
        // material reads texels from the wrong place and the picture is merely wrong.
        for (var level = 1; level < chain.Length; level++) {
            var expected = Math.Max(1, chain[0].Size >> level);

            if (chain[level].Size != expected) {
                throw new ArgumentException(
                    $"Level {level} of the chain is {chain[level].Size}² where the level above makes it "
                    + $"{expected}². A mip chain halves, and the hardware assumes so.",
                    nameof(prefiltered)
                );
            }
        }

        device = graphics;
        Format = format;

        Create();
    }

    /// <summary>Bakes a chain from one environment and mirrors it.</summary>
    /// <param name="graphics">The device the cube lives on.</param>
    /// <param name="source">The environment's radiance.</param>
    /// <param name="mipCount">How many roughness levels to convolve.</param>
    /// <param name="samples">How many GGX samples per texel.</param>
    /// <returns>The texture, allocated and not yet filled.</returns>
    /// <exception cref="ArgumentNullException">There is no device or no source.</exception>
    /// <remarks>
    ///     The convolution is on the CPU and is not cheap — <paramref name="samples" /> importance
    ///     samples per texel per face per level. A 64² source at four levels is about a second, which
    ///     is a load-time cost and not a frame one.
    /// </remarks>
    public static EnvironmentTexture Bake(
        IGraphicsDevice graphics,
        CubeImage source,
        int mipCount = 5,
        int samples = 64
    ) {
        ArgumentNullException.ThrowIfNull(graphics);
        ArgumentNullException.ThrowIfNull(source);

        return new(graphics, EnvironmentBaker.Prefilter(source, mipCount, samples));
    }

    /// <summary>What the levels are stored as.</summary>
    /// <remarks>
    ///     Half, because an environment is radiance rather than data: it is filtered by the hardware
    ///     at every sample, it is read through a roughness-selected mip, and the precision that
    ///     matters is the convolution's rather than the storage's. Full float doubles a chain that is
    ///     already the largest thing a small level ships.
    /// </remarks>
    public PixelFormat Format { get; }

    /// <summary>How many roughness levels the chain has.</summary>
    public int MipCount => chain.Length;

    /// <summary>One side of the finest level, in texels.</summary>
    public int Size => chain[0].Size;

    /// <summary>The cube.</summary>
    public TextureHandle Texture => texture;

    /// <summary>The whole cube as one view, which is what a descriptor holds.</summary>
    public TextureViewHandle View => view;

    /// <summary>How it is sampled: trilinear, clamped, across the whole chain.</summary>
    public SamplerHandle Sampler => sampler;

    /// <summary>Whether the chain's contents have reached the device.</summary>
    public bool IsFilled => uploaded;

    /// <summary>How many times the chain has been copied up.</summary>
    /// <remarks>
    ///     One, for a level whose sky does not change — which is what makes it worth counting. A
    ///     number that climbs every frame is something calling <see cref="Invalidate" /> in a loop.
    /// </remarks>
    public int Uploads { get; private set; }

    /// <summary>Says the chain's contents changed, so the next upload copies them again.</summary>
    /// <remarks>
    ///     The levels are mutable — a caller holds the same <see cref="CubeImage" />s it passed in —
    ///     so re-baking in place is possible and this is how it is announced. Nothing detects it: an
    ///     array does not have a version, and giving one to <see cref="CubeImage" /> would be a
    ///     version on the wrong type.
    /// </remarks>
    public void Invalidate() => uploaded = false;

    /// <summary>Copies the chain up, once, into the cube the constructor allocated.</summary>
    /// <param name="commands">The list to record the copies into.</param>
    /// <returns>Whether anything was copied.</returns>
    /// <exception cref="ArgumentNullException">There is no command list.</exception>
    /// <remarks>
    ///     One staging buffer for the whole chain, written once and copied per level per face. Thirty
    ///     copies for a five-level cube is thirty calls to record and one allocation to make, which is
    ///     the right way round for something that runs once.
    /// </remarks>
    public bool Upload(ICommandList commands) {
        ArgumentNullException.ThrowIfNull(commands);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (uploaded) {
            return false;
        }

        // ⚠ Undefined only on the first upload. A cube that was copied into and left in ShaderRead is
        // in ShaderRead, and telling the barrier otherwise discards its contents on a tiler.
        Transition(commands, Uploads == 0 ? ResourceState.Undefined : ResourceState.ShaderRead, ResourceState.CopyDestination);

        var offset = 0L;

        for (var level = 0; level < chain.Length; level++) {
            var image = chain[level];
            var faceTexels = image.Size * image.Size;

            for (var face = 0; face < 6; face++) {
                device.Write(staging, offset, Staged(image.Face((CubeFace)face)));

                commands.CopyBufferToTexture(
                    staging,
                    offset,
                    new TextureRegion(texture, level, face),
                    new Int3(image.Size, image.Size, 1)
                );

                offset += faceTexels * BytesPerTexel;
            }
        }

        Transition(commands, ResourceState.CopyDestination, ResourceState.ShaderRead);

        uploaded = true;
        Uploads++;
        return true;
    }

    /// <summary>Points an environment light at this cube.</summary>
    /// <param name="light">The light to fill.</param>
    /// <exception cref="ArgumentNullException">There is no light.</exception>
    /// <remarks>
    ///     <para>
    ///         <see cref="EnvironmentLight.MipCount" /> is the chain's own count rather than the
    ///         texture's capacity, which is the distinction its own remarks make: one too many and a
    ///         rough material samples a level nothing filled.
    ///     </para>
    ///     <para>
    ///         The coefficients are <em>not</em> written. They are projected from the source
    ///         environment rather than from the prefiltered chain — see
    ///         <see cref="SphericalHarmonics.Project" /> — and a chain's level zero is already
    ///         convolved, so deriving them here would give a diffuse term that disagrees with the
    ///         specular one. Both halves come from the same source, which is
    ///         <see cref="EnvironmentLight" />'s own standing requirement.
    ///     </para>
    /// </remarks>
    public void Apply(EnvironmentLight light) {
        ArgumentNullException.ThrowIfNull(light);
        ObjectDisposedException.ThrowIf(disposed, this);

        light.Prefiltered = view;
        light.Sampler = sampler;
        light.MipCount = MipCount;
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        if (view.IsValid) {
            device.Destroy(view);
        }

        if (texture.IsValid) {
            device.Destroy(texture);
        }

        if (staging.IsValid) {
            device.Destroy(staging);
        }

        if (sampler.IsValid) {
            device.Destroy(sampler);
        }
    }

    /// <summary>How wide one stored texel is.</summary>
    int BytesPerTexel => Format == PixelFormat.Rgba32Float ? sizeof(float) * 4 : sizeof(ushort) * 4;

    /// <summary>How many bytes the whole chain occupies, tightly packed.</summary>
    long StagingSize {
        get {
            var total = 0L;

            foreach (var level in chain) {
                total += (long)level.Size * level.Size * 6 * BytesPerTexel;
            }

            return total;
        }
    }

    /// <summary>Moves the whole cube from one state to another, in one barrier.</summary>
    void Transition(ICommandList commands, ResourceState before, ResourceState after) =>
        commands.Barrier(new([], [new TextureBarrier(texture, before, after)]));

    /// <summary>One face's texels in whatever <see cref="Format" /> says, with an opaque alpha.</summary>
    /// <remarks>
    ///     The alpha is written because the format has one and an uninitialised channel is whatever
    ///     the staging buffer held. Nothing reads it — the shader takes <c>.rgb</c> — but a capture
    ///     full of noise in the fourth channel is a thing somebody spends an hour on.
    /// </remarks>
    ReadOnlySpan<byte> Staged(ReadOnlySpan<Vector3> texels) {
        if (Format == PixelFormat.Rgba32Float) {
            var wide = new float[texels.Length * 4];

            for (var index = 0; index < texels.Length; index++) {
                wide[(index * 4) + 0] = texels[index].X;
                wide[(index * 4) + 1] = texels[index].Y;
                wide[(index * 4) + 2] = texels[index].Z;
                wide[(index * 4) + 3] = 1f;
            }

            return MemoryMarshal.AsBytes(wide.AsSpan());
        }

        var narrow = new Half[texels.Length * 4];

        for (var index = 0; index < texels.Length; index++) {
            narrow[(index * 4) + 0] = (Half)texels[index].X;
            narrow[(index * 4) + 1] = (Half)texels[index].Y;
            narrow[(index * 4) + 2] = (Half)texels[index].Z;
            narrow[(index * 4) + 3] = (Half)1f;
        }

        return MemoryMarshal.AsBytes(narrow.AsSpan());
    }

    /// <summary>Allocates the cube, the staging buffer, the view and the sampler.</summary>
    void Create() {
        texture = device.CreateTexture(
            new TextureDescription(
                Format,
                Size,
                Size,
                TextureUsage.Sampled | TextureUsage.CopyDestination,
                MipLevels: MipCount,

                // Six, and the dimension says what they mean. A cube bound as an array is a shader
                // sampling by layer index where it meant to sample by direction.
                ArrayLayers: 6,
                Dimension: TextureDimension.TextureCube,
                Name: "Environment"
            )
        );

        view = device.CreateTextureView(texture);

        staging = device.CreateBuffer(
            new BufferDescription(StagingSize, BufferUsage.CopySource, MemoryAccess.HostUpload, "Environment.Staging")
        );

        // MaxLod is the chain's last level rather than the default thousand: a roughness that selects
        // past the levels that exist is clamped by the sampler, not by the shader's arithmetic.
        sampler = device.CreateSampler(
            SamplerDescription.LinearClamp with { MaxLod = MipCount - 1, Name = "Environment" }
        );
    }
}
