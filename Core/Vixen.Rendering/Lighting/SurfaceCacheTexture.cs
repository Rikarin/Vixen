// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Rendering.SurfaceCache;
using Vixen.Shaders;

namespace Vixen.Rendering.Lighting;

/// <summary>One card as <c>SurfaceCache.rvn</c>'s <c>SurfaceCacheCard</c> lays it out.</summary>
/// <remarks>
///     <b>Explicit offsets, copied from the reflection rather than left to the runtime.</b> The
///     shader's struct is std430: two <c>float3</c>-shaped members each rounded to sixteen bytes,
///     two <c>int2</c>s, an <c>int</c>, sixty-four in all — where sequential layout of the same
///     fields is fifty-two, and every card after the first would then be read from the middle of the
///     one before it. The device tests assert these numbers against
///     <c>SurfaceCacheGather.reflect.json</c>, because a struct that agrees with a comment agrees
///     with nothing.
/// </remarks>
[StructLayout(LayoutKind.Explicit, Size = Stride)]
public struct SurfaceCacheCardData {
    /// <summary>How many bytes from one card to the next.</summary>
    public const int Stride = 64;

    /// <summary>The centre of its box, in world space.</summary>
    [FieldOffset(0)]
    public Vector3 Centre;

    /// <summary>The box's half-extents, world-axis aligned.</summary>
    [FieldOffset(16)]
    public Vector3 HalfSize;

    /// <summary>Where the card's texels start in the atlas.</summary>
    [FieldOffset(32)]
    public Int2 Origin;

    /// <summary>Texels along the card's U and V.</summary>
    [FieldOffset(40)]
    public Int2 Resolution;

    /// <summary>Which of the six directions it faces, 0 to 5.</summary>
    [FieldOffset(48)]
    public int Axis;

    /// <summary>One card, flattened.</summary>
    /// <param name="card">Its shape.</param>
    /// <param name="origin">Its atlas rectangle's origin.</param>
    public static SurfaceCacheCardData From(in SurfaceCard card, Int2 origin) =>
        new() {
            Centre = card.Centre,
            HalfSize = card.HalfSize,
            Origin = origin,
            Resolution = card.Resolution,
            Axis = card.Axis
        };
}

/// <summary>A surface cache, mirrored into the atlas textures the kernels read — doc 19 § L4's device half.</summary>
/// <remarks>
///     <para>
///         <b>Five sampled planes, one card buffer, and a double-buffered gather.</b> Albedo with the
///         stored depth in alpha, the normal with validity in alpha, emissive, direct, and two
///         gathered planes ping-ponged the way <see cref="SurfaceCacheStore" /> ping-pongs its
///         arrays: a bounce reads the front while writing the back, because a bounce that reads its
///         own pass converges to whatever the texel order made of it. The packing is the store's own
///         — a texel uploaded from the CPU capture and one the rasterising capture will someday write
///         are the same texel, the property every pool here shares.
///     </para>
///     <para>
///         <b><see cref="Apply" /> is the <c>SurfaceCacheSource</c> contract.</b> The composed
///         sampler's bindings are named for the slot that composed it —
///         <c>SurfaceCacheGather.SurfaceCacheSource.surfaceCards</c> — and this writes every one of
///         them, so a pass that composes the cache asks by name and gets this mirror's textures. A
///         rename on either side is a binding that silently resolves to nothing.
///     </para>
///     <para>
///         ⚠ The textures are not graph resources — they are named into descriptor sets — so nothing
///         else in a frame will transition them. Whoever dispatches into the direct or gather planes
///         brackets with <see cref="TransitionDirect" /> / <see cref="TransitionGatherBack" />, the
///         way every fill over the irradiance pool already does.
///     </para>
/// </remarks>
public sealed class SurfaceCacheTexture : IDisposable {
    /// <summary>Floats per atlas texel, in every plane.</summary>
    const int Channels = 4;

    /// <summary>The planes, in upload order: albedo+depth, normal+valid, emissive, direct, gather A, gather B.</summary>
    const int AlbedoPlane = 0;

    const int NormalPlane = 1;
    const int EmissivePlane = 2;
    const int DirectPlane = 3;
    const int GatherA = 4;
    const int GatherB = 5;
    const int Planes = 6;

