// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Rendering.IrradianceFields;
using Vixen.Shaders;

namespace Vixen.Rendering.Lighting;

/// <summary>The GPU's copy of an <see cref="IrradianceField" />: four pool volumes and an index.</summary>
/// <remarks>
///     <para>
///         <b>The field itself references no device, and this is why.</b> Everything about what a field
///         <i>says</i> — the brick layout, the borders, the dilation, the refinement, the filler — is
///         arithmetic with closed-form answers, and it lives in an assembly that could not open a
///         device if it wanted to. What is left over is this: allocate, pack, stage, copy, and name.
///         The same split as <see cref="GlobalDistanceFieldTexture" />, for the same reason.
///     </para>
///     <para>
///         <b>Four volumes for a payload of fourteen numbers, not six.</b> The constant term takes
///         three channels and validity rides in its fourth; each colour channel's three linear
///         coefficients take a volume, and the sun's shadow rides in the red one's fourth. Two more
///         textures for two scalars would be two more fetches at every shaded pixel for numbers that
///         fit in alpha channels nothing else wanted.
///     </para>
///     <para>
///         <b>A texture per coefficient set rather than one atlas</b>, because a three-dimensional
///         texture cannot be an array layer — the same constraint that makes the distance-field clipmap
///         a texture per level.
///     </para>
///     <para>
///         <b>The index volume is point sampled and always half-precision.</b> Interpolating two slot
///         indices gives a third that means nothing, so it never filters — which removes the only
///         reason to want the wider format. What it holds is a pool origin in texels and a brick size
///         in cells, and half represents integers exactly to 2048, well past what a pool that fits in
///         memory can reach. <see cref="Upload" /> refuses a pool larger than that rather than storing
///         a texel origin that rounds.
///     </para>
/// </remarks>
public sealed class IrradianceFieldTexture : IDisposable {
    /// <summary>How many channels every volume here has.</summary>
    const int Channels = 4;

    /// <summary>The largest integer a half-float holds exactly, which is what bounds the pool.</summary>
    const int ExactInHalf = 2048;

    /// <summary>The shader's names for the four pool volumes, in the order they are packed.</summary>
    static readonly string[] PoolNames = ["irradianceL0", "irradianceL1R", "irradianceL1G", "irradianceL1B"];

    /// <summary>The shader's name for the index volume.</summary>
    const string IndirectionName = "irradianceIndirection";

    /// <summary>The shader's name for how the pool is sampled.</summary>
    const string SamplerName = "irradianceSampler";

    /// <summary>The shader's name for how the index is sampled.</summary>
    const string PointSamplerName = "irradiancePointSampler";

    /// <summary>The shader's name for the struct describing where the field is.</summary>
    const string VolumeName = "irradianceField";

    readonly TextureHandle[] pool = new TextureHandle[Channels];
    readonly TextureViewHandle[] poolViews = new TextureViewHandle[Channels];

    float[] scratch = [];
    Half[] halves = [];

    TextureHandle indirection;
    TextureViewHandle indirectionView;
    BufferHandle staging;
    BufferHandle download;
    SamplerHandle sampler;
    SamplerHandle pointSampler;
    IGraphicsDevice? device;
    bool disposed;

    /// <summary>Mirrors one field.</summary>
    /// <param name="field">The field.</param>
    /// <exception cref="ArgumentNullException">There is no field.</exception>
    public IrradianceFieldTexture(IrradianceField field) {
        ArgumentNullException.ThrowIfNull(field);

        Field = field;
    }

    /// <summary>The field this mirrors.</summary>
    public IrradianceField Field { get; }

    /// <summary>Whether the textures exist yet.</summary>
    public bool IsCreated { get; private set; }

    /// <summary>How many times the whole field has been copied up.</summary>
    /// <remarks>
    ///     Everything, every call, for now — which is what makes this worth counting rather than
    ///     assuming. A filler doing N bricks a frame dirties N slots of the pool and nothing else, and
    ///     copying only those is the optimisation this number will measure.
    /// </remarks>
    public int Uploads { get; private set; }

    /// <summary>How the pool volumes are sampled.</summary>
    public SamplerHandle Sampler => sampler;

    /// <summary>How the index volume is sampled.</summary>
    public SamplerHandle PointSampler => pointSampler;

