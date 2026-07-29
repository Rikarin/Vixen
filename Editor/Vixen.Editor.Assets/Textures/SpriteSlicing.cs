// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Rendering.Sprites;

namespace Vixen.Editor.Assets.Textures;

/// <summary>Whether a texture produces sprites, and how many.</summary>
/// <remarks>
///     ⚠ <b>Three states rather than a <c>bool</c>, because "one sprite" and "many" differ in what
///     they produce and not only in how many.</b> A single sprite is the texture itself with a pivot
///     and a border; a sliced sheet is a sub-asset per cell, each of which something in a scene can
///     reference by name. The distinction is the one Unity draws and it is the one that decides
///     whether a re-slice can break an existing reference.
/// </remarks>
public enum SpriteMode {
    /// <summary>Not a sprite. The texture ships and nothing else is produced.</summary>
    None,

    /// <summary>One sprite covering the whole texture.</summary>
    Single,

    /// <summary>Many sprites, each a sub-asset.</summary>
    Multiple
}

/// <summary>One sprite cut out of a texture, as the sidecar records it.</summary>
/// <remarks>
///     <para>
///         <b>Primitives rather than the engine's own structs, and that is the sidecar talking.</b> A
///         <c>Rectangle</c> is four readonly fields with no setter and a <c>NineSlice</c> is a
///         positional record; the YAML binder builds a settings object by writing its members, so
///         either of them here would round-trip as a key the reader could not fill in. Written out
///         flat, a sprite in a <c>.meta</c> is eleven scalars a human can read and edit, which is
///         what a sidecar is for. <see cref="Region" />, <see cref="Pivot" /> and
///         <see cref="Border" /> put the engine's shapes back on top, and are computed rather than
///         stored so nothing has two answers.
///     </para>
///     <para>
///         ⚠ <b>Texels of the <i>source</i> image, not of the shipped texture.</b> A texture over
///         <c>MaxSize</c> ships halved, and the rects are not rescaled to match — they do not need to
///         be. A UV is a region divided by the texture's size and both halve together, so the sprite
///         samples the same picture either way, and its world size stays what the artist drew at the
///         resolution they drew it. Rescaling would round every rect to a texel grid it was not
///         authored on, once per halving.
///     </para>
/// </remarks>
[DataContract("SpriteRect")]
public sealed record SpriteRect {
    /// <summary>What the sprite is called. This is what a reference to it resolves by.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Its left edge, in texels from the left.</summary>
    public int X { get; init; }

    /// <summary>Its top edge, in texels from the top.</summary>
    public int Y { get; init; }

    /// <summary>How wide it is, in texels.</summary>
    public int Width { get; init; }

    /// <summary>How tall it is, in texels.</summary>
    public int Height { get; init; }

    /// <summary>Where its origin sits, as a fraction from the bottom-left.</summary>
    public float PivotX { get; init; } = 0.5f;

    /// <summary>The same, vertically.</summary>
    public float PivotY { get; init; } = 0.5f;

    /// <summary>How far the nine-slice's left border reaches in, in texels.</summary>
    public int BorderLeft { get; init; }

    /// <summary>The same, from the top.</summary>
    public int BorderTop { get; init; }

    /// <summary>The same, from the right.</summary>
    public int BorderRight { get; init; }

    /// <summary>The same, from the bottom.</summary>
    public int BorderBottom { get; init; }

    /// <summary>Where it is, as the shape the rest of the engine uses.</summary>
    public Rectangle Region => new(X, Y, Width, Height);

    /// <summary>Its pivot, as the shape the rest of the engine uses.</summary>
    public Vector2 Pivot => new(PivotX, PivotY);

    /// <summary>Its border, as the shape the rest of the engine uses.</summary>
    public NineSlice Border => new(BorderLeft, BorderTop, BorderRight, BorderBottom);

