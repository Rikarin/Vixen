// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Texturing.Painting;

/// <summary>The <c>.vxpaint</c> canvases a session has open, keyed by where they are.</summary>
/// <remarks>
///     <para>
///         <b>One store because <a href="https://github.com/Rikarin/Vixen/issues/948">#948</a> and
///         <a href="https://github.com/Rikarin/Vixen/issues/885">#885</a> are one piece of work, and
///         the last batch stopped rather than do half of it.</b> A paint stroke read the whole file
///         off disk twice — <c>PaintSurface.Open</c> at pointer-down for the target and again at
///         pointer-up to put the layer's pixels back in the pane — before
///         <c>TextureExternalImages</c> read it a third time on the way to the map. At 4K that is
///         three times 67 MB of read and of allocation per channel per stroke.
///     </para>
///     <para>
///         ⚠ <b>Neither half was worth doing alone, and the design finding is why the cache is
///         <em>here</em>.</b> #885 asked for a cache in the preview's resolver and refused to write
///         one, correctly: a live session writes <c>PaintImage.Texels</c> in memory and does not
///         touch the file until pointer-up, so a pane serving its own cached copy of the file would
///         show the picture from before the stroke. A store of <em>open canvases</em> has the
///         opposite property — the session and the pane hold the same <see cref="PaintCanvas" />
///         object, so the pane cannot be stale by construction. That is the whole reason this is a
///         store rather than a cache.
///     </para>
///     <para>
///         ⚠ <b>Invalidated by the file's own <c>(LastWriteTimeUtc, Length)</c> and not by anything
///         this process knows.</b> A <c>.vxpaint</c> is a file in a project other tools touch — a
///         version-control checkout, a second editor, an artist copying one in — and the stamp is
///         what makes a canvas nobody here changed come back changed. ⚠ Read the direction of it
///         carefully: while the file is untouched the <em>in-memory</em> canvas wins, strokes and
///         all, and that is exactly #885's requirement rather than a leniency.
///     </para>
///     <para>
///         ⚠ <b>Bounded by bytes, and the entry a surface is painting into is exempt.</b> A 4K
///         canvas is 67 MB a channel, so a store that kept every canvas of a twelve-layer stack
///         would trade three reads a stroke for a gigabyte held for the session. The exemption is
///         not a refinement: without it a stack whose canvases exceed the budget could evict the one
///         under the pointer, and the next <c>PaintSurface.Open</c> would read the file and hand the
///         drag a <em>different</em> object from the one the pane is showing.
///     </para>
///     <para>
///         ⚠ <b>Not thread-safe, and it does not need to be.</b> Every route into it is a pointer
///         event, a command handler or a panel build, all of which run on the interface's one
///         thread — which is <c>Vixen.Ui</c>'s standing contract rather than an assumption made
///         here.
///     </para>
/// </remarks>
sealed class PaintCanvasStore {
    /// <summary>How many bytes of canvas are kept by default.</summary>
    /// <remarks>
    ///     ⚠ <b>256 MiB, which is one 4K canvas of three channels and a bit.</b> Sized to the working
    ///     set of a stroke rather than to a stack: what has to be resident is the canvas being
    ///     painted and whatever the map's last evaluation read, and a budget large enough for a whole
    ///     project is a budget that is never reached and therefore never tested.
    /// </remarks>
    public const long DefaultBudget = 256L * 1024L * 1024L;

    readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    readonly List<string> order = [];
    readonly long budget;

    string? pinned;