    /// <summary>What each pool sample is stored as. Fixed once the textures exist.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Half is enough and full is the default anyway</b> — the same decision, with the same
    ///         reason, as <see cref="GlobalDistanceFieldTexture.Format" />. Linear filtering of a
    ///         half-float texture is an extension on WebGL2 and not guaranteed on every GLES 3.0
    ///         driver, and an unfilterable irradiance field is a field sampled at its nearest probe:
    ///         blocky lighting that reads as broken rather than as low quality. Switching this on is a
    ///         decision about which devices a project runs on, so the project makes it.
    ///     </para>
    ///     <para>
    ///         The cost of the default is real and worth stating: a pool of sixteen slots an axis is
    ///         eighty texels an axis, and four volumes of that at sixteen bytes a texel is thirty-two
    ///         megabytes. Half is sixteen.
    ///     </para>
    /// </remarks>
    public PixelFormat Format { get; init; } = PixelFormat.Rgba32Float;

    /// <summary>Whether a compute shader writes the pool instead of this copying it up.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The fork between doc 19's two fillers, and it is exactly one property wide.</b> With
    ///         it set the pool is a storage image and the probes are the GPU's — this uploads the index
    ///         volume and nothing else, because allocation and refinement stay a CPU decision and only
    ///         the <i>probes</i> move. With it clear the CPU field is authoritative and the whole
    ///         thing is copied up, which is filler B and what a target with no compute gets.
    ///     </para>
    ///     <para>
    ///         The two cannot both be live: an upload of the CPU pool would overwrite whatever the
    ///         dispatch just wrote, one frame after it wrote it, which reads as lighting that flickers
    ///         between two answers rather than as a mode nobody chose.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Whoever dispatches owns the pool's state.</b> This leaves it in
    ///         <see cref="ResourceState.ShaderRead" /> and expects to find it there; a fill pass has to
    ///         move it to <see cref="ResourceState.ShaderWrite" /> and back around its own dispatch,
    ///         because the volumes are not graph resources and nothing else can see them.
    ///     </para>
    /// </remarks>
    public bool PoolIsWritten { get; init; }

    /// <summary>One of the four pool volumes.</summary>
    /// <param name="channel">Which one — zero is the constant term, one to three the linear ones.</param>
    /// <returns>The texture, or an invalid handle before the first upload.</returns>
    public TextureHandle Pool(int channel) => pool[channel];

    /// <summary>One pool volume's view, which is what a descriptor actually holds.</summary>
    /// <param name="channel">Which one.</param>
    /// <returns>The view, or an invalid handle before the first upload.</returns>
    public TextureViewHandle PoolView(int channel) => poolViews[channel];

    /// <summary>The index volume.</summary>
    public TextureHandle Indirection => indirection;

    /// <summary>The index volume's view.</summary>
    public TextureViewHandle IndirectionView => indirectionView;