    readonly TextureHandle[] textures = new TextureHandle[Planes];
    readonly TextureViewHandle[] views = new TextureViewHandle[Planes];
    readonly float[] scratch;

    IGraphicsDevice? device;
    BufferHandle cards;
    BufferHandle staging;
    BufferHandle download;
    long cardsCapacity;
    int front = GatherA;
    int uploadedCards;
    bool disposed;

    /// <summary>Builds a mirror of one store. Nothing exists on the device until the first upload.</summary>
    /// <param name="store">The cache to mirror.</param>
    /// <exception cref="ArgumentNullException">There is no store.</exception>
    public SurfaceCacheTexture(SurfaceCacheStore store) {
        ArgumentNullException.ThrowIfNull(store);

        Store = store;
        scratch = new float[(long)store.Atlas.Size.X * store.Atlas.Size.Y * Channels];
    }

    /// <summary>The cache this mirrors.</summary>
    public SurfaceCacheStore Store { get; }

    /// <summary>Whether the device objects exist yet.</summary>
    public bool IsCreated { get; private set; }

    /// <summary>How many times the cache has been uploaded.</summary>
    public int Uploads { get; private set; }

    /// <summary>How many cards the last upload staged — the count <see cref="Apply" /> publishes.</summary>
    public int CardCount => uploadedCards;

    /// <summary>Whether the lighting dispatch owns the direct plane, so the upload must not copy it.</summary>
    /// <remarks>The same arrangement, for the same doc 19 reason, as
    ///     <see cref="IrradianceFieldTexture.PoolIsWritten" />: once a dispatch writes the plane, an
    ///     upload after it would overwrite a frame's lighting with the stale CPU copy.</remarks>
    public bool DirectIsWritten { get; init; }

    /// <summary>What a written plane is in while a dispatch owns it.</summary>
    public const ResourceState PlaneIsBeingWritten = ResourceState.ShaderWrite | ResourceState.ShaderRead;

    /// <summary>The card buffer, for the kernels' own <c>cards</c> binding.</summary>
    public BufferHandle CardsBuffer => cards;

    /// <summary>The albedo-and-depth plane's view.</summary>
    public TextureViewHandle AlbedoDepthView => views[AlbedoPlane];

    /// <summary>The normal-and-validity plane's view.</summary>
    public TextureViewHandle NormalValidView => views[NormalPlane];

    /// <summary>The emissive plane's view.</summary>
    public TextureViewHandle EmissiveView => views[EmissivePlane];

    /// <summary>The direct plane's view — the lighting dispatch's target.</summary>
    public TextureViewHandle DirectView => views[DirectPlane];

    /// <summary>The front gather plane — what the sampler reads, as of the last swap.</summary>
    public TextureViewHandle GatherFrontView => views[front];

    /// <summary>The back gather plane — what the bounce dispatch writes.</summary>
    public TextureViewHandle GatherBackView => views[Back];

    int Back => front == GatherA ? GatherB : GatherA;

    /// <summary>Makes the staged gather the one the sampler reads, without touching the device.</summary>
    /// <remarks>The mirror of <see cref="SurfaceCacheStore.SwapGathered" />: the swap is an index
    ///     flip on the host, and the next <see cref="Apply" /> publishes the new front.</remarks>
    public void SwapGather() => front = Back;

    /// <summary>Creates the device objects without recording anything.</summary>
    /// <param name="graphics">The device.</param>
    /// <exception cref="ArgumentNullException">There is no device.</exception>
    public void EnsureCreated(IGraphicsDevice graphics) {
        ArgumentNullException.ThrowIfNull(graphics);
        ObjectDisposedException.ThrowIf(disposed, this);

        Create(graphics);
    }

