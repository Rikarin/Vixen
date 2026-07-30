// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using Vixen.Core;
using Vixen.Core.Imaging;
using Vixen.Graphics;
using Vixen.Editor.Assets.Textures;
using Vixen.Editor.Core;

namespace Vixen.Editor.App;

/// <summary>Where a decoded thumbnail becomes something the interface can draw.</summary>
/// <remarks>
///     ⚠ <b>An interface because the application has no device and must not acquire one.</b>
///     <c>Image.Texture</c> is a number handed out by <c>UiRenderer.RegisterImage</c>, which needs a
///     texture, which needs the graphics device — and the device is <c>EditorHost</c>'s. The same
///     seam <c>EditorServices</c> is: what the host can do that the application can only ask for.
///     Without one the browser draws type glyphs, which is what it does on a headless run and in
///     every test.
/// </remarks>
interface IThumbnailSurface {
    /// <summary>Uploads pixels and returns the number an <c>Image</c> draws them by.</summary>
    /// <param name="width">How wide.</param>
    /// <param name="height">How tall.</param>
    /// <param name="rgba">The pixels, four bytes each, top row first.</param>
    /// <returns>The image number, or zero if it could not be made.</returns>
    ulong Upload(int width, int height, ReadOnlySpan<byte> rgba);

    /// <summary>Gives up an image, so its texture can go.</summary>
    /// <param name="image">What <see cref="Upload" /> returned.</param>
    void Release(ulong image);
}

/// <summary>Pictures of assets, decoded off the frame thread and uploaded on it.</summary>
/// <remarks>
///     <para>
///         <b>What turns the browser's type glyphs into thumbnails.</b> A glyph says what kind of
///         thing a file is; a picture says <i>which</i> one, which is the difference between a grid
///         you scan and a grid you read.
///     </para>
///     <para>
///         ⚠ <b>Decoded on a background task and uploaded on the frame thread, and the split is not
///         negotiable.</b> Decoding a 4K PNG is tens of milliseconds — done inline it is a stutter
///         every time the grid scrolls — and the device is not thread-safe, so the upload cannot go
///         where the decode does. What crosses back is a queue of finished pixels, the same
///         arrangement <c>ContentTasks</c> uses and for the same reason.
///     </para>
///     <para>
///         ⚠ <b>Bounded, and the eviction releases the texture.</b> A cache with no ceiling is a leak
///         with a picture on it: a project of forty thousand textures scrolled through once would
///         hold forty thousand GPU images. Least-recently-asked-for goes first, which for a grid
///         being scrolled is what has left the screen.
///     </para>
///     <para>
///         ⚠ <b>Only what a decoder claims, and only source images.</b> A scene, a material and a
///         prefab have no picture that is not a render of them — that is E5's preview work — and
///         asking a decoder about a 200 MB <c>.fbx</c> would be a background task per model doing
///         nothing useful.
///     </para>
/// </remarks>
sealed class ThumbnailCache : IDisposable {
    /// <summary>How big a thumbnail is, in pixels.</summary>
    /// <remarks>
    ///     Bigger than the tile draws it, so the grid can grow a size or two without every picture
    ///     turning soft, and small enough that a thousand of them is a few megabytes.
    /// </remarks>
    public const int Size = 64;

    /// <summary>How many pictures are kept before the least recently wanted is dropped.</summary>
    /// <remarks>
    ///     Settable so that a test can fill it with six files rather than five hundred and twelve —
    ///     the eviction is what is worth testing and painting five hundred PNGs to reach it would be
    ///     a test of the thread pool.
    /// </remarks>
    public int Capacity { get; init; } = 512;

    /// <summary>Where thumbnail image numbers start.</summary>
    /// <remarks>
    ///     ⚠ <b>Above everything else the editor registers.</b> <c>ScenePresenter</c> takes 1 and a
    ///     torn-off pane takes the next few; a thumbnail that collided with one would draw the
    ///     viewport in a tile, or worse, the tile in the viewport.
    /// </remarks>
    public const ulong FirstImage = 0x1000;

    readonly EditorProject project;

    /// <summary>What is ready, by asset, with the order they were last asked for.</summary>
    readonly Dictionary<AssetId, ulong> ready = [];
    readonly List<AssetId> recent = [];

    /// <summary>What has been asked for and not answered, so it is not asked twice.</summary>
    readonly HashSet<AssetId> pending = [];