    /// <summary>Copies the whole field up, creating the textures if they are not there.</summary>
    /// <param name="graphics">The device.</param>
    /// <param name="commands">The list to record the copies into.</param>
    /// <exception cref="ArgumentNullException">There is no device or no command list.</exception>
    /// <exception cref="InvalidOperationException">The pool is too large to index in half-precision.</exception>
    /// <remarks>
    ///     <b>One staging buffer for everything, written in pieces and copied per texture.</b> A buffer
    ///     per volume would be a driver call per volume for bytes a single allocation already has, and
    ///     reusing one small buffer would overwrite a region a copy has been recorded against but has
    ///     not yet run. The scratch array <i>is</i> reused, because <c>Write</c> copies out of it
    ///     immediately.
    /// </remarks>
    public void Upload(IGraphicsDevice graphics, ICommandList commands) {
        ArgumentNullException.ThrowIfNull(graphics);
        ArgumentNullException.ThrowIfNull(commands);
        ObjectDisposedException.ThrowIf(disposed, this);

        var texels = Field.Pool.TexelResolution;

        if (texels.X > ExactInHalf || texels.Y > ExactInHalf || texels.Z > ExactInHalf) {
            throw new InvalidOperationException(
                $"A pool of {texels} texels cannot be indexed by a half-precision index volume, which "
                + $"holds integers exactly only to {ExactInHalf}. A brick origin that rounds puts every "
                + "probe of that brick in the wrong place."
            );
        }

        Create(graphics);

        // ⚠ These are not graph resources — they are named into a descriptor set rather than read
        // through the graph — so nothing else in the frame will transition them and this has to. A
        // texture that was copied into and left in TRANSFER_DST is a validation error at the draw that
        // samples it, and one never transitioned at all is one at the copy.
        var settled = Uploads == 0 ? ResourceState.Undefined : ResourceState.ShaderRead;

        if (PoolIsWritten) {
            // The probes are the dispatch's. All this owes them is a state they can be found in, once.
            if (Uploads == 0) {
                TransitionPool(commands, ResourceState.Undefined, ResourceState.ShaderRead);
            }
        } else {
            TransitionPool(commands, settled, ResourceState.CopyDestination);
        }

        TransitionIndirection(commands, settled, ResourceState.CopyDestination);

        var poolBytes = PoolBytes;

        if (!PoolIsWritten) {
            for (var channel = 0; channel < Channels; channel++) {
                PackPool(channel);

                graphics.Write(staging, channel * poolBytes, Staged(Field.Pool.Texels.Length * Channels));

                commands.CopyBufferToTexture(
                    staging,
                    channel * poolBytes,
                    new TextureRegion(pool[channel]),
                    texels
                );
            }
        }

        PackIndirection();

        var cells = Field.Indirection.Resolution;

        // Always narrowed, whatever Format says: the index volume never filters and holds small
        // integers, so the wide format would buy nothing and cost twice the memory.
        graphics.Write(
            staging,
            Channels * poolBytes,
            Staged(Field.Indirection.Cells.Length * Channels, narrow: true)
        );

        commands.CopyBufferToTexture(
            staging,
            Channels * poolBytes,
            new TextureRegion(indirection),
            cells
        );

        if (!PoolIsWritten) {
            TransitionPool(commands, ResourceState.CopyDestination, ResourceState.ShaderRead);
        }

        TransitionIndirection(commands, ResourceState.CopyDestination, ResourceState.ShaderRead);

        Uploads++;
    }

    /// <summary>Moves the four pool volumes from one state to another, in one barrier.</summary>
    /// <param name="commands">Where to record it.</param>
    /// <param name="before">What they are in.</param>
    /// <param name="after">What they need to be in.</param>
    /// <exception cref="ArgumentNullException">There is no command list.</exception>
    /// <remarks>
    ///     Public because a fill pass has to bracket its own dispatch with it — see
    ///     <see cref="PoolIsWritten" />. The volumes are not graph resources, so nothing else in the
    ///     frame can see them and nothing else will move them.
    /// </remarks>
    public void TransitionPool(ICommandList commands, ResourceState before, ResourceState after) {
        ArgumentNullException.ThrowIfNull(commands);

        var barriers = new TextureBarrier[Channels];

        for (var channel = 0; channel < Channels; channel++) {
            barriers[channel] = new(pool[channel], before, after);
        }

        commands.Barrier(new([], barriers));
    }

    /// <summary>What the pool is in while a dispatch owns it — writable, and readable by the next one.</summary>
    /// <remarks>
    ///     Both bits, and the second is not decoration. A barrier from <see cref="ResourceState.ShaderWrite" />
    ///     to itself carries <c>SHADER_WRITE</c> in both access masks, which orders one dispatch's writes
    ///     against the next one's <i>writes</i> and says nothing about its <i>reads</i> — and every repair
    ///     pass reads what the pass before it wrote. Naming both states gives the barrier a read bit; the
    ///     layout is <c>General</c> either way, because a storage image has no other.
    /// </remarks>
    public const ResourceState PoolIsBeingWritten = ResourceState.ShaderWrite | ResourceState.ShaderRead;

    /// <summary>Orders everything written to the pool so far against whatever reads it next.</summary>
    /// <param name="commands">Where to record it.</param>
    /// <exception cref="ArgumentNullException">There is no command list.</exception>
    /// <remarks>
    ///     What goes between two dispatches that both touch the pool. Without it the second reads whatever
    ///     of the first's writes happened to have landed, which is not a wrong picture so much as a
    ///     different one every time it runs.
    /// </remarks>
    public void OrderPool(ICommandList commands) =>
        TransitionPool(commands, PoolIsBeingWritten, PoolIsBeingWritten);

