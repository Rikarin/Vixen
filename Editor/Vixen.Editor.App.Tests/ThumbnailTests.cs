// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using Vixen.Core;
using Vixen.Editor.Testing;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Pictures of assets, decoded off the frame thread and uploaded on it.</summary>
/// <remarks>
///     ⚠ <b>The upload half needs a device and these tests have none, which is the point of the
///     seam.</b> <c>IThumbnailSurface</c> is implemented here by something that records what it was
///     handed — so the decode, the reduction, the queue, the cache and the eviction are all under
///     test and only the Vulkan call is not. The same bargain the software rasteriser makes for the
///     golden suite.
/// </remarks>
public class ThumbnailTests {
    /// <summary>A surface that hands out numbers and remembers the pixels.</summary>
    sealed class Recording : IThumbnailSurface {
        readonly List<ulong> released = [];

        ulong next = 1;

        public List<(int Width, int Height, byte[] Pixels)> Uploads { get; } = [];

        public IReadOnlyList<ulong> Released => released;

        public ulong Upload(int width, int height, ReadOnlySpan<byte> rgba) {
            Uploads.Add((width, height, rgba.ToArray()));

            return next++;
        }

        public void Release(ulong image) => released.Add(image);
    }

    /// <summary>Pumps until the cache has nothing left in flight.</summary>
    /// <remarks>
    ///     ⚠ <b>Bounded by <c>IsBusy</c> rather than by a turn count or a clock.</b> The decode is on
    ///     the thread pool, so the wait has to end when the work does — counting turns makes the
    ///     suite pass on a quiet laptop and fail on a loaded runner, which is exactly the flakiness
    ///     doc 12 forbids. The turn cap is a backstop against a decode that never returns, not the
    ///     mechanism.
    /// </remarks>
    static bool Settle(ThumbnailCache cache, Func<bool> until, int turns = 100_000) {
        for (var turn = 0; turn < turns && !until() && cache.IsBusy; turn++) {
            cache.Pump();
            Thread.Yield();
        }

        cache.Pump();
        return until();
    }

    /// <summary>Writes a PNG into the project and makes the editor notice it.</summary>
    static AssetId Paint(EditorSession editor, string path, int width, int height, Func<int, int, byte> shade) {
        var absolute = Path.Combine(editor.ProjectRoot, path.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);

        var pixels = new byte[width * height * 4];

        for (var y = 0; y < height; y++) {
            for (var x = 0; x < width; x++) {
                var at = ((y * width) + x) * 4;
                var value = shade(x, y);

                pixels[at] = value;
                pixels[at + 1] = value;
                pixels[at + 2] = value;
                pixels[at + 3] = 255;
            }
        }

        File.WriteAllBytes(absolute, Png(width, height, pixels));
        editor.Run("assets.refresh");

        if (!editor.Project.Assets.TryGetByPath(path, out var entry)) {
            throw editor.Fail($"'{path}' is not in the index");
        }

        return entry.Guid;
    }

    [Fact]
    public void A_texture_is_decoded_reduced_and_uploaded() {
        using var editor = EditorSession.Start();

        var crate = Paint(editor, "Assets/Textures/crate.png", 128, 64, static (_, _) => 200);
        var surface = new Recording();
        var cache = new ThumbnailCache(editor.Project) { Surface = surface };

        Assert.False(cache.TryGet(crate, out _), "a picture existed before anything decoded one");
        Assert.True(Settle(cache, () => surface.Uploads.Count > 0), "no thumbnail was uploaded");

        var uploaded = Assert.Single(surface.Uploads);

        // ⚠ Reduced to fit the box with its aspect kept — 128×64 is twice as wide as it is tall, so
        // it comes out 64×32 rather than squashed into a square.
        Assert.Equal(ThumbnailCache.Size, uploaded.Width);
        Assert.Equal(ThumbnailCache.Size / 2, uploaded.Height);
        Assert.Equal(uploaded.Width * uploaded.Height * 4, uploaded.Pixels.Length);

        // And it is the shade that was painted: a box filter over one colour is that colour, which
        // is the assertion that the reduction reads the source rather than inventing a picture.
        Assert.Equal(200, uploaded.Pixels[0]);
        Assert.Equal(255, uploaded.Pixels[3]);

        // The second ask is answered from the cache rather than decoded again.
        Assert.True(cache.TryGet(crate, out var image));
        Assert.NotEqual(0ul, image);
        Assert.Single(surface.Uploads);
    }

