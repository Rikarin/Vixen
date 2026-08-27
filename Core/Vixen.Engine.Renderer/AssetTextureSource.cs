// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using Microsoft.Extensions.Logging;
using Vixen.Assets;
using Vixen.Core;
using Vixen.Core.Imaging;
using Vixen.Graphics;

namespace Vixen.Engine.Renderer;

/// <summary>
///     The textures a material's features sample, loaded, uploaded and viewable.
/// </summary>
/// <remarks>
///     <para>
///         <b>The half of a bindless material that had nothing on the far side.</b> A feature carries
///         the name of a map it wants sampled and the material carries which texture that is; what
///         neither carries is a view, because a material is serialised on machines that have no device.
///         This is what turns the reference into one — read the <c>Texture</c> artefact a build wrote,
///         decode the KTX2, put the pixels on the device, and hand back a
///         <see cref="TextureViewHandle" /> for <c>MaterialRenderFeature</c> to register in the frame's
///         table.
///     </para>
///     <para>
///         <b>Three stages, and the split between them is where the costs are.</b> Reading a bundle and
///         decoding a container are file work and happen on a task; creating the texture is a device
///         call and happens on whichever thread asked; recording the copy needs a command list and
///         happens in <see cref="Update" />, once a frame. A texture is therefore ready the frame after
///         its bytes were recorded, which is what the fallback slot exists to cover.
///     </para>
///     <para>
///         ⚠ <b>Two dimensions and one layer.</b> A cube map and an array both decode fine and neither
///         is uploaded — <see cref="TextureData.FaceCount" /> and <see cref="TextureData.LayerCount" />
///         past one are refused rather than half-copied, because a sky sampled as its first face is a
///         picture that looks deliberate. The environment path builds its own cube today; a material
///         that wants one is the reason this will grow.
///     </para>
/// </remarks>
public sealed class AssetTextureSource : IDisposable {
    /// <summary>How many bytes of tail one frame slot may stage.</summary>
    /// <remarks>
    ///     A ceiling rather than a growable buffer, for <see cref="TexturePagePool" />'s reason: a
    ///     camera that turns to face a city wants every texture at once, and staging all of it would
    ///     spend the frame on uploads the next frame will not want either. Over the cap the swap is
    ///     skipped and counted, and the next frame asks again.
    /// </remarks>
    const long StreamStagingCap = 8 * 1024 * 1024;

    const long StreamStagingAlignment = 16;

    readonly IGraphicsDevice device;
    readonly AssetManager assets;
    readonly Dictionary<AssetReference, Entry> entries = [];
    readonly List<Entry> uploading = [];
    readonly List<TextureHandle> textures = [];
    readonly List<BufferHandle> staging = [];

    readonly AssetTextureStreamSource? streamSource;
    readonly List<Entry> streamed = [];

    readonly BufferHandle[] streamStaging;
    readonly long[] streamStagingCapacity;

    ILogger? logger;
    long streamAt;
    int streamSlot;
    int nextStreamId;
    bool disposed;

    /// <summary>Builds a source over a device and a content manager.</summary>
    /// <param name="device">Where the textures live.</param>
    /// <param name="assets">Where their bytes come from.</param>
    /// <param name="streamingPoolBytes">
    ///     How many bytes of mip tail may be resident, or zero to load every texture whole. Zero is
    ///     the default and is <em>exactly</em> the behaviour that existed before streaming did: no
    ///     residency, no per-frame cost, and the same commands recorded in the same order.
    /// </param>
    /// <param name="mipBias">
    ///     Levels added to every wanted width, positive for coarser. Only read when streaming is on;
    ///     see <see cref="TextureStreamer.MipBias" /> for why the bias is here and not on a sampler.
    /// </param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="streamingPoolBytes" /> is negative.</exception>
    public AssetTextureSource(
        IGraphicsDevice device,
        AssetManager assets,
        long streamingPoolBytes = 0,
        float mipBias = 0f
    ) {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentOutOfRangeException.ThrowIfNegative(streamingPoolBytes);

        this.device = device;
        this.assets = assets;

        if (streamingPoolBytes <= 0) {
            streamStaging = [];
            streamStagingCapacity = [];

            return;
        }

        streamSource = new(assets);
        Streaming = new(streamSource, streamingPoolBytes) { MipBias = mipBias };

        // ⚠ Rung by frame slot, because a recorded copy reads its source at submit and not at
        // record: writing this frame's tail over bytes a previous frame's copy has not yet read is
        // the bug the ring exists to rule out. TerrainRenderer.Stage has the same shape and the
        // longer explanation.
        var slots = Math.Max(1, device.FramesInFlight);

        streamStaging = new BufferHandle[slots];
        streamStagingCapacity = new long[slots];
    }