    /// <summary>Records a copy of the whole pool back into host memory.</summary>
    /// <param name="commands">Where to record it.</param>
    /// <returns>False before the textures exist, in which case nothing was recorded.</returns>
    /// <exception cref="ArgumentNullException">There is no command list.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>What makes a device-authored pool checkable.</b> With <see cref="PoolIsWritten" />
    ///         set, the CPU field beside this is no longer what the shader reads — the probes are the
    ///         dispatch's — so every closed form L2 was built against has nothing to test. This is the
    ///         way back: read the texels the sampler would read, and hold them against the reference
    ///         filler's answer for the same bricks.
    ///     </para>
    ///     <para>
    ///         It also distinguishes the two failures that look identical from a picture. A pool of
    ///         zeroes is a dispatch that wrote nothing; plausible numbers in the wrong texels are a
    ///         dispatch that wrote somewhere else. Both draw an unlit frame.
    ///     </para>
    ///     <para>
    ///         ⚠ The result is only readable once the queue has finished with it —
    ///         <see cref="TryRead" /> reads host memory and cannot wait for anything. The pool is left
    ///         in <see cref="ResourceState.ShaderRead" />, which is where both fillers expect it.
    ///     </para>
    /// </remarks>
    public bool RecordReadback(ICommandList commands) {
        ArgumentNullException.ThrowIfNull(commands);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (!IsCreated || device is null) {
            return false;
        }

        if (!download.IsValid) {
            download = device.CreateBuffer(
                new BufferDescription(
                    PoolBytes * Channels,
                    BufferUsage.CopyDestination,
                    MemoryAccess.HostReadback,
                    "IrradianceField.Readback"
                )
            );
        }

        TransitionPool(commands, ResourceState.ShaderRead, ResourceState.CopySource);

        var texels = Field.Pool.TexelResolution;

        for (var channel = 0; channel < Channels; channel++) {
            commands.CopyTextureToBuffer(new TextureRegion(pool[channel]), texels, download, channel * PoolBytes);
        }

        TransitionPool(commands, ResourceState.CopySource, ResourceState.ShaderRead);

        return true;
    }

    /// <summary>Decodes what the last <see cref="RecordReadback" /> copied.</summary>
    /// <param name="probes">Where they go — one per texel of the pool, in the pool's own order.</param>
    /// <returns>False when nothing has been read back, or the span is too short.</returns>
    /// <remarks>
    ///     The inverse of <see cref="PackPool" />, and deliberately written as one: a decode that
    ///     agreed with the encode by construction would test nothing, so this reads the four volumes
    ///     back in the colour-major arrangement the shader reads them in and reassembles a probe from
    ///     the four fetches the shader makes.
    /// </remarks>
    public bool TryRead(Span<IrradianceProbe> probes) {
        ObjectDisposedException.ThrowIf(disposed, this);

        var texels = Field.Pool.Texels.Length;

        if (!download.IsValid || device is null || probes.Length < texels) {
            return false;
        }

        var bytes = new byte[PoolBytes * Channels];

        device.Read(download, 0, bytes);

        var samples = new float[texels * Channels * Channels];

        for (var channel = 0; channel < Channels; channel++) {
            Widen(bytes.AsSpan((int)(channel * PoolBytes), (int)PoolBytes), samples.AsSpan(channel * texels * Channels));
        }

        for (var index = 0; index < texels; index++) {
            var at = index * Channels;
            var l0 = samples.AsSpan(at);
            var red = samples.AsSpan((texels * Channels) + at);
            var green = samples.AsSpan((2 * texels * Channels) + at);
            var blue = samples.AsSpan((3 * texels * Channels) + at);

            probes[index] = new IrradianceProbe(
                new SphericalHarmonicsL1(
                    new Vector3(l0[0], l0[1], l0[2]),
                    new Vector3(red[0], green[0], blue[0]),
                    new Vector3(red[1], green[1], blue[1]),
                    new Vector3(red[2], green[2], blue[2])
                ),
                l0[3],
                red[3]
            );
        }

        return true;
    }

    /// <summary>How many bytes one pool volume is.</summary>
    long PoolBytes => (long)Field.Pool.Texels.Length * Channels * BytesPerChannel;

    /// <summary>One volume's bytes as floats, whichever width they were stored at.</summary>
    void Widen(ReadOnlySpan<byte> source, Span<float> destination) {
        if (Format != PixelFormat.Rgba16Float) {
            MemoryMarshal.Cast<byte, float>(source).CopyTo(destination);

            return;
        }

        var stored = MemoryMarshal.Cast<byte, Half>(source);

        for (var index = 0; index < destination.Length; index++) {
            destination[index] = (float)stored[index];
        }
    }

