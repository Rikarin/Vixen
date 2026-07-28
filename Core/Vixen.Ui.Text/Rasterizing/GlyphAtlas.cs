// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Text.Rasterizing;

/// <summary>Which glyph's field an atlas entry holds.</summary>
/// <param name="Font">The face, by whatever identity the caller gives its fonts.</param>
/// <param name="Glyph">The glyph id.</param>
/// <param name="Size">Which field resolution, for a caller that keeps more than one.</param>
/// <param name="Variation">Where along its axes a variable font was read, or null for a static face.</param>
/// <remarks>
///     ⚠ <b>No point size.</b> A distance field is read at any scale, which is the whole reason for
///     one — the same property that keeps the shaping cache size-independent. A key carrying the
///     size would miss on every frame of a growing label and fill the atlas with the same glyph.
/// </remarks>
/// <remarks>
///     ⚠ <b><c>Variation</c> is what stops one variable font's instances sharing a field.</b> The
///     same font and the same glyph at two axis positions are two different outlines, so a key
///     without it hands the second instance the first one's distance field — a bold that draws as a
///     regular, from a cache that reports a hit. <c>Size</c> is the resolution the field was
///     generated at rather than a point size; see <c>GlyphFieldCache</c>.
/// </remarks>
public readonly record struct GlyphKey(int Font, ushort Glyph, int Size, FontVariation? Variation = null);

/// <summary>Where a glyph's field sits in the atlas texture, in pixels.</summary>
/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">How wide.</param>
/// <param name="Height">How tall.</param>
public readonly record struct AtlasRegion(int X, int Y, int Width, int Height);

/// <summary>
///     A texture of glyph distance fields, packed as they are asked for and evicted least-recently
///     used when it fills.
/// </summary>
/// <remarks>
///     <para>
///         <b>Dynamic rather than built ahead.</b> CJK alone is tens of thousands of glyphs and a
///         font may carry more; an atlas holding every glyph a font can draw is a texture nobody has
///         the memory for, and one holding the glyphs an interface actually uses is a few hundred.
///     </para>
///     <para>
///         <b>Shelf packing.</b> Rows are opened as needed, each as tall as the first glyph that
///         starts it, and glyphs fill along the row. It wastes the difference between a row's height
///         and each glyph's — real, and bounded by how much glyph heights vary within one field
///         resolution, which is not much. A skyline packer would waste less and would have to move
///         entries to stay that way, and moving one entry invalidates a texture coordinate somebody
///         is holding.
///     </para>
///     <para>
///         ⚠ <b>An evicted slot is reused only by a glyph that fits it.</b> Eviction leaves a hole
///         of one exact size, and a hole is worth having only if the next glyph is no larger — so
///         the freed slots are kept per shelf and matched by width. What that cannot do is answer a
///         glyph wider than every hole while the atlas is nominally full, which is what
///         <see cref="Compact" /> is for: it rebuilds the packing from the live entries and
///         <b>changes every region</b>, so <see cref="Version" /> moves and a caller holding
///         coordinates has to ask again.
///     </para>
/// </remarks>
public sealed class GlyphAtlas {
    readonly Dictionary<GlyphKey, LinkedListNode<Entry>> entries = [];
    readonly LinkedList<Entry> order = new();
    readonly List<Shelf> shelves = [];
    readonly int padding;

    /// <summary>Creates an atlas.</summary>
    /// <param name="width">The texture's width in pixels.</param>
    /// <param name="height">Its height.</param>
    /// <param name="padding">
    ///     How many pixels to leave between entries, so a filtered read of one glyph's edge cannot
    ///     pick up its neighbour.
    /// </param>
    public GlyphAtlas(int width = 1024, int height = 1024, int padding = 1) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegative(padding);

