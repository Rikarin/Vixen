// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using Vixen.Core;
using Vixen.Editor.Core;
using Vixen.Editor.Inspector;
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

        public bool Update(ulong image, int x, int y, int width, int height, ReadOnlySpan<byte> rgba) => false;

        public void Release(ulong image) => released.Add(image);
    }

    /// <summary>
    ///     How long a single decode may take before the wait calls it hung. ⚠ A hang check and not a
    ///     bound: nothing here waits for it in any run that passes, because every wait below ends when
    ///     the decode does.
    /// </summary>
    static readonly TimeSpan Hung = TimeSpan.FromMinutes(10);

    /// <summary>Pumps until what the caller is waiting for has happened, or can no longer happen.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Ordered by the work and not by a clock — the fourth shape this helper has had,
    ///         and the first with no budget in it.</b> It used to poll the caller's condition against
    ///         a turn count, then against thirty seconds, then against two minutes, and each one
    ///         failed the same way on a loaded machine: a decode queued behind other work on the pool
    ///         outlasted whatever number had been chosen on an idle one
    ///         (<a href="https://github.com/Rikarin/Vixen/issues/1407">#1407</a>). Now it blocks on
    ///         <see cref="ThumbnailCache.Decoding" />, which completes when the decodes have queued
    ///         their answers — so it waits exactly as long as the work takes, and it gives the core
    ///         it was spinning on to the pool thread doing the decode.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And it stops when nothing is in flight, which is <c>IsBusy</c> — this used to say
    ///         that was wrong, and in this code it is not.</b> The remark claimed <c>IsBusy</c> is
    ///         false "in the gap between a request being dispatched and its decode being queued".
    ///         <c>Request</c> adds to <c>pending</c> before it starts the task and only
    ///         <c>Pump</c>'s dequeue removes it, so there is no such gap: <c>IsBusy</c> false after a
    ///         <c>Pump</c> means no decode is running and none is queued, so the condition's answer
    ///         is final. A request that has not been <em>made</em> yet is the caller's to make — a
    ///         condition may ask, and <see cref="ThumbnailCache.Decoding" /> is read after it.
    ///     </para>
    ///     <para>
    ///         A failing condition therefore fails as soon as the work is done rather than after a
    ///         budget, and the only clock left is <see cref="Hung" />.
    ///     </para>
    /// </remarks>
    static bool Settle(ThumbnailCache cache, Func<bool> until) {
        while (true) {
            cache.Pump();

            if (until()) {
                return true;
            }

            if (!cache.IsBusy) {
                return false;
            }

            Await(cache.Decoding);
        }
    }

    /// <summary>Blocks until the decodes in flight have queued their answers.</summary>
    static void Await(Task decoding) {
        if (!decoding.Wait(Hung)) {
            throw new TimeoutException(
                $"A thumbnail decode ran for {Hung.TotalMinutes} minutes. That is a hang check, not a "
                + "budget: the decode is not coming back."
            );
        }
    }

    /// <summary>Writes a PNG into the project and makes the editor notice it.</summary>
    internal static AssetId Paint(EditorSession editor, string path, int width, int height, Func<int, int, byte> shade) {
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

        Assert.False(cache.TryGet(broken, out _));
        Assert.True(cache.IsBusy, "no decode was started, so nothing below is about one");

        // It is refused rather than uploaded, and the editor is still standing.
        //
        // ⚠ Waited to the end rather than asked once. This used to be a condition that held on its
        // first evaluation — nothing had been uploaded *yet* — so it returned before the decode had
        // run and asserted nothing about what the decode did with a broken file.
        Assert.False(Settle(cache, () => surface.Uploads.Count > 0), "a file that will not decode was uploaded");
        Assert.Equal(0, cache.Count);

        // And refused for good: asking again starts nothing.
        Assert.False(cache.TryGet(broken, out _));
        Assert.False(cache.IsBusy, "a refused file was decoded again");
    }

    /// <summary>⚠ A repainted file gets a new picture, and it used to keep the old one all session.</summary>
    /// <remarks>
    ///     <para>
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1187">#1187</a>. <c>ThumbnailCache</c>
    ///         removed an entry in exactly one place — <c>Evict</c>, on capacity — and nothing in the
    ///         editor ever told it a file had changed. So repainting a <c>.png</c> in another program
    ///         left the content browser and the asset picker drawing the version from before the
    ///         edit, until 512 other assets pushed it out or the editor was restarted.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The middle assertion is the instrument and it asserts the <em>defect</em>.</b>
    ///         Nothing has told the cache yet, so it still answers with the old picture — which is
    ///         what makes the last assertion about <c>Forget</c> rather than about a cache that
    ///         happened never to hold anything. Two shades neither of which is a decoder's default,
    ///         because a picture read back at 0 would pass against a path that uploaded nothing.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_repainted_file_is_forgotten_rather_than_kept_for_the_session() {
        using var editor = EditorSession.Start();

        var crate = Paint(editor, "Assets/Textures/crate.png", 16, 16, static (_, _) => 40);
        var surface = new Recording();
        var cache = new ThumbnailCache(editor.Project) { Surface = surface };

        cache.TryGet(crate, out _);

        Assert.True(Settle(cache, () => surface.Uploads.Count > 0), "no thumbnail was uploaded");
        Assert.Equal(40, surface.Uploads[0].Pixels[0]);

        Paint(editor, "Assets/Textures/crate.png", 16, 16, static (_, _) => 200);

        Assert.True(cache.TryGet(crate, out _), "the cache did not hold the picture it decoded");
        Assert.Single(surface.Uploads);

        cache.Forget();

        Assert.False(cache.TryGet(crate, out _), "the picture survived being forgotten");
        Assert.True(Settle(cache, () => surface.Uploads.Count > 1), "the repainted file was not decoded again");

        Assert.Equal(200, surface.Uploads[1].Pixels[0]);

        // And the image the old picture held is given back rather than leaked, which is the half a
        // `ready.Clear()` on its own would get wrong.
        Assert.Equal([1ul], surface.Released);
    }

    /// <summary>⚠ A decode already in flight when the file changed is dropped, not uploaded.</summary>
    /// <remarks>
    ///     <b>The half clearing the ready set cannot reach.</b> A task started before the file
    ///     changed carries the old bytes on a pool thread and lands in a later <c>Pump</c>; uploaded
    ///     there, it would be cached and drawn as the current picture and the forgetting would have
    ///     made the staleness slower rather than fixed it. ⚠ The asset comes back neither ready nor
    ///     refused, which is what lets the next look decode the file as it now is — a drop that
    ///     refused it instead would leave a type glyph in the grid for the rest of the session.
    /// </remarks>
    [Fact]
    public void A_decode_in_flight_when_the_pictures_were_forgotten_is_dropped() {
        using var editor = EditorSession.Start();

        var crate = Paint(editor, "Assets/Textures/crate.png", 16, 16, static (_, _) => 40);
        var surface = new Recording();
        var cache = new ThumbnailCache(editor.Project) { Surface = surface };

        // Requested and *not* pumped, so the decode is in flight and its answer has not been taken.
        Assert.False(cache.TryGet(crate, out _));

        cache.Forget();

        Paint(editor, "Assets/Textures/crate.png", 16, 16, static (_, _) => 200);

        // The answer comes back and is dropped. This is the assertion: without it there is one
        // upload here, of the bytes the file had before it was repainted.
        Assert.True(Settle(cache, () => !cache.IsBusy), "the decode in flight never came back");
        Assert.Empty(surface.Uploads);
        Assert.Equal(0, cache.Count);

        // And it left nothing behind — not a picture and not a refusal — so the next ask starts a
        // decode of the file as it now is.
        Assert.False(cache.TryGet(crate, out _));
        Assert.True(Settle(cache, () => surface.Uploads.Count > 0), "the repainted file was never decoded");

        Assert.Equal(200, Assert.Single(surface.Uploads).Pixels[0]);
    }

    /// <summary>⚠ A file a thumbnail is being read from can still be saved over.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/1326">#1326</a>, which
    ///         <see cref="A_decode_in_flight_when_the_pictures_were_forgotten_is_dropped" /> met by
    ///         accident on Windows</b>: its repaint landed while the decode it had left running still
    ///         held <c>File.OpenRead</c>'s handle, and a write over a file open for reading-only-sharing
    ///         is a sharing violation there. That was a test racing its own decode on a few hundredths
    ///         of a second's window; the same window is a person's paint program failing to save.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The overlap is forced, not hoped for.</b> <c>Reading</c> runs on the decode's
    ///         thread with the file open and does not return until the repaint is done, so the write
    ///         meets the open handle every time. ⚠ It is a Windows property — .NET's share modes are
    ///         advisory on Linux and macOS, so there this passes with or without the fix — which is
    ///         why the Windows leg is the one that runs it for real.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_file_being_read_for_a_thumbnail_can_still_be_saved_over() {
        using var editor = EditorSession.Start();
        using var open = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var crate = Paint(editor, "Assets/Textures/crate.png", 16, 16, static (_, _) => 40);
        var surface = new Recording();
        var cache = new ThumbnailCache(editor.Project) {
            Surface = surface,
            Reading = () => {
                open.Set();
                release.Wait();
            }
        };

        try {
            Assert.False(cache.TryGet(crate, out _));
            Assert.True(
                open.Wait(Hung, TestContext.Current.CancellationToken),
                "the decode never opened the file, which is a hang check and not a result"
            );

            // The decode has the file open now, and is waiting for this to finish. This is the line
            // that threw.
            Paint(editor, "Assets/Textures/crate.png", 16, 16, static (_, _) => 200);
        } finally {
            release.Set();
        }

        // And what it read is what is on disk now: the write was finished before the read began.
        Assert.True(Settle(cache, () => surface.Uploads.Count > 0), "no thumbnail was uploaded");
        Assert.Equal(200, Assert.Single(surface.Uploads).Pixels[0]);
    }

    /// <summary>
    ///     ⚠ And the editor is what calls it: refreshing the project draws the repainted file.
    /// </summary>
    /// <remarks>
    ///     <b>The caller, which is the half that was missing rather than the verb.</b> #1187's
    ///     measurement was that <c>EditorApplication</c> touches the cache four times and none of them
    ///     is about content — <c>assets.refresh</c>, the file watcher and an import all left it alone.
    ///     A <c>Forget</c> nothing called would be this repository's commonest defect wearing the
    ///     fix's clothes, so this goes through the command a person presses and reads the pixels the
    ///     application's own cache uploaded.
    /// </remarks>
    [Fact]
    public void Refreshing_the_project_draws_a_repainted_file() {
        using var editor = EditorSession.Start();
        var surface = new Recording();

        editor.Editor.ThumbnailSurface = surface;

        var crate = Paint(editor, "Assets/crate.png", 16, 16, static (_, _) => 40);

        editor.Open("project");
        editor.Settle();

        Assert.True(Pumped(editor, () => surface.Uploads.Count > 0), "the grid never decoded the file");
        Assert.Equal(40, surface.Uploads[0].Pixels[0]);

        // `Paint` ends with `assets.refresh`, which is the command somebody presses precisely because
        // they have just repainted a file outside the editor.
        Paint(editor, "Assets/crate.png", 16, 16, static (_, _) => 200);

        Assert.True(Pumped(editor, () => surface.Uploads.Count > 1), "the repainted file was never decoded again");
        Assert.Equal(200, surface.Uploads[^1].Pixels[0]);
        Assert.NotEqual(AssetId.Empty, crate);
    }

    /// <summary>
    ///     ⚠ A decode that <c>assets.refresh</c> itself made stale is asked for again, and it used to be
    ///     the picture that never came.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>What <a href="https://github.com/Rikarin/Vixen/issues/1407">#1407</a> actually
    ///         was.</b> The issue blamed <see cref="Pumped" />'s thirty-second stopwatch, and a slow
    ///         decode did outlast it — but a decode made thirty-five seconds long fails the old
    ///         <see cref="Refreshing_the_project_draws_a_repainted_file" /> at its first assertion
    ///         <em>however long</em> the wait. <c>RefreshAssets</c> rescans first, which binds the new
    ///         file and starts its decode, and then calls <c>Forget</c>, which marks that decode stale;
    ///         the <c>Changed</c> it raises finds the asset still pending, so the grid's ask is a no-op;
    ///         and the drop, when the answer lands, raised nothing — so no tile ever asked again. On an
    ///         idle machine the decode finished before <c>Open("project")</c> rebound the grid, and
    ///         that rebind is what asked again. Under load it did not, and the picture never came.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Since <a href="https://github.com/Rikarin/Vixen/issues/1406">#1406</a> one refresh
    ///         no longer does both halves.</b> The markup tile asks at the document's next flush, after
    ///         <c>Forget</c>, so the decode it starts is live. The drop is now reached by a refresh
    ///         that lands while an earlier decode is still in flight, and that is what this test does.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Held by a contributed preview, so the order is forced rather than hoped for.</b>
    ///         The first decode blocks inside the delegate until the refresh has marked it stale;
    ///         every later one returns at once. Two calls is the assertion that the drop was followed
    ///         by a fresh ask rather than the stale answer sneaking through.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_decode_the_refresh_made_stale_is_asked_for_again() {
        var registry = new EditorRegistry();
        using var editor = EditorSession.Start(new() { Extensions = registry });
        using var running = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var surface = new Recording();
        var calls = 0;
        var pixels = Enumerable.Repeat((byte) 0x60, 8 * 8 * 4).ToArray();

        editor.Editor.ThumbnailSurface = surface;
        editor.Open("project");
        editor.Settle();

        using var scope = registry.Add(
            new AssetPreview(".png", _ => {
                if (Interlocked.Increment(ref calls) == 1) {
                    running.Set();
                    release.Wait();
                }

                return new AssetPreviewImage(8, 8, pixels);
            })
        );

        var crate = AssetId.Empty;

        try {
            // The first `assets.refresh` binds the file and the grid asks for it; the decode starts
            // and is held inside the delegate.
            crate = Paint(editor, "Assets/crate.png", 8, 8, static (_, _) => 0);

            Assert.True(editor.Editor.Thumbnails.IsBusy, "the refresh asked for no picture, so nothing below is about one");
            Assert.True(
                running.Wait(Hung, TestContext.Current.CancellationToken),
                "the decode never started, which is a hang check and not a result"
            );

            // ⚠ A second refresh while that decode is in flight is what marks it stale. The one
            // refresh used to do both, because the hand-written grid asked from inside the rescan,
            // before `Forget`. Since #1406 the tile is markup and asks at the document's next flush,
            // which is after `Forget` has found nothing pending. So a single refresh no longer reaches
            // the drop, and the test went on passing the tile while asserting one decode rather than two.
            editor.Run("assets.refresh");
        } finally {
            release.Set();
        }

        // ⚠ The tile and not only the upload: what the defect looked like was a type glyph where the
        // picture belonged, so the assertion is the grid's own tile holding an image number.
        var grid = editor.Control<AssetGrid>("project");

        Assert.True(
            Pumped(editor, () => grid.Tiles.Any(tile => tile.Node?.Guid == crate && tile.Picture.Texture != 0)),
            "the dropped decode was never asked for again, so the tile kept its type glyph"
        );

        var tile = Assert.Single(grid.Tiles, candidate => candidate.Node?.Guid == crate);

        Assert.False(tile.Picture.HasClass("hidden"));
        Assert.True(tile.Glyph.HasClass("hidden"));
        Assert.Equal(0x60, Assert.Single(surface.Uploads).Pixels[0]);
        Assert.Equal(2, Volatile.Read(ref calls));
    }

    /// <summary>⚠ The wait every test here uses ends when the decode does — not before, not after.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The instrument under <see cref="Settle" /> and <see cref="Pumped" />, checked
    ///         first.</b> <a href="https://github.com/Rikarin/Vixen/issues/1407">#1407</a> replaced a
    ///         stopwatch with <see cref="ThumbnailCache.Decoding" />, and a completion that was already
    ///         complete would turn every wait here into "pump once and give up" — which passes on an
    ///         idle machine, where the decode has usually finished by the time anyone looks, and fails
    ///         on a loaded one. That is the flake this replaced, so it is asserted directly.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Held by a contributed preview, which is the one decode a test can stop
    ///         half-way</b> with no hook in the cache: its delegate runs on the pool thread, inside
    ///         the decode, and blocks until it is released. So the "still running" half is an
    ///         ordering and not a hope that the pool was slow.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_wait_is_on_the_decode_itself() {
        using var editor = EditorSession.Start();
        using var running = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var registry = new EditorRegistry();
        var pixels = Enumerable.Repeat((byte) 0x60, 8 * 8 * 4).ToArray();

        using var scope = registry.Add(
            new AssetPreview(".png", _ => {
                running.Set();
                release.Wait();

                return new AssetPreviewImage(8, 8, pixels);
            })
        );

        var crate = Paint(editor, "Assets/Textures/crate.png", 8, 8, static (_, _) => 0);
        var surface = new Recording();
        var cache = new ThumbnailCache(editor.Project, registry) { Surface = surface };

        try {
            Assert.False(cache.TryGet(crate, out _));
            Assert.True(
                running.Wait(Hung, TestContext.Current.CancellationToken),
                "the decode never started, which is a hang check and not a result"
            );

            var decoding = cache.Decoding;

            // The half that makes it a wait: while the decode is running, it is not done, and a pump
            // has nothing to take.
            Assert.False(decoding.IsCompleted, "the wait would have ended with the decode still running");
            cache.Pump();
            Assert.Empty(surface.Uploads);
            Assert.True(cache.IsBusy);

            release.Set();
            Await(decoding);
        } finally {
            release.Set();
        }

        // And the half that makes it enough: once it completes the answer is queued, so one pump
        // uploads it — no second wait, no spin.
        cache.Pump();

        Assert.Equal(0x60, Assert.Single(surface.Uploads).Pixels[0]);
        Assert.False(cache.IsBusy);
        Assert.True(cache.Decoding.IsCompleted);
    }

    /// <summary>
    ///     Frames in a row with no decode in flight after which nothing more can happen. A count of
    ///     frame-thread work, not of time.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Frames can be counted where pool work cannot</b>, because a frame is run on this
    ///     thread and does the same thing on a loaded machine as on an idle one: a grid rebinds on the
    ///     frame after <c>Changed</c>, and one that has asked for nothing in this many has nothing to
    ///     ask for. The decode is the only part that is not a frame, and it is waited on, never
    ///     counted.
    /// </remarks>
    const int QuietFrames = 8;

    /// <summary>Runs frames until something a pool thread does has happened, or can no longer happen.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><see cref="Settle" />'s shape, driving the application rather than a bare cache,
    ///         because what is under test is the wiring.</b> This is where
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1407">#1407</a> failed: it spun
    ///         <c>Frame</c> and <c>Thread.Yield</c> against a thirty-second stopwatch, so on a
    ///         machine running three editor test hosts the decode — queued on the pool behind all of
    ///         them, and competing with this loop for a core — outlasted the stopwatch and the test
    ///         reported "the grid never decoded the file" about work that was merely slow.
    ///     </para>
    ///     <para>
    ///         Now a frame that leaves a decode in flight is followed by a wait on that decode, and
    ///         the loop gives up only after <see cref="QuietFrames" /> frames in which nothing was
    ///         asked for — so a broken wiring fails in a handful of frames rather than after a
    ///         budget, and a slow decode is simply waited for.
    ///     </para>
    /// </remarks>
    internal static bool Pumped(EditorSession editor, Func<bool> until) {
        var cache = editor.Editor.Thumbnails;

        for (var quiet = 0; quiet < QuietFrames;) {
            editor.Frame();

            if (until()) {
                return true;
            }

            if (cache.IsBusy) {
                quiet = 0;
                Await(cache.Decoding);
            } else {
                quiet++;
            }
        }

        return until();
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
    ///     <b>The picker is the second thing that draws pictures, and it was the reason to have
    ///     them.</b> A name says what an asset is called and a picture says <i>which</i> one it is —
    ///     which is the whole difference between choosing between <c>crate.png</c> and
    ///     <c>crate2.png</c> and guessing between them.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The picture arrives after the dialog was built, which is why the subscription
    ///     matters.</b> A decode lands a few frames later, and a modal dialog nobody is scrolling has
    ///     nothing else that would make it rebind — so a picker that did not listen to
    ///     <c>ThumbnailCache.Changed</c> would show glyphs for as long as it was open, on a machine
    ///     that had made every picture it needed.
    /// </remarks>
    [Fact]
    public void The_asset_picker_shows_a_picture_once_one_has_been_decoded() {
        using var editor = EditorSession.Start();

        editor.Open("project");

        var crate = Paint(editor, "Assets/Textures/crate.png", 32, 32, static (x, _) => (byte) (x * 8));
        var surface = new Recording();
        var cache = new ThumbnailCache(editor.Project) { Surface = surface };

        Pick(editor, cache);

        var grid = PickerGrid(editor);

        Assert.Contains(grid.Items, item => item.Guid == crate);

        // Bound once with no picture — the request is the bind — and then again when the decode
        // lands, which is what the dialog's subscription to `Changed` is for.
        Assert.All(grid.Tiles, tile => Assert.Equal(0UL, tile.Picture.Texture));

        // ⚠ A frame on every look, because since #1406 the tile template is markup: the decode's
        // `Changed` makes the dialog `Refresh` the grid, which moves a signal the tile's bindings
        // read, and a binding runs at the document's next flush — the top of the next frame, before
        // anything is drawn. The hand-written grid rebound inside `Refresh` itself, and this loop,
        // which pumped the cache and never ran a frame, was relying on that. A frame rather than a
        // bare `Effects.Flush()`, which the first fix used: what is asserted is what the picker
        // draws, and a drain by hand could pass for a template the frame never reached.
        Assert.True(
            Settle(
                cache,
                () => {
                    editor.Frame();
                    return grid.Tiles.Any(tile => tile.Node?.Guid == crate && tile.Picture.Texture != 0);
                }
            ),
            "the picker never showed a picture"
        );

        var pictured = Assert.Single(grid.Tiles, tile => tile.Node?.Guid == crate);

        Assert.False(pictured.Picture.HasClass("hidden"));
        Assert.True(pictured.Glyph.HasClass("hidden"));
        Assert.NotEmpty(surface.Uploads);
    }

    /// <summary>
    ///     ⚠ Null is the ordinary state on a headless run, so the picker has to be usable without a
    ///     device — a grid of nothing at all would make every asset field unassignable on a machine
    ///     with no GPU, which is what a build server is.
    /// </summary>
    [Fact]
    public void With_no_surface_the_picker_falls_back_to_type_glyphs() {
        using var editor = EditorSession.Start();

        editor.Open("project");
        Paint(editor, "Assets/Textures/crate.png", 32, 32, static (_, _) => 10);

        Pick(editor, thumbnails: null);

        var tiles = PickerGrid(editor).Tiles;

        Assert.NotEmpty(tiles);
        Assert.All(tiles, tile => Assert.True(tile.Picture.HasClass("hidden")));
        Assert.All(tiles, tile => Assert.False(tile.Glyph.HasClass("hidden")));
    }

    /// <summary>Opens the picker over a field that takes any asset.</summary>
    static void Pick(EditorSession editor, ThumbnailCache? thumbnails) {
        var descriptor = InspectorRegistry.Find(typeof(PickerFixture))
            ?? throw editor.Fail("the generator registered no descriptor for PickerFixture");

        var member = descriptor.Members.Single(candidate => candidate.Name == "Anything");
        var field = new InspectorField(descriptor, member, [new PickerFixture()], editor.Scene);

        new AssetPicker(editor.Project, editor.Shell.Dialogs, thumbnails).Open(field);
        editor.Frames(2);
    }

    static AssetGrid PickerGrid(EditorSession editor) {
        var dialog = editor.Shell.Dialogs.Current ?? throw editor.Fail("the picker did not open");

        foreach (var element in Descendants(dialog.Body)) {
            if (element is AssetGrid grid) {
                return grid;
            }
        }

        throw editor.Fail("the picker has no grid");
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

        var tiles = Descendants(editor.Panel("project")).OfType<AssetGrid>().Single().Tiles;

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