    /// <summary>
    ///     ⚠ Nearest-sampling a large texture takes one pixel in sixteen, so a brick wall becomes a
    ///     moiré pattern and a UI atlas becomes static. A chequerboard is what tells the two apart:
    ///     averaged it is uniformly mid-grey, sampled it is all black or all white.
    /// </summary>
    [Fact]
    public void The_reduction_averages_rather_than_samples() {
        using var editor = EditorSession.Start();

        var checks = Paint(
            editor,
            "Assets/Textures/checks.png",
            256,
            256,
            static (x, y) => (x + y) % 2 == 0 ? (byte) 255 : (byte) 0
        );

        var surface = new Recording();
        var cache = new ThumbnailCache(editor.Project) { Surface = surface };

        cache.TryGet(checks, out _);

        Assert.True(Settle(cache, () => surface.Uploads.Count > 0), "no thumbnail was uploaded");

        var reduced = surface.Uploads[0].Pixels;

        Assert.All(
            Enumerable.Range(0, reduced.Length / 4).Select(index => reduced[index * 4]),
            channel => Assert.InRange(channel, 100, 155)
        );
    }

    /// <summary>
    ///     ⚠ Without a ceiling this is a leak with a picture on it: a project of forty thousand
    ///     textures scrolled through once would hold forty thousand GPU images.
    /// </summary>
    [Fact]
    public void A_cache_that_is_full_releases_the_least_recently_wanted() {
        using var editor = EditorSession.Start();

        List<AssetId> painted = [];

        for (var index = 0; index < 5; index++) {
            painted.Add(Paint(editor, $"Assets/Textures/tile{index}.png", 8, 8, (x, _) => (byte) (x * 30)));
        }

        var surface = new Recording();
        var cache = new ThumbnailCache(editor.Project) { Surface = surface, Capacity = 3 };

        foreach (var asset in painted) {
            cache.TryGet(asset, out _);
        }

        Assert.True(Settle(cache, () => surface.Uploads.Count == 5), "not every texture decoded");

        Assert.Equal(3, cache.Count);
        Assert.Equal(2, surface.Released.Count);

        // The two that went are the two that arrived first, which for a grid being scrolled is what
        // has left the screen.
        Assert.Equal([1ul, 2ul], surface.Released);
    }

    [Fact]
    public void A_file_no_decoder_claims_is_refused_once_and_never_retried() {
        using var editor = EditorSession.Start();

        File.WriteAllText(Path.Combine(editor.ProjectRoot, "Assets", "notes.txt"), "not a picture");
        editor.Run("assets.refresh");

        var notes = editor.Project.Assets.Entries.First(entry => entry.Name == "notes.txt").Guid;
        var surface = new Recording();
        var cache = new ThumbnailCache(editor.Project) { Surface = surface };

        for (var attempt = 0; attempt < 50; attempt++) {
            Assert.False(cache.TryGet(notes, out _));
            cache.Pump();
        }

        Assert.Empty(surface.Uploads);
        Assert.Equal(0, cache.Count);
    }

