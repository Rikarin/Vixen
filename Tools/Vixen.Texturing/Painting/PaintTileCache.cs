// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.TextureGraph;

namespace Vixen.Texturing.Painting;

/// <summary>Which tiles of the painted images a device holds, and what it takes to bring them up to date.</summary>
/// <remarks>
///     <para>
///         <b>A plan reads a paint layer as one image, so the device has to hold one.</b> What this
///         saves is the upload: each image remembers the revision the device last saw, so a stroke
///         costs the tiles it touched, and an undo costs the tiles it brought back. The rest of the
///         picture never crosses to the device twice.
///     </para>
///     <para>
///         ⚠ <b>An image the layer has no canvas for yet is watched too.</b> A channel appears on its
///         first stroke, and a plan is compiled before that, so the source is asked each time rather
///         than held.
///     </para>
///     <para>
///         ⚠ <b>Not budgeted.</b> The textures are as large as the images they mirror, because the
///         kernels read them directly. A real cache would have to evict whole textures and fall back
///         to re-uploading, and no stack has needed that yet.
///     </para>
/// </remarks>
public sealed class PaintTileCache {
    readonly List<Watched> images = [];
    readonly List<(int TileX, int TileY)> moved = [];

    /// <summary>How many tiles have crossed to the device, over this cache's life.</summary>
    /// <remarks>What says a stroke costs its tiles, since the picture would be right either way.</remarks>
    public long TilesUploaded { get; private set; }

    /// <summary>Starts watching a plan's image.</summary>
    /// <param name="image">Its index in the plan, already uploaded.</param>
    /// <param name="source">Where its pixels are now, or nothing while it has none.</param>
    public void Watch(int image, Func<PaintImage?> source) {
        ArgumentNullException.ThrowIfNull(source);

        var current = source();

        images.Add(new(image, source) { Shown = current, Revision = current?.Revision ?? 0 });
    }

    /// <summary>Stops watching everything: the plan is gone.</summary>
    public void Clear() => images.Clear();

    /// <summary>Whether every image it watches is still the size the device holds it at.</summary>
    /// <param name="uploads">Where the images are.</param>
    /// <returns>False when one has another size, which only uploading the plan's images again can follow.</returns>
    /// <remarks>
    ///     ⚠ <b>A canvas made larger is a new image of another size</b> (Patina #106, #241): a set whose size
    ///     changed enlarges a canvas painted before it on its next stroke. Its tiles do not fit the texture the
    ///     smaller one was uploaded to, so <see cref="Sync" /> would refuse them.
    /// </remarks>
    public bool Fits(TextureUploads uploads) {
        ArgumentNullException.ThrowIfNull(uploads);

        foreach (var watched in images) {
            if (watched.Source() is { } image
                && uploads.Externals.TryGetValue(watched.Image, out var held)
                && (held.Size.X != image.Width || held.Size.Y != image.Height)) {
                return false;
            }
        }

        return true;
    }

    /// <summary>Brings the device up to date.</summary>
    /// <param name="uploads">Where the images are.</param>
    /// <returns>Each image that moved, and the rectangles of it that did.</returns>
    public Dictionary<int, ImmutableArray<TextureRect>> Sync(TextureUploads uploads) {
        ArgumentNullException.ThrowIfNull(uploads);

        Dictionary<int, ImmutableArray<TextureRect>> changed = [];
        List<TextureImageChange> changes = [];

        foreach (var watched in images) {
            var image = watched.Source();

            if (image is null || (ReferenceEquals(image, watched.Shown) && image.Revision == watched.Revision)) {
                continue;
            }

            // ⚠ From zero for an image the device has not seen: a fresh one has written nothing, so
            // its untouched tiles are what the device was given. A *different* object, as after a
            // file changed behind the store, says nothing about any tile.
            if (watched.Shown is { } shown && !ReferenceEquals(shown, image)) {
                watched.Revision = -1;
            }

            image.TilesChangedSince(watched.Revision, moved);

            List<TextureTileWrite> writes = new(moved.Count);
            List<TextureRect> rects = new(moved.Count);

            foreach (var (tileX, tileY) in moved) {
                var tile = image.TileRect(tileX, tileY);
                var rect = new TextureRect(tile.X, tile.Y, tile.Width, tile.Height);

                rects.Add(rect);
                writes.Add(new(rect, image.TileTexelsOrBackground(tileX, tileY)));
            }

            TilesUploaded += writes.Count;
            watched.Shown = image;
            watched.Revision = image.Revision;
            changes.Add(new(watched.Image, writes, new(image.Digest)));

            if (rects.Count > 0) {
                changed[watched.Image] = Merged(rects);
            }
        }

        if (changes.Count > 0) {
            uploads.Update(changes);
        }

        return changed;
    }

    /// <summary>Tiles joined along rows and then down columns, since each rectangle is a dispatch per op.</summary>
    /// <param name="tiles">The tiles, in rows from the top.</param>
    /// <returns>Fewer rectangles covering the same texels.</returns>
    internal static ImmutableArray<TextureRect> Merged(List<TextureRect> tiles) {
        List<TextureRect> rows = [];

        foreach (var tile in tiles) {
            if (rows.Count > 0 && rows[^1].Y == tile.Y && rows[^1].Height == tile.Height && rows[^1].EndX == tile.X) {
                rows[^1] = rows[^1] with { Width = rows[^1].Width + tile.Width };
            } else {
                rows.Add(tile);
            }
        }

        List<TextureRect> joined = [];

        foreach (var row in rows) {
            var above = joined.FindIndex(r => r.X == row.X && r.Width == row.Width && r.EndY == row.Y);

            if (above >= 0) {
                joined[above] = joined[above] with { Height = joined[above].Height + row.Height };
            } else {
                joined.Add(row);
            }
        }

        return [.. joined];
    }

    sealed class Watched(int image, Func<PaintImage?> source) {
        public int Image { get; } = image;

        public Func<PaintImage?> Source { get; } = source;

        public PaintImage? Shown { get; set; }

        public long Revision { get; set; }
    }
}