    /// <summary>Creates the textures if they do not exist, and uploads the CPU cache.</summary>
    /// <param name="graphics">The device.</param>
    /// <param name="commands">An open command list, outside a render pass.</param>
    /// <exception cref="ArgumentNullException">There is no device or command list.</exception>
    /// <remarks>
    ///     Everything the store holds goes up — surfaces, lighting, the front gather — so a cache lit
    ///     on the CPU seeds a dispatch and a cache lit by dispatches starts from the capture. The
    ///     back gather plane only ever gets a state: its texels are the bounce dispatch's.
    /// </remarks>
    public void Upload(IGraphicsDevice graphics, ICommandList commands) {
        ArgumentNullException.ThrowIfNull(graphics);
        ArgumentNullException.ThrowIfNull(commands);
        ObjectDisposedException.ThrowIf(disposed, this);

        Create(graphics);

        var first = Uploads == 0;

        if (first) {
            Transition(commands, Back, ResourceState.Undefined, ResourceState.ShaderRead);
        }

        for (var plane = 0; plane < Planes; plane++) {
            if (plane == Back || (plane == DirectPlane && DirectIsWritten && !first)) {
                continue;
            }

            if (plane == DirectPlane && DirectIsWritten) {
                Transition(commands, plane, ResourceState.Undefined, ResourceState.ShaderRead);

                continue;
            }

            Transition(commands, plane, first ? ResourceState.Undefined : ResourceState.ShaderRead, ResourceState.CopyDestination);
            Pack(plane);
            graphics.Write(staging, (long)plane * scratch.Length * sizeof(float), MemoryMarshal.AsBytes(scratch.AsSpan()));

            var size = Store.Atlas.Size;

            commands.CopyBufferToTexture(
                staging,
                (long)plane * scratch.Length * sizeof(float),
                new TextureRegion(textures[plane]),
                new(size.X, size.Y, 1)
            );

            Transition(commands, plane, ResourceState.CopyDestination, ResourceState.ShaderRead);
        }

        StageCards(graphics);

        Uploads++;
    }

    /// <summary>Moves the direct plane from one state to another.</summary>
    /// <param name="commands">Where to record it.</param>
    /// <param name="before">What it is in.</param>
    /// <param name="after">What it needs to be in.</param>
    /// <exception cref="ArgumentNullException">There is no command list.</exception>
    public void TransitionDirect(ICommandList commands, ResourceState before, ResourceState after) =>
        Transition(commands, DirectPlane, before, after);

    /// <summary>Moves the back gather plane from one state to another.</summary>
    /// <param name="commands">Where to record it.</param>
    /// <param name="before">What it is in.</param>
    /// <param name="after">What it needs to be in.</param>
    /// <exception cref="ArgumentNullException">There is no command list.</exception>
    public void TransitionGatherBack(ICommandList commands, ResourceState before, ResourceState after) =>
        Transition(commands, Back, before, after);

    /// <summary>Records a copy of the direct plane back into host memory.</summary>
    /// <param name="commands">Where to record it.</param>
    /// <returns>False before the textures exist, in which case nothing was recorded.</returns>
    /// <exception cref="ArgumentNullException">There is no command list.</exception>
    public bool RecordDirectReadback(ICommandList commands) => RecordReadback(commands, DirectPlane, 0);

    /// <summary>Records a copy of the back gather plane — the bounce dispatch's output — into host memory.</summary>
    /// <param name="commands">Where to record it.</param>
    /// <returns>False before the textures exist, in which case nothing was recorded.</returns>
    /// <exception cref="ArgumentNullException">There is no command list.</exception>
    public bool RecordGatherReadback(ICommandList commands) => RecordReadback(commands, Back, 1);

    /// <summary>Decodes the direct plane the last <see cref="RecordDirectReadback" /> copied.</summary>
    /// <param name="texels">One entry per atlas texel, row-major.</param>
    /// <returns>False when nothing has been read back, or the span is too short.</returns>
    public bool TryReadDirect(Span<Vector4> texels) => TryRead(texels, 0);

    /// <summary>Decodes the gather plane the last <see cref="RecordGatherReadback" /> copied.</summary>
    /// <param name="texels">One entry per atlas texel, row-major.</param>
    /// <returns>False when nothing has been read back, or the span is too short.</returns>
    public bool TryReadGather(Span<Vector4> texels) => TryRead(texels, 1);

