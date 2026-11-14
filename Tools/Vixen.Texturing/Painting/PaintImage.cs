// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;

namespace Vixen.Texturing.Painting;

/// <summary>A rectangle of texels, half-open on the high side.</summary>
/// <param name="X">The low column.</param>
/// <param name="Y">The low row.</param>
/// <param name="Width">How many columns.</param>
/// <param name="Height">How many rows.</param>
public readonly record struct PaintRect(int X, int Y, int Width, int Height) {
    /// <summary>The rectangle that covers nothing.</summary>
    public static PaintRect Empty => new(0, 0, 0, 0);

    /// <summary>Whether it covers nothing.</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>One past the last column.</summary>
    public int EndX => X + Width;

    /// <summary>One past the last row.</summary>
    public int EndY => Y + Height;

    /// <summary>How many texels it covers.</summary>
    public int Area => IsEmpty ? 0 : Width * Height;

    /// <summary>The whole-texel rectangle covering a float region.</summary>
    /// <param name="minimum">The low corner, in texels.</param>
    /// <param name="maximum">The high corner, in texels.</param>
    /// <returns>The rectangle, floored and ceiled outward.</returns>
    public static PaintRect Covering(Vector2 minimum, Vector2 maximum) {
        var x = (int)MathF.Floor(minimum.X);
        var y = (int)MathF.Floor(minimum.Y);
        var endX = (int)MathF.Ceiling(maximum.X);
        var endY = (int)MathF.Ceiling(maximum.Y);

        return new(x, y, Math.Max(0, endX - x), Math.Max(0, endY - y));
    }

    /// <summary>The rectangle clipped to an image.</summary>
    /// <param name="width">The image's width.</param>
    /// <param name="height">Its height.</param>
    /// <returns>The clipped rectangle, possibly empty.</returns>
    public PaintRect Clip(int width, int height) {
        var x = Math.Max(X, 0);
        var y = Math.Max(Y, 0);
        var endX = Math.Min(EndX, width);
        var endY = Math.Min(EndY, height);

        return new(x, y, Math.Max(0, endX - x), Math.Max(0, endY - y));
    }

    /// <summary>The rectangle grown by a margin on every side.</summary>
    /// <param name="margin">How many texels.</param>
    /// <returns>The grown rectangle.</returns>
    public PaintRect Grow(int margin) =>
        IsEmpty ? this : new(X - margin, Y - margin, Width + 2 * margin, Height + 2 * margin);

    /// <summary>What two rectangles share.</summary>
    /// <param name="other">The other.</param>
    /// <returns>Their overlap, possibly empty.</returns>
    public PaintRect Intersect(PaintRect other) {
        var x = Math.Max(X, other.X);
        var y = Math.Max(Y, other.Y);

        return new(x, y, Math.Max(0, Math.Min(EndX, other.EndX) - x), Math.Max(0, Math.Min(EndY, other.EndY) - y));
    }

    /// <summary>The smallest rectangle containing both.</summary>
    /// <param name="other">The other.</param>
    /// <returns>The union. An empty operand returns the other.</returns>
    public PaintRect Union(PaintRect other) {
        if (IsEmpty) {
            return other;
        }

        if (other.IsEmpty) {
            return this;
        }

        var x = Math.Min(X, other.X);
        var y = Math.Min(Y, other.Y);

        return new(x, y, Math.Max(EndX, other.EndX) - x, Math.Max(EndY, other.EndY) - y);
    }

    /// <summary>Whether a texel is inside.</summary>
    /// <param name="x">Its column.</param>
    /// <param name="y">Its row.</param>
    /// <returns>Whether it is inside.</returns>
    public bool Contains(int x, int y) => x >= X && x < EndX && y >= Y && y < EndY;
}