    /// <summary>Moves the index volume from one state to another.</summary>
    void TransitionIndirection(ICommandList commands, ResourceState before, ResourceState after) =>
        commands.Barrier(new([], [new TextureBarrier(indirection, before, after)]));

    /// <summary>Writes the names a shader reads the field through.</summary>
    /// <param name="parameters">Where the values go — the frame's, since the field follows the scene.</param>
    /// <param name="shaderName">The pass whose keys to write.</param>
    /// <exception cref="ArgumentNullException">There are no parameters.</exception>
    /// <exception cref="ArgumentException">The shader name is empty.</exception>
    /// <remarks>
    ///     <para>
    ///         Reciprocals rather than sizes, because a shader turns a world position into a cell
    ///         coordinate by multiplying and a divide per sample is a divide nobody needs. The
    ///         resolution goes up as it is, because the shader clamps against it rather than dividing
    ///         by it.
    ///     </para>
    ///     <para>
    ///         <b><paramref name="shaderName" /> has no default, deliberately.</b> A composed slot's
    ///         bindings are named for the <i>slot</i> rather than for the shader that declared them —
    ///         <c>Deferred.IrradianceFieldProbes.irradianceL0</c>, not <c>Deferred.irradianceL0</c>. A
    ///         default here was wrong for the only consumer that existed last time and would have bound
    ///         nothing at all, silently.
    ///     </para>
    /// </remarks>
    public void Apply(ParameterCollection parameters, string shaderName) {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentException.ThrowIfNullOrEmpty(shaderName);

        var slot = $"{shaderName}.{VolumeName}";
        var bounds = Field.Bounds;
        var cell = Field.Indirection.CellSize;
        var resolution = Field.Indirection.Resolution;
        var texels = Field.Pool.TexelResolution;

        parameters.Set(ParameterKeys.New<Vector3>($"{slot}.minimum"), bounds.Minimum);
        parameters.Set(ParameterKeys.New<Vector3>($"{slot}.maximum"), bounds.Maximum);
        parameters.Set(ParameterKeys.New<Vector3>($"{slot}.inverseCellSize"), Vector3.One / cell);
        parameters.Set(
            ParameterKeys.New<Vector3>($"{slot}.resolution"),
            new(resolution.X, resolution.Y, resolution.Z)
        );
        parameters.Set(
            ParameterKeys.New<Vector3>($"{slot}.inversePoolSize"),
            Vector3.One / new Vector3(texels.X, texels.Y, texels.Z)
        );
        parameters.Set(ParameterKeys.New<float>($"{slot}.normalBias"), Field.NormalBias);
        parameters.Set(ParameterKeys.New<float>($"{slot}.viewBias"), Field.ViewBias);

        // The volumes the numbers describe, beside the numbers describing them. Writing one without
        // the other is a shader told exactly where to look in a texture nothing bound.
        for (var channel = 0; channel < Channels; channel++) {
            parameters.Set(
                ParameterKeys.New<TextureViewHandle>(PoolBinding(channel, shaderName)),
                poolViews[channel]
            );
        }

        parameters.Set(ParameterKeys.New<TextureViewHandle>(IndirectionBinding(shaderName)), indirectionView);
        parameters.Set(ParameterKeys.New<SamplerHandle>(SamplerBinding(shaderName)), sampler);
        parameters.Set(ParameterKeys.New<SamplerHandle>(PointSamplerBinding(shaderName)), pointSampler);
    }

    /// <summary>The shader's name for one of the pool volumes.</summary>
    /// <param name="channel">Which one.</param>
    /// <param name="shaderName">The pass whose keys to name.</param>
    /// <returns>The binding's name.</returns>
    public static string PoolBinding(int channel, string shaderName) => $"{shaderName}.{PoolNames[channel]}";

    /// <summary>The shader's name for the index volume.</summary>
    /// <param name="shaderName">The pass whose keys to name.</param>
    /// <returns>The binding's name.</returns>
    public static string IndirectionBinding(string shaderName) => $"{shaderName}.{IndirectionName}";

    /// <summary>The shader's name for the sampler the pool volumes share.</summary>
    /// <param name="shaderName">The pass whose keys to name.</param>
    /// <returns>The binding's name.</returns>
    public static string SamplerBinding(string shaderName) => $"{shaderName}.{SamplerName}";

