// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Vixen.Rendering;

namespace Vixen.Engine.Renderer;

/// <summary>The texture numbers a host's quality tier resolved to.</summary>
/// <remarks>
///     <para>
///         <c>RenderQuality</c>'s <c>textures</c> group, handed over rather than referenced —
///         <c>TerrainFactory.Vegetation</c>'s arrangement and for its reason. This assembly must not
///         depend on <c>Vixen.Rendering.PostFx</c> to know how big a pool to make, and the tier
///         table must not know that a texture pool is what the number ends up as.
///     </para>
///     <para>
///         ⚠ <b>The default is off, and off is exactly what existed before streaming did.</b> A zero
///         pool loads every texture whole with no residency and no per-frame cost. A host that wants
///         streaming says so with a number; nothing turns it on by inference.
///     </para>
/// </remarks>
public sealed record TextureStreamingQuality {
    /// <summary>How many megabytes of mip tail may be resident, or zero to load every texture whole.</summary>
    public int PoolMegabytes { get; init; }

    /// <summary>Levels added to every wanted width, positive for coarser.</summary>
    public float MipBias { get; init; }
}

/// <summary>Where a streamed texture's bytes are read from.</summary>
/// <remarks>
///     One method, and deliberately not a <c>Stream</c>: a pool reads byte ranges of one file at a
///     time from several threads, and a stream has a position that two of those would fight over.
///     What implements this over content is <see cref="AssetTextureStreamSource" />; what implements
///     it in a test is an array.
/// </remarks>
public interface ITextureStreamSource {
    /// <summary>Reads a range of one texture's file.</summary>
    /// <param name="texture">Which texture, in the numbering the caller registered.</param>
    /// <param name="offset">Where to start, in bytes from the start of the file.</param>
    /// <param name="destination">Where to put them.</param>
    /// <param name="cancellation">Cancelled when the streamer is disposed, or the page is no longer wanted.</param>
    /// <returns>How many bytes were read.</returns>
    ValueTask<int> ReadAsync(int texture, long offset, Memory<byte> destination, CancellationToken cancellation);
}

/// <summary>The pool a streamed texture's mip tail lands in, one fixed-size page at a time.</summary>
/// <remarks>
///     <para>
///         <b>A page is a fixed byte-size slice of a file's level data, numbered from the front.</b>
///         That is the decision the whole design turns on, and the alternatives were worse.
///         <em>A page per mip level</em> maps onto <see cref="IPageStore" /> most obviously and is
///         unusable: <see cref="IPageStore.PageSize" /> is one number, mip levels differ by factors
///         of thousands, and <see cref="PageResidency" /> charges the budget
///         <c>residentPages × PageSize</c> — so a 1×1 level would occupy, and be billed for, a slot
///         big enough for a 2048×2048 one. <em>A pool per texture</em> gives every texture its own
///         budget and its own eviction order, which is the failure
///         <see cref="PageResidency" />'s own remarks name: three budgets to tune means there is no
///         budget at all, and here it would be three thousand.
///     </para>
///     <para>
///         <b>Fixed-size slices work because KTX2 stores level data smallest first.</b> Page
///         <c>n</c> is bytes <c>[n·PageSize, (n+1)·PageSize)</c> of the level data, and the level
///         data runs from the 1×1 level upwards — so <em>a contiguous prefix of pages is exactly a
///         complete mip tail</em>. "Pages 0 to k are resident" and "levels L and smaller are
///         resident" are the same statement, which is what makes the accounting exact and the
///         residency question answerable by counting.
///     </para>
///     <para>
///         <b>Page 0 is pinned, and that is the floor the whole degradation story rests on.</b> It
///         holds the first <see cref="PageSize" /> bytes of level data, which is always at least the
///         smallest levels, so a registered texture always has something complete to sample. A
///         texture whose entire level data fits in one page is therefore never streamed at all.
///     </para>
///     <para>
///         ⚠ <b>The bytes stay in host memory and the pool never refuses.</b> This is a byte pool,
///         not a staging ring — <see cref="Place" /> is a copy into an array this owns, so the
///         refusal <see cref="IPageStore.Place" /> allows for cannot arise here. What can still fail
///         is a request, when the budget is full of pinned pages; that is
///         <see cref="PageResidency.Rejections" /> and it is counted there.
///     </para>
/// </remarks>
public sealed class TexturePagePool : IPageStore {
    readonly ITextureStreamSource source;
    readonly Dictionary<int, Ktx2Layout> layouts = [];