    /// <summary>The streamer, when streaming is on.</summary>
    /// <remarks>
    ///     Null is the whole of the degradation story: with no streamer every texture is loaded
    ///     whole exactly as it was before this existed, and none of the per-frame work below runs.
    /// </remarks>
    public TextureStreamer? Streaming { get; }

    /// <summary>Where the streamer's refusals are reported, or null for nobody watching.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The middle link of the chain that carries a host's logger to
    ///         <c>PageResidency.Logger</c>.</b> A game builds this and never sees the streamer
    ///         or the residency under it, so a logger that stopped here would leave events 4001 and
    ///         4002 documented and unreachable.
    ///     </para>
    ///     <para>
    ///         Kept in a field as well as forwarded, so that a source with no pool round-trips what it
    ///         was given rather than answering null and reading as a link that did not take. There is
    ///         nothing under it to log in that case — with <see cref="Streaming" /> null there is no
    ///         residency and no refusal to report.
    ///     </para>
    /// </remarks>
    public ILogger? Logger {
        get => logger;
        set {
            logger = value;

            if (Streaming is { } streaming) {
                streaming.Logger = value;
            }
        }
    }

    /// <summary>How many swaps were skipped because the frame's staging was full.</summary>
    /// <remarks>
    ///     Distinct from <see cref="TextureStreamer.Rejections" />, and the two have opposite fixes:
    ///     a rejection says the pool is too small for the scene, and this says one frame tried to
    ///     upload more than a frame carries. Both mean a texture drew coarser than it could have.
    /// </remarks>
    public long StreamingRefusals { get; private set; }

    /// <summary>How many streamed textures have been swapped to a different resolution.</summary>
    public long StreamingSwaps { get; private set; }

    /// <summary>How many bytes of streamed mip tail are on the device right now.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The second copy, and the number a project has to add to its pool budget itself.</b>
    ///         <see cref="TextureStreamer.ResidentBytes" /> is the host figure — the pages
    ///         <see cref="TexturePagePool" /> holds — and this is the images those pages were uploaded
    ///         into. Both exist at once, on purpose: a swap re-uploads a whole tail, and re-reading it
    ///         from disk on every resolution change would put a file read in the frame path.
    ///     </para>
    ///     <para>
    ///         <b>Measured rather than assumed to be equal, because it is not.</b> A tail whose pages
    ///         have arrived but whose swap was refused for staging — see
    ///         <see cref="StreamingRefusals" /> — is large in the pool and small on the device, and one
    ///         that arrived and was then evicted is the other way round. So the two numbers are
    ///         reported separately and a budget covering both would be an approximation stated as a
    ///         promise. <c>PoolMegabytes</c> is, and remains, the host number.
    ///     </para>
    /// </remarks>
    public long StreamedImageBytes { get; private set; }

    /// <summary>How many distinct textures have been asked for.</summary>
    public int Requested => entries.Count;