    /// <summary>Writes the composed sampler's bindings, by the names the reflection interned.</summary>
    /// <param name="parameters">Where the consuming pass reads its set 0 from.</param>
    /// <param name="shaderName">The slot's qualified name — <c>SurfaceCacheGather.SurfaceCacheSource</c>
    ///     for the bounce, <c>ScreenProbeTrace.SurfaceCacheSource</c> for the trace. No default,
    ///     deliberately: a composed slot's bindings are named for the slot, and a default here would
    ///     be right for one consumer and bind nothing at all for the rest.</param>
    /// <exception cref="ArgumentNullException">There are no parameters.</exception>
    /// <exception cref="ArgumentException">There is no shader name.</exception>
    /// <exception cref="InvalidOperationException">The textures do not exist yet.</exception>
    public void Apply(ParameterCollection parameters, string shaderName) {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentException.ThrowIfNullOrEmpty(shaderName);

        if (!IsCreated) {
            throw new InvalidOperationException("the cache's textures do not exist yet — upload first, then apply");
        }

        parameters.Set(ParameterKeys.New<BufferHandle>($"{shaderName}.surfaceCards"), cards);
        parameters.Set(ParameterKeys.New<TextureViewHandle>($"{shaderName}.surfaceAlbedoDepth"), views[AlbedoPlane]);
        parameters.Set(ParameterKeys.New<TextureViewHandle>($"{shaderName}.surfaceNormalValid"), views[NormalPlane]);
        parameters.Set(ParameterKeys.New<TextureViewHandle>($"{shaderName}.surfaceEmissive"), views[EmissivePlane]);
        parameters.Set(ParameterKeys.New<TextureViewHandle>($"{shaderName}.surfaceDirect"), views[DirectPlane]);
        parameters.Set(ParameterKeys.New<TextureViewHandle>($"{shaderName}.surfaceGathered"), views[front]);
        parameters.Set(ParameterKeys.New<int>($"{shaderName}.surfaceCardCount"), uploadedCards);
        parameters.Set(ParameterKeys.New<float>($"{shaderName}.surfaceDepthTolerance"), Store.DepthTolerance);
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        if (device is null) {
            return;
        }

        foreach (var view in views) {
            if (view.IsValid) {
                device.Destroy(view);
            }
        }

        foreach (var texture in textures) {
            if (texture.IsValid) {
                device.Destroy(texture);
            }
        }

        if (cards.IsValid) {
            device.Destroy(cards);
        }

        if (staging.IsValid) {
            device.Destroy(staging);
        }

        if (download.IsValid) {
            device.Destroy(download);
        }

        IsCreated = false;
    }

    void Transition(ICommandList commands, int plane, ResourceState before, ResourceState after) {
        ArgumentNullException.ThrowIfNull(commands);

        commands.Barrier(new([], [new TextureBarrier(textures[plane], before, after)]));
    }

    bool RecordReadback(ICommandList commands, int plane, int slot) {
        ArgumentNullException.ThrowIfNull(commands);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (!IsCreated || device is null) {
            return false;
        }

        if (!download.IsValid) {
            download = device.CreateBuffer(
                new BufferDescription(
                    (long)scratch.Length * sizeof(float) * 2,
                    BufferUsage.CopyDestination,
                    MemoryAccess.HostReadback,
                    "SurfaceCache.Readback"
                )
            );
        }

        var size = Store.Atlas.Size;

        Transition(commands, plane, ResourceState.ShaderRead, ResourceState.CopySource);
        commands.CopyTextureToBuffer(
            new TextureRegion(textures[plane]),
            new(size.X, size.Y, 1),
            download,
            (long)slot * scratch.Length * sizeof(float)
        );
        Transition(commands, plane, ResourceState.CopySource, ResourceState.ShaderRead);

        return true;
    }

    bool TryRead(Span<Vector4> texels, int slot) {
        ObjectDisposedException.ThrowIf(disposed, this);

        var size = Store.Atlas.Size;
        var count = size.X * size.Y;

        if (device is null || !download.IsValid || texels.Length < count) {
            return false;
        }

        var floats = new float[count * Channels];

        device.Read(download, (long)slot * scratch.Length * sizeof(float), MemoryMarshal.AsBytes(floats.AsSpan()));

        for (var index = 0; index < count; index++) {
            var at = index * Channels;

            texels[index] = new(floats[at], floats[at + 1], floats[at + 2], floats[at + 3]);
        }

        return true;
    }