/// <summary>
///     A paint layer's pixels: RGBA, eight bits a channel, straight alpha, held in sparse tiles.
/// </summary>
/// <remarks>
///     <para>
///         <b>Doc 48 § D10: a paint layer stores pixels and not strokes.</b> Storing strokes would
///         make every brush, every falloff and every blend mode a format compatibility surface —
///         change the falloff curve and every existing project repaints differently. This is the
///         other half of that decision: the file beside a <c>.vxlayers</c> holds texels, and
///         <c>PaintStroke</c>'s list of stamps is a session's undo record that is discarded on save.
///     </para>
///     <para>
///         ⚠ <b>Eight bits, and the reason is the size rather than the fidelity.</b> A 4K set is
///         16.8 million texels; at four floats a texel one paint layer is 268 MB and a stack with
///         twelve of them is not openable. Painter and InstaMAT both store eight or sixteen bits for
///         the same arithmetic. The accumulation that would actually suffer from eight bits — a
///         low-flow build-up over hundreds of overlapping stamps — does not happen here at all,
///         because <c>PaintStroke</c> accumulates coverage in <see langword="float" /> and writes
///         each texel <em>once per stamp from the value it had before the stroke</em>. Quantisation
///         therefore happens once, not once per stamp, which is the difference between a rounding
///         error and a drift.
///     </para>
///     <para>
///         <b>Tiles, for three things a flat array cannot do</b> (plan § 3.2, doc 48 § D13). An
///         unpainted region costs nothing, so twelve layers of a 4K set are not 800 MB before the
///         first stroke. A stroke says which tiles it touched, so a composite repairs those and not
///         the image. And a <see cref="PaintImageSnapshot" /> is a table of references, so an undo
///         record holds the tiles the stroke replaced and shares the rest with the image.
///     </para>
///     <para>
///         ⚠ <b>Copy-on-write is by marking, not counting.</b> A snapshot marks every tile it can
///         reach as shared, and the next write to one clones it first. A count would need to hear
///         about every snapshot that was dropped, and all a stale mark costs is one clone.
///     </para>
/// </remarks>
public sealed class PaintImage {
    /// <summary>How many bytes one texel occupies.</summary>
    public const int BytesPerTexel = 4;

    /// <summary>How many texels a tile has on a side, except at the image's far edges.</summary>
    public const int TileSize = 256;

    readonly int tilesWide;
    readonly int tilesHigh;
    readonly long[] written;
    readonly int texelBytes;
    Tile?[] tiles;

    /// <summary>The background as eight bits a lane, which is all of it for <see cref="PaintTexelFormat.Rgba8" />.</summary>
    uint background;

    /// <summary>The background in the image's own format: what an unwritten tile holds, byte for byte.</summary>
    byte[] backgroundTexel;

    long revision;

    /// <summary>An image of a size, filled with one colour.</summary>
    /// <param name="width">Its width in texels.</param>
    /// <param name="height">Its height in texels.</param>
    /// <param name="fill">What every texel starts as.</param>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not positive.</exception>
    public PaintImage(int width, int height, uint fill = 0u) : this(width, height, PaintTexelFormat.Rgba8) {
        var texel = new byte[BytesPerTexel];

        BinaryPrimitives.WriteUInt32LittleEndian(texel, fill);
        Backed(texel);
    }

