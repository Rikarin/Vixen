// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Editor.Texturing.Painting;
using Vixen.Graphics;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>The store of open canvases: one read where there were three, and never a stale one.</summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/948">#948</a> and
///         <a href="https://github.com/Rikarin/Vixen/issues/885">#885</a>, which are one piece of
///         work.</b> The reads are asserted end to end in <see cref="PaintCanvasStoreWiringTests" />,
///         through a real drag; what is here is the store's own contract, which is where the design
///         decision lives — an <em>open canvas</em> rather than a cached file, so that a session
///         painting in memory and a pane resolving a plan hold the same object.
///     </para>
///     <para>
///         ⚠ <b>Every assertion is about a counter or an object identity, never about pixels being
///         equal.</b> A store that re-read the file on every call would produce equal pixels
///         throughout — that is exactly the defect — so pixels cannot be the test. Where a texel is
///         read at all it is one written <em>after</em> the file, which no re-read could produce.
///     </para>
/// </remarks>
public class PaintCanvasStoreTests : IDisposable {
    readonly string folder = Path.Combine(Path.GetTempPath(), "vixen-tests", Guid.NewGuid().ToString("N"));

    /// <summary>Makes the throwaway folder the canvases are written into.</summary>
    public PaintCanvasStoreTests() => Directory.CreateDirectory(folder);

