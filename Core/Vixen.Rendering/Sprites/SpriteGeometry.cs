// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;

namespace Vixen.Rendering.Sprites;

/// <summary>One corner of a sprite quad, in the layout the sprite shaders read.</summary>
/// <param name="Position">Where it is, in the sprite's own space with the pivot at the origin.</param>
/// <param name="Texture">Which texel it samples, in UVs.</param>
/// <param name="Colour">The tint, multiplied into the sample.</param>
/// <remarks>
///     <para>
///         ⚠ <b>The same three attributes as <c>ParticleVertex</c>, and deliberately not the same
///         type.</b> They agree today because a textured quad has nothing else in it, and a shared
///         struct would make the next thing either of them needs — a second UV set for a sprite's
///         normal map, a per-particle random — a change to both. What they can share is the
///         <i>layout</i>: a project that wants one pipeline for both registers one entry in
///         <c>EffectPipelineDescriber.VertexLayouts</c> and points both features at it.
///     </para>
///     <para>
///         <b>Local space, not world.</b> A sprite is a flat quad in its own XY plane and where it
///         sits is <c>TransformRenderFeature</c>'s answer, pushed as a constant — so the geometry is
///         the same for every view that draws it and is built once a frame rather than once a view.
///         That is the difference between this and the particle expansion, which faces a camera and
///         therefore cannot be.
///     </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct SpriteVertex(Vector3 Position, Vector2 Texture, Vector4 Colour);

/// <summary>Which way round a sprite is drawn.</summary>
/// <remarks>
///     ⚠ <b>Mirrored geometry <i>and</i> mirrored UVs, which is why this is not just a UV swap.</b>
///     Swapping the texture coordinates alone mirrors each cell of a nine-slice in place — the left
///     border would show mirrored left-border art still on the left. Mirroring the cell layout as
///     well is what puts the right-hand corner on the left, and doing both keeps the triangle winding
///     the way it was so a back-face cull does not eat every flipped sprite.
/// </remarks>
[Flags]
public enum SpriteFlip : byte {
    /// <summary>As drawn.</summary>
    None = 0,

    /// <summary>Mirrored left to right about the pivot.</summary>
    Horizontal = 1,

    /// <summary>Mirrored top to bottom about the pivot.</summary>
    Vertical = 2
}

/// <summary>What a sprite's stretchable parts do when the sprite is drawn larger than it was drawn at.</summary>
public enum SpriteFill : byte {
    /// <summary>The edges and the middle are stretched to fit.</summary>
    Stretch,

    /// <summary>
    ///     The edges and the middle repeat at their own pixel size, with the last repeat clipped.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The mode <c>Vixen.Ui</c> cannot offer</b>, and the reason is worth knowing because it
    ///     is what decides where this code lives: how many times a strip repeats is
    ///     <c>destination ÷ natural size</c>, and the natural size is in texels. A draw list does not
    ///     know how big a texture is — see <c>DrawCommand.Source</c> — so it cannot count the repeats,
    ///     and a nine-slice there is stretched only. Here the sprite carries its own pixel density,
    ///     so it can.
    /// </remarks>
    Tile
}

/// <summary>How one sprite is drawn: the parts that are not the sprite itself.</summary>
/// <remarks>
///     Unmanaged on purpose, so that it lives in the renderer's per-object arrays beside every other
///     feature's data rather than in a dictionary of its own — see <c>RenderDataHolder</c>.
/// </remarks>
public readonly record struct SpriteAppearance {
    /// <summary>What to multiply the sample by.</summary>
    /// <remarks>
    ///     ⚠ <b>Transparent black means white</b>, the same sentinel <c>DrawContext.DrawImage</c>
    ///     uses. A struct's default is all zeroes, and the two readings of it are "draw nothing" and
    ///     "leave the colours alone" — the first is what an appearance nobody filled in would do,
    ///     which reads as a missing sprite rather than as an unset field, and a tint that is both
    ///     black and fully transparent draws nothing either way.
    /// </remarks>
    public Color4 Colour { get; init; }

    /// <summary>How big to draw it in world units, or zero for the size it was authored at.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>An override of the size, never of the border.</b> A panel stretched to twice its
    ///         width keeps its corners at the size the artist drew them — that is the entire promise
    ///         of a nine-slice, and scaling the border with the box would quietly turn it into a
    ///         plain quad with extra vertices.
    ///     </para>
    ///     <para>
    ///         Both axes or neither: a zero in either one is read as unspecified, because half a
    ///         size is not a size and a sprite drawn nought units wide is not something anybody asks
    ///         for.
    ///     </para>
    /// </remarks>
    public Vector2 Size { get; init; }

    /// <summary>What the stretchable parts do.</summary>
    public SpriteFill Fill { get; init; }

    /// <summary>Which way round it is drawn.</summary>
    public SpriteFlip Flip { get; init; }

    /// <summary>Whether a nine-slice leaves its middle undrawn.</summary>
    /// <remarks>A frame rather than a panel: a selection outline, a window over a viewport.</remarks>
    public bool HollowCentre { get; init; }

    /// <summary>
    ///     Where this sprite sits in painting order, for a stage that sorts <c>ByGroup</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>2D has no depth to sort by, and this is what replaces it.</b> Sprites are drawn
    ///     blended and overlapping, so what is in front is what was drawn last — which is a decision
    ///     an artist makes ("the character is in front of the grass") and not one a depth buffer can
    ///     make for them. The number is a sorting layer and an order within it, packed however the
    ///     game likes; the renderer only sorts by it.
    /// </remarks>
    public uint SortGroup { get; init; }
}