    /// <summary>How many are on the device and viewable.</summary>
    public int Loaded {
        get {
            var count = 0;

            foreach (var entry in entries.Values) {
                if (entry.View.IsValid) {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>How many will not arrive.</summary>
    /// <remarks>
    ///     A reference nothing shipped, a chunk that is not a KTX2, a texture whose shape this does not
    ///     upload. All three are content problems and all three look like a material sampling the
    ///     table's fallback, which is a defined picture and the wrong one.
    /// </remarks>
    public int Failed {
        get {
            var count = 0;

            foreach (var entry in entries.Values) {
                if (entry.Failed) {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>The view a reference names, if its pixels are on the device yet.</summary>
    /// <param name="reference">Which texture.</param>
    /// <param name="view">The view, when this returns true.</param>
    /// <returns>Whether it is ready.</returns>
    /// <exception cref="ObjectDisposedException">This has been disposed.</exception>
    public bool TryGet(AssetReference reference, out TextureViewHandle view) {
        ObjectDisposedException.ThrowIf(disposed, this);

        view = default;

        if (reference.IsNull) {
            return false;
        }

        if (!entries.TryGetValue(reference, out var entry)) {
            entries[reference] = entry = Begin(reference);
        }

        // Asking for a texture is what says it is still being drawn, which is the whole of the LRU
        // signal — a streamed texture nothing asked for this frame is the first to give its pages up.
        entry.Sampled = true;

        if (entry.View.IsValid) {
            view = entry.View;
            return true;
        }

        if (entry.Layouted is not null) {
            Enrol(entry);
        }

        // Created here rather than on the task, because a device call belongs on a thread the caller
        // chose. The copy still has to be recorded, so this is not yet ready.
        if (!entry.Failed && entry.Texture.IsValid == false && entry.Decoded is { IsCompletedSuccessfully: true }) {
            Create(entry);
        }

        if (entry.Decoded is { IsFaulted: true }) {
            entry.Failed = true;
        }

        return false;
    }

    /// <summary>Says how many texels across a frame needs a texture to be.</summary>
    /// <param name="reference">Which texture.</param>
    /// <param name="width">
    ///     The wanted width in texels —
    ///     <see cref="TextureStreamer.WantedWidth(float,float,float,float)" /> computes one from a
    ///     bounding radius and a view.
    /// </param>
    /// <remarks>
    ///     <para>
    ///         Does nothing when streaming is off, and nothing for a texture that is not streamed.
    ///         It holds for one frame: <see cref="Update" /> reads it and clears it, because a want
    ///         is a statement about a camera and a camera moves. Idempotent within a frame, and the
    ///         largest of everything said wins, so six materials sizing one texture is one want.
    ///     </para>
    ///     <para>
    ///         <b><see cref="TextureDemand" /> is what calls this</b>, once a frame, from
    ///         <see cref="WorldRenderer.Draw" />. A project with a better signal — a sampling
    ///         feedback buffer, an author's own priority — calls it instead or as well; this is the
    ///         seam, and it is deliberately one method wide.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Not calling this is not the same as wanting nothing.</b> A texture
    ///         <see cref="TryGet" /> asked for and nobody sized wants to be <em>complete</em> — see
    ///         <see cref="Update" /> — so a texture the survey cannot see, a terrain layer or a
    ///         particle's map, gets today's picture wherever the budget allows one, and the budget
    ///         rather than a guess is what makes it coarser.
    ///     </para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">This has been disposed.</exception>
    public void Want(AssetReference reference, int width) {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (Streaming is null || reference.IsNull) {
            return;
        }

        if (!entries.TryGetValue(reference, out var entry)) {
            entries[reference] = entry = Begin(reference);
        }

        if (entry.Layouted is not null) {
            Enrol(entry);
        }

        entry.Wanted = Math.Max(entry.Wanted, width);
    }

    /// <summary>Records the copies for every texture whose bytes are waiting.</summary>
    /// <param name="commands">The frame's list.</param>
    /// <exception cref="ArgumentNullException"><paramref name="commands" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The state a barrier claims a texture was in has to be the state it was in.</b> A
    ///         fresh texture is <see cref="ResourceState.Undefined" />, so the transition into
    ///         <see cref="ResourceState.CopyDestination" /> is from undefined exactly once and from
    ///         nothing else ever — this only ever uploads a texture once, which is what makes that
    ///         simple here and is why <c>UiRenderer</c>'s atlas, which re-uploads, has to track it.
    ///     </para>
    ///     <para>
    ///         The staging buffers are kept rather than freed, and that is a cost worth naming: a
    ///         scene's textures hold a second copy of themselves in host memory until this is disposed.
    ///         Freeing them needs to know the copy has retired, which needs a fence this does not have;
    ///         the alternative — destroying a buffer the GPU may still be reading — is the one failure
    ///         the RHI's deferred destroy cannot save a caller from.
    ///     </para>
    /// </remarks>
    public void Update(ICommandList commands) {
        ArgumentNullException.ThrowIfNull(commands);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (Streaming is not null) {
            Ask();
            Streaming.Service();
            Swap(commands);
        }

        if (uploading.Count == 0) {
            return;
        }

        foreach (var entry in uploading) {
            var data = entry.Data!;

            commands.Barrier(
                new([], [new(entry.Texture, ResourceState.Undefined, ResourceState.CopyDestination)])
            );

            for (var level = 0; level < data.LevelCount; level++) {
                var mip = data.Levels[level];

                commands.CopyBufferToTexture(
                    entry.Staging,
                    mip.Offset,
                    new(entry.Texture, level),
                    new(mip.Width, mip.Height, mip.Depth)
                );
            }

            commands.Barrier(
                new([], [new(entry.Texture, ResourceState.CopyDestination, ResourceState.ShaderRead)])
            );

            // The view last, because it is what TryGet answers with: a view that existed before the
            // copy was recorded would be a material sampling undefined memory for a frame, which is
            // exactly the glitch the fallback slot is meant to make impossible.
            entry.View = device.CreateTextureView(entry.Texture);

            // Nothing needs the pixels once they are recorded, and a scene's worth of them is the
            // largest thing this holds.
            entry.Data = null;
        }

        uploading.Clear();
    }

    /// <summary>Starts a read, and answers with the entry that will hold it.</summary>
    Entry Begin(AssetReference reference) {
        var entry = new Entry();

        string address;

        try {
            address = assets.AddressOf(reference);
        } catch (ReferenceNotFoundException) {
            // A reference the catalog does not know is one texture in a level, and a frame that threw
            // would take the level with it.
            entry.Failed = true;
            return entry;
        }

        if (Streaming is not null) {
            entry.StreamId = nextStreamId++;
            entry.Address = address;
            streamSource!.Register(entry.StreamId, address);

            // The header and the level index, which is 80 + 24n bytes off the front of the file. The
            // pixels are what streaming exists not to read.
            entry.Layouted = Task.Run(
                async () => {
                    await using var stream = await assets.OpenAsync(address).ConfigureAwait(false);

                    // ⚠ Checked here rather than at the first page read, because this is the last
                    // point at which falling back costs one header. A page is a byte range of a
                    // mapped bundle and an LZ4-packed chunk has no such range — see Vixen.Assets on
                    // building streamed payloads uncompressed — so a compressed texture is not a
                    // failure, it is one that loads whole.
                    if (!stream.CanSeek) {
                        throw new NotSupportedException(
                            $"'{address}' is in a compressed chunk, so it cannot be paged."
                        );
                    }

                    return await Ktx2.ReadLayoutAsync(stream).ConfigureAwait(false);
                }
            );

            return entry;
        }

        entry.Decoded = Decode(address);

        return entry;
    }

    /// <summary>Reads and decodes a whole texture, off the asking thread.</summary>
    /// <remarks>
    ///     The ask happens inside extraction, and a bundle read plus a block-compressed decode is the
    ///     worst work there is to do inside a frame.
    /// </remarks>
    Task<TextureData> Decode(string address) =>
        Task.Run(
            () => {
                using var stream = assets.Open(address);
                using var memory = new MemoryStream();

                stream.CopyTo(memory);

                return Ktx2.Read(memory.ToArray());
            }
        );

    /// <summary>Registers a texture with the streamer once its layout has been read.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A texture whose whole chain fits in one page falls back to being loaded
    ///         whole.</b> There is nothing to stream in it, and registering it anyway would cost a
    ///         residency entry and a swap for a texture that was never going to change resolution.
    ///         The fallback re-reads a file that is by construction under one page, which is the
    ///         cheapest read there is.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And so does one this cannot page at all.</b> A compressed chunk, a source that
    ///         does not seek, a container this cannot parse — none of them are a reason for a
    ///         material to sample the fallback slot, because the whole-file path handles every one
    ///         of them and always did. A texture that is genuinely broken fails in the decode
    ///         instead, and is counted there. This is what makes turning streaming on a decision
    ///         about memory rather than a decision about which content ships.
    ///     </para>
    /// </remarks>
    void Enrol(Entry entry) {
        var layouted = entry.Layouted!;

        if (layouted.IsFaulted) {
            entry.Layouted = null;
            entry.StreamId = -1;
            entry.Decoded = Decode(entry.Address!);

            return;
        }

        if (!layouted.IsCompletedSuccessfully) {
            return;
        }

        entry.Layouted = null;

        if (Streaming!.Register(entry.StreamId, layouted.Result)) {
            entry.Layout = layouted.Result;
            streamed.Add(entry);

            return;
        }

        entry.StreamId = -1;
        entry.Decoded = Decode(entry.Address!);
    }

    /// <summary>Turns this frame's asks into page requests, and forgets them.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>A texture that was sampled and not sized wants to be complete.</b> That is the
    ///         default residency signal, and it is chosen over a guess for one reason: with a big
    ///         enough pool it produces exactly the picture the whole-file path produced, so turning
    ///         a pool on is a decision about memory and never a silent drop in quality. What makes
    ///         it coarser under pressure is the budget and the least-recently-used order, which are
    ///         measurable, rather than a heuristic nobody tuned.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Here rather than in <see cref="TryGet" />, which is extraction's hot path.</b>
    ///         Wanting a texture walks every page of its tail — hundreds for a 4K albedo — and doing
    ///         that once per material per frame would do it many times over for the texture six
    ///         materials share. Once per streamed texture per frame is the same information.
    ///     </para>
    /// </remarks>
    void Ask() {
        foreach (var entry in streamed) {
            if (entry.Wanted >= 0) {
                Streaming!.Want(entry.StreamId, entry.Wanted);
            } else if (entry.Sampled) {
                Streaming!.Want(entry.StreamId, entry.Layout!.Width);
            }

            entry.Wanted = -1;
            entry.Sampled = false;
        }
    }

    /// <summary>Swaps every streamed texture whose resident tail is no longer the one on the device.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>A complete smaller image, swapped for a larger one, rather than a full-size image
    ///         with its top levels missing.</b> The alternative — allocate at full size and hide the
    ///         absent levels behind a view's <c>baseMipLevel</c> or a sampler's <c>MinLod</c> —
    ///         fails on two counts. It saves no memory, because the allocation is the memory
    ///         streaming exists not to spend; and <c>baseMipLevel</c> is honoured for sampled
    ///         bindings on Vulkan and WebGPU but not on OpenGL, which binds the whole chain and
    ///         reads from level zero whatever the view said. Vulkan sparse residency would do it
    ///         properly and exists on one backend of three.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A swap changes what <see cref="TryGet" /> answers.</b> A caller that cached the
    ///         view is holding a destroyed one; the frame's bindless table is rebuilt from
    ///         <see cref="TryGet" /> every frame, which is what makes that safe here and is a
    ///         constraint on anyone who caches.
    ///     </para>
    /// </remarks>
    void Swap(ICommandList commands) {
        streamSlot = (streamSlot + 1) % streamStaging.Length;
        streamAt = 0;

        var uploaded = 0L;

        foreach (var entry in streamed) {
            var layout = entry.Layout!;
            var level = Streaming!.ResidentLevel(entry.StreamId);

            if (level == entry.Level) {
                continue;
            }

            var length = (int)layout.TailLength(level);

            if (uploaded + length > StreamStagingCap) {
                StreamingRefusals++;
                continue;
            }

            var buffer = ArrayPool<byte>.Shared.Rent(length);

            try {
                // Raced with an eviction between the level being asked for and the bytes being read.
                // Nothing is lost but a frame: the next one asks again.
                if (Streaming.CopyTail(entry.StreamId, level, buffer.AsSpan(0, length)) < 0) {
                    continue;
                }

                Upload(commands, entry, layout, level, buffer.AsSpan(0, length));
                uploaded += length;
            } finally {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    /// <summary>Creates the image a tail decodes to, records its copies, and swaps it in.</summary>
    void Upload(ICommandList commands, Entry entry, Ktx2Layout layout, int level, ReadOnlySpan<byte> tail) {
        var source = Stage(tail, out var staged);
        var described = layout.Levels[level];

        var texture = device.CreateTexture(
            new(
                layout.Format,
                described.Width,
                described.Height,
                TextureUsage.Sampled | TextureUsage.CopyDestination,
                described.Depth,
                layout.LevelCount - level,
                Dimension: layout.Depth > 1 ? TextureDimension.Texture3D : TextureDimension.Texture2D,
                Name: "Material.Texture.Streamed"
            )
        );

        // A fresh texture every swap, so the transition into CopyDestination is from Undefined
        // exactly once and from nothing else ever.
        commands.Barrier(new([], [new(texture, ResourceState.Undefined, ResourceState.CopyDestination)]));

        // ⚠ The staged bytes run smallest level first — the file's order, which is what makes a tail
        // one contiguous read — and the image's levels run the other way. The source offset is the
        // level's own place in the file and not a running total, which is what keeps the two apart.
        for (var from = level; from < layout.LevelCount; from++) {
            var mip = layout.Levels[from];

            commands.CopyBufferToTexture(
                source,
                staged + (mip.Offset - layout.DataOffset),
                new(texture, from - level),
                new(mip.Width, mip.Height, mip.Depth)
            );
        }

        commands.Barrier(new([], [new(texture, ResourceState.CopyDestination, ResourceState.ShaderRead)]));

        var view = device.CreateTextureView(texture);

        // The old pair last, and destroyed rather than kept: the device defers destruction until the
        // frames that could still be reading them have retired, which is the one thing that makes a
        // swap safe inside a recorded frame.
        if (entry.View.IsValid) {
            device.Destroy(entry.View);
        }

        if (entry.Texture.IsValid) {
            device.Destroy(entry.Texture);
        }

        // The image the tail decoded to is exactly the tail's bytes, so the level it starts at is
        // all this has to know: what was there is subtracted and what replaced it is added.
        if (entry.Level >= 0) {
            StreamedImageBytes -= layout.TailLength(entry.Level);
        }

        StreamedImageBytes += layout.TailLength(level);

        entry.Texture = texture;
        entry.View = view;
        entry.Level = level;

        StreamingSwaps++;
    }

    /// <summary>Writes a tail into this frame's staging slot and answers where a copy reads it.</summary>
    /// <remarks>
    ///     ⚠ <b>Growth swaps the buffer rather than moving it.</b> The copies already recorded hold
    ///     the old handle, whose destruction the device defers until the frames that could read it
    ///     have retired — so the old bytes stay put and only new writes land in the replacement.
    ///     <c>TerrainRenderer.Stage</c> has the same shape and the longer explanation.
    /// </remarks>
    BufferHandle Stage(ReadOnlySpan<byte> bytes, out long offset) {
        var at = (streamAt + StreamStagingAlignment - 1) / StreamStagingAlignment * StreamStagingAlignment;

        if (!streamStaging[streamSlot].IsValid || at + bytes.Length > streamStagingCapacity[streamSlot]) {
            if (streamStaging[streamSlot].IsValid) {
                device.Destroy(streamStaging[streamSlot]);
            }

            streamStagingCapacity[streamSlot] = Math.Max(
                bytes.Length,
                Math.Max(streamStagingCapacity[streamSlot] * 2, 1024 * 1024)
            );

            streamStaging[streamSlot] = device.CreateBuffer(
                new(
                    streamStagingCapacity[streamSlot],
                    BufferUsage.CopySource,
                    MemoryAccess.HostUpload,
                    "Material.Texture.StreamStaging"
                )
            );

            at = 0;
        }

        device.Write(streamStaging[streamSlot], at, bytes);

        offset = at;
        streamAt = at + bytes.Length;

        return streamStaging[streamSlot];
    }

    /// <summary>Creates the texture and fills its staging buffer.</summary>
    void Create(Entry entry) {
        var data = entry.Decoded!.Result;

        if (data.FaceCount > 1 || data.LayerCount > 1) {
            entry.Failed = true;
            return;
        }

        // ⚠ The dimension follows the depth, and it did not before. A `.cube` grading table arrives
        // here as a volume, and a description that left `Dimension` at its 2D default created an
        // array-shaped texture the shader's `Texture3D` binding cannot be satisfied by — the
        // descriptor write is refused and the pass loses its whole set. Every other texture in the
        // engine has a depth of one and takes the same branch it always did.
        var texture = device.CreateTexture(
            new(
                data.Format,
                data.Width,
                data.Height,
                TextureUsage.Sampled | TextureUsage.CopyDestination,
                data.Depth,
                data.LevelCount,
                Dimension: data.Depth > 1 ? TextureDimension.Texture3D : TextureDimension.Texture2D,
                Name: "Material.Texture"
            )
        );

        var buffer = device.CreateBuffer(
            new(data.ByteLength, BufferUsage.CopySource, MemoryAccess.HostUpload, "Material.Texture.Staging")
        );

        device.Write(buffer, 0, data.Pixels);

        entry.Texture = texture;
        entry.Staging = buffer;
        entry.Data = data;

        textures.Add(texture);
        staging.Add(buffer);
        uploading.Add(entry);
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        foreach (var entry in entries.Values) {
            if (entry.View.IsValid) {
                device.Destroy(entry.View);
            }

            // A streamed texture is not in `textures`: it is replaced rather than accumulated, so the
            // only handle that ever exists is the one on the entry.
            if (entry.StreamId >= 0 && entry.Texture.IsValid) {
                device.Destroy(entry.Texture);
            }
        }

        foreach (var texture in textures) {
            device.Destroy(texture);
        }

        foreach (var buffer in staging) {
            device.Destroy(buffer);
        }

        foreach (var buffer in streamStaging) {
            if (buffer.IsValid) {
                device.Destroy(buffer);
            }
        }

        Streaming?.Dispose();

        entries.Clear();
        textures.Clear();
        staging.Clear();
        uploading.Clear();
        streamed.Clear();
    }

    /// <summary>
    ///     The off-thread read a reference is waiting on, or null when it is waiting on none.
    /// </summary>
    /// <param name="reference">Which texture.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>For a test that has to wait for the work rather than for a clock.</b> The header
    ///         read and the whole-file decode are <see cref="Task.Run(Action)" />, so how long a
    ///         fixture waits for one is decided by the thread pool and not by the read; a suite that
    ///         gave up after an interval was measuring the runner. This is how a settle can say
    ///         "something is still on its way" instead — see <c>AssetTextureStreamingTests</c>, which
    ///         keeps running frames for as long as this, <c>TextureStreamer.Loading</c> or
    ///         <c>TextureStreamer.PendingRequests</c> says there is anything left to arrive.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Null the moment the read finishes, rather than when its result is taken up.</b> A
    ///         handle that stayed non-null after the task completed would make "is anything still
    ///         outstanding" permanently true, and a fixture that gives up when nothing is outstanding
    ///         would then never give up at all — it would hang on a real regression instead of failing.
    ///         A reference nothing has asked for yet has no entry, which is the same answer.
    ///     </para>
    /// </remarks>
    internal Task? Reading(AssetReference reference) {
        if (!entries.TryGetValue(reference, out var entry)) {
            return null;
        }

        Task? read = entry.Layouted ?? (Task?)entry.Decoded;

        return read is { IsCompleted: false } ? read : null;
    }

    /// <summary>One texture, somewhere between named and sampled.</summary>
    sealed class Entry {
        public Task<TextureData>? Decoded;
        public TextureData? Data;
        public TextureHandle Texture;
        public BufferHandle Staging;
        public TextureViewHandle View;
        public bool Failed;

        /// <summary>What the streamer numbers it, or <c>-1</c> when it is not streamed.</summary>
        public int StreamId = -1;

        /// <summary>Where its bytes are, kept for the fall back to loading it whole.</summary>
        public string? Address;

        public Task<Ktx2Layout>? Layouted;
        public Ktx2Layout? Layout;

        /// <summary>The level its image starts at, or <c>-1</c> when nothing is on the device.</summary>
        public int Level = -1;

        /// <summary>The width a caller sized it at this frame, or <c>-1</c> for nobody.</summary>
        public int Wanted = -1;

        /// <summary>Whether anything asked for it this frame.</summary>
        public bool Sampled;
    }
}
