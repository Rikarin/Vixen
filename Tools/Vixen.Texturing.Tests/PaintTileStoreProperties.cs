// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Text;
using CsCheck;
using Vixen.Core.Mathematics;
using Vixen.Texturing.Painting;
using Xunit;

namespace Vixen.Texturing.Tests;

/// <summary>The tile store against the flat array it replaced, which is the only oracle that means anything.</summary>
[Trait("Category", "Property")]
public class PaintTileStoreProperties {
    /// <summary>One thing that can happen to an image.</summary>
    public readonly record struct Edit(int X, int Y, int Width, int Height, uint Colour);

    static Gen<Edit> Edits(int width, int height) =>
        Gen.Select(Gen.Int[0, width - 1], Gen.Int[0, height - 1], Gen.Int[1, 400], Gen.Int[1, 400], Gen.UInt)
            .Select(static (x, y, w, h, colour) => new Edit(x, y, w, h, colour));

    static readonly Gen<(int Width, int Height, Edit[] Edits)> Scripts =
        Gen.Select(Gen.Int[1, 700], Gen.Int[1, 700])
            .SelectMany(static (width, height) =>
                Edits(width, height).Array[0, 12].Select(edits => (width, height, edits))
            );

    /// <summary>Every texel reads back as a flat array would have held it, wherever tiles fell.</summary>
    [Fact]
    public void The_tiles_read_as_the_flat_array_they_replaced() =>
        Scripts.Sample(
            static script => {
                var (width, height, edits) = script;
                PaintImage image = new(width, height);
                var flat = new uint[width * height];

                foreach (var edit in edits) {
                    Apply(image, edit);
                    Model(flat, width, height, edit);
                }

                Assert.Equal(Bytes(flat), image.ToTexels());
            },
            iter: 60,
            threads: 1
        );

    /// <summary>A write, a read and a second write land on the same bytes, and the picture survives.</summary>
    [Fact]
    public void A_canvas_round_trips_to_byte_identity() =>
        Scripts.Sample(
            static script => {
                var (width, height, edits) = script;
                PaintCanvas canvas = new(width, height);
                var colour = canvas.Channel("baseColor");

                canvas.Channel("roughness");

                foreach (var edit in edits) {
                    Apply(colour, edit);
                }

                var once = Written(canvas);
                var read = PaintCanvas.Read(new MemoryStream(once));

                Assert.Equal(canvas.Channels, read.Channels);
                Assert.Equal(colour.ToTexels(), read.Channel("baseColor").ToTexels());
                Assert.Equal(once, Written(read));
            },
            iter: 40,
            threads: 1
        );

    /// <summary>A file from before tiles opens as the same picture, and the next save moves it on.</summary>
    /// <param name="version">Which old format.</param>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Every_older_format_opens_as_the_same_picture(int version) =>
        Scripts.Sample(
            script => {
                var (width, height, edits) = script;
                PaintImage image = new(width, height);

                foreach (var edit in edits) {
                    Apply(image, edit);
                }

                var read = PaintCanvas.Read(new MemoryStream(Legacy(version, width, height, image.ToTexels())));

                Assert.Equal(image.ToTexels(), read.Channel("baseColor").ToTexels());

                var moved = Written(read);

                Assert.Equal(PaintCanvas.CurrentVersion, BitConverter.ToInt32(moved, PaintCanvas.Magic.Length));
                Assert.Equal(
                    image.ToTexels(),
                    PaintCanvas.Read(new MemoryStream(moved)).Channel("baseColor").ToTexels()
                );
            },
            iter: 30,
            threads: 1
        );

    /// <summary>A real drag, undone and redone, in any number of paths.</summary>
    /// <remarks>
    ///     ⚠ <b>The mirrored case is the one per-texel records got wrong</b>: two paths that cross record
    ///     each other's paint, and four texels used to survive their own undo.
    /// </remarks>
    [Fact]
    public void A_drag_undoes_and_redoes_to_byte_identity() {
        var gen = Gen.Select(
            Gen.Select(Gen.Float[0f, 600f], Gen.Float[0f, 600f]).Array[2, 40],
            Gen.Int[1, 3],
            Gen.Int[1, 40],
            Gen.UInt
        );

        gen.Sample(
            (points, paths, radius, colour) => {
                const int Size = 640;

                PaintImage image = new(Size, Size);

                image[1234] = 0xFF112233u; // a tile that exists and that the drag may or may not touch

                var coverage = PaintCoverage.Everywhere(Size, Size);
                PaintTarget target = new(image, coverage, 2);
                var before = image.ToTexels();
                var digest = image.Digest;
                var session = PaintSession.Begin(
                    target,
                    PaintBrush.Default with { Radius = radius, Spacing = 0.25f, Flow = 1f },
                    colour | 0xFF000000u
                );
                Span<Vector2> at = stackalloc Vector2[paths];

                foreach (var (x, y) in points) {
                    at[0] = new(x, y);

                    // Mirrored about a vertical line, so that with a wide brush the paths overlap.
                    for (var path = 1; path < paths; path++) {
                        at[path] = new(Size - x - path * 8f, y);
                    }

                    session.MoveAll(at);
                }

                if (session.End("Stroke") is not { } command) {
                    return;
                }

                var after = image.ToTexels();

                for (var round = 0; round < 2; round++) {
                    command.Undo(null!);
                    Assert.Equal(before, image.ToTexels());
                    Assert.Equal(digest, image.Digest);

                    command.Do(null!);
                    Assert.Equal(after, image.ToTexels());
                }
            },
            iter: 25,
            threads: 1
        );
    }