    /// <inheritdoc />
    public void Dispose() {
        GC.SuppressFinalize(this);

        try {
            Directory.Delete(folder, recursive: true);
        } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) {
            // A test that cannot tidy its own temporary folder has still said what it had to say.
        }
    }

    /// <summary>A second open of an untouched file is the same canvas and no second read.</summary>
    [Fact]
    public void An_untouched_file_is_read_once_however_often_it_is_opened() {
        var file = Written("Hull.vxpaint", 16, 16);

        PaintCanvasStore store = new();

        var first = store.Open(file);
        var second = store.Open(file);
        var third = store.Open(file);

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Same(first, third);

        Assert.Equal(1, store.Reads);
        Assert.Equal(2, store.Hits);
    }

    /// <summary>⚠ The open canvas wins over the file, strokes and all, while the file is untouched.</summary>
    /// <remarks>
    ///     <b>This is <a href="https://github.com/Rikarin/Vixen/issues/885">#885</a>'s whole design
    ///     finding, said as a test.</b> That issue asked for a cache in the layers pane's resolver
    ///     and refused to write one, because a session writes <c>PaintImage.Texels</c> in memory and
    ///     does not touch the file until pointer-up — so a pane serving its own cached copy of the
    ///     <em>file</em> would show the picture from before the stroke. A store of open canvases has
    ///     the opposite property, and the texel below is one that exists in no file anywhere.
    /// </remarks>
    [Fact]
    public void A_canvas_painted_in_memory_is_what_the_next_open_gets() {
        var file = Written("Hull.vxpaint", 16, 16);

        PaintCanvasStore store = new();

        var open = store.Open(file);

        Assert.NotNull(open);
        Assert.Equal(0u, open.Channel("baseColor").At(4, 4));

        // The stroke: in memory, and deliberately not saved.
        open.Channel("baseColor")[(4 * 16) + 4] = 0xFF3366CCu;

        var again = store.Open(file);

        Assert.NotNull(again);
        Assert.Equal(0xFF3366CCu, again.Channel("baseColor").At(4, 4));
        Assert.Equal(1, store.Reads);
    }

    /// <summary>⚠ And a file somebody else rewrote wins over the open canvas.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The other direction, and without it the store would be a way of never seeing an
    ///         edit.</b> A <c>.vxpaint</c> is a file in a project other things touch — a
    ///         version-control checkout, a second editor, an artist copying one in — and a store keyed
    ///         only by path would hold whatever it read first for the session.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The rewrite changes the file's <em>length</em> rather than only its content</b>,
    ///         because a test that relied on <c>LastWriteTimeUtc</c> moving would be a test of the
    ///         file system's timestamp resolution — which on a fast machine writing twice in a
    ///         millisecond is the thing that would make it flake.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_file_rewritten_underneath_is_read_again() {
        var file = Written("Hull.vxpaint", 16, 16);

        PaintCanvasStore store = new();

        var open = store.Open(file);

        Assert.NotNull(open);

        open.Channel("baseColor")[0] = 0xFF3366CCu;

        // Somebody else wrote it: a second channel, so the length moves whatever the clock did.
        PaintCanvas replacement = new(16, 16);

        replacement.Channel("baseColor").Fill(0xFF102030u);
        replacement.Channel("roughness").Fill(0xFF404040u);

        using (var stream = File.Create(file)) {
            replacement.Write(stream);
        }

        var reread = store.Open(file);

        Assert.NotNull(reread);
        Assert.NotSame(open, reread);
        Assert.Equal(2, store.Reads);
        Assert.Equal(0xFF102030u, reread.Channel("baseColor").At(0, 0));
    }

    /// <summary>⚠ Saving a canvas keeps it open rather than invalidating it.</summary>
    /// <remarks>
    ///     <b>Without this the fix for the first two reads restores the third.</b> A save moves the
    ///     file's <c>LastWriteTimeUtc</c> and <c>Length</c>, which is exactly what the store keys
    ///     staleness on — so the canvas those bytes came from would fail its own stamp and be read
    ///     back off the disk on the next evaluation, which for a stroke is immediately.
    /// </remarks>
    [Fact]
    public void A_saved_canvas_stays_open_rather_than_failing_its_own_stamp() {
        var file = Path.Combine(folder, "Hull.vxpaint");

        PaintCanvasStore store = new();
        PaintCanvas canvas = new(16, 16);

        canvas.Channel("baseColor").Fill(0xFF3366CCu);
        store.Adopt(file, canvas);

        // What `PaintSurface.Save` does, in the order it does it.
        using (var stream = File.Create(file)) {
            canvas.Write(stream);
        }

        store.Saved(file);

        Assert.Same(canvas, store.Open(file));
        Assert.Equal(0, store.Reads);
    }

    /// <summary>A canvas that is not on disk yet is served, and one that never existed is not.</summary>
    /// <remarks>
    ///     ⚠ <b>The first half is a behaviour change and the point of it.</b> Before the store,
    ///     <c>TextureExternalImages</c> refused a <c>vxpaint:</c> whose file did not exist — which is
    ///     every paint layer whose first stroke is still under the pointer. The second half is what
    ///     stops that becoming "a missing canvas is silently empty".
    /// </remarks>
    [Fact]
    public void A_canvas_with_no_file_yet_is_served_and_one_that_was_never_opened_is_not() {
        var file = Path.Combine(folder, "Unwritten.vxpaint");

        PaintCanvasStore store = new();

        Assert.Null(store.Open(file));

        PaintCanvas canvas = new(8, 8);

        store.Adopt(file, canvas);

        Assert.Same(canvas, store.Open(file));
        Assert.False(File.Exists(file), "the store wrote the file, which is the surface's job and not its");
        Assert.Equal(0, store.Reads);
    }

    /// <summary>⚠ The budget drops the least recently opened canvas, and never the pinned one.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The exemption is not a refinement.</b> A drag holds its <c>PaintCanvas</c> for as
    ///         long as the pointer is down; evicting that entry would not lose the strokes — the
    ///         surface holds the object — but the next <c>PaintSurface.Open</c> would read the file
    ///         and hand the drag a <em>second</em> canvas for the same layer, and the pane and the
    ///         stroke would then be looking at different ones. That is the divergence the whole type
    ///         exists to remove, reintroduced by its own memory bound.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The budget is a constructor parameter for exactly this test.</b> Reaching the
    ///         256 MiB default means allocating 256 MiB, so a store whose bound could not be made
    ///         small would have a bound nothing ever ran — which is this repository's
    ///         "measure on the hard case" the other way round.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_budget_evicts_the_oldest_canvas_and_spares_the_pinned_one() {
        var pinned = Written("Pinned.vxpaint", 16, 16);
        var middle = Written("Middle.vxpaint", 16, 16);
        var newest = Written("Newest.vxpaint", 16, 16);

        // One channel of 16² is 1024 bytes, so a budget of two and a half canvases holds two.
        PaintCanvasStore store = new(2560);

        var held = store.Open(pinned);

        store.Pin(pinned);

        Assert.NotNull(held);
        Assert.NotNull(store.Open(middle));
        Assert.NotNull(store.Open(newest));

        Assert.Equal(2, store.Count);
        Assert.Equal(3, store.Reads);

        // The pinned one is still open — the same object, which is the thing that matters — and the
        // one in the middle is what went.
        Assert.Same(held, store.Open(pinned));
        Assert.Equal(3, store.Reads);

        Assert.NotNull(store.Open(middle));
        Assert.Equal(4, store.Reads);
    }

    /// <summary>Clearing drops everything, which is what a module going away does.</summary>
    [Fact]
    public void Clearing_gives_back_every_canvas() {
        var file = Written("Hull.vxpaint", 16, 16);

        PaintCanvasStore store = new();

        Assert.NotNull(store.Open(file));
        Assert.Equal(1, store.Count);
        Assert.True(store.Bytes > 0);

        store.Clear();

        Assert.Equal(0, store.Count);
        Assert.Equal(0L, store.Bytes);

        Assert.NotNull(store.Open(file));
        Assert.Equal(2, store.Reads);
    }

    /// <summary>An imported picture is decoded once however often a plan's externals ask for it.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The last bullet of <a href="https://github.com/Rikarin/Vixen/issues/885">#885</a>,
    ///         which was left owed on the argument that an imported picture is a different problem
    ///         from a canvas.</b> It is the same problem with one half unused: the key, the stamp and
    ///         the budget are shared, and only the in-memory writer is missing. Before this,
    ///         <c>TextureExternalImages</c> re-decoded a texture-fill layer's PNG once per
    ///         evaluation, and a preview evaluates on every edit.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The decode is counted in the callback rather than inferred from
    ///         <c>Reads</c>.</b> A store that answered every call by decoding would report the same
    ///         <em>picture</em> as one that answered from memory — that is the defect — so what is
    ///         asserted is how many times the file was actually opened, and separately that the
    ///         store's own counters agree with it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_imported_picture_is_decoded_once_however_often_it_is_asked_for() {
        var file = Path.Combine(folder, "Rust.png");

        File.WriteAllBytes(file, [1, 2, 3, 4]);

        PaintCanvasStore store = new();

        var decodes = 0;

        TextureData Decode(string path) {
            decodes++;

            // Opened, so that a missing file is a throw here exactly as a real decoder's would be.
            using var stream = File.OpenRead(path);

            return new(PixelFormat.Rgba8UNorm, 2, 2, levelCount: 1);
        }

        var first = store.Picture(file, Decode);
        var second = store.Picture(file, Decode);
        var third = store.Picture(file, Decode);

        Assert.Equal(1, decodes);
        Assert.Same(first, second);
        Assert.Same(first, third);

        Assert.Equal(1, store.Reads);
        Assert.Equal(2, store.Hits);
        Assert.Equal(3, store.Opens);
    }

    /// <summary>⚠ And a picture somebody else rewrote is decoded again.</summary>
    /// <remarks>
    ///     <b>The same stamp as a canvas's, and it has to be: an imported picture is the one an
    ///     artist edits in another application while the editor is open.</b> The rewrite changes the
    ///     file's length rather than only its content, for
    ///     <see cref="A_file_rewritten_underneath_is_read_again" />'s reason — a test that relied on
    ///     the clock moving would be a test of the file system's timestamp resolution.
    /// </remarks>
    [Fact]
    public void A_picture_rewritten_underneath_is_decoded_again() {
        var file = Path.Combine(folder, "Rust.png");

        File.WriteAllBytes(file, [1, 2, 3, 4]);

        PaintCanvasStore store = new();

        var first = store.Picture(file, Decoded);

        File.WriteAllBytes(file, [1, 2, 3, 4, 5, 6, 7, 8]);

        var second = store.Picture(file, Decoded);

        Assert.NotSame(first, second);
        Assert.Equal(2, store.Reads);
        Assert.Equal(0, store.Hits);
    }

    /// <summary>⚠ A picture with no file is handed back and not held, which is the asymmetry with a canvas.</summary>
    /// <remarks>
    ///     <b>An adopted canvas with no file is a layer whose first stroke is under the pointer, and
    ///     serving it is the whole point of <c>Adopt</c>.</b> There is no such thing for an imported
    ///     picture: nothing in this process writes one, so an entry with no file could only be one
    ///     nothing can ever invalidate — and it would be served as current for the rest of the
    ///     session. So a decoder that produced something anyway is believed once and forgotten.
    /// </remarks>
    [Fact]
    public void A_picture_the_store_cannot_stamp_is_never_held() {
        var file = Path.Combine(folder, "Gone.png");

        PaintCanvasStore store = new();

        var decodes = 0;

        TextureData Decode(string path) {
            decodes++;

            return new(PixelFormat.Rgba8UNorm, 2, 2, levelCount: 1);
        }

        Assert.NotNull(store.Picture(file, Decode));
        Assert.Equal(0, store.Count);
        Assert.Equal(0L, store.Bytes);

        Assert.NotNull(store.Picture(file, Decode));
        Assert.Equal(2, decodes);
        Assert.Equal(0, store.Hits);
    }

    /// <summary>Decodes a two-by-two picture, opening the file the way a real decoder would.</summary>
    static TextureData Decoded(string path) {
        using var stream = File.OpenRead(path);

        return new(PixelFormat.Rgba8UNorm, 2, 2, levelCount: 1);
    }

    /// <summary>Writes a one-channel canvas into the throwaway folder and returns its absolute path.</summary>
    string Written(string name, int width, int height) {
        var file = Path.Combine(folder, name);

        PaintCanvas canvas = new(width, height);

        canvas.Channel("baseColor");

        using var stream = File.Create(file);

        canvas.Write(stream);

        return file;
    }
}
