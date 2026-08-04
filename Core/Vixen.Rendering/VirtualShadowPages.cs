// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Rendering;

/// <summary>
///     The physical page pool behind a virtual shadow map, and the page table that finds it.
/// </summary>
/// <remarks>
///     <para>
///         <b>Improvement 6's second consumer, and the one that proves the seam was worth drawing.</b>
///         <see cref="PageResidency" /> was built in phase 2 with three consumers in view — geometry
///         pages, texture mip tails and these — precisely so it would not come out geometry-shaped.
///         What it owns is the request queue, the byte budget, the eviction order and the counters;
///         what a store owns is where the bytes go and how they are read. A shadow page is the case
///         that tests that division hardest, because <b>there is nothing to read</b>: a shadow page's
///         content is <em>rendered</em>, not loaded, so <see cref="IPageStore.LoadAsync" /> here
///         returns immediately with nothing and <see cref="IPageStore.Place" /> allocates rather than
///         copies.
///     </para>
///     <para>
///         That is not the interface being bent. Everything the service does — dropping stale
///         requests, refusing to exceed a budget, evicting least-recently-used, never evicting a
///         pinned page — is exactly as meaningful for a page that is drawn as for one that is read,
///         and it is the *only* part of either that a frame has to get right. What the store adds is
///         a single extra fact the service has no opinion about: a page that has just been given a
///         slot holds nothing yet, and somebody has to draw into it.
///         <see cref="TakePending" /> is that fact.
///     </para>
///     <para>
///         <b>Eviction is what makes the page table dangerous, so it is what the store handles most
///         carefully.</b> A slot handed to a new virtual page while a shading pass still points at it
///         under the old one reads another part of the world's depth — a shadow of something that is
///         not there, in the right place, at a plausible depth. <see cref="IPageStore.Evict" /> clears
///         the table entry <em>before</em> the slot is reused, which is the frame boundary the service
///         cannot see and this can.
///     </para>
/// </remarks>
public sealed class VirtualShadowPages : IPageStore {
    /// <summary>The one source id every shadow page carries in a <see cref="PageKey" />.</summary>
    /// <remarks>
    ///     Shadow pages are already globally numbered — a virtual page index spans every clipmap level
    ///     and every spot light — so they need no second dimension to tell them apart. The constant is
    ///     stated rather than left at zero so that a service one day shared with geometry can attribute
    ///     a key, which is the arrangement improvement 6 is aiming at.
    /// </remarks>
    public const int Source = 0x5344;

    /// <summary>How many bytes a page notionally occupies, for the budget to be about something.</summary>
    /// <remarks>
    ///     A 128-texel square of 32-bit depth. The atlas is a texture rather than a buffer, so nothing
    ///     ever moves this many bytes — but a budget measured in pages would be a budget that means
    ///     something different for every consumer of the same service, and the whole point of one
    ///     service is one number to tune.
    /// </remarks>
    public const int PageBytes = VirtualShadowMap.PageTexels * VirtualShadowMap.PageTexels * sizeof(float);

    readonly uint[] table = new uint[VirtualShadowMap.MaxPages];
    readonly uint[] slots = new uint[VirtualShadowMap.MaxPages];
    readonly List<int> pending = [];
    readonly HashSet<int> pendingSet = [];