/// <summary>
///     Turns a sprite into the quads that draw it: one, or the nine a border cuts it into, or the
///     many a tiled fill repeats.
/// </summary>
/// <remarks>
///     <para>
///         A pure function of a sprite and an appearance, which is what lets every claim below be
///         checked without a device — the same bargain <c>Vixen.Vfx</c>'s expansion makes and the same
///         one <c>UiGeometryBuilder</c> makes on the other side of the engine.
///     </para>
///     <para>
///         ⚠ <b>The nine-slice arithmetic itself is <c>NineSlice</c>'s, in
///         <c>Vixen.Core.Mathematics</c>, and it is shared with the interface.</b> A panel stretched
///         by a stylesheet and a sprite stretched by a scene are the same nine pairs of rectangles,
///         and the two assemblies cannot see each other. What this adds on top is everything that
///         needs to know a texture's pixel size: the conversion from texels to world units, and
///         tiling.
///     </para>
/// </remarks>
public static class SpriteGeometry {
    /// <summary>How many vertices one quad needs.</summary>
    public const int VerticesPerQuad = 4;

    /// <summary>How many indices one quad needs.</summary>
    public const int IndicesPerQuad = 6;

    /// <summary>How many times one strip may repeat before it is stretched instead.</summary>
    /// <remarks>
    ///     ⚠ <b>A real ceiling, and a cell that hits it is stretched rather than clipped.</b> The
    ///     repeat count is destination over natural size, so a sixteen-texel tile across a hundred
    ///     world units at a hundred pixels per unit is six hundred and twenty-five repeats in one
    ///     direction and nearly four hundred thousand quads in two — a vertex buffer sized by how
    ///     small somebody drew their artwork. Both <see cref="Build" /> and <see cref="QuadsFor" />
    ///     apply it, so the count a caller allocates for is always the count that is written.
    /// </remarks>
    public const int TileLimit = 64;

    /// <summary>How many quads drawing this sprite will take.</summary>
    /// <param name="sprite">The sprite.</param>
    /// <param name="appearance">How it is drawn.</param>
    /// <returns>The number of quads, which is what <see cref="Build" /> will write.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sprite" /> is null.</exception>
    /// <remarks>
    ///     Exact rather than an upper bound, and it has to be: a caller sizes a buffer with this and
    ///     a bound that was merely safe would leave gaps of uninitialised vertices in the middle of
    ///     the frame's geometry.
    /// </remarks>
    public static int QuadsFor(Sprite sprite, in SpriteAppearance appearance) {
        ArgumentNullException.ThrowIfNull(sprite);

        Span<Cell> cells = stackalloc Cell[NineSlice.CellCount];

        return Plan(sprite, appearance, cells);
    }

    /// <summary>Expands a sprite into quads.</summary>
    /// <param name="sprite">The sprite.</param>
    /// <param name="appearance">How it is drawn.</param>
    /// <param name="into">
    ///     Where to write. Needs <see cref="VerticesPerQuad" /> per quad — see
    ///     <see cref="QuadsFor" />. A shorter span writes as many whole quads as fit.
    /// </param>
    /// <returns>How many quads were written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sprite" /> is null.</exception>
    public static int Build(Sprite sprite, in SpriteAppearance appearance, Span<SpriteVertex> into) {
        ArgumentNullException.ThrowIfNull(sprite);

        Span<Cell> cells = stackalloc Cell[NineSlice.CellCount];

        if (Plan(sprite, appearance, cells) == 0) {
            return 0;
        }

        var size = SizeOf(sprite, appearance);
        var colour = appearance.Colour == default ? Color4.White : appearance.Colour;

        // Where the pivot is, as the offsets that move the sprite's top-left corner to it. The
        // layout below is Y-down from that corner, because that is the space a texture region and a
        // nine-slice are both written in; the world is Y-up, so the vertical one is a subtraction.
        var originX = sprite.Pivot.X * size.X;
        var originY = (1f - sprite.Pivot.Y) * size.Y;

        var written = 0;

        for (var index = 0; index < cells.Length; index++) {
            ref readonly var cell = ref cells[index];

            for (var y = 0; y < cell.Tiles.Y; y++) {
                for (var x = 0; x < cell.Tiles.X; x++) {
                    if ((written + 1) * VerticesPerQuad > into.Length) {
                        return written;
                    }

                    var (left, right, uLeft, uRight) = Extent(
                        cell.Destination.Left,
                        cell.Destination.Right,
                        cell.Source.Left,
                        cell.Source.Right,
                        cell.Natural.X,
                        x
                    );

                    var (top, bottom, vTop, vBottom) = Extent(
                        cell.Destination.Top,
                        cell.Destination.Bottom,
                        cell.Source.Top,
                        cell.Source.Bottom,
                        cell.Natural.Y,
                        y
                    );

                    Quad(
                        into.Slice(written * VerticesPerQuad, VerticesPerQuad),
                        left - originX,
                        right - originX,
                        originY - top,
                        originY - bottom,
                        new(uLeft, vTop),
                        new(uRight, vBottom),
                        (Vector4)colour,
                        appearance.Flip
                    );

                    written++;
                }
            }
        }

        return written;
    }