    /// <summary>Whether it encloses anything.</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>Builds a rect from the shapes the rest of the engine uses.</summary>
    /// <param name="name">What it is called.</param>
    /// <param name="region">Where it is, in texels. Rounded outwards to whole texels.</param>
    /// <param name="pivot">Where its origin sits, or null for the centre.</param>
    /// <param name="border">Its nine-slice border, in texels.</param>
    /// <returns>The rect.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Outwards, never to nearest.</b> A rect a person dragged is a float and a texel is
    ///         not; rounding the edges inwards on either side takes a column of the artwork off, and a
    ///         sprite one texel short shows a seam against the cell beside it rather than looking
    ///         misplaced.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The pivot is nullable rather than defaulted to <c>default</c>.</b> Zero is the
    ///         bottom-left corner and the most-asked-for pivot after the centre — a character stands
    ///         on it — so a sentinel that read it as "unspecified" would silently centre exactly the
    ///         sprites somebody had bothered to say something about.
    ///     </para>
    /// </remarks>
    public static SpriteRect From(string name, Rectangle region, Vector2? pivot = null, NineSlice border = default) {
        ArgumentNullException.ThrowIfNull(name);

        var left = (int)MathF.Floor(region.Left);
        var top = (int)MathF.Floor(region.Top);
        var origin = pivot ?? new Vector2(0.5f, 0.5f);

        return new() {
            Name = name,
            X = left,
            Y = top,
            Width = (int)MathF.Ceiling(region.Right) - left,
            Height = (int)MathF.Ceiling(region.Bottom) - top,
            PivotX = origin.X,
            PivotY = origin.Y,
            BorderLeft = (int)MathF.Round(border.Left),
            BorderTop = (int)MathF.Round(border.Top),
            BorderRight = (int)MathF.Round(border.Right),
            BorderBottom = (int)MathF.Round(border.Bottom)
        };
    }

    /// <summary>Turns this into the sprite the runtime draws.</summary>
    /// <param name="textureSize">How big the source texture is, in texels.</param>
    /// <param name="pixelsPerUnit">How many texels make one world unit.</param>
    /// <returns>The sprite.</returns>
    public Sprite ToSprite(Int2 textureSize, float pixelsPerUnit) =>
        new() {
            Name = Name,
            Region = Region,
            TextureSize = textureSize,
            Pivot = Pivot,
            Border = Border,
            PixelsPerUnit = pixelsPerUnit
        };
}

/// <summary>How a texture is cut into sprites.</summary>
public enum SliceMethod {
    /// <summary>A grid of cells of a given size.</summary>
    GridBySize,

    /// <summary>A grid of a given number of columns and rows, sized to fit.</summary>
    GridByCount,

    /// <summary>One sprite per island of opaque texels, found by looking at the alpha.</summary>
    Automatic
}

/// <summary>What a slice is asked for.</summary>
/// <param name="Method">How to cut.</param>
public sealed record SpriteSliceOptions(SliceMethod Method = SliceMethod.GridBySize) {
    /// <summary>How big one cell is, for <see cref="SliceMethod.GridBySize" />.</summary>
    public Int2 CellSize { get; init; } = new(32, 32);

    /// <summary>How many columns and rows, for <see cref="SliceMethod.GridByCount" />.</summary>
    public Int2 CellCount { get; init; } = new(4, 4);

    /// <summary>Where the grid starts, in texels from the top-left.</summary>
    public Int2 Offset { get; init; }

    /// <summary>The gap between cells, in texels.</summary>
    public Int2 Padding { get; init; }

    /// <summary>Where each sprite's origin sits, as a fraction from the bottom-left.</summary>
    public Vector2 Pivot { get; init; } = new(0.5f, 0.5f);

    /// <summary>Each sprite's nine-slice border, in texels.</summary>
    public NineSlice Border { get; init; }

    /// <summary>The alpha at or below which a texel counts as empty.</summary>
    /// <remarks>
    ///     Zero is the right default and not a disabled one: a texel with alpha 1 is very nearly
    ///     invisible and is still <i>drawn</i>, so a slicer that ignored it would cut a sprite
    ///     through artwork a compositing tool left behind. Raising it is how a sheet exported with a
    ///     soft halo gets tight rects.
    /// </remarks>
    public byte AlphaThreshold { get; init; }

    /// <summary>The smallest island <see cref="SliceMethod.Automatic" /> will call a sprite.</summary>
    /// <remarks>
    ///     Four by four, because a stray pixel from a brush or a JPEG artefact is a sprite under any
    ///     rule that has no floor, and a sheet that slices into six hundred one-texel rects is worse
    ///     than one that slices into none.
    /// </remarks>
    public int MinimumSize { get; init; } = 4;