    /// <summary>What a background decode finished with, waiting for a frame to upload it.</summary>
    readonly ConcurrentQueue<Decoded> finished = new();

    /// <summary>What could not be decoded, so it is never tried again.</summary>
    readonly HashSet<AssetId> refused = [];

    bool closed;

    /// <summary>Raised on the frame thread when a picture became available.</summary>
    public event Action? Changed;

    /// <summary>Builds a cache over a project.</summary>
    /// <param name="project">Where the files are.</param>
    public ThumbnailCache(EditorProject project) {
        ArgumentNullException.ThrowIfNull(project);

        this.project = project;
    }

    /// <summary>What can upload, or <see langword="null" /> for a headless editor.</summary>
    /// <remarks>
    ///     ⚠ <b>Set afterwards rather than taken in the constructor, because of when the device
    ///     exists.</b> <c>EditorHost</c> builds the application first and the Vulkan device second —
    ///     the window has to be up before a surface can be made from it — so a cache that demanded
    ///     its uploader up front would be a cache the host could not construct. Null is the ordinary
    ///     state on a headless run and in every test, and the browser draws type glyphs.
    /// </remarks>
    public IThumbnailSurface? Surface { get; set; }

    /// <summary>Whether pictures are possible at all here.</summary>
    public bool IsAvailable => Surface is not null;

    /// <summary>How many pictures are held.</summary>
    public int Count => ready.Count;

    /// <summary>Whether anything has been asked for and not yet drawn.</summary>
    /// <remarks>
    ///     ⚠ <b>A definite answer rather than a guess at how long a decode takes.</b> A caller
    ///     waiting for pictures — a test, or a browser that wants to say it is still working — needs
    ///     to know when there is nothing left in flight, and counting frames or milliseconds is what
    ///     makes a suite pass on a quiet laptop and fail on a loaded runner.
    ///     <para>
    ///         <see cref="pending" /> is only ever touched on the frame thread and the queue is
    ///         concurrent, so this is safe to read from the thread that pumps.
    ///     </para>
    /// </remarks>
    public bool IsBusy => pending.Count > 0 || !finished.IsEmpty;

    /// <summary>The picture for an asset, asking for one if there is none yet.</summary>
    /// <param name="asset">Which asset.</param>
    /// <param name="image">The image number to draw.</param>
    /// <returns>Whether there is one to draw now.</returns>
    /// <remarks>
    ///     ⚠ <b>Asking is the same call as reading, deliberately.</b> The set of things worth a
    ///     picture is "what the grid just bound", which changes as it scrolls — a separate request
    ///     API would mean the caller maintaining that set a second time and getting it wrong at the
    ///     edges.
    /// </remarks>
    public bool TryGet(AssetId asset, out ulong image) {
        image = 0;

        if (Surface is null || asset.IsEmpty) {
            return false;
        }

        if (ready.TryGetValue(asset, out image)) {
            Touch(asset);
            return true;
        }

        Request(asset);
        return false;
    }