    /// <summary>An image of a size and a texel format, every texel zero.</summary>
    /// <param name="width">Its width in texels.</param>
    /// <param name="height">Its height in texels.</param>
    /// <param name="format">How wide each texel is.</param>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not positive, or the format is not one.</exception>
    public PaintImage(int width, int height, PaintTexelFormat format) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        Width = width;
        Height = height;
        Format = format;
        texelBytes = PaintTexelFormats.BytesPerTexel(format);
        backgroundTexel = new byte[texelBytes];
        tilesWide = (width + TileSize - 1) / TileSize;
        tilesHigh = (height + TileSize - 1) / TileSize;
        tiles = new Tile?[tilesWide * tilesHigh];
        written = new long[tiles.Length];
    }

    /// <summary>Its width in texels.</summary>
    public int Width { get; }

    /// <summary>Its height in texels.</summary>
    public int Height { get; }

    /// <summary>How wide each texel is.</summary>
    public PaintTexelFormat Format { get; }

    /// <summary>How many bytes one of its texels occupies, which is <see cref="BytesPerTexel" /> for eight bits.</summary>
    public int TexelBytes => texelBytes;

    /// <summary>How many tiles it has in each row.</summary>
    public int TilesWide => tilesWide;

    /// <summary>How many rows of tiles it has.</summary>
    public int TilesHigh => tilesHigh;

    /// <summary>What every tile nobody wrote to holds, eight bits a lane.</summary>
    public uint Background => background;

    /// <summary>What every tile nobody wrote to holds, in full.</summary>
    public Vector4 BackgroundValue => PaintTexelFormats.Decode(Format, backgroundTexel);

    /// <summary>What every tile nobody wrote to holds, in the image's format.</summary>
    public ReadOnlySpan<byte> BackgroundTexel => backgroundTexel;

    /// <summary>The whole image, as a rectangle.</summary>
    public PaintRect Bounds => new(0, 0, Width, Height);

    /// <summary>How many tiles hold texels of their own.</summary>
    public int ResidentTiles {
        get {
            var count = 0;

            foreach (var tile in tiles) {
                if (tile is not null) {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>How many bytes of texels it holds, which is what a budget has to count.</summary>
    public long ResidentBytes {
        get {
            var total = 0L;

            foreach (var tile in tiles) {
                total += tile?.Texels.Length ?? 0;
            }

            return total;
        }
    }

    /// <summary>A number that moves whenever a texel may have.</summary>
    /// <remarks>Pass it back to <see cref="TilesChangedSince" /> to hear what moved after it.</remarks>
    public long Revision => revision;

    /// <summary>Reads a texel.</summary>
    /// <param name="index">Its index, row-major.</param>
    /// <returns>The texel, packed <c>0xAABBGGRR</c>.</returns>
    public uint this[int index] {
        get {
            var (y, x) = Math.DivRem(index, Width);

            return At(x, y);
        }

        set {
            var (y, x) = Math.DivRem(index, Width);

            Put(x, y, value);
        }
    }

    /// <summary>Reads a texel by position.</summary>
    /// <param name="x">Its column.</param>
    /// <param name="y">Its row.</param>
    /// <returns>The texel.</returns>
    /// <remarks>
    ///     An image wider than eight bits a lane answers with its texel rounded to eight: <see cref="Read(int, int)" /> is
    ///     all of it.
    /// </remarks>
    public uint At(int x, int y) {
        if (Format != PaintTexelFormat.Rgba8) {
            var value = Read(x, y);

            return Pack(value.X, value.Y, value.Z, value.W);
        }

        var slot = y / TileSize * tilesWide + x / TileSize;

        if (tiles[slot] is not { } tile) {
            return background;
        }

        var at = TexelOffset(x, y, slot);
        var texels = tile.Texels;

        return texels[at]
            | ((uint)texels[at + 1] << 8)
            | ((uint)texels[at + 2] << 16)
            | ((uint)texels[at + 3] << 24);
    }

    /// <summary>Writes a texel by position.</summary>
    /// <param name="x">Its column.</param>
    /// <param name="y">Its row.</param>
    /// <param name="value">The texel.</param>
    public void Put(int x, int y, uint value) {
        if (Format != PaintTexelFormat.Rgba8) {
            Write(x, y, Unpack(value));

            return;
        }

        var slot = y / TileSize * tilesWide + x / TileSize;
        var tile = Writable(slot);
        var at = TexelOffset(x, y, slot);
        var texels = tile.Texels;

        texels[at] = (byte)value;
        texels[at + 1] = (byte)(value >> 8);
        texels[at + 2] = (byte)(value >> 16);
        texels[at + 3] = (byte)(value >> 24);
    }

    /// <summary>Which tile a texel is in.</summary>
    /// <param name="index">Its index, row-major.</param>
    /// <returns>The tile's slot.</returns>
    internal int SlotOf(int index) {
        var (y, x) = Math.DivRem(index, Width);

        return y / TileSize * tilesWide + x / TileSize;
    }

    /// <summary>Makes a tile writable, once, before <see cref="PutReady" /> writes into it from any thread.</summary>
    /// <param name="slot">The tile.</param>
    /// <remarks>
    ///     ⚠ <b>The one step of a write that is not the tile's own.</b> <see cref="Put" /> counts a revision and may
    ///     copy a shared tile for every texel, and the revision is the whole image's, so texels in different tiles
    ///     written from different threads would race on it. This does both once per tile, on one thread.
    /// </remarks>
    internal void Ready(int slot) => Writable(slot);

    /// <summary>Writes a texel into a tile <see cref="Ready" /> made writable, touching nothing outside the tile.</summary>
    /// <param name="index">Its index, row-major.</param>
    /// <param name="value">Its lanes, which the image's own width rounds as <see cref="Write(int, Vector4)" /> does.</param>
    internal void PutReady(int index, Vector4 value) {
        var (y, x) = Math.DivRem(index, Width);
        var slot = y / TileSize * tilesWide + x / TileSize;
        var texels = tiles[slot]!.Texels;

        PaintTexelFormats.Encode(Format, value, texels.AsSpan(TexelOffset(x, y, slot), texelBytes));
    }

    /// <summary>Reads a texel in full, at the image's own width.</summary>
    /// <param name="x">Its column.</param>
    /// <param name="y">Its row.</param>
    /// <returns>Its lanes.</returns>
    public Vector4 Read(int x, int y) {
        var slot = y / TileSize * tilesWide + x / TileSize;

        return tiles[slot] is { } tile
            ? PaintTexelFormats.Decode(Format, tile.Texels.AsSpan(TexelOffset(x, y, slot), texelBytes))
            : BackgroundValue;
    }

    /// <summary>Reads a texel in full by its index, row-major.</summary>
    /// <param name="index">Its index.</param>
    /// <returns>Its lanes.</returns>
    public Vector4 Read(int index) {
        var (y, x) = Math.DivRem(index, Width);

        return Read(x, y);
    }

    /// <summary>Writes a texel in full; an eight-bit image rounds it.</summary>
    /// <param name="x">Its column.</param>
    /// <param name="y">Its row.</param>
    /// <param name="value">Its lanes.</param>
    public void Write(int x, int y, Vector4 value) {
        var slot = y / TileSize * tilesWide + x / TileSize;
        var tile = Writable(slot);

        PaintTexelFormats.Encode(Format, value, tile.Texels.AsSpan(TexelOffset(x, y, slot), texelBytes));
    }

    /// <summary>Writes a texel in full by its index, row-major.</summary>
    /// <param name="index">Its index.</param>
    /// <param name="value">Its lanes.</param>
    public void Write(int index, Vector4 value) {
        var (y, x) = Math.DivRem(index, Width);

        Write(x, y, value);
    }

    /// <summary>
    ///     One texel of <paramref name="from" /> mixed toward <paramref name="to" />, as this image holds the result.
    /// </summary>
    /// <param name="from">Where the texel started.</param>
    /// <param name="to">The colour being painted.</param>
    /// <param name="amount">How far, 0…1.</param>
    /// <returns>The mixed texel.</returns>
    /// <remarks>
    ///     ⚠ <b>Eight bits mix as <see cref="Mix" /> does, to the bit</b>, from the levels the two colours round to, so a
    ///     stroke painted through here on an eight-bit image is the one it always was. A wider image mixes the lanes as
    ///     they are, and its write rounds them to its own width.
    /// </remarks>
    public Vector4 Mixed(Vector4 from, Vector4 to, float amount) {
        if (Format == PaintTexelFormat.Rgba8) {
            return Unpack(Mix(Pack(from.X, from.Y, from.Z, from.W), Pack(to.X, to.Y, to.Z, to.W), amount));
        }

        return from + (to - from) * Math.Clamp(amount, 0f, 1f);
    }

    /// <summary>Writes every texel.</summary>
    /// <param name="value">The colour, eight bits a lane.</param>
    /// <remarks>Every tile goes, so this is one word and not 67 MB, whatever the image's size.</remarks>
    public void Fill(uint value) {
        if (Format == PaintTexelFormat.Rgba8) {
            var texel = new byte[texelBytes];

            BinaryPrimitives.WriteUInt32LittleEndian(texel, value);
            Cleared(texel);
        } else {
            Fill(Unpack(value));
        }
    }

    /// <summary>Writes every texel, in full.</summary>
    /// <param name="value">The colour.</param>
    public void Fill(Vector4 value) {
        var texel = new byte[texelBytes];

        PaintTexelFormats.Encode(Format, value, texel);
        Cleared(texel);
    }

    /// <summary>Every tile gone, and the background this texel.</summary>
    void Cleared(byte[] texel) {
        Backed(texel);
        tiles = new Tile?[tiles.Length];
        revision++;
        Array.Fill(written, revision);
    }

    /// <summary>Takes a background, in the image's format and as eight bits a lane.</summary>
    void Backed(byte[] texel) {
        backgroundTexel = texel;

        var value = BackgroundValue;

        background = Format == PaintTexelFormat.Rgba8
            ? BinaryPrimitives.ReadUInt32LittleEndian(texel)
            : Pack(value.X, value.Y, value.Z, value.W);
    }

    /// <summary>The whole image as one flat array, RGBA, row-major from the top.</summary>
    /// <returns>A new array the caller owns, which writing to changes nothing here.</returns>
    public byte[] ToTexels() {
        var flat = new byte[(long)Width * Height * texelBytes];

        CopyTo(flat);

        return flat;
    }

    /// <summary>Copies the whole image into a flat array, RGBA, row-major from the top.</summary>
    /// <param name="flat">Where to, exactly the image's size.</param>
    /// <exception cref="ArgumentException">It is not the image's size.</exception>
    public void CopyTo(Span<byte> flat) {
        if (flat.Length != (long)Width * Height * texelBytes) {
            throw new ArgumentException(
                $"A {Width}×{Height} image is {(long)Width * Height * texelBytes} bytes, and {flat.Length} were given.",
                nameof(flat)
            );
        }

        for (var tileY = 0; tileY < tilesHigh; tileY++) {
            for (var tileX = 0; tileX < tilesWide; tileX++) {
                var rect = TileRect(tileX, tileY);
                var row = rect.Width * texelBytes;
                var tile = tiles[tileY * tilesWide + tileX];

                for (var y = 0; y < rect.Height; y++) {
                    var to = flat.Slice(((rect.Y + y) * Width + rect.X) * texelBytes, row);

                    if (tile is null) {
                        Repeat(to, backgroundTexel);
                    } else {
                        tile.Texels.AsSpan(y * row, row).CopyTo(to);
                    }
                }
            }
        }
    }

    /// <summary>Replaces the whole image from a flat array, keeping only the tiles that hold something.</summary>
    /// <param name="flat">RGBA, row-major from the top, exactly the image's size.</param>
    /// <exception cref="ArgumentException">It is not the image's size.</exception>
    /// <remarks>
    ///     ⚠ <b>Sparse on the way in.</b> Every <c>.vxpaint</c> written before tiles existed is flat,
    ///     and a layer that painted one corner has to cost one tile when it opens rather than 67 MB.
    /// </remarks>
    public void CopyFrom(ReadOnlySpan<byte> flat) {
        if (flat.Length != (long)Width * Height * texelBytes) {
            throw new ArgumentException(
                $"A {Width}×{Height} image is {(long)Width * Height * texelBytes} bytes, and {flat.Length} were given.",
                nameof(flat)
            );
        }

        revision++;
        Backed(new byte[texelBytes]);

        for (var tileY = 0; tileY < tilesHigh; tileY++) {
            for (var tileX = 0; tileX < tilesWide; tileX++) {
                var rect = TileRect(tileX, tileY);
                var row = rect.Width * texelBytes;
                var bytes = new byte[row * rect.Height];

                for (var y = 0; y < rect.Height; y++) {
                    flat.Slice(((rect.Y + y) * Width + rect.X) * texelBytes, row).CopyTo(bytes.AsSpan(y * row));
                }

                var slot = tileY * tilesWide + tileX;

                tiles[slot] = bytes.AsSpan().IndexOfAnyExcept((byte)0) < 0 ? null : new(bytes);
                written[slot] = revision;
            }
        }
    }

    /// <summary>Becomes another image of the same size, sharing its tiles until either is written.</summary>
    /// <param name="source">The image.</param>
    /// <exception cref="ArgumentException">It is another size.</exception>
    public void CopyFrom(PaintImage source) {
        ArgumentNullException.ThrowIfNull(source);

        if (source.Width != Width || source.Height != Height || source.Format != Format) {
            throw new ArgumentException(
                $"This image is {Width}×{Height} of {Format} and that one is {source.Width}×{source.Height} of "
                + $"{source.Format}.",
                nameof(source)
            );
        }

        revision++;
        Backed(source.backgroundTexel);

        for (var slot = 0; slot < tiles.Length; slot++) {
            source.tiles[slot]?.Share();
            tiles[slot] = source.tiles[slot];
            written[slot] = revision;
        }
    }

    /// <summary>Copies a rectangle out, row by row.</summary>
    /// <param name="rect">Which texels, inside the image.</param>
    /// <param name="into">Where to, at least the rectangle's size.</param>
    public void CopyRect(PaintRect rect, Span<byte> into) {
        var stride = rect.Width * texelBytes;

        for (var y = 0; y < rect.Height; y++) {
            var row = into.Slice(y * stride, stride);
            var x = rect.X;

            while (x < rect.EndX) {
                var tileX = x / TileSize;
                var tileY = (rect.Y + y) / TileSize;
                var tile = tiles[tileY * tilesWide + tileX];
                var box = TileRect(tileX, tileY);
                var run = Math.Min(box.EndX, rect.EndX) - x;
                var to = row.Slice((x - rect.X) * texelBytes, run * texelBytes);

                if (tile is null) {
                    Repeat(to, backgroundTexel);
                } else {
                    tile.Texels.AsSpan(((rect.Y + y - box.Y) * box.Width + (x - box.X)) * texelBytes, to.Length)
                        .CopyTo(to);
                }

                x += run;
            }
        }
    }

    /// <summary>Copies a rectangle in, row by row.</summary>
    /// <param name="rect">Which texels, inside the image.</param>
    /// <param name="from">Their bytes, exactly the rectangle's size.</param>
    /// <exception cref="ArgumentException">It is not the rectangle's size, or the rectangle leaves the image.</exception>
    /// <remarks>The other half of <see cref="CopyRect" />, for a picture repaired a rectangle at a time.</remarks>
    public void WriteRect(PaintRect rect, ReadOnlySpan<byte> from) {
        var stride = rect.Width * texelBytes;

        if (rect.X < 0
            || rect.Y < 0
            || rect.EndX > Width
            || rect.EndY > Height
            || from.Length != stride * rect.Height) {
            throw new ArgumentException(
                $"{rect} of a {Width}×{Height} image is {stride * rect.Height} bytes, and {from.Length} were given.",
                nameof(from)
            );
        }

        for (var y = 0; y < rect.Height; y++) {
            var row = from.Slice(y * stride, stride);
            var x = rect.X;

            while (x < rect.EndX) {
                var tileX = x / TileSize;
                var tileY = (rect.Y + y) / TileSize;
                var box = TileRect(tileX, tileY);
                var run = Math.Min(box.EndX, rect.EndX) - x;
                var tile = Writable(tileY * tilesWide + tileX);

                row.Slice((x - rect.X) * texelBytes, run * texelBytes)
                    .CopyTo(tile.Texels.AsSpan(((rect.Y + y - box.Y) * box.Width + (x - box.X)) * texelBytes));

                x += run;
            }
        }
    }

    /// <summary>Gives every tile texels of its own now, so that threads can write different texels at once.</summary>
    /// <remarks>
    ///     ⚠ <b>The one way this type is safe to write from two threads.</b> A tile that appears on
    ///     first write is a tile two bands can each allocate, and one band's texels go with the loser.
    ///     A raster's picture is written from every band and is never sparse, so it says so once.
    /// </remarks>
    internal void Materialise() {
        for (var slot = 0; slot < tiles.Length; slot++) {
            Writable(slot);
        }
    }

    /// <summary>Whether a tile reads as nothing but the background, so that a file can leave it out.</summary>
    /// <param name="tileX">Its column of tiles.</param>
    /// <param name="tileY">Its row of tiles.</param>
    /// <returns>Whether it does.</returns>
    internal bool IsBackground(int tileX, int tileY) {
        if (tiles[tileY * tilesWide + tileX] is not { } tile) {
            return true;
        }

        return Format switch {
            PaintTexelFormat.Rgba8 => Only<uint>(tile.Texels, backgroundTexel),
            PaintTexelFormat.Rgba16Float => Only<ulong>(tile.Texels, backgroundTexel),
            _ => Only<UInt128>(tile.Texels, backgroundTexel)
        };
    }

    /// <summary>Whether bytes are one texel over and over, read a texel at a time.</summary>
    static bool Only<T>(byte[] texels, byte[] texel) where T : unmanaged, IEquatable<T> =>
        MemoryMarshal.Cast<byte, T>(texels.AsSpan()).IndexOfAnyExcept(MemoryMarshal.Read<T>(texel)) < 0;

    /// <summary>Starts over at a background, for a reader about to adopt tiles.</summary>
    /// <param name="value">The background.</param>
    internal void Reset(uint value) => Fill(value);

    /// <summary>Starts over at a background in the image's own format, for a reader about to adopt tiles.</summary>
    /// <param name="texel">The background's bytes, exactly one texel.</param>
    /// <exception cref="ArgumentException">It is not one texel.</exception>
    internal void Reset(byte[] texel) {
        if (texel.Length != texelBytes) {
            throw new ArgumentException(
                $"A texel of {Format} is {texelBytes} bytes, and {texel.Length} were given.",
                nameof(texel)
            );
        }

        Cleared(texel);
    }

    /// <summary>Fills bytes with one texel over and over.</summary>
    static void Repeat(Span<byte> into, ReadOnlySpan<byte> texel) {
        switch (texel.Length) {
            case 4:
                MemoryMarshal.Cast<byte, uint>(into).Fill(MemoryMarshal.Read<uint>(texel));
                break;
            case 8:
                MemoryMarshal.Cast<byte, ulong>(into).Fill(MemoryMarshal.Read<ulong>(texel));
                break;
            default:
                MemoryMarshal.Cast<byte, UInt128>(into).Fill(MemoryMarshal.Read<UInt128>(texel));
                break;
        }
    }

    /// <summary>The rectangle a tile covers.</summary>
    /// <param name="tileX">Its column of tiles.</param>
    /// <param name="tileY">Its row of tiles.</param>
    /// <returns>The rectangle, cut short at the image's far edges.</returns>
    public PaintRect TileRect(int tileX, int tileY) {
        var x = tileX * TileSize;
        var y = tileY * TileSize;

        return new(x, y, Math.Min(TileSize, Width - x), Math.Min(TileSize, Height - y));
    }

    /// <summary>A tile's texels, row by row.</summary>
    /// <param name="tileX">Its column of tiles.</param>
    /// <param name="tileY">Its row of tiles.</param>
    /// <returns>The bytes, shared with the image until the next write, or empty when it holds none.</returns>
    public ReadOnlyMemory<byte> TileTexels(int tileX, int tileY) =>
        tiles[tileY * tilesWide + tileX]?.Texels ?? ReadOnlyMemory<byte>.Empty;

    /// <summary>A tile's texels, whether it holds any or not.</summary>
    /// <param name="tileX">Its column of tiles.</param>
    /// <param name="tileY">Its row of tiles.</param>
    /// <returns>The bytes, which the caller must not write to.</returns>
    public ReadOnlyMemory<byte> TileTexelsOrBackground(int tileX, int tileY) {
        var held = TileTexels(tileX, tileY);

        if (held.Length > 0) {
            return held;
        }

        var rect = TileRect(tileX, tileY);
        var bytes = new byte[rect.Area * texelBytes];

        Repeat(bytes, backgroundTexel);

        return bytes;
    }

    /// <summary>Puts one tile's texels in, from a file.</summary>
    /// <param name="tileX">Its column of tiles.</param>
    /// <param name="tileY">Its row of tiles.</param>
    /// <param name="texels">Its texels, which this now owns.</param>
    /// <exception cref="ArgumentException">They are not the tile's size.</exception>
    internal void Adopt(int tileX, int tileY, byte[] texels) {
        var rect = TileRect(tileX, tileY);

        if (texels.Length != rect.Area * texelBytes) {
            throw new ArgumentException(
                $"Tile ({tileX}, {tileY}) is {rect.Area * texelBytes} bytes, and {texels.Length} were given.",
                nameof(texels)
            );
        }

        var slot = tileY * tilesWide + tileX;

        revision++;
        tiles[slot] = new(texels);
        written[slot] = revision;
    }

    /// <summary>Which tiles have moved since a <see cref="Revision" />.</summary>
    /// <param name="since">The revision.</param>
    /// <param name="moved">Cleared and filled with one <c>(column, row)</c> per tile.</param>
    public void TilesChangedSince(long since, List<(int TileX, int TileY)> moved) {
        ArgumentNullException.ThrowIfNull(moved);

        moved.Clear();

        for (var slot = 0; slot < written.Length; slot++) {
            if (written[slot] > since) {
                moved.Add((slot % tilesWide, slot / tilesWide));
            }
        }
    }

    /// <summary>A digest of the whole image, from the digests of its tiles.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Cheap because a tile keeps its digest until it is written.</b> A flat digest is
    ///         the whole image, 67 MB per stamp at 4K; this costs the tiles the stamp touched and a
    ///         table of a few hundred numbers.
    ///     </para>
    ///     <para>
    ///         <b>By value</b>, so an undo that brings the tiles back brings the digest back, and an
    ///         evaluator holding the old result can use it.
    ///     </para>
    /// </remarks>
    public UInt128 Digest {
        get {
            Span<byte> table = stackalloc byte[sizeof(uint) + sizeof(int)];

            XxHash128 hash = new();

            // ⚠ An eight-bit image hashes as it always did, so no digest anything holds moves; a wider one says so.
            if (Format != PaintTexelFormat.Rgba8) {
                hash.Append([(byte)Format]);
                hash.Append(backgroundTexel);
            }

            BinaryPrimitives.WriteUInt32LittleEndian(table, background);
            BinaryPrimitives.WriteInt32LittleEndian(table[sizeof(uint)..], Width);
            hash.Append(table);

            Span<byte> one = stackalloc byte[16];

            foreach (var tile in tiles) {
                BinaryPrimitives.WriteUInt128LittleEndian(one, tile?.Digest ?? UInt128.Zero);
                hash.Append(one);
            }

            return hash.GetCurrentHashAsUInt128();
        }
    }

    /// <summary>The image as it stands, cheaply.</summary>
    /// <returns>A snapshot, which holds the tiles it saw for as long as it is kept.</returns>
    public PaintImageSnapshot Snapshot() {
        foreach (var tile in tiles) {
            tile?.Share();
        }

        return new(this, (Tile?[])tiles.Clone(), backgroundTexel);
    }

    /// <summary>Puts the image back to a snapshot, moving only the tiles that differ.</summary>
    /// <param name="snapshot">A snapshot of this image.</param>
    /// <exception cref="ArgumentException">It is of another image.</exception>
    public void Restore(PaintImageSnapshot snapshot) {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!ReferenceEquals(snapshot.Image, this)) {
            throw new ArgumentException("A snapshot restores the image it was taken of.", nameof(snapshot));
        }

        revision++;

        var fill = !backgroundTexel.AsSpan().SequenceEqual(snapshot.Background);

        Backed(snapshot.Background);

        for (var slot = 0; slot < tiles.Length; slot++) {
            if (fill || !ReferenceEquals(tiles[slot], snapshot.Tiles[slot])) {
                tiles[slot] = snapshot.Tiles[slot];
                tiles[slot]?.Share();
                written[slot] = revision;
            }
        }
    }

    /// <summary>How many bytes of tiles this holds that a snapshot does not, which is what undo keeps.</summary>
    /// <param name="snapshot">A snapshot of this image.</param>
    /// <returns>The bytes.</returns>
    internal long BytesApartFrom(PaintImageSnapshot snapshot) {
        var total = 0L;

        for (var slot = 0; slot < tiles.Length; slot++) {
            if (!ReferenceEquals(tiles[slot], snapshot.Tiles[slot])) {
                total += tiles[slot]?.Texels.Length ?? 0;
                total += snapshot.Tiles[slot]?.Texels.Length ?? 0;
            }
        }

        return total;
    }

    int TexelOffset(int x, int y, int slot) {
        var column = x % TileSize;
        var row = y % TileSize;
        var wide = Math.Min(TileSize, Width - slot % tilesWide * TileSize);

        return (row * wide + column) * texelBytes;
    }

    Tile Writable(int slot) {
        revision++;
        written[slot] = revision;

        var tile = tiles[slot];

        if (tile is null) {
            var rect = TileRect(slot % tilesWide, slot / tilesWide);
            var bytes = new byte[rect.Area * texelBytes];

            if (backgroundTexel.AsSpan().IndexOfAnyExcept((byte)0) >= 0) {
                Repeat(bytes, backgroundTexel);
            }

            return tiles[slot] = new(bytes);
        }

        if (tile.Shared) {
            return tiles[slot] = new((byte[])tile.Texels.Clone());
        }

        tile.Touch();

        return tile;
    }

    /// <summary>One tile's texels.</summary>
    /// <param name="texels">Its bytes.</param>
    internal sealed class Tile(byte[] texels) {
        UInt128 digest;

        public byte[] Texels { get; } = texels;

        /// <summary>Whether a snapshot can see it, so that it has to be cloned to be written.</summary>
        public bool Shared { get; private set; }

        public UInt128 Digest {
            get {
                if (digest == UInt128.Zero) {
                    digest = XxHash128.HashToUInt128(Texels);
                }

                return digest;
            }
        }

        public void Share() => Shared = true;

        public void Touch() => digest = UInt128.Zero;
    }

    /// <summary>Packs four 0…1 channels into a texel.</summary>
    /// <param name="r">Red.</param>
    /// <param name="g">Green.</param>
    /// <param name="b">Blue.</param>
    /// <param name="a">Alpha.</param>
    /// <returns>The texel.</returns>
    public static uint Pack(float r, float g, float b, float a) =>
        Quantise(r) | ((uint)Quantise(g) << 8) | ((uint)Quantise(b) << 16) | ((uint)Quantise(a) << 24);

    /// <summary>A texel's four lanes, 0…1.</summary>
    /// <param name="texel">The texel, packed <c>0xAABBGGRR</c>.</param>
    /// <returns>Red, green, blue and alpha.</returns>
    public static Vector4 Unpack(uint texel) =>
        new(Channel(texel, 0), Channel(texel, 1), Channel(texel, 2), Channel(texel, 3));

    /// <summary>The channel of a texel, 0…1.</summary>
    /// <param name="texel">The texel.</param>
    /// <param name="channel">Which channel, 0 for red through 3 for alpha.</param>
    /// <returns>The value.</returns>
    public static float Channel(uint texel, int channel) => ((texel >> (channel * 8)) & 0xFF) / 255f;

    /// <summary>
    ///     One texel of <paramref name="from" /> mixed toward <paramref name="to" />.
    /// </summary>
    /// <param name="from">Where the texel started.</param>
    /// <param name="to">The colour being painted.</param>
    /// <param name="amount">How far, 0…1.</param>
    /// <returns>The mixed texel.</returns>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         From the value the texel had <em>before the stroke</em>, never from the value it has
    ///         now.
    ///     </b>
    ///     Compositing a stamp onto the current value makes a texel that two stamps cross
    ///     darker than a texel one stamp crosses, which is the defect where a slow drag paints
    ///     stronger than a fast one over the same ground. <c>PaintStroke</c> keeps the before-value —
    ///     the undo record already holds it, so the correct arithmetic costs nothing extra.
    /// </remarks>
    public static uint Mix(uint from, uint to, float amount) {
        var t = Math.Clamp(amount, 0f, 1f);
        var mixed = 0u;

        for (var channel = 0; channel < 4; channel++) {
            var a = (from >> (channel * 8)) & 0xFF;
            var b = (to >> (channel * 8)) & 0xFF;

            mixed |= (uint)Quantise((a + (b - (float)a) * t) / 255f) << (channel * 8);
        }

        return mixed;
    }

    static byte Quantise(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
}

/// <summary>A <see cref="PaintImage" /> as it stood, holding references to its tiles and not copies.</summary>
/// <remarks>
///     <b>What undo keeps.</b> Two of these around a stroke share every tile the stroke did not touch,
///     so an entry costs the tiles it changed, however large the image.
/// </remarks>
public sealed class PaintImageSnapshot {
    internal PaintImageSnapshot(PaintImage image, PaintImage.Tile?[] tiles, byte[] background) {
        Image = image;
        Tiles = tiles;
        Background = background;
    }

    /// <summary>The image it was taken of.</summary>
    public PaintImage Image { get; }

    internal PaintImage.Tile?[] Tiles { get; }

    internal byte[] Background { get; }
}