    /// <summary>Flattens the cards and writes them into the storage buffer, growing it if they outgrew it.</summary>
    void StageCards(IGraphicsDevice graphics) {
        var count = Store.Cards.Count;
        var bytes = (long)Math.Max(count, 1) * SurfaceCacheCardData.Stride;

        if (!cards.IsValid || bytes > cardsCapacity) {
            if (cards.IsValid) {
                graphics.Destroy(cards);
            }

            cards = graphics.CreateBuffer(
                new BufferDescription(bytes, BufferUsage.Storage, MemoryAccess.HostUpload, "SurfaceCache.Cards")
            );

            cardsCapacity = bytes;
        }

        if (count > 0) {
            var data = new SurfaceCacheCardData[count];

            for (var index = 0; index < count; index++) {
                var (card, origin) = Store.Cards[index];

                data[index] = SurfaceCacheCardData.From(card, origin);
            }

            graphics.Write(cards, 0, MemoryMarshal.AsBytes(data.AsSpan()));
        }

        uploadedCards = count;
    }

    /// <summary>Lays one plane of the store out in <see cref="scratch" />.</summary>
    /// <remarks>
    ///     Whole-atlas rather than per-card, so a texel no card owns uploads as zero — an uncovered
    ///     region of the atlas must read as "nothing", not as whatever the last tenant left.
    /// </remarks>
    void Pack(int plane) {
        Array.Clear(scratch);

        for (var index = 0; index < Store.Cards.Count; index++) {
            var (card, origin) = Store.Cards[index];
            var width = Store.Atlas.Size.X;

            for (var y = 0; y < card.Resolution.Y; y++) {
                for (var x = 0; x < card.Resolution.X; x++) {
                    var texel = new Int2(x, y);
                    var at = (((origin.Y + y) * width) + origin.X + x) * Channels;
                    var valid = Store.IsValid(index, texel);

                    switch (plane) {
                        case AlbedoPlane: {
                            var surface = valid ? Store.Surface(index, texel) : default;

                            scratch[at] = surface.Albedo.X;
                            scratch[at + 1] = surface.Albedo.Y;
                            scratch[at + 2] = surface.Albedo.Z;
                            scratch[at + 3] = surface.Depth;

                            break;
                        }

                        case NormalPlane: {
                            var surface = valid ? Store.Surface(index, texel) : default;

                            scratch[at] = surface.Normal.X;
                            scratch[at + 1] = surface.Normal.Y;
                            scratch[at + 2] = surface.Normal.Z;
                            scratch[at + 3] = valid ? 1f : 0f;

                            break;
                        }

                        case EmissivePlane: {
                            var surface = valid ? Store.Surface(index, texel) : default;

                            scratch[at] = surface.Emissive.X;
                            scratch[at + 1] = surface.Emissive.Y;
                            scratch[at + 2] = surface.Emissive.Z;

                            break;
                        }

                        case DirectPlane: {
                            var direct = valid ? Store.Direct(index, texel) : default;

                            scratch[at] = direct.X;
                            scratch[at + 1] = direct.Y;
                            scratch[at + 2] = direct.Z;

                            break;
                        }

                        default: {
                            var gathered = valid ? Store.Gathered(index, texel) : default;

                            scratch[at] = gathered.X;
                            scratch[at + 1] = gathered.Y;
                            scratch[at + 2] = gathered.Z;

                            break;
                        }
                    }
                }
            }
        }
    }

    void Create(IGraphicsDevice graphics) {
        if (IsCreated) {
            return;
        }

        device = graphics;

        var size = Store.Atlas.Size;

        for (var plane = 0; plane < Planes; plane++) {
            // Every plane gets every usage the pool textures get, for the pool's own reason: the
            // sampled planes are seeded from the host today and rasterised into tomorrow, and the
            // written planes are read back by every comparison test there is.
            textures[plane] = graphics.CreateTexture(
                new TextureDescription(
                    PixelFormat.Rgba32Float,
                    size.X,
                    size.Y,
                    TextureUsage.Sampled
                    | TextureUsage.CopySource
                    | TextureUsage.CopyDestination
                    | TextureUsage.Storage,
                    Name: $"SurfaceCache.Plane{plane.ToString(CultureInfo.InvariantCulture)}"
                )
            );

            views[plane] = graphics.CreateTextureView(textures[plane]);
        }

        staging = graphics.CreateBuffer(
            new BufferDescription(
                (long)scratch.Length * sizeof(float) * Planes,
                BufferUsage.CopySource,
                MemoryAccess.HostUpload,
                "SurfaceCache.Staging"
            )
        );

        IsCreated = true;
    }
}