    /// <summary>The shader's name for the sampler the index volume uses.</summary>
    /// <param name="shaderName">The pass whose keys to name.</param>
    /// <returns>The binding's name.</returns>
    public static string PointSamplerBinding(string shaderName) => $"{shaderName}.{PointSamplerName}";

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        if (device is null) {
            return;
        }

        foreach (var view in poolViews) {
            if (view.IsValid) {
                device.Destroy(view);
            }
        }

        foreach (var texture in pool) {
            if (texture.IsValid) {
                device.Destroy(texture);
            }
        }

        if (indirectionView.IsValid) {
            device.Destroy(indirectionView);
        }

        if (indirection.IsValid) {
            device.Destroy(indirection);
        }

        if (staging.IsValid) {
            device.Destroy(staging);
        }

        if (download.IsValid) {
            device.Destroy(download);
        }

        if (sampler.IsValid) {
            device.Destroy(sampler);
        }

        if (pointSampler.IsValid) {
            device.Destroy(pointSampler);
        }

        IsCreated = false;
    }

    /// <summary>How wide one stored channel of the pool is.</summary>
    int BytesPerChannel => Format == PixelFormat.Rgba16Float ? sizeof(ushort) : sizeof(float);

    /// <summary>Lays one of the four pool volumes out in <see cref="scratch" />.</summary>
    /// <param name="channel">Which one.</param>
    /// <remarks>
    ///     <para>
    ///         Channel zero is the constant term with validity in alpha; one to three are the red,
    ///         green and blue components of the three linear coefficients, with the sun's shadow in
    ///         the red one's alpha.
    ///     </para>
    ///     <para>
    ///         <b>The packing is transposed, and that is the whole trick.</b> A probe holds three
    ///         coefficients of three colours; the shader wants one fetch to give it one coefficient's
    ///         worth of all three colours <i>or</i> one colour's worth of all three coefficients. The
    ///         second is what a colour-major layout gives, and it is why a fetch of the red volume
    ///         returns <c>(l1m1.r, l10.r, l11.r, sunShadow)</c> rather than one coefficient's RGB.
    ///     </para>
    /// </remarks>
    void PackPool(int channel) {
        var texels = Field.Pool.Texels;

        Reserve(texels.Length * Channels);

        for (var index = 0; index < texels.Length; index++) {
            var probe = texels[index];
            var radiance = probe.Radiance;
            var at = index * Channels;

            switch (channel) {
                case 0:
                    scratch[at] = radiance.L00.X;
                    scratch[at + 1] = radiance.L00.Y;
                    scratch[at + 2] = radiance.L00.Z;
                    scratch[at + 3] = probe.Validity;

                    break;

                case 1:
                    scratch[at] = radiance.L1m1.X;
                    scratch[at + 1] = radiance.L10.X;
                    scratch[at + 2] = radiance.L11.X;
                    scratch[at + 3] = probe.SunShadow;

                    break;

                case 2:
                    scratch[at] = radiance.L1m1.Y;
                    scratch[at + 1] = radiance.L10.Y;
                    scratch[at + 2] = radiance.L11.Y;
                    scratch[at + 3] = 0f;

                    break;

                default:
                    scratch[at] = radiance.L1m1.Z;
                    scratch[at + 1] = radiance.L10.Z;
                    scratch[at + 2] = radiance.L11.Z;
                    scratch[at + 3] = 0f;

                    break;
            }
        }
    }

    /// <summary>Lays the index volume out in <see cref="scratch" />.</summary>
    /// <remarks>
    ///     Where a brick starts in the pool, in texels, and how many cells across it is — which is
    ///     everything the shader needs and nothing about how slots are laid out, so the pool's own
    ///     arrangement stays a fact about the CPU. A size of zero is how a cell says no brick covers
    ///     it, which is a real answer: a field allocated from renderer bounds has nothing in the air.
    /// </remarks>
    void PackIndirection() {
        var cells = Field.Indirection.Cells;

        Reserve(cells.Length * Channels);

        for (var index = 0; index < cells.Length; index++) {
            var at = index * Channels;
            var cell = cells[index];

            if (!cell.HasBrick) {
                scratch[at] = 0f;
                scratch[at + 1] = 0f;
                scratch[at + 2] = 0f;
                scratch[at + 3] = 0f;

                continue;
            }

            var origin = Field.Pool.OriginOf(cell.Slot);

            scratch[at] = origin.X;
            scratch[at + 1] = origin.Y;
            scratch[at + 2] = origin.Z;
            scratch[at + 3] = cell.Size;
        }
    }

    /// <summary>Grows the scratch array if it has to.</summary>
    void Reserve(int floats) {
        if (scratch.Length < floats) {
            scratch = new float[floats];
        }
    }

    /// <summary>The first so many floats of <see cref="scratch" />, in whatever format is wanted.</summary>
    /// <param name="floats">How many of them.</param>
    /// <returns>The bytes.</returns>
    /// <remarks>
    ///     The index volume is always half, because it never filters and its values are small integers
    ///     — see the type's remarks. The pool follows <see cref="Format" />, and the full-precision
    ///     path hands the array's own bytes over untouched, which is the case worth keeping free: a
    ///     readback then compares against the CPU field with no conversion in between to argue about.
    /// </remarks>
    ReadOnlySpan<byte> Staged(int floats) => Staged(floats, Format == PixelFormat.Rgba16Float);

    /// <summary>The first so many floats of <see cref="scratch" />, narrowed or not.</summary>
    ReadOnlySpan<byte> Staged(int floats, bool narrow) {
        var samples = scratch.AsSpan(0, floats);

        if (!narrow) {
            return MemoryMarshal.AsBytes(samples);
        }

        if (halves.Length < floats) {
            halves = new Half[floats];
        }

        for (var index = 0; index < floats; index++) {
            halves[index] = (Half)samples[index];
        }

        return MemoryMarshal.AsBytes(halves.AsSpan(0, floats));
    }

    /// <summary>Allocates the textures, the staging buffer and the samplers, once.</summary>
    /// <param name="graphics">The device.</param>
    void Create(IGraphicsDevice graphics) {
        if (IsCreated) {
            return;
        }

        device = graphics;

        var texels = Field.Pool.TexelResolution;
        var cells = Field.Indirection.Resolution;

        for (var channel = 0; channel < Channels; channel++) {
            // Every usage, whichever filler is live, and none of them is speculative. CopySource is
            // what RecordReadback needs, and a pool a compute shader authored has no other way of
            // being checked at all. CopyDestination and Storage together are what let a pool be
            // seeded from the host and then refined on the device — which is doc 19's two fillers
            // meeting, and is also the only way to test the repair without a distance field in the
            // way. A usage flag nothing exercises costs nothing on any target.
            pool[channel] = graphics.CreateTexture(
                new TextureDescription(
                    Format,
                    texels.X,
                    texels.Y,
                    TextureUsage.Sampled
                    | TextureUsage.CopySource
                    | TextureUsage.CopyDestination
                    | TextureUsage.Storage,
                    Depth: texels.Z,
                    Dimension: TextureDimension.Texture3D,
                    Name: $"IrradianceField.{PoolNames[channel]}"
                )
            );

            poolViews[channel] = graphics.CreateTextureView(pool[channel]);
        }

        indirection = graphics.CreateTexture(
            new TextureDescription(
                PixelFormat.Rgba16Float,
                cells.X,
                cells.Y,
                TextureUsage.Sampled | TextureUsage.CopyDestination,
                Depth: cells.Z,
                Dimension: TextureDimension.Texture3D,
                Name: $"IrradianceField.{IndirectionName}"
            )
        );

        indirectionView = graphics.CreateTextureView(indirection);

        var poolBytes = PoolBytes;
        var indirectionBytes = (long)Field.Indirection.Cells.Length * Channels * sizeof(ushort);

        staging = graphics.CreateBuffer(
            new BufferDescription(
                (poolBytes * Channels) + indirectionBytes,
                BufferUsage.CopySource,
                MemoryAccess.HostUpload,
                "IrradianceField.Staging"
            )
        );

        // Clamped, not repeated: a sample that walks off the pool must read its own edge rather than
        // wrap into a brick belonging to somewhere else entirely. Linear for the pool because the
        // whole point of a probe grid is that the hardware interpolates it; point for the index
        // because interpolating two slot indices gives a third that means nothing.
        sampler = graphics.CreateSampler(SamplerDescription.LinearClamp);
        pointSampler = graphics.CreateSampler(SamplerDescription.PointClamp);

        IsCreated = true;
    }
}