    /// <summary>Uploads whatever finished decoding, on the thread that owns the device.</summary>
    /// <remarks>Called once a frame, the way every other queue in this application is drained.</remarks>
    public void Pump() {
        if (Surface is not { } surface) {
            return;
        }

        var arrived = false;

        while (finished.TryDequeue(out var decoded)) {
            pending.Remove(decoded.Asset);

            if (closed) {
                continue;
            }

            if (decoded.Pixels is not { Length: > 0 } pixels) {
                refused.Add(decoded.Asset);
                continue;
            }

            var image = surface.Upload(decoded.Width, decoded.Height, pixels);

            if (image == 0) {
                refused.Add(decoded.Asset);
                continue;
            }

            ready[decoded.Asset] = image;
            Touch(decoded.Asset);
            Evict();

            arrived = true;
        }

        if (arrived) {
            Changed?.Invoke();
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        closed = true;

        if (Surface is { } surface) {
            foreach (var image in ready.Values) {
                surface.Release(image);
            }
        }

        ready.Clear();
        recent.Clear();
    }

    void Request(AssetId asset) {
        if (refused.Contains(asset) || !pending.Add(asset)) {
            return;
        }

        if (!project.Assets.TryGetByGuid(asset, out var entry) || entry.IsFolder) {
            pending.Remove(asset);
            refused.Add(asset);

            return;
        }

        var extension = Path.GetExtension(entry.Path);

        if (ImageDecoders.For(ImageDecoders.BuiltIn, extension) is null) {
            pending.Remove(asset);
            refused.Add(asset);

            return;
        }

        var path = project.Paths.Absolute(entry.Path);

        // ⚠ Long-running is not asked for and would be wrong: these are short, there are many, and
        // the pool's own scheduling is what keeps a folder of two hundred from starting two hundred
        // threads.
        _ = Task.Run(() => finished.Enqueue(Decode(asset, path, extension)));
    }

    /// <summary>Reads a file and reduces it to a thumbnail, off the frame thread.</summary>
    /// <remarks>
    ///     ⚠ <b>Every failure is a refusal rather than an exception.</b> A file being written by
    ///     another program, a truncated download, an extension that lies about its contents — all of
    ///     them are ordinary, all of them arrive here, and a background task that threw would take
    ///     the editor down from a thread nobody was watching.
    /// </remarks>
    static Decoded Decode(AssetId asset, string path, string extension) {
        try {
            if (ImageDecoders.For(ImageDecoders.BuiltIn, extension) is not { } decoder) {
                return new Decoded(asset, 0, 0, null);
            }

            using var stream = File.OpenRead(path);
            var texture = decoder.Decode(stream, extension);

            // ⚠ Only the eight-bit form. An HDR source decodes to `Rgba32Float`, which is four times
            // the bytes and needs a tone map to look like anything — and a thumbnail that showed a
            // clipped exposure would be a worse answer than the type glyph.
            if (texture.Format != PixelFormat.Rgba8UNorm || texture.Width <= 0 || texture.Height <= 0) {
                return new Decoded(asset, 0, 0, null);
            }

            return Reduce(asset, texture);
        } catch (Exception failure) when (failure is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or InvalidDataException
            or ArgumentException) {
            return new Decoded(asset, 0, 0, null);
        }
    }

    /// <summary>Box-filters a decoded image down to a square thumbnail.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A box filter and not nearest, which is the difference between a thumbnail and
    ///         noise.</b> Taking one source pixel per destination pixel of a 4096-wide texture samples
    ///         one in sixty-four, so a brick wall becomes a moiré pattern and a UI atlas becomes
    ///         static. Averaging the block each destination pixel covers costs a pass over the source
    ///         and is what makes the picture recognisable.
    ///     </para>
    ///     <para>
    ///         The aspect is kept and the rest is left transparent, because a thumbnail that stretched
    ///         a 16:9 texture into a square would misrepresent the one thing somebody is looking at it
    ///         to check.
    ///     </para>
    /// </remarks>
    static Decoded Reduce(AssetId asset, TextureData texture) {
        var source = texture.Level(0);

        var scale = Math.Min((float) Size / texture.Width, (float) Size / texture.Height);
        var width = Math.Max(1, (int) (texture.Width * scale));
        var height = Math.Max(1, (int) (texture.Height * scale));

        var pixels = new byte[width * height * 4];

        for (var y = 0; y < height; y++) {
            var top = y * texture.Height / height;
            var bottom = Math.Max(top + 1, (y + 1) * texture.Height / height);

            for (var x = 0; x < width; x++) {
                var left = x * texture.Width / width;
                var right = Math.Max(left + 1, (x + 1) * texture.Width / width);

                long r = 0, g = 0, b = 0, a = 0;
                var taken = 0;

                for (var sy = top; sy < bottom; sy++) {
                    var row = sy * texture.Width * 4;

                    for (var sx = left; sx < right; sx++) {
                        var at = row + (sx * 4);

                        if (at + 3 >= source.Length) {
                            continue;
                        }

                        r += source[at];
                        g += source[at + 1];
                        b += source[at + 2];
                        a += source[at + 3];
                        taken++;
                    }
                }

                if (taken == 0) {
                    continue;
                }

                var to = ((y * width) + x) * 4;

                pixels[to] = (byte) (r / taken);
                pixels[to + 1] = (byte) (g / taken);
                pixels[to + 2] = (byte) (b / taken);
                pixels[to + 3] = (byte) (a / taken);
            }
        }

        return new Decoded(asset, width, height, pixels);
    }

    void Touch(AssetId asset) {
        recent.Remove(asset);
        recent.Add(asset);
    }

    void Evict() {
        while (recent.Count > Math.Max(1, Capacity)) {
            var oldest = recent[0];

            recent.RemoveAt(0);

            if (ready.Remove(oldest, out var image)) {
                Surface?.Release(image);
            }
        }
    }

    readonly record struct Decoded(AssetId Asset, int Width, int Height, byte[]? Pixels);
}