    /// <summary>One array per slot, allocated when the slot is first written.</summary>
    /// <remarks>
    ///     ⚠ <b>Lazily, because a budget is a ceiling and not a reservation.</b> A gigabyte budget at
    ///     64 KiB a page is sixteen thousand slots, and allocating that up front would spend the
    ///     whole budget on a menu screen with four textures on it. What a pool has to guarantee is
    ///     that it never exceeds the budget, which per-slot arrays do exactly as well as one big one.
    /// </remarks>
    readonly byte[]?[] slots;
    readonly int[] lengths;

    /// <summary>Creates a pool over a source.</summary>
    /// <param name="source">Where page bytes are read from.</param>
    /// <param name="slotCount">How many pages it holds.</param>
    /// <param name="pageSize">How many bytes one page is.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A count is not positive.</exception>
    public TexturePagePool(ITextureStreamSource source, int slotCount, int pageSize = 64 * 1024) {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slotCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);

        this.source = source;

        SlotCount = slotCount;
        PageSize = pageSize;

        slots = new byte[slotCount][];
        lengths = new int[slotCount];
    }

    /// <inheritdoc />
    public int PageSize { get; }

    /// <inheritdoc />
    public int SlotCount { get; }

    /// <summary>How many pages have been placed since this was created.</summary>
    public long Placements { get; private set; }

    /// <summary>How many have been evicted.</summary>
    public long Evictions { get; private set; }

    /// <summary>Says a texture exists and where its levels are.</summary>
    /// <param name="texture">The number a caller identifies it by.</param>
    /// <param name="layout">What <see cref="Ktx2.ReadLayout" /> said about its file.</param>
    /// <exception cref="ArgumentNullException"><paramref name="layout" /> is null.</exception>
    public void Register(int texture, Ktx2Layout layout) {
        ArgumentNullException.ThrowIfNull(layout);
        layouts[texture] = layout;
    }

    /// <summary>What a texture's file said about itself, if it was registered.</summary>
    /// <param name="texture">Which texture.</param>
    /// <param name="layout">Its layout, when this returns true.</param>
    public bool TryGetLayout(int texture, out Ktx2Layout layout) => layouts.TryGetValue(texture, out layout!);

    /// <summary>How many pages a texture's level data occupies.</summary>
    /// <param name="texture">Which texture.</param>
    /// <exception cref="KeyNotFoundException">It was never registered.</exception>
    public int PageCount(int texture) => PageCount(layouts[texture], PageSize);

    /// <summary>How many pages cover a tail down from a level.</summary>
    /// <param name="texture">Which texture.</param>
    /// <param name="firstLevel">The largest level of the tail.</param>
    /// <exception cref="KeyNotFoundException">It was never registered.</exception>
    public int PagesFor(int texture, int firstLevel) {
        var layout = layouts[texture];
        return (int)((layout.TailLength(firstLevel) + PageSize - 1) / PageSize);
    }

    /// <inheritdoc />
    public async ValueTask<int> LoadAsync(PageKey key, Memory<byte> destination, CancellationToken cancellation) {
        if (!layouts.TryGetValue(key.Source, out var layout)) {
            return 0;
        }

        var at = (long)key.Index * PageSize;
        var length = (int)Math.Min(PageSize, layout.DataLength - at);

        if (length <= 0) {
            return 0;
        }

        return await source.ReadAsync(key.Source, layout.DataOffset + at, destination[..length], cancellation)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public bool Place(PageKey key, int slot, ReadOnlySpan<byte> bytes) {
        if (bytes.IsEmpty) {
            return false;
        }

        var page = slots[slot] ??= new byte[PageSize];

        bytes.CopyTo(page);
        lengths[slot] = bytes.Length;
        Placements++;

        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Nothing is erased: the next <see cref="Place" /> overwrites the slot, and the length is
    ///     zeroed only so that a read of a stale slot is empty rather than plausible.
    /// </remarks>
    public void Evict(PageKey key, int slot) {
        lengths[slot] = 0;
        Evictions++;
    }

    /// <summary>One slot's bytes.</summary>
    /// <param name="slot">Which slot.</param>
    /// <param name="length">How many of them are a page's.</param>
    public ReadOnlySpan<byte> Slot(int slot, out int length) {
        length = lengths[slot];
        return slots[slot] is { } page ? page.AsSpan(0, length) : [];
    }

    /// <summary>How many pages a layout's level data occupies at a page size.</summary>
    internal static int PageCount(Ktx2Layout layout, int pageSize) =>
        (int)((layout.DataLength + pageSize - 1) / pageSize);
}

/// <summary>Mip tails in, a byte budget held, and the coarser picture when it cannot be.</summary>
/// <remarks>
///     <para>
///         <b>Doc 08's streaming manager, textures only, and the third consumer
///         <see cref="PageResidency" /> was built for.</b> The service owns the queue, the budget and
///         the eviction order; <see cref="TexturePagePool" /> owns where the bytes go and how they
///         are read; and this owns the one thing neither can know — what a frame actually wants,
///         and therefore which pages to ask for.
///     </para>
///     <para>
///         <b>What drives residency is a wanted width, and that is the narrowest seam that works.</b>
///         A caller says how many texels across it needs a texture to be —
///         <see cref="WantedWidth(float,float,float,float)" /> computes that from a bounding radius
///         and a view, which is the same screen-coverage estimate mesh LOD selection uses — and this
///         turns it into a level and asks for the pages that cover it. Sampling feedback would be a
///         better signal and there is no feedback buffer to read; an authored per-texture priority
///         would be a worse one, because it cannot know where the camera is. When a feedback buffer
///         exists it replaces <see cref="Want(int,int)" /> and nothing else.
///     </para>
///     <para>
///         <b>What says the wanted width is <see cref="TextureDemand" />.</b> It surveys the frame's
///         visible drawables, takes the maximum over every user of a texture, and quantises that onto
///         a ladder of mip widths with a dead band — because a wanted width that oscillates at a
///         level boundary is a swap on alternate frames, and a swap is an upload.
///     </para>
///     <para>
///         <b><see cref="MipBias" /> is consumed here rather than on the sampler, and that is a
///         portability decision.</b> <c>SamplerDescription.LodBias</c> reaches the API on Vulkan
///         only — OpenGL drops it on every GLES profile and WebGPU has no such field — so a
///         streamer that expressed pressure as a sampler bias would express it on one backend of
///         three. A bias applied to the wanted width is arithmetic, works everywhere, and has the
///         effect the knob is named for: a positive bias asks for a coarser tail and frees the
///         budget it would have taken.
///     </para>
///     <para>
///         ⚠ <b>Eviction is least-recently-used over pages and not over textures.</b> A page evicted
///         from the middle of a texture's prefix drops that texture's resident level immediately,
///         and leaves the pages above it resident and useless until they age out in their turn.
///         That self-corrects — an unusable page is never touched, so it is the next victim — and
///         the alternative is a second eviction policy that knows about textures, which is the
///         beginning of the per-consumer budget this design exists to not have.
///     </para>
/// </remarks>
public sealed class TextureStreamer : IDisposable {
    readonly TexturePagePool pool;
    readonly PageResidency residency;
    readonly Dictionary<int, Ktx2Layout> layouts = [];

    bool disposed;

    /// <summary>Creates a streamer over a source, with a budget.</summary>
    /// <param name="source">Where the bytes come from.</param>
    /// <param name="budgetBytes">How many bytes of mip tail may be resident at once.</param>
    /// <param name="pageSize">
    ///     How many bytes one page is, which is also the floor: the always-resident part of every
    ///     texture is the first page of its level data.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="source" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A size is not positive.</exception>
    public TextureStreamer(ITextureStreamSource source, long budgetBytes, int pageSize = 64 * 1024) {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budgetBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);

        var slots = (int)Math.Max(1, (budgetBytes + pageSize - 1) / pageSize);

        pool = new(source, slots, pageSize);
        residency = new(pool, budgetBytes);
    }

    /// <summary>How many bytes of mip tail may be resident at once.</summary>
    public long Budget => residency.Budget;

    /// <summary>How many are.</summary>
    public long ResidentBytes => residency.ResidentBytes;

    /// <summary>How many bytes one page is.</summary>
    public int PageSize => pool.PageSize;

    /// <summary>How many pages have been loaded since this was created.</summary>
    public long Loads => residency.Loads;

    /// <summary>How many have been evicted.</summary>
    public long Evictions => residency.Evictions;

    /// <summary>How many requests were dropped because nothing could be evicted to make room.</summary>
    /// <remarks>
    ///     The number that says the pool is too small for the scene rather than that the streamer is
    ///     broken. A frame with a positive number here sampled a coarser texture than it asked for.
    /// </remarks>
    public long Rejections => residency.Rejections;

    /// <summary>How many loads are in flight.</summary>
    public int Loading => residency.Loading;

    /// <summary>How many requests are waiting.</summary>
    public int PendingRequests => residency.PendingRequests;

    /// <summary>How many textures are registered.</summary>
    public int Textures => layouts.Count;

    /// <summary>Levels added to every wanted width, positive for coarser.</summary>
    /// <remarks>
    ///     <c>RenderQuality</c>'s <c>textures.mipBias</c>, applied to what is asked for rather than
    ///     to what is sampled. See the class remarks for why that is not where it looks like it
    ///     should be.
    /// </remarks>
    public float MipBias { get; set; }

    /// <summary>
    ///     Where the residency's refusals are reported, or null for a service nobody is watching.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>A forward onto <see cref="PageResidency.Logger" /> and nothing else</b>, because the
    ///         residency is built in this constructor and a host never sees it. Without this link the
    ///         two lines that say "the streaming budget is too small for this scene" — events 4001 and
    ///         4002 — are reachable only from a test that reaches past the streamer and sets the
    ///         property itself, which is to say not reachable from a game at all.
    ///     </para>
    ///     <para>
    ///         Settable rather than a constructor argument for the reason
    ///         <see cref="PageResidency.Logger" /> is: the streamer is built where the budget is known
    ///         and the logger arrives from the host, and those are not the same place. Nothing is
    ///         logged per frame — <see cref="Service" /> compares two longs when the frame is healthy.
    ///     </para>
    /// </remarks>
    public ILogger? Logger {
        get => residency.Logger;
        set => residency.Logger = value;
    }

    /// <summary>Says a texture is streamable, and pins the page that is always resident.</summary>
    /// <param name="texture">The number a caller identifies it by.</param>
    /// <param name="layout">What <see cref="Ktx2.ReadLayout" /> said about its file.</param>
    /// <returns>
    ///     Whether it is streamed at all. A texture whose whole level data fits in one page is not:
    ///     there is nothing to stream, and pretending otherwise costs a residency entry per frame.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="layout" /> is null.</exception>
    /// <exception cref="ObjectDisposedException">This has been disposed.</exception>
    public bool Register(int texture, Ktx2Layout layout) {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(layout);

        if (TexturePagePool.PageCount(layout, pool.PageSize) <= 1) {
            return false;
        }

        layouts[texture] = layout;
        pool.Register(texture, layout);
        residency.Pin(new(texture, 0));

        return true;
    }

    /// <summary>Whether a texture is one this streams.</summary>
    /// <param name="texture">Which texture.</param>
    public bool IsRegistered(int texture) => layouts.ContainsKey(texture);

    /// <summary>The coarsest level a texture is guaranteed, whatever the budget.</summary>
    /// <param name="texture">Which texture.</param>
    /// <returns>The level the pinned first page covers.</returns>
    /// <exception cref="KeyNotFoundException">It was never registered.</exception>
    public int PinnedLevel(int texture) {
        var layout = layouts[texture];
        return layout.TailFor(Math.Min(pool.PageSize, layout.DataLength));
    }

    /// <summary>The largest level of a texture that is completely resident.</summary>
    /// <param name="texture">Which texture.</param>
    /// <returns>
    ///     The level to start a tail at: <c>0</c> when the whole texture is here, and the smallest
    ///     level when nothing but the pinned page is.
    /// </returns>
    /// <remarks>
    ///     Counting, because a contiguous prefix of pages is a complete mip tail — see
    ///     <see cref="TexturePagePool" /> on why the pages are sliced the way they are. A page
    ///     resident above a hole is resident and unusable, and is not counted.
    /// </remarks>
    /// <exception cref="KeyNotFoundException">It was never registered.</exception>
    public int ResidentLevel(int texture) {
        var layout = layouts[texture];
        var pages = 0;

        while (pages < TexturePagePool.PageCount(layout, pool.PageSize)
            && residency.IsResident(new(texture, pages))) {
            pages++;
        }

        return layout.TailFor(Math.Min((long)pages * pool.PageSize, layout.DataLength));
    }

    /// <summary>Asks for a texture at a width, and says its pages were used this frame.</summary>
    /// <param name="texture">Which texture.</param>
    /// <param name="width">How many texels across the frame needs it to be.</param>
    /// <remarks>
    ///     Idempotent and demand-driven: a texture wanted by six views is one set of requests, and
    ///     one wanted every frame until it arrives is still one. Touching is not optional — a page
    ///     that is resident is never requested, so a frame that only requested would evict exactly
    ///     the pages it was drawing with.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">This has been disposed.</exception>
    public void Want(int texture, int width) {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (!layouts.TryGetValue(texture, out var layout)) {
            return;
        }

        var level = LevelFor(layout, width, MipBias);
        var pages = pool.PagesFor(texture, level);

        for (var page = 0; page < pages; page++) {
            var key = new PageKey(texture, page);

            residency.Touch(key);
            residency.Request(key);
        }
    }

    /// <summary>Places what has arrived and starts what there is room for.</summary>
    /// <param name="maxLoads">How many loads may be started this call.</param>
    /// <returns>How many pages became resident.</returns>
    /// <exception cref="ObjectDisposedException">This has been disposed.</exception>
    public int Service(int maxLoads = 8) {
        ObjectDisposedException.ThrowIf(disposed, this);
        return residency.Service(maxLoads);
    }

    /// <summary>Drops every request that has not started. What a camera that jumped calls.</summary>
    public void ClearRequests() => residency.ClearRequests();

    /// <summary>Copies a resident mip tail out of the pool, in file order.</summary>
    /// <param name="texture">Which texture.</param>
    /// <param name="firstLevel">The largest level of the tail.</param>
    /// <param name="destination">Where to put it; at least the tail's length.</param>
    /// <returns>How many bytes were copied, or <c>-1</c> when the tail is not all resident.</returns>
    /// <remarks>
    ///     The bytes come out in the file's order — smallest level first — because that is the order
    ///     the pages hold them in and the order <see cref="Ktx2Layout" /> describes. Whoever uploads
    ///     them un-reverses them; nothing here rearranges a mip chain it does not have to.
    /// </remarks>
    /// <exception cref="KeyNotFoundException">It was never registered.</exception>
    public int CopyTail(int texture, int firstLevel, Span<byte> destination) {
        var layout = layouts[texture];
        var wanted = layout.TailLength(firstLevel);
        var pages = pool.PagesFor(texture, firstLevel);
        var copied = 0;

        for (var page = 0; page < pages; page++) {
            if (!residency.TryGetPlacement(new(texture, page), out var placement)) {
                return -1;
            }

            var bytes = pool.Slot(placement.Slot, out var length);
            var take = (int)Math.Min(length, wanted - copied);

            bytes[..take].CopyTo(destination[copied..]);
            copied += take;
        }

        return copied;
    }

    /// <summary>The texture width a view wants of an object it can see.</summary>
    /// <param name="radius">The object's bounding radius, in world units.</param>
    /// <param name="distance">How far its centre is from the eye, in world units.</param>
    /// <param name="viewportHeight">How many pixels tall the view is.</param>
    /// <param name="verticalFieldOfView">The view's vertical field of view, in radians.</param>
    /// <returns>How many texels across the texture should be, at one texel per pixel.</returns>
    /// <remarks>
    ///     The same projected-size estimate mesh LOD selection uses, and deliberately so: an object
    ///     that has dropped to a coarse LOD is one whose textures should have dropped with it, and
    ///     two heuristics that disagree produce a low-poly mesh wearing a 4K albedo. One texel per
    ///     pixel is the assumption, which is right for an object filling its bounds and generous for
    ///     one that does not — and being generous is the safe direction, because the cost of asking
    ///     too high is a page and the cost of asking too low is a blurry frame.
    /// </remarks>
    public static int WantedWidth(float radius, float distance, float viewportHeight, float verticalFieldOfView) {
        if (radius <= 0f || viewportHeight <= 0f || verticalFieldOfView <= 0f) {
            return 0;
        }

        // Behind or at the eye: the object is not culled by this and must not divide by zero.
        var away = MathF.Max(distance, 1e-4f);
        var pixelsPerUnit = viewportHeight / (2f * MathF.Tan(verticalFieldOfView * 0.5f));

        return (int)MathF.Ceiling(2f * radius * pixelsPerUnit / away);
    }

    /// <summary>The same estimate, from the numbers a frame actually holds.</summary>
    /// <param name="bounds">The object's world bounding sphere, which is what a render object carries.</param>
    /// <param name="view">The view looking at it.</param>
    /// <param name="viewportHeight">How many pixels tall the output is.</param>
    /// <returns>
    ///     How many texels across the texture should be, or zero for a view that does no screen-size
    ///     work.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         <b>The same arithmetic as the overload above, and it has to be.</b> A view carries
    ///         <see cref="RenderView.ScreenHeightScale" /> — <c>1 / tan(fov / 2)</c>, the
    ///         resolution-independent half of the projection — rather than the field of view itself,
    ///         so the caller that has a view would otherwise have to run an <c>atan</c> back into a
    ///         field of view for this to run a <c>tan</c> forward out of it. The identity is
    ///         <c>pixelsPerUnit = viewportHeight × ScreenHeightScale / 2</c>, which is what makes this
    ///         one multiply.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Zero for a view whose <see cref="RenderView.ScreenHeightScale" /> is zero</b>,
    ///         which is what a shadow cascade and a probe face are —
    ///         <c>LodRenderFeature.Prepare</c> skips those views for the same reason. A cascade covers
    ///         the world at whatever density its extent implies and has no viewport height to measure
    ///         a texel against, so a texture wanted at a cascade's screen size would be a number with
    ///         no meaning behind it.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="view" /> is null.</exception>
    public static int WantedWidth(in BoundingSphere bounds, RenderView view, float viewportHeight) {
        ArgumentNullException.ThrowIfNull(view);

        if (bounds.Radius <= 0f || viewportHeight <= 0f || view.ScreenHeightScale <= 0f) {
            return 0;
        }

        // Behind or at the eye: the object is not culled by this and must not divide by zero.
        var away = MathF.Max(Vector3.Distance(bounds.Center, view.Position), 1e-4f);

        return (int)MathF.Ceiling(bounds.Radius * view.ScreenHeightScale * viewportHeight / away);
    }

    /// <summary>The coarsest level whose width still covers what was asked for.</summary>
    static int LevelFor(Ktx2Layout layout, int width, float bias) {
        if (width <= 0) {
            return layout.LevelCount - 1;
        }

        // Positive bias asks for a coarser tail: one level of bias halves the wanted width.
        var wanted = bias == 0f ? width : width * MathF.Pow(2f, -bias);
        var level = 0;

        while (level < layout.LevelCount - 1 && (layout.Width >> (level + 1)) >= wanted) {
            level++;
        }

        return level;
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        residency.Dispose();
        layouts.Clear();
    }
}
