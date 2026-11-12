// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Graphics;
using Vixen.Texturing.Painting;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>The store of open canvases: one read where there were three, and never a stale one.</summary>
/// <remarks>
///     <para>
///         <b>
///             <a href="https://github.com/Rikarin/Vixen/issues/948">#948</a> and
///             <a href="https://github.com/Rikarin/Vixen/issues/885">#885</a>, which are one piece of
///             work.
///         </b>
///         The reads are asserted end to end in <see cref="PaintCanvasStoreWiringTests" />,
///         through a real drag; what is here is the store's own contract, which is where the design
///         decision lives — an <em>open canvas</em> rather than a cached file, so that a session
///         painting in memory and a pane resolving a plan hold the same object.
///     </para>
///     <para>
///         ⚠
///         <b>
///             Every assertion is about a counter or an object identity, never about pixels being
///             equal.
///         </b>
///         A store that re-read the file on every call would produce equal pixels
///         throughout — that is exactly the defect — so pixels cannot be the test. Where a texel is
///         read at all it is one written <em>after</em> the file, which no re-read could produce.
///     </para>
/// </remarks>
public class PaintCanvasStoreTests : IDisposable {
    /// <summary>The moment the same-tick cases stamp their files with and stop their clock at.</summary>
    static readonly DateTime Tick = new(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);

    readonly string folder = Path.Combine(Path.GetTempPath(), "vixen-tests", Guid.NewGuid().ToString("N"));

    /// <summary>Makes the throwaway folder the canvases are written into.</summary>
    public PaintCanvasStoreTests() {
        Directory.CreateDirectory(folder);
    }

    /// <inheritdoc />
    public void Dispose() {
        GC.SuppressFinalize(this);

        try {
            Directory.Delete(folder, true);
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
    ///     <b>
    ///         This is <a href="https://github.com/Rikarin/Vixen/issues/885">#885</a>'s whole design
    ///         finding, said as a test.
    ///     </b>
    ///     That issue asked for a cache in the layers pane's resolver
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
        open.Channel("baseColor")[4 * 16 + 4] = 0xFF3366CCu;

        var again = store.Open(file);

        Assert.NotNull(again);
        Assert.Equal(0xFF3366CCu, again.Channel("baseColor").At(4, 4));
        Assert.Equal(1, store.Reads);
    }

    /// <summary>⚠ And a file somebody else rewrote wins over the open canvas.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>
    ///             The other direction, and without it the store would be a way of never seeing an
    ///             edit.
    ///         </b>
    ///         A <c>.vxpaint</c> is a file in a project other things touch — a
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

    /// <summary>⚠ A file rewritten in the same tick and at the same length is read again.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>
    ///             The rewrite <see cref="A_file_rewritten_underneath_is_read_again" /> steers clear
    ///             of, and the one Windows CI found anyway
    ///         </b>
    ///         — Vixen run 42670028234, where
    ///         <c>TextureExternalPassTests</c> rewrote a canvas inside one tick of NTFS's clock and
    ///         its "different size" turned out to be the same length. A checkout or a second editor
    ///         landing in the tick of a read is that, with an artist's work on the line.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             Both halves of the stamp are forced, so the store has nothing but the bytes to go
    ///             on.
    ///         </b>
    ///         The new canvas is a different colour of the same layout, which is a different
    ///         file of the same length; the time is set back by hand; the clock stands at it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_file_rewritten_in_the_same_tick_at_the_same_length_is_read_again() {
        var file = Filled("Hull.vxpaint", 0xFF3366CCu);

        PaintCanvasStore store = new(PaintCanvasStore.DefaultBudget, new StoppedClock(Tick));

        var open = store.Open(file);

        Assert.NotNull(open);

        var length = new FileInfo(file).Length;

        Filled("Hull.vxpaint", 0xFF102030u);

        Assert.Equal(length, new FileInfo(file).Length);
        Assert.Equal(Tick, File.GetLastWriteTimeUtc(file));

        var reread = store.Open(file);

        Assert.NotNull(reread);
        Assert.NotSame(open, reread);
        Assert.Equal(2, store.Reads);
        Assert.Equal(0xFF102030u, reread.Channel("baseColor").At(0, 0));
    }