    /// <summary>An undo record costs the tiles a stroke touched, and a picture nobody painted costs nothing.</summary>
    [Fact]
    public void An_undo_record_holds_tiles_and_not_the_image() {
        const int Size = 4096;

        PaintImage image = new(Size, Size);

        Assert.Equal(0L, image.ResidentBytes);

        var session = PaintSession.Begin(
            new(image, PaintCoverage.Everywhere(Size, Size), 0),
            PaintBrush.Default with { Radius = 16f, Spacing = 0.25f, Flow = 1f },
            0xFF0000FFu
        );

        session.Move(new(100f, 100f));
        session.Move(new(101f, 100f));

        var command = Assert.IsType<PaintStrokeCommand>(session.End("Stroke"));
        var tile = (long)PaintImage.TileSize * PaintImage.TileSize * PaintImage.BytesPerTexel;

        Assert.Equal(tile, image.ResidentBytes);
        Assert.InRange(command.Bytes, tile, 2 * tile);
    }

    /// <summary>A snapshot does not see writes made after it, and the write did not copy what it left alone.</summary>
    [Fact]
    public void A_snapshot_is_copy_on_write() {
        PaintImage image = new(512, 256);

        image.Put(0, 0, 1u);
        image.Put(300, 0, 2u);

        var snapshot = image.Snapshot();

        image.Put(0, 0, 3u);

        var moved = new List<(int, int)>();
        var seen = image.Revision;

        image.Put(1, 0, 4u);
        image.TilesChangedSince(seen, moved);

        Assert.Equal([(0, 0)], moved);

        image.Restore(snapshot);

        Assert.Equal(1u, image.At(0, 0));
        Assert.Equal(0u, image.At(1, 0));
        Assert.Equal(2u, image.At(300, 0));

        image.TilesChangedSince(seen, moved);

        Assert.Equal([(0, 0)], moved);
    }

    /// <summary>A tile out of order is refused rather than read as a picture nobody wrote.</summary>
    [Fact]
    public void A_tile_written_twice_is_refused() {
        using MemoryStream stream = new();

        using (BinaryWriter writer = new(stream, Encoding.UTF8, true)) {
            writer.Write(PaintCanvas.Magic);
            writer.Write(PaintCanvas.CurrentVersion);
            writer.Write(512);
            writer.Write(512);
            writer.Write(1);
            writer.Write("baseColor");
            writer.Write(0u);
            writer.Write(2);

            for (var tile = 0; tile < 2; tile++) {
                writer.Write(0);

                var packed = Deflated(new byte[PaintImage.TileSize * PaintImage.TileSize * PaintImage.BytesPerTexel]);

                writer.Write(packed.Length);
                writer.Write(packed);
            }
        }

        stream.Position = 0;

        Assert.Throws<InvalidDataException>(() => PaintCanvas.Read(stream));
    }

    static void Apply(PaintImage image, Edit edit) {
        for (var y = edit.Y; y < Math.Min(edit.Y + edit.Height, image.Height); y++) {
            for (var x = edit.X; x < Math.Min(edit.X + edit.Width, image.Width); x++) {
                image.Put(x, y, edit.Colour);
            }
        }
    }

    static void Model(uint[] flat, int width, int height, Edit edit) {
        for (var y = edit.Y; y < Math.Min(edit.Y + edit.Height, height); y++) {
            for (var x = edit.X; x < Math.Min(edit.X + edit.Width, width); x++) {
                flat[y * width + x] = edit.Colour;
            }
        }
    }

    static byte[] Bytes(uint[] flat) {
        var bytes = new byte[flat.Length * 4];

        Buffer.BlockCopy(flat, 0, bytes, 0, bytes.Length);

        return bytes;
    }

    static byte[] Written(PaintCanvas canvas) {
        using MemoryStream stream = new();

        canvas.Write(stream);

        return stream.ToArray();
    }

    /// <summary>The two formats before tiles, laid out by hand because no writer speaks them any more.</summary>
    static byte[] Legacy(int version, int width, int height, byte[] texels) {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.UTF8, true);

        writer.Write(PaintCanvas.Magic);
        writer.Write(version);
        writer.Write(width);
        writer.Write(height);
        writer.Write(1);
        writer.Write("baseColor");

        if (version == 1) {
            writer.Write(texels);
        } else {
            var packed = Deflated(texels);

            writer.Write(packed.Length);
            writer.Write(packed);
        }

        writer.Flush();

        return stream.ToArray();
    }

    static byte[] Deflated(byte[] texels) {
        using MemoryStream packed = new();

        using (DeflateStream deflate = new(packed, CompressionLevel.Fastest, true)) {
            deflate.Write(texels);
        }

        return packed.ToArray();
    }
}