    /// <summary>Creates a pool of physical pages.</summary>
    /// <param name="pagesPerSide">How many pages the physical atlas is on a side.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pagesPerSide" /> is not positive.</exception>
    public VirtualShadowPages(int pagesPerSide = 32) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pagesPerSide);

        PagesPerSide = pagesPerSide;
        Array.Fill(table, VirtualShadowMap.PageAbsent);
        Array.Fill(slots, VirtualShadowMap.PageAbsent);
    }

    /// <summary>How many pages the physical atlas is on a side.</summary>
    public int PagesPerSide { get; }

    /// <inheritdoc />
    public int PageSize => PageBytes;

    /// <inheritdoc />
    public int SlotCount => PagesPerSide * PagesPerSide;

    /// <summary>How many texels the atlas is on a side.</summary>
    public int AtlasTexels => PagesPerSide * VirtualShadowMap.PageTexels;

    /// <summary>
    ///     The page table a shading pass reads: one entry per virtual page, or
    ///     <see cref="VirtualShadowMap.PageAbsent" />.
    /// </summary>
    /// <remarks>
    ///     <b>An entry appears when the page has been <em>drawn</em>, not when it was allocated.</b> A
    ///     slot just handed over holds whatever the last page left in it, so a table that published it
    ///     on allocation would have a shading pass reading another part of the world's depth — a shadow
    ///     of something that is not there, in the right place, at a plausible depth. Absent until drawn
    ///     makes the failure "unshadowed for a frame", which is the direction a shadow is allowed to be
    ///     wrong in and is the same degradation a geometry page's absence produces.
    /// </remarks>
    public ReadOnlySpan<uint> Table => table;

    /// <summary>How many pages have been allocated a slot since this was created.</summary>
    public long Allocations { get; private set; }

    /// <summary>
    ///     Which resident pages hold nothing yet and have to be drawn.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The steady state is empty, and that is the whole win.</b> A page whose slot it
    ///         already had, whose level's projection did not move — because the clipmap snaps to whole
    ///         pages, so a camera that moved less than a page leaves every footprint bit-identical —
    ///         and whose casters did not move, holds depths that are still correct. Cascades cannot
    ///         make that claim about anything, which is why they redraw a level's worth of static
    ///         geometry four times a frame.
    ///     </para>
    ///     <para>
    ///         Drained rather than read, because a page drawn once must not be drawn again: what puts
    ///         an entry back is a fresh allocation or an explicit <see cref="Invalidate" />.
    ///     </para>
    /// </remarks>
    public IReadOnlyList<int> Pending => pending;

    /// <summary>Which physical page a virtual one was allocated, drawn or not.</summary>
    /// <param name="page">The virtual page index.</param>
    /// <param name="slot">Which physical page it holds.</param>
    /// <remarks>
    ///     What a draw needs and what <see cref="Table" /> deliberately does not answer until the draw
    ///     has happened. The two are the same number a frame apart, and keeping them apart is what
    ///     stops a pass sampling a page nobody has written yet.
    /// </remarks>
    public bool TryGetAllocation(int page, out int slot) {
        slot = 0;

        if (page < 0 || page >= slots.Length || slots[page] == VirtualShadowMap.PageAbsent) {
            return false;
        }

        slot = (int)slots[page];

        return true;
    }

    /// <summary>Publishes a page's depths, now that something has drawn them.</summary>
    /// <param name="page">The virtual page index.</param>
    /// <returns>Whether there was an allocation to publish.</returns>
    public bool Drawn(int page) {
        if (!TryGetAllocation(page, out var slot)) {
            return false;
        }

        table[page] = (uint)slot;

        return true;
    }

    /// <summary>Where a virtual page's depths are, or false when nothing backs it.</summary>
    /// <param name="page">The virtual page index.</param>
    /// <param name="slot">Which physical page holds it.</param>
    public bool TryGetSlot(int page, out int slot) {
        slot = 0;

        if (page < 0 || page >= table.Length || table[page] == VirtualShadowMap.PageAbsent) {
            return false;
        }

        slot = (int)table[page];

        return true;
    }

    /// <summary>Takes the pages owed a draw, and forgets them.</summary>
    /// <param name="most">At most this many, newest first.</param>
    /// <returns>Their virtual page indices.</returns>
    /// <remarks>
    ///     <b>A budget per frame, on <see cref="PageResidency.Service" />'s terms and for its reason.</b>
    ///     A camera that turns to face a city allocates every page it can see at once, and drawing all
    ///     of them in one frame spends that frame's whole caster budget on pages the next frame may not
    ///     want either. What is left over is not lost: the page is resident and empty, so the marking
    ///     pass asks for it again and it is drawn a frame or two later — which costs a soft edge on
    ///     newly-revealed geometry rather than a stall.
    /// </remarks>
    public int[] TakePending(int most = 16) {
        if (pending.Count == 0 || most <= 0) {
            return [];
        }

        var count = Math.Min(most, pending.Count);
        var taken = new int[count];

        // From the end, because Place appends and the newest allocation is about the camera as it is
        // now — the same argument PageResidency's request stack makes.
        for (var i = 0; i < count; i++) {
            var page = pending[^1];

            pending.RemoveAt(pending.Count - 1);
            pendingSet.Remove(page);
            taken[i] = page;
        }

        return taken;
    }

    /// <summary>Says a resident page's depths are stale and it has to be drawn again.</summary>
    /// <param name="page">The virtual page index.</param>
    /// <returns>Whether there was a resident page to invalidate.</returns>
    /// <remarks>
    ///     What a moved caster costs. A page nothing invalidates is never redrawn, which is the whole
    ///     of why a virtual shadow map is cheaper than a cascade — and it is also why an invalidation
    ///     that does not happen is a shadow left behind by an object that has moved, standing in the
    ///     air with nothing casting it.
    /// </remarks>
    public bool Invalidate(int page) {
        if (page < 0 || page >= slots.Length || slots[page] == VirtualShadowMap.PageAbsent) {
            return false;
        }

        // Unpublished as well as re-queued: a stale page is one whose depths are wrong, and leaving it
        // in the table until it is redrawn is a frame of exactly the shadow the invalidation is about.
        table[page] = VirtualShadowMap.PageAbsent;

        if (pendingSet.Add(page)) {
            pending.Add(page);
        }

        return true;
    }

    /// <summary>Says every resident page is stale.</summary>
    /// <remarks>
    ///     What a light that turned costs, and a level whose projection moved. Coarse on purpose: the
    ///     alternative is a per-page test against a matrix that changed, and a matrix that changed
    ///     moved every page of that level by construction.
    /// </remarks>
    public void InvalidateAll() {
        for (var page = 0; page < slots.Length; page++) {
            Invalidate(page);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <b>Nothing to read, and that is the honest answer rather than a stub.</b> A shadow page's
    ///     depths are produced by drawing casters into it, which happens inside a frame and cannot
    ///     happen on an I/O thread. So the load is the allocation, it completes immediately, and
    ///     <see cref="Place" /> is where the work actually is.
    /// </remarks>
    public ValueTask<int> LoadAsync(PageKey key, Memory<byte> destination, CancellationToken cancellation) =>
        ValueTask.FromResult(0);

    /// <inheritdoc />
    public bool Place(PageKey key, int slot, ReadOnlySpan<byte> bytes) {
        if (key.Index < 0 || key.Index >= table.Length || slot < 0 || slot >= SlotCount) {
            return false;
        }

        slots[key.Index] = (uint)slot;
        Allocations++;

        // The *allocation*, and deliberately not the table. A slot just handed over holds whatever the
        // last page left in it, so publishing it here would have a shading pass reading another part of
        // the world's depth. `Drawn` is what publishes, and it is called by whoever recorded the draw.
        if (pendingSet.Add(key.Index)) {
            pending.Add(key.Index);
        }

        return true;
    }

    /// <inheritdoc />
    public void Evict(PageKey key, int slot) {
        if (key.Index < 0 || key.Index >= table.Length) {
            return;
        }

        table[key.Index] = VirtualShadowMap.PageAbsent;
        slots[key.Index] = VirtualShadowMap.PageAbsent;

        if (pendingSet.Remove(key.Index)) {
            pending.Remove(key.Index);
        }
    }
}