    /// <summary>⚠ A young stamp is checked against the bytes until it is old, and then trusted alone.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>
    ///             Both halves of the cost, and the second is the one that makes the first
    ///             affordable.
    ///         </b>
    ///         While a file is younger than the file system could stamp apart, every
    ///         ask reads it; once it is older, a later write must have moved the time, so the stamp
    ///         alone answers and a settled stack costs no reads at all.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The rewrite in the middle is made while the stamp is young and asked about only
    ///             after it is old
    ///         </b>
    ///         , which is the order that catches a store deciding by the clock
    ///         alone: the digest it took when the file was young is still owed a comparison. The
    ///         last rewrite, made after the stamp was trusted, is unseen — and that is the only way
    ///         to observe a read <em>not</em> happening.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_young_stamp_is_checked_against_the_bytes_until_it_is_old_and_then_trusted() {
        var file = Filled("Hull.vxpaint", 0xFF3366CCu);

        StoppedClock clock = new(Tick);
        PaintCanvasStore store = new(PaintCanvasStore.DefaultBudget, clock);

        var young = store.Open(file);

        Filled("Hull.vxpaint", 0xFF102030u);
        clock.At = Tick + TimeSpan.FromMinutes(1);

        var reread = store.Open(file);

        Assert.NotNull(reread);
        Assert.NotSame(young, reread);
        Assert.Equal(0xFF102030u, reread.Channel("baseColor").At(0, 0));

        Filled("Hull.vxpaint", 0xFF405060u);