    /// <summary>
    ///     ⚠ A truncated download, a file being written by another program, an extension that lies
    ///     about its contents — all ordinary, all arriving on a thread nobody is watching.
    /// </summary>
    [Fact]
    public void A_file_that_will_not_decode_is_refused_rather_than_thrown_from_a_background_task() {
        using var editor = EditorSession.Start();

        File.WriteAllBytes(Path.Combine(editor.ProjectRoot, "Assets", "broken.png"), [0x89, 0x50, 0x4E, 0x47, 1, 2]);
        editor.Run("assets.refresh");

        var broken = editor.Project.Assets.Entries.First(entry => entry.Name == "broken.png").Guid;
        var surface = new Recording();
        var cache = new ThumbnailCache(editor.Project) { Surface = surface };

        cache.TryGet(broken, out _);

        // It is refused rather than uploaded, and the editor is still standing.
        Assert.True(Settle(cache, () => !cache.TryGet(broken, out _) && surface.Uploads.Count == 0));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void With_no_surface_nothing_is_decoded_at_all() {
        using var editor = EditorSession.Start();

        var crate = Paint(editor, "Assets/Textures/crate.png", 16, 16, static (_, _) => 90);
        var cache = new ThumbnailCache(editor.Project);

        Assert.False(cache.IsAvailable);
        Assert.False(cache.TryGet(crate, out _));

        cache.Pump();

        Assert.Equal(0, cache.Count);
    }

    /// <summary>
    ///     ⚠ Null is the ordinary state on a headless run and in every test, and the grid has to draw
    ///     something rather than nothing.
    /// </summary>
    [Fact]
    public void With_no_surface_the_grid_falls_back_to_type_glyphs() {
        using var editor = EditorSession.Start();

        editor.Open("project");
        Paint(editor, "Assets/Textures/crate.png", 32, 32, static (_, _) => 10);

        Assert.Null(editor.Application.ThumbnailSurface);

        Descendants(editor.Panel("project")).OfType<ButtonBase>().First(button => button.Label == "Grid").Activate();
        editor.Settle();

        var tiles = Descendants(editor.Panel("project"))
            .OfType<AssetTile>()
            .Where(tile => !tile.HasClass("parked"))
            .ToList();

        Assert.NotEmpty(tiles);
        Assert.All(tiles, tile => Assert.True(tile.Picture.HasClass("hidden")));
        Assert.All(tiles, tile => Assert.False(tile.Glyph.HasClass("hidden")));
    }

    static IEnumerable<UiElement> Descendants(UiElement element) {
        foreach (var child in element.Children) {
            yield return child;

            foreach (var found in Descendants(child)) {
                yield return found;
            }
        }
    }

    /// <summary>An uncompressed 8-bit RGBA PNG, so the decoder has something real to read.</summary>
    /// <remarks>
    ///     ⚠ <b>A real file rather than a stub, because the decoder is half of what is under
    ///     test.</b> Stored deflate blocks keep this short and produce a file any PNG reader accepts.
    /// </remarks>
    static byte[] Png(int width, int height, byte[] rgba) {
        var raw = new byte[height * ((width * 4) + 1)];

        for (var y = 0; y < height; y++) {
            raw[y * ((width * 4) + 1)] = 0;
            Array.Copy(rgba, y * width * 4, raw, (y * ((width * 4) + 1)) + 1, width * 4);
        }

        using var file = new MemoryStream();

        file.Write([0x89, (byte) 'P', (byte) 'N', (byte) 'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];

        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 6;

        Chunk(file, "IHDR", header);
        Chunk(file, "IDAT", Deflate(raw));
        Chunk(file, "IEND", []);

        return file.ToArray();

        static void Chunk(Stream into, string kind, byte[] data) {
            Span<byte> length = stackalloc byte[4];

            BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
            into.Write(length);

            var name = System.Text.Encoding.ASCII.GetBytes(kind);
            var crc = new System.IO.Hashing.Crc32();

            crc.Append(name);
            crc.Append(data);

            into.Write(name);
            into.Write(data);

            // ⚠ Big-endian, and `GetCurrentHash` is little. A PNG whose CRC is byte-reversed is one
            // every reader refuses, which would make this fixture look like a decoder bug.
            var checksum = crc.GetCurrentHash();

            Array.Reverse(checksum);
            into.Write(checksum);
        }

        static byte[] Deflate(byte[] data) {
            using var stream = new MemoryStream();

            // A zlib wrapper around stored blocks: no compression, and every reader takes it.
            stream.WriteByte(0x78);
            stream.WriteByte(0x01);

            var offset = 0;

            do {
                var take = Math.Min(65535, data.Length - offset);
                var last = offset + take >= data.Length;

                stream.WriteByte((byte) (last ? 1 : 0));
                stream.WriteByte((byte) (take & 0xFF));
                stream.WriteByte((byte) (take >> 8));
                stream.WriteByte((byte) (~take & 0xFF));
                stream.WriteByte((byte) ((~take >> 8) & 0xFF));
                stream.Write(data, offset, take);

                offset += take;
            } while (offset < data.Length);

            uint a = 1, b = 0;

            foreach (var value in data) {
                a = (a + value) % 65521;
                b = (b + a) % 65521;
            }

            Span<byte> adler = stackalloc byte[4];

            BinaryPrimitives.WriteUInt32BigEndian(adler, (b << 16) | a);
            stream.Write(adler);

            return stream.ToArray();
        }
    }
}