    /// <summary>Writes the index pattern for a number of quads.</summary>
    /// <param name="indices">Where to write. Needs <see cref="IndicesPerQuad" /> per quad.</param>
    /// <param name="quads">How many quads to cover.</param>
    /// <returns>How many indices were written.</returns>
    /// <remarks>
    ///     Two triangles over four corners, wound counter-clockwise from the bottom left: 0-1-2 and
    ///     0-2-3. The same pattern the particle expansion uses, because it depends on nothing but the
    ///     count and one buffer of it serves every quad in the frame.
    /// </remarks>
    public static int WriteQuadIndices(Span<uint> indices, int quads) {
        var written = 0;

        for (var quad = 0; quad < quads; quad++) {
            if (written + IndicesPerQuad > indices.Length) {
                break;
            }

            var corner = (uint)(quad * VerticesPerQuad);

            indices[written++] = corner;
            indices[written++] = corner + 1;
            indices[written++] = corner + 2;
            indices[written++] = corner;
            indices[written++] = corner + 2;
            indices[written++] = corner + 3;
        }

        return written;
    }

    /// <summary>How big the sprite is drawn, which is the appearance's answer or its own.</summary>
    static Vector2 SizeOf(Sprite sprite, in SpriteAppearance appearance) =>
        appearance.Size.X > 0f && appearance.Size.Y > 0f ? appearance.Size : sprite.Size;

    /// <summary>
    ///     Works out every cell's destination, source and repeat count, and returns the total quads.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>One plan shared by the count and the emission</b>, which is what makes
    ///         <see cref="QuadsFor" /> exact rather than merely safe. Two functions computing the same
    ///         number by different routes is how a buffer ends up one quad short in the one case
    ///         nobody tested.
    ///     </para>
    ///     <para>
    ///         A sprite with no border is still nine cells, eight of them empty — the whole picture
    ///         lands in the middle one, which is exactly where a tiled fill wants it. That is why a
    ///         floor tile repeated over a large quad needs no path of its own: it is a nine-slice
    ///         whose border happens to be nothing.
    ///     </para>
    /// </remarks>
    static int Plan(Sprite sprite, in SpriteAppearance appearance, Span<Cell> cells) {
        cells.Clear();

        var size = SizeOf(sprite, appearance);

        if (!sprite.IsDrawable || size.X <= 0f || size.Y <= 0f) {
            return 0;
        }

        Span<Rectangle> destination = stackalloc Rectangle[NineSlice.CellCount];
        Span<Rectangle> source = stackalloc Rectangle[NineSlice.CellCount];

        // ⚠ The destination border is fitted to the box and the source border is not: a panel drawn
        // narrower than its own two corners shows them compressed rather than reading different
        // texels. NineSlice.Fit says the rest.
        sprite.UnitBorder.Fit(size.X, size.Y).Split(new(0f, 0f, size.X, size.Y), destination);
        sprite.UvBorder.Split(sprite.Uv, source);

        var texels = new Vector2(Math.Max(sprite.TextureSize.X, 1), Math.Max(sprite.TextureSize.Y, 1));
        var density = sprite.PixelsPerUnit > 0f ? sprite.PixelsPerUnit : Sprite.DefaultPixelsPerUnit;

        var quads = 0;

        for (var index = 0; index < NineSlice.CellCount; index++) {
            if (destination[index].IsEmpty || source[index].IsEmpty) {
                continue;
            }

            if (appearance.HollowCentre && index == NineSlice.Centre) {
                continue;
            }

            // The middle column tiles horizontally and the middle row vertically; a corner is drawn
            // once whatever the fill says, because a corner is the one part of a nine-slice that is
            // never stretched and therefore has nothing to repeat.
            var natural = new Vector2(
                source[index].Width * texels.X / density,
                source[index].Height * texels.Y / density
            );

            var tiles = new Int2(
                Repeats(appearance.Fill == SpriteFill.Tile && index % 3 == 1, destination[index].Width, natural.X),
                Repeats(appearance.Fill == SpriteFill.Tile && index / 3 == 1, destination[index].Height, natural.Y)
            );

            // A cell that is not tiled has one repeat covering the whole of it, which is what makes
            // the emission loop the same code for both: the natural size *is* the destination size,
            // so the one repeat reads the whole source.
            cells[index] = new() {
                Destination = destination[index],
                Source = source[index],
                Natural = new(
                    tiles.X > 1 ? natural.X : destination[index].Width,
                    tiles.Y > 1 ? natural.Y : destination[index].Height
                ),
                Tiles = tiles
            };

            quads += tiles.X * tiles.Y;
        }

        return quads;
    }