        Assert.Same(reread, store.Open(file));
        Assert.Equal(2, store.Reads);
    }

    /// <summary>⚠ A canvas saved and then rewritten in the same tick is read again.</summary>
    /// <remarks>
    ///     <b>A save is always young</b>, so the stamp <see cref="PaintCanvasStore.Saved" /> records
    ///     is the one most exposed to a rewrite that keeps it — a checkout landing just after a
    ///     stroke. The store digests what the save wrote, and the next ask compares.
    /// </remarks>
    [Fact]
    public void A_canvas_saved_and_rewritten_in_the_same_tick_is_read_again() {
        var file = Path.Combine(folder, "Hull.vxpaint");

        PaintCanvasStore store = new(PaintCanvasStore.DefaultBudget, new StoppedClock(Tick));
        PaintCanvas canvas = new(16, 16);

        canvas.Channel("baseColor").Fill(0xFF3366CCu);
        store.Adopt(file, canvas);

        using (var stream = File.Create(file)) {
            canvas.Write(stream);
        }

        File.SetLastWriteTimeUtc(file, Tick);
        store.Saved(file);

        Assert.Same(canvas, store.Open(file));

        Filled("Hull.vxpaint", 0xFF102030u);

        var reread = store.Open(file);

        Assert.NotNull(reread);
        Assert.NotSame(canvas, reread);
        Assert.Equal(0xFF102030u, reread.Channel("baseColor").At(0, 0));
    }

    /// <summary>⚠ A young stamp whose bytes cannot be read answers by the stamp, as before.</summary>
    /// <remarks>
    ///     <b>
    ///         The digest is a check added to the stamp, and a check that cannot run is not evidence
    ///         of a change.
    ///     </b>
    ///     The file is there with the stamp it had, so a read that fails is another
    ///     program holding it — and a miss would read it too, fail the same way, and throw away the
    ///     open canvas a pinned surface is painting into. The file is made unreadable through its
    ///     Unix mode, which is how this runs off Windows; on Windows the same is a sharing violation,
    ///     which no portable fixture can hold open across the call.
    /// </remarks>
    [Fact]
    public void A_young_stamp_that_cannot_be_read_back_is_answered_by_the_stamp() {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The file is made unreadable through its Unix mode.");

        // The analyser's guard: it does not read the skip above as one.
        if (OperatingSystem.IsWindows()) {
            return;
        }

        var file = Filled("Hull.vxpaint", 0xFF3366CCu);

        PaintCanvasStore store = new(PaintCanvasStore.DefaultBudget, new StoppedClock(Tick));

        var open = store.Open(file);

        File.SetUnixFileMode(file, UnixFileMode.None);

        try {
            Assert.Same(open, store.Open(file));
        } finally {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
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
    ///         <b>
    ///             The last bullet of <a href="https://github.com/Rikarin/Vixen/issues/885">#885</a>,
    ///             which was left owed on the argument that an imported picture is a different problem
    ///             from a canvas.
    ///         </b>
    ///         It is the same problem with one half unused: the key, the stamp and
    ///         the budget are shared, and only the in-memory writer is missing. Before this,
    ///         <c>TextureExternalImages</c> re-decoded a texture-fill layer's PNG once per
    ///         evaluation, and a preview evaluates on every edit.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The decode is counted in the callback rather than inferred from
    ///             <c>Reads</c>.
    ///         </b>
    ///         A store that answered every call by decoding would report the same
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

            return new(PixelFormat.Rgba8UNorm, 2, 2, 1);
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
    ///     <b>
    ///         The same stamp as a canvas's, and it has to be: an imported picture is the one an
    ///         artist edits in another application while the editor is open.
    ///     </b>
    ///     The rewrite changes the
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

    /// <summary>⚠ And a picture rewritten in the same tick at the same length is decoded again.</summary>
    /// <remarks>
    ///     <b>
    ///         The same rule as <see cref="A_file_rewritten_in_the_same_tick_at_the_same_length_is_read_again" />
    ///         through the other payload
    ///     </b>
    ///     , and the one an artist meets most: saving a texture in a
    ///     paint program over itself keeps its dimensions, and an uncompressed format keeps its length.
    /// </remarks>
    [Fact]
    public void A_picture_rewritten_in_the_same_tick_at_the_same_length_is_decoded_again() {
        var file = Path.Combine(folder, "Rust.png");

        File.WriteAllBytes(file, [1, 2, 3, 4]);
        File.SetLastWriteTimeUtc(file, Tick);

        PaintCanvasStore store = new(PaintCanvasStore.DefaultBudget, new StoppedClock(Tick));

        var first = store.Picture(file, Decoded);

        File.WriteAllBytes(file, [5, 6, 7, 8]);
        File.SetLastWriteTimeUtc(file, Tick);

        var second = store.Picture(file, Decoded);

        Assert.NotSame(first, second);
        Assert.Equal(2, store.Reads);
        Assert.Equal(0, store.Hits);
    }

    /// <summary>⚠ A picture with no file is handed back and not held, which is the asymmetry with a canvas.</summary>
    /// <remarks>
    ///     <b>
    ///         An adopted canvas with no file is a layer whose first stroke is under the pointer, and
    ///         serving it is the whole point of <c>Adopt</c>.
    ///     </b>
    ///     There is no such thing for an imported
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

            return new(PixelFormat.Rgba8UNorm, 2, 2, 1);
        }

        Assert.NotNull(store.Picture(file, Decode));
        Assert.Equal(0, store.Count);
        Assert.Equal(0L, store.Bytes);

        Assert.NotNull(store.Picture(file, Decode));
        Assert.Equal(2, decodes);
        Assert.Equal(0, store.Hits);
    }

    /// <summary>⚠ And a held picture whose file has since gone is dropped, not charged for the session.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>
    ///             <a href="https://github.com/Rikarin/Vixen/issues/995">#995</a>, and it is the one
    ///             path where the decode cannot succeed.
    ///         </b>
    ///         A deleted file fails its own stamp, so the
    ///         entry is a miss and the decoder is called — and a real decoder opens the file, so it
    ///         throws. The <c>Forget</c> that handles an unstampable file used to sit on the far side
    ///         of that call and therefore never ran: the picture stayed in the store, unservable,
    ///         with its bytes still counted against the budget for the rest of the session.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             Asserted on <c>Bytes</c> as well as <c>Count</c>, because the budget is what the
    ///             defect costs.
    ///         </b>
    ///         A store that dropped the entry and forgot to give its bytes back
    ///         would be the same leak with the dictionary tidied, and <c>Count</c> alone cannot see
    ///         it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_picture_whose_file_has_gone_is_dropped_rather_than_held_unservable() {
        var file = Path.Combine(folder, "Rust.png");

        File.WriteAllBytes(file, [1, 2, 3, 4]);

        PaintCanvasStore store = new();

        Assert.NotNull(store.Picture(file, Decoded));
        Assert.Equal(1, store.Count);
        Assert.NotEqual(0L, store.Bytes);

        File.Delete(file);

        Assert.Throws<FileNotFoundException>(() => store.Picture(file, Decoded));

        Assert.Equal(0, store.Count);
        Assert.Equal(0L, store.Bytes);
    }

    /// <summary>⚠ And a held canvas whose re-read throws is dropped, not left charged and unservable.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>
    ///             <a href="https://github.com/Rikarin/Vixen/issues/1001">#1001</a>, the sibling of
    ///             <a href="https://github.com/Rikarin/Vixen/issues/995">#995</a> one method along.
    ///         </b>
    ///         <c>Open</c> forgot before its read only on the branch where the file cannot be
    ///         stamped. The <em>other</em> miss is this one — an entry is held and the file has been
    ///         rewritten — and its <c>Put</c> is on the far side of the read, so a read that throws
    ///         left the entry behind with a stamp that can never match again: the bytes it was
    ///         stamped from are gone. Unservable for the rest of the session, and still counted
    ///         against the budget.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The rewrite is garbage of a different length, which is both halves of the
    ///             fixture at once.
    ///         </b>
    ///         The length is what moves the stamp without depending on the file
    ///         system's timestamp resolution, and the missing magic is what makes
    ///         <c>PaintCanvas.Read</c> throw — a file that stamps and does not decode is the cheaper
    ///         half of the pair the issue named, since the other needs a read to fail on a file that
    ///         exists.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Asserted on <c>Bytes</c> as well as <c>Count</c>.</b> A store that dropped the
    ///         entry and kept its bytes charged would be the same leak with the dictionary tidied,
    ///         and the budget is what the defect actually costs.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_canvas_whose_re_read_throws_is_dropped_rather_than_held_unservable() {
        var file = Written("Hull.vxpaint", 16, 16);

        PaintCanvasStore store = new();

        Assert.NotNull(store.Open(file));
        Assert.Equal(1, store.Count);
        Assert.NotEqual(0L, store.Bytes);

        File.WriteAllBytes(file, "NOT A VXPAINT AT ALL"u8.ToArray());

        Assert.Throws<InvalidDataException>(() => store.Open(file));

        Assert.Equal(0, store.Count);
        Assert.Equal(0L, store.Bytes);
    }

    /// <summary>⚠ And the same of a picture whose file was rewritten into one the decoder refuses.</summary>
    /// <remarks>
    ///     <b>
    ///         The half of <a href="https://github.com/Rikarin/Vixen/issues/1001">#1001</a> that is
    ///         not in the issue's title, and it is why both methods now forget on every miss.
    ///     </b>
    ///     #995
    ///     covered <c>Picture</c>'s <em>deleted</em> file only, so a picture whose file was rewritten
    ///     into something the decoder throws on had exactly <c>Open</c>'s defect: the old entry
    ///     survived with the old stamp, which the new file can never match.
    /// </remarks>
    [Fact]
    public void A_picture_whose_decode_throws_is_dropped_rather_than_held_unservable() {
        var file = Path.Combine(folder, "Rust.png");

        File.WriteAllBytes(file, [1, 2, 3, 4]);

        PaintCanvasStore store = new();

        Assert.NotNull(store.Picture(file, Decoded));
        Assert.Equal(1, store.Count);
        Assert.NotEqual(0L, store.Bytes);

        File.WriteAllBytes(file, [1, 2, 3, 4, 5, 6, 7, 8]);

        Assert.Throws<InvalidDataException>(() => store.Picture(
                file,
                static _ => throw new InvalidDataException("This is not a picture this build can decode.")
            )
        );

        Assert.Equal(0, store.Count);
        Assert.Equal(0L, store.Bytes);
    }

    /// <summary>Decodes a two-by-two picture, opening the file the way a real decoder would.</summary>
    static TextureData Decoded(string path) {
        using var stream = File.OpenRead(path);

        return new(PixelFormat.Rgba8UNorm, 2, 2, 1);
    }

    /// <summary>
    ///     Writes a 16² canvas of one colour stamped at <see cref="Tick" />, so two of them differ in
    ///     their bytes and in nothing a stamp sees.
    /// </summary>
    string Filled(string name, uint colour) {
        var file = Path.Combine(folder, name);

        PaintCanvas canvas = new(16, 16);

        // A fill is the channel's background and no tiles, so every colour is the same length.
        canvas.Channel("baseColor").Fill(colour);

        using (var stream = File.Create(file)) {
            canvas.Write(stream);
        }

        File.SetLastWriteTimeUtc(file, Tick);

        return file;
    }

    /// <summary>Writes a one-channel canvas into the throwaway folder and returns its absolute path.</summary>
    string Written(string name, int width, int height) {
        var file = Path.Combine(folder, name);

        PaintCanvas canvas = new(width, height);

        // One texel, or the canvas holds no tile and costs nothing against a budget.
        canvas.Channel("baseColor")[0] = 0xFF0000FFu;

        using var stream = File.Create(file);

        canvas.Write(stream);

        return file;
    }
}