    /// <summary>A store with a byte budget.</summary>
    /// <param name="budget">
    ///     How many bytes of canvas to keep, or <see cref="DefaultBudget" />. ⚠ A budget smaller than
    ///     one canvas does not refuse to hold it: the entry just opened is never the one evicted, or
    ///     opening a canvas would be a way of not having it.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">The budget is not positive.</exception>
    public PaintCanvasStore(long budget = DefaultBudget) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budget);

        this.budget = budget;
    }

    /// <summary>How many times a canvas was read off the disk over this store's life.</summary>
    /// <remarks>
    ///     ⚠ <b>The number #948 is about, and it is a counter rather than a wall clock for this
    ///     repository's usual reason.</b> A stroke that reads the file three times and a stroke that
    ///     reads it once produce the same picture and the same file, so the picture cannot be the
    ///     test — and a millisecond budget calibrated on an idle machine is this repository's largest
    ///     single flake source.
    /// </remarks>
    public int Reads { get; private set; }

    /// <summary>How many times an open canvas answered instead.</summary>
    /// <remarks>
    ///     Kept beside <see cref="Reads" /> because "one read" is also what a store that was never
    ///     consulted a second time reports. The two together say which.
    /// </remarks>
    public int Hits { get; private set; }

    /// <summary>How many canvases are open.</summary>
    public int Count => entries.Count;

    /// <summary>How many bytes of texels they hold.</summary>
    public long Bytes { get; private set; }

    /// <summary>The canvas at a path, from memory when it is open and from the disk when it is not.</summary>
    /// <param name="absolute">Where the <c>.vxpaint</c> is, absolute.</param>
    /// <returns>The canvas, or <see langword="null" /> when there is no such file and none is open.</returns>
    /// <exception cref="ArgumentException"><paramref name="absolute" /> is blank.</exception>
    /// <exception cref="IOException">The file would not open.</exception>
    /// <exception cref="InvalidDataException">It is not a <c>.vxpaint</c> this build can read.</exception>
    /// <remarks>
    ///     ⚠ <b>Every failure is thrown rather than returned, which is the opposite of the rule the
    ///     callers follow — and it is deliberate.</b> <c>PaintSurface.Open</c> and
    ///     <c>TextureExternalImages</c> each turn a failure into a different sentence, naming the
    ///     layer and the relative path an artist wrote; a store that returned a message would be
    ///     writing one of those sentences for both, in a type that knows neither.
    /// </remarks>
    public PaintCanvas? Open(string absolute) {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolute);

        var stamp = Stamp(absolute);

        if (entries.TryGetValue(absolute, out var open) && open.Stamp == stamp) {
            Hits++;
            Touch(absolute);

            return open.Canvas;
        }

        if (stamp is null) {
            // Never on disk and nothing open for it: this is a layer whose first stroke has not
            // happened. `Adopt` is how the canvas that stroke creates gets in here.
            Forget(absolute);

            return null;
        }

        PaintCanvas canvas;

        using (var stream = File.OpenRead(absolute)) {
            canvas = PaintCanvas.Read(stream);
        }

        Reads++;

        // ⚠ Stamped from *before* the read rather than after it. A file rewritten while it was being
        // read would otherwise be recorded under the state it ended in, and the half-old canvas this
        // read produced would be served as current for the rest of the session.
        Put(absolute, canvas, stamp);

        return canvas;
    }

    /// <summary>Puts a canvas that is not on disk yet into the store.</summary>
    /// <param name="absolute">Where it will be written.</param>
    /// <param name="canvas">It.</param>
    /// <exception cref="ArgumentNullException">The canvas is null.</exception>
    /// <exception cref="ArgumentException">The path is blank.</exception>
    /// <remarks>
    ///     ⚠ <b>A paint layer's first stroke creates its canvas in memory, and without this the
    ///     second <c>PaintSurface.Open</c> of that same drag would create a second one.</b> The
    ///     stroke would then land in whichever of the two the pointer happened to be holding and the
    ///     pane would show the other.
    /// </remarks>
    public void Adopt(string absolute, PaintCanvas canvas) {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolute);
        ArgumentNullException.ThrowIfNull(canvas);

        Put(absolute, canvas, Stamp(absolute));
    }

    /// <summary>Records that the canvas at a path has just been written, so it stays current.</summary>
    /// <param name="absolute">Where it was written.</param>
    /// <exception cref="ArgumentException">The path is blank.</exception>
    /// <remarks>
    ///     ⚠ <b>Without this every save invalidates the thing it saved.</b> The write moves
    ///     <c>LastWriteTimeUtc</c> and <c>Length</c>, so the open canvas — which is the canvas those
    ///     bytes came from — would fail its own stamp and be re-read off the disk on the very next
    ///     evaluation. That is #948's third read, restored by the fix for the first two.
    /// </remarks>
    public void Saved(string absolute) {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolute);

        if (!entries.TryGetValue(absolute, out var open)) {
            return;
        }

        // ⚠ The size is re-measured here and nowhere else, because a canvas grows a channel the
        // first time a layer is painted into one — `PaintCanvas.Channel` adds on demand. A store
        // that only ever measured at insertion would run a budget against the size a canvas had
        // when it was empty. A save is when a canvas's channels have settled, so it is the moment
        // to ask.
        Bytes += Size(open.Canvas) - open.Bytes;

        entries[absolute] = open with { Stamp = Stamp(absolute), Bytes = Size(open.Canvas) };

        Evict(absolute);
    }

    /// <summary>Keeps one canvas whatever the budget says, or nothing.</summary>
    /// <param name="absolute">The path to hold, or <see langword="null" /> to hold none.</param>
    /// <remarks>
    ///     ⚠ <b>What is pinned is the canvas a <c>PaintSurface</c> is painting into, and there is at
    ///     most one.</b> A drag holds a <see cref="PaintCanvas" /> for as long as the pointer is
    ///     down; an eviction of that entry would not lose the strokes — the surface holds the object
    ///     — but the next open would read the file and produce a <em>second</em> canvas for the same
    ///     layer, which is the divergence this whole type exists to remove.
    /// </remarks>
    public void Pin(string? absolute) => pinned = absolute;

    /// <summary>Drops one canvas.</summary>
    /// <param name="absolute">Its path.</param>
    /// <returns>Whether there was one.</returns>
    public bool Forget(string absolute) {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolute);

        if (!entries.Remove(absolute, out var open)) {
            return false;
        }

        Bytes -= open.Bytes;
        order.Remove(absolute);

        return true;
    }

    /// <summary>Drops every canvas.</summary>
    /// <remarks>
    ///     ⚠ <b>Called when the module goes and when the open stack is replaced, because a canvas is
    ///     the largest thing this plugin ever allocates.</b> <c>TexturingModule.Deactivate</c> says
    ///     the same of the one surface it holds; this is that decision for the rest of them.
    /// </remarks>
    public void Clear() {
        entries.Clear();
        order.Clear();

        Bytes = 0;
        pinned = null;
    }

    void Put(string absolute, PaintCanvas canvas, (DateTime Written, long Length)? stamp) {
        Forget(absolute);

        var size = Size(canvas);

        entries[absolute] = new(canvas, stamp, size);
        order.Add(absolute);
        Bytes += size;

        Evict(absolute);
    }

    /// <summary>Drops the least recently opened canvases until the budget is met.</summary>
    /// <param name="keep">The entry that was just opened, which is never the one to go.</param>
    void Evict(string keep) {
        for (var index = 0; index < order.Count && Bytes > budget; index++) {
            var candidate = order[index];

            if (string.Equals(candidate, keep, StringComparison.Ordinal)
                || string.Equals(candidate, pinned, StringComparison.Ordinal)) {
                continue;
            }

            Forget(candidate);
            index--;
        }
    }

    void Touch(string absolute) {
        order.Remove(absolute);
        order.Add(absolute);
    }

    /// <summary>What the file looks like now, or nothing when there is none.</summary>
    /// <remarks>
    ///     ⚠ <b>Both halves, because either alone is a stamp a real edit slips past.</b> A painting
    ///     rewritten inside a file system's timestamp resolution keeps its <c>LastWriteTimeUtc</c>,
    ///     and a stroke that changes texels without changing the compressed length is ordinary rather
    ///     than contrived — a <c>.vxpaint</c> is Deflate over a fixed-size buffer.
    /// </remarks>
    static (DateTime Written, long Length)? Stamp(string absolute) {
        FileInfo file = new(absolute);

        return file.Exists ? (file.LastWriteTimeUtc, file.Length) : null;
    }

    static long Size(PaintCanvas canvas) =>
        (long)canvas.Width * canvas.Height * PaintImage.BytesPerTexel * Math.Max(1, canvas.Channels.Count);

    /// <summary>One open canvas, what the file looked like when it arrived, and what it costs.</summary>
    /// <param name="Canvas">The canvas, which a live session may be writing into.</param>
    /// <param name="Stamp">The file's state when this was read, or null when it was never on disk.</param>
    /// <param name="Bytes">What it was measured at, so <see cref="Forget" /> gives back what it took.</param>
    readonly record struct Entry(PaintCanvas Canvas, (DateTime Written, long Length)? Stamp, long Bytes);
}