    /// <summary>How many times a strip repeats across a length.</summary>
    /// <remarks>
    ///     Rounded up, so the last repeat is the partial one — and clamped, so a tile far smaller
    ///     than the box it fills gives up and stretches instead of asking for a vertex buffer nobody
    ///     has. See <see cref="TileLimit" />.
    /// </remarks>
    static int Repeats(bool tiled, float length, float natural) {
        if (!tiled || natural <= 0f || length <= 0f) {
            return 1;
        }

        var repeats = (int)MathF.Ceiling(length / natural);

        return repeats is > 1 and <= TileLimit ? repeats : 1;
    }

    /// <summary>One repeat's extent and the part of the source it reads.</summary>
    /// <remarks>
    ///     The last repeat is the one that does not fit whole, and it is clipped rather than
    ///     squeezed: the destination stops at the cell's edge and the source is truncated by the same
    ///     fraction, so a tiled edge ends mid-pattern the way a tiled floor does. Squeezing it to fit
    ///     would make one repeat in every cell a different size from its neighbours, which is more
    ///     visible than the cut.
    /// </remarks>
    static (float Near, float Far, float SourceNear, float SourceFar) Extent(
        float near,
        float far,
        float sourceNear,
        float sourceFar,
        float natural,
        int repeat
    ) {
        var start = near + (repeat * natural);
        var end = MathF.Min(start + natural, far);
        var fraction = natural > 0f ? MathF.Min((end - start) / natural, 1f) : 1f;

        return (start, end, sourceNear, sourceNear + ((sourceFar - sourceNear) * fraction));
    }

    /// <summary>Four corners, wound counter-clockwise from the bottom left.</summary>
    /// <remarks>
    ///     ⚠ <b>A flip mirrors the positions about the pivot and swaps the texture coordinates, both
    ///     at once.</b> Either alone is wrong in a way that only shows on a nine-slice: mirroring the
    ///     positions without the UVs draws the sprite's own art back to front in mirrored places, and
    ///     swapping the UVs without the positions mirrors each cell where it stands. Doing both keeps
    ///     the winding, which is why this can flip without the pipeline having to disable culling.
    /// </remarks>
    static void Quad(
        Span<SpriteVertex> into,
        float left,
        float right,
        float top,
        float bottom,
        Vector2 textureMin,
        Vector2 textureMax,
        Vector4 colour,
        SpriteFlip flip
    ) {
        if (flip.HasFlag(SpriteFlip.Horizontal)) {
            (left, right) = (-right, -left);
            (textureMin, textureMax) = (new(textureMax.X, textureMin.Y), new(textureMin.X, textureMax.Y));
        }

        if (flip.HasFlag(SpriteFlip.Vertical)) {
            (top, bottom) = (-bottom, -top);
            (textureMin, textureMax) = (new(textureMin.X, textureMax.Y), new(textureMax.X, textureMin.Y));
        }

        into[0] = new(new(left, bottom, 0f), new(textureMin.X, textureMax.Y), colour);
        into[1] = new(new(right, bottom, 0f), new(textureMax.X, textureMax.Y), colour);
        into[2] = new(new(right, top, 0f), new(textureMax.X, textureMin.Y), colour);
        into[3] = new(new(left, top, 0f), new(textureMin.X, textureMin.Y), colour);
    }

    /// <summary>What one of the nine cells resolved to.</summary>
    struct Cell {
        /// <summary>Where it goes, in the sprite's top-left-origin layout space.</summary>
        public Rectangle Destination;

        /// <summary>What it reads, in UVs.</summary>
        public Rectangle Source;

        /// <summary>How large one repeat is, which for an untiled cell is the whole destination.</summary>
        public Vector2 Natural;

        /// <summary>How many repeats each way. Zero for a cell that draws nothing.</summary>
        public Int2 Tiles;
    }
}