    /// <summary>Whether a grid keeps cells with nothing in them.</summary>
    /// <remarks>
    ///     ⚠ Off by default, which is what makes grid slicing usable on a sheet that is not full: a
    ///     character with eleven frames on a four-by-three grid should produce eleven sprites, not
    ///     eleven and a blank. On is for a tile set, where the empty tile is a tile.
    /// </remarks>
    public bool KeepEmpty { get; init; }

    /// <summary>Whether each rect is shrunk to what is actually drawn inside it.</summary>
    public bool Trim { get; init; }

    /// <summary>What the sprites are called, before the number.</summary>
    public string NamePrefix { get; init; } = "sprite";
}

/// <summary>Cuts a texture into sprite rects.</summary>
/// <remarks>
///     <para>
///         <b>The part of a sprite editor that is not a user interface.</b> Everything here is a pure
///         function of the pixels and the options, which is what lets the three slicing modes be
///         checked against images built in a test rather than against a screenshot of a panel — the
///         same split <c>TextureLadder</c> makes for the mip inspector.
///     </para>
///     <para>
///         ⚠ <b>Slicing is a suggestion, not a result.</b> What it returns is what the panel puts in
///         front of an author to adjust, and the sidecar records the adjusted rects rather than the
///         options that produced them. That is deliberate: an automatic slice depends on the pixels,
///         so a sheet re-exported with one frame moved would silently renumber every sprite after it
///         and break every reference — where a recorded rect stays where it was until somebody moves
///         it.
///     </para>
/// </remarks>
public static class SpriteSlicer {
    /// <summary>Whether the alpha of a decoded texture can be read at all.</summary>
    /// <param name="source">The pixels.</param>
    /// <returns>Whether they are in a format this can look at.</returns>
    /// <remarks>
    ///     Eight-bit RGBA, which is what every decoder in the build produces. A compressed source —
    ///     a <c>.ktx2</c> an artist encoded themselves — arrives in blocks, and decoding those to
    ///     look at the alpha is a job for the block decompressor rather than for a slicer.
    /// </remarks>
    public static bool CanReadAlpha(TextureData? source) =>
        source is { Depth: 1 } data
        && (data.Format == PixelFormat.Rgba8UNorm || data.Format == PixelFormat.Rgba8UNormSrgb)
        && data.Level(0).Length >= data.Width * data.Height * 4;

    /// <summary>Cuts a texture into sprites.</summary>
    /// <param name="source">The decoded source pixels.</param>
    /// <param name="options">How to cut.</param>
    /// <returns>The rects, in reading order.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public static IReadOnlyList<SpriteRect> Slice(TextureData source, SpriteSliceOptions options) {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);

        var regions = options.Method switch {
            SliceMethod.Automatic => Islands(source, options),
            SliceMethod.GridByCount => Grid(source, CellFor(source, options), options),
            _ => Grid(source, options.CellSize, options)
        };

        var rects = new List<SpriteRect>(regions.Count);

        for (var index = 0; index < regions.Count; index++) {
            rects.Add(
                SpriteRect.From(
                    $"{options.NamePrefix}_{index}",
                    regions[index],
                    options.Pivot,
                    options.Border
                )
            );
        }