        Width = width;
        Height = height;
        this.padding = padding;
        Pixels = new float[width * height * 3];
    }

    /// <summary>The texture's width.</summary>
    public int Width { get; }

    /// <summary>The texture's height.</summary>
    public int Height { get; }

    /// <summary>The three channels per pixel, row-major. What a renderer uploads.</summary>
    public float[] Pixels { get; }

    /// <summary>How many glyphs are held.</summary>
    public int Count => entries.Count;

    /// <summary>How many lookups were answered from the atlas.</summary>
    public long Hits { get; private set; }

    /// <summary>How many lookups had nothing to answer with.</summary>
    public long Misses { get; private set; }

    /// <summary>How many entries have been evicted to make room.</summary>
    public long Evictions { get; private set; }

    /// <summary>Moves whenever every region changes, which only <see cref="Compact" /> does.</summary>
    /// <remarks>
    ///     A caller caching texture coordinates compares this and re-reads when it differs — the same
    ///     arrangement the draw list uses for its own content version.
    /// </remarks>
    public int Version { get; private set; }

    /// <summary>Moves whenever the pixels change: a glyph added, or the whole thing repacked.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Not <see cref="Version" />, and confusing the two is a texture that is never
    ///         re-uploaded.</b> A version moves when the <i>layout</i> changes, so a caller holding
    ///         texture coordinates knows to ask again; adding a glyph does not move it, because every
    ///         region it was holding is still where it was. But the pixels did change — an atlas whose
    ///         reader uploads on the version alone uploads on the first frame and never again, and
    ///         every glyph the interface meets after that samples whatever was in the texture at that
    ///         coordinate before: nothing where the label should be, or some earlier glyph's field.
    ///     </para>
    ///     <para>
    ///         This is the counter an uploader watches. It is a number rather than
    ///         <see cref="Dirty" /> for the reason a version is: a flag belongs to whoever clears it,
    ///         and two windows over one font cache is the ordinary case — a reader that remembers the
    ///         number it last saw cannot have it cleared out from under it by the other one.
    ///     </para>
    /// </remarks>
    public int Revision { get; private set; }

    /// <summary>Whether the texture's contents have changed since the flag was last cleared.</summary>
    public bool Dirty { get; private set; }

    /// <summary>Clears <see cref="Dirty" />, after a renderer has uploaded the texture.</summary>
    public void Uploaded() => Dirty = false;

    /// <summary>Finds a glyph's field, marking it as the most recently used.</summary>
    /// <param name="key">Which glyph.</param>
    /// <param name="region">Where it is.</param>
    /// <returns>Whether the atlas held it.</returns>
    public bool TryGet(GlyphKey key, out AtlasRegion region) {
        if (entries.TryGetValue(key, out var node)) {
            Hits++;
            order.Remove(node);
            order.AddFirst(node);
            region = node.Value.Region;
            return true;
        }

        Misses++;
        region = default;
        return false;
    }

    /// <summary>Puts a glyph's field into the atlas, making room if it has to.</summary>
    /// <param name="key">Which glyph.</param>
    /// <param name="field">Its field.</param>
    /// <param name="region">Where it landed.</param>
    /// <returns>Whether it fitted at all.</returns>
    /// <remarks>
    ///     False only for a field larger than the texture, which no amount of evicting can help.
    /// </remarks>
    public bool Add(GlyphKey key, DistanceFieldBitmap field, out AtlasRegion region) {
        if (entries.TryGetValue(key, out var existing)) {
            region = existing.Value.Region;
            return true;
        }

        if (field.Width > Width || field.Height > Height) {
            region = default;
            return false;
        }

        // ⚠ <b>Evict first, compact only when the space is there and the shape is wrong.</b>
        // Compacting first would be tidier and would bump the version on every addition to a full
        // atlas, which throws away every texture coordinate anyone is holding — for a steady-state
        // interface that is every frame. So entries go one at a time until either one fits or
        // enough area has been freed that fragmentation must be the reason it does not.
        var needed = (field.Width + padding) * (field.Height + padding);
        var freed = 0;
        var compacted = false;

        while (!TryPlace(field.Width, field.Height, out region)) {
            if (!compacted && freed >= needed) {
                Compact();
                compacted = true;
                continue;
            }

            if (!Evict(out var area)) {
                if (compacted) {
                    return false;
                }

                Compact();
                compacted = true;
                continue;
            }

            freed += area;
        }

        Write(region, field);

        var node = order.AddFirst(new Entry(key, region, field));
        entries[key] = node;

        Revision++;
        Dirty = true;
        return true;
    }

    /// <summary>Throws everything away.</summary>
    public void Clear() {
        entries.Clear();
        order.Clear();
        shelves.Clear();
        Array.Clear(Pixels);

        Version++;
        Revision++;
        Dirty = true;
    }

    /// <summary>Rebuilds the packing from the live entries, tightest first.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Every region changes</b>, so <see cref="Version" /> moves with it.
    ///     </para>
    ///     <para>
    ///         Entries are replaced warmest first, so that if some did not fit the ones dropped
    ///         would be the coldest — the same rule eviction follows. ⚠ <b>Whether any can fail to
    ///         fit is unproven, and the order is insurance rather than a covered claim.</b>
    ///         Compaction only ever runs on a set that already fitted, minus whatever eviction has
    ///         just taken out, so it is not obvious that repacking can lose one; shelf packing is
    ///         not monotone in the insertion order, which is why the guard is there at all, but
    ///         several attempts to build a set that repacks worse than it packed all fitted. A
    ///         sabotage reversing the order fails nothing.
    ///     </para>
    /// </remarks>
    public void Compact() {
        var live = new List<Entry>(order);

        shelves.Clear();
        entries.Clear();
        order.Clear();
        Array.Clear(Pixels);

        foreach (var entry in live) {
            if (!TryPlace(entry.Field.Width, entry.Field.Height, out var region)) {
                continue;
            }

            Write(region, entry.Field);
            entries[entry.Key] = order.AddLast(entry with { Region = region });
        }

        Version++;
        Revision++;
        Dirty = true;
    }

    // ================================================================== Packing

    bool TryPlace(int width, int height, out AtlasRegion region) {
        var padded = new { Width = width + padding, Height = height + padding };

        // A freed slot first, so an atlas that is full of the same-sized glyphs never grows a shelf.
        foreach (var shelf in shelves) {
            for (var i = 0; i < shelf.Free.Count; i++) {
                var hole = shelf.Free[i];
                if (hole.Width < width || hole.Height < height) {
                    continue;
                }

                shelf.Free.RemoveAt(i);
                region = new AtlasRegion(hole.X, hole.Y, width, height);
                return true;
            }
        }

        foreach (var shelf in shelves) {
            if (shelf.Height < padded.Height || Width - shelf.Cursor < padded.Width) {
                continue;
            }

            region = new AtlasRegion(shelf.Cursor, shelf.Top, width, height);
            shelf.Cursor += padded.Width;
            return true;
        }

        var top = shelves.Count == 0 ? 0 : shelves[^1].Top + shelves[^1].Height;
        if (top + padded.Height > Height) {
            region = default;
            return false;
        }

        var opened = new Shelf(top, padded.Height) { Cursor = padded.Width };
        shelves.Add(opened);
        region = new AtlasRegion(0, top, width, height);
        return true;
    }

    bool Evict(out int area) {
        area = 0;
        if (order.Last is not { } coldest) {
            return false;
        }

        order.RemoveLast();
        entries.Remove(coldest.Value.Key);
        Evictions++;

        var region = coldest.Value.Region;
        area = region.Width * region.Height;

        foreach (var shelf in shelves) {
            if (shelf.Top == region.Y) {
                shelf.Free.Add(region);
                break;
            }
        }

        return true;
    }

    void Write(AtlasRegion region, DistanceFieldBitmap field) {
        for (var y = 0; y < field.Height; y++) {
            var source = y * field.Width * 3;
            var destination = (((region.Y + y) * Width) + region.X) * 3;
            Array.Copy(field.Channels, source, Pixels, destination, field.Width * 3);
        }
    }

    sealed record Entry(GlyphKey Key, AtlasRegion Region, DistanceFieldBitmap Field);

    sealed class Shelf(int top, int height) {
        public int Top { get; } = top;

        public int Height { get; } = height;

        public int Cursor { get; set; }

        /// <summary>Slots freed by eviction, reusable by anything that fits.</summary>
        public List<AtlasRegion> Free { get; } = [];
    }
}