        return rects;
    }

    /// <summary>The whole texture as one sprite.</summary>
    /// <param name="width">Its width in texels.</param>
    /// <param name="height">Its height.</param>
    /// <param name="name">What to call it.</param>
    /// <param name="pivot">Where its origin sits.</param>
    /// <param name="border">Its nine-slice border, in texels.</param>
    /// <returns>The rect.</returns>
    public static SpriteRect Whole(int width, int height, string name, Vector2? pivot = null, NineSlice border = default) =>
        SpriteRect.From(name, new(0f, 0f, Math.Max(width, 0), Math.Max(height, 0)), pivot, border);

    /// <summary>Shrinks a rect to what is actually drawn inside it.</summary>
    /// <param name="source">The pixels.</param>
    /// <param name="region">The rect, in texels.</param>
    /// <param name="threshold">The alpha at or below which a texel counts as empty.</param>
    /// <returns>The tight rect, or an empty one when nothing inside it is drawn.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>An empty result is an empty rect, not the rect unchanged.</b> Trimming a blank cell
    ///     has one honest answer and it is "there is nothing here" — returning the cell would make
    ///     trim look like it had worked and leave a sprite of nothing in the sheet.
    /// </remarks>
    public static Rectangle Trim(TextureData source, Rectangle region, byte threshold = 0) {
        ArgumentNullException.ThrowIfNull(source);

        if (!CanReadAlpha(source)) {
            return region;
        }

        var (left, top, right, bottom) = Clamp(source, region);

        var minX = right;
        var minY = bottom;
        var maxX = left - 1;
        var maxY = top - 1;

        var pixels = source.Level(0);

        for (var y = top; y < bottom; y++) {
            for (var x = left; x < right; x++) {
                if (pixels[(((y * source.Width) + x) * 4) + 3] <= threshold) {
                    continue;
                }

                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        return maxX < minX || maxY < minY ? default : new(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>The cell size a column and row count implies.</summary>
    /// <remarks>
    ///     The padding between cells and the offset before the first come out of the available
    ///     extent first, which is what makes "four by four" mean four whole cells rather than four
    ///     cells and three gaps' worth of overflow.
    /// </remarks>
    static Int2 CellFor(TextureData source, SpriteSliceOptions options) {
        var columns = Math.Max(options.CellCount.X, 1);
        var rows = Math.Max(options.CellCount.Y, 1);

        var width = source.Width - options.Offset.X - (options.Padding.X * (columns - 1));
        var height = source.Height - options.Offset.Y - (options.Padding.Y * (rows - 1));

        return new(Math.Max(width / columns, 0), Math.Max(height / rows, 0));
    }

    /// <summary>Every whole cell of a grid, less the ones with nothing in them.</summary>
    static List<Rectangle> Grid(TextureData source, Int2 cell, SpriteSliceOptions options) {
        List<Rectangle> regions = [];

        if (cell.X <= 0 || cell.Y <= 0) {
            return regions;
        }

        var readable = CanReadAlpha(source);

        for (var y = options.Offset.Y; y + cell.Y <= source.Height; y += cell.Y + options.Padding.Y) {
            for (var x = options.Offset.X; x + cell.X <= source.Width; x += cell.X + options.Padding.X) {
                var region = new Rectangle(x, y, cell.X, cell.Y);
                var tight = readable ? Trim(source, region, options.AlphaThreshold) : region;

                if (tight.IsEmpty && !options.KeepEmpty) {
                    continue;
                }

                // ⚠ A kept blank keeps its whole cell. Trimming answers "there is nothing here" with
                // an empty rect, so trimming one anyway would turn every empty a caller asked to keep
                // into a zero-size sprite nobody can select — which is worse than either behaviour on
                // its own and is only reachable with both options on.
                regions.Add(options.Trim && !tight.IsEmpty ? tight : region);
            }
        }

        return regions;
    }

    /// <summary>One rect per island of opaque texels.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Connected components over the alpha, eight-connected, then a merge of whatever
    ///         overlaps.</b> Eight rather than four because a diagonal line of pixels is one stroke to
    ///         everyone who has ever drawn one, and four-connectivity cuts it into a sprite per
    ///         pixel. The merge is what puts a character's detached eye back inside its head: two
    ///         islands whose bounding boxes overlap are one drawing, and slicing between them cuts a
    ///         frame in half.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An explicit stack rather than recursion.</b> A flood fill over a 4096-square
    ///         background is a call depth in the millions, which is a stack overflow rather than a
    ///         slow slice — and it is the one input where the failure is a crash rather than a bad
    ///         result.
    ///     </para>
    /// </remarks>
    static List<Rectangle> Islands(TextureData source, SpriteSliceOptions options) {
        List<Rectangle> regions = [];

        if (!CanReadAlpha(source)) {
            return regions;
        }

        var width = source.Width;
        var height = source.Height;
        var pixels = source.Level(0);
        var seen = new bool[width * height];
        var stack = new Stack<int>();

        for (var start = 0; start < seen.Length; start++) {
            if (seen[start] || pixels[(start * 4) + 3] <= options.AlphaThreshold) {
                continue;
            }

            var minX = start % width;
            var maxX = minX;
            var minY = start / width;
            var maxY = minY;

            seen[start] = true;
            stack.Push(start);

            while (stack.Count > 0) {
                var index = stack.Pop();
                var x = index % width;
                var y = index / width;

                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);

                for (var dy = -1; dy <= 1; dy++) {
                    for (var dx = -1; dx <= 1; dx++) {
                        var nx = x + dx;
                        var ny = y + dy;

                        if (nx < 0 || ny < 0 || nx >= width || ny >= height) {
                            continue;
                        }

                        var neighbour = (ny * width) + nx;

                        if (seen[neighbour] || pixels[(neighbour * 4) + 3] <= options.AlphaThreshold) {
                            continue;
                        }

                        seen[neighbour] = true;
                        stack.Push(neighbour);
                    }
                }
            }

            regions.Add(new(minX, minY, maxX - minX + 1, maxY - minY + 1));
        }

        Merge(regions);

        regions.RemoveAll(region => region.Width < options.MinimumSize || region.Height < options.MinimumSize);

        return Order(regions);
    }

    /// <summary>Merges every pair of overlapping rects, until none overlap.</summary>
    /// <remarks>
    ///     Repeated to a fixed point rather than done in one pass: merging two rects makes a larger
    ///     one that may now overlap a third, and a single pass would leave a sprite cut in three
    ///     joined in two.
    /// </remarks>
    static void Merge(List<Rectangle> regions) {
        var merged = true;

        while (merged) {
            merged = false;

            for (var i = 0; i < regions.Count && !merged; i++) {
                for (var j = i + 1; j < regions.Count; j++) {
                    if (!Overlaps(regions[i], regions[j])) {
                        continue;
                    }

                    regions[i] = Union(regions[i], regions[j]);
                    regions.RemoveAt(j);
                    merged = true;

                    break;
                }
            }
        }
    }

    static bool Overlaps(Rectangle a, Rectangle b) =>
        a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

    static Rectangle Union(Rectangle a, Rectangle b) {
        var left = MathF.Min(a.Left, b.Left);
        var top = MathF.Min(a.Top, b.Top);

        return new(left, top, MathF.Max(a.Right, b.Right) - left, MathF.Max(a.Bottom, b.Bottom) - top);
    }

    /// <summary>Puts the rects in the order somebody reads them: rows down the sheet, left to right.</summary>
    /// <remarks>
    ///     ⚠ <b>Banded rather than sorted by top edge.</b> Frames on one row of a hand-drawn sheet
    ///     are rarely aligned to the texel, so ordering by the top edge alone interleaves two rows
    ///     wherever one frame reaches a pixel higher than its neighbour — and the numbering an
    ///     animation depends on comes out shuffled. A band ends where the next rect starts below
    ///     everything in it, which is the rule a person uses looking at the sheet.
    /// </remarks>
    static List<Rectangle> Order(List<Rectangle> regions) {
        regions.Sort((a, b) => a.Top != b.Top ? a.Top.CompareTo(b.Top) : a.Left.CompareTo(b.Left));

        List<Rectangle> ordered = [];
        var band = new List<Rectangle>();
        var bottom = float.MinValue;

        foreach (var region in regions) {
            if (band.Count > 0 && region.Top >= bottom) {
                Flush(band, ordered);
                bottom = float.MinValue;
            }

            band.Add(region);
            bottom = MathF.Max(bottom, region.Bottom);
        }

        Flush(band, ordered);

        return ordered;

        static void Flush(List<Rectangle> band, List<Rectangle> ordered) {
            band.Sort((a, b) => a.Left.CompareTo(b.Left));
            ordered.AddRange(band);
            band.Clear();
        }
    }

    static (int Left, int Top, int Right, int Bottom) Clamp(TextureData source, Rectangle region) => (
        Math.Clamp((int)MathF.Floor(region.Left), 0, source.Width),
        Math.Clamp((int)MathF.Floor(region.Top), 0, source.Height),
        Math.Clamp((int)MathF.Ceiling(region.Right), 0, source.Width),
        Math.Clamp((int)MathF.Ceiling(region.Bottom), 0, source.Height)
    );
}
