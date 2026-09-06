// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Plugin;
using Vixen.Editor.Testing;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>What a plugin that draws is handed, and why it is a view rather than a device.</summary>
/// <remarks>
///     <para>
///         <b>Doc 36 § F2's gap, closed — <a href="https://github.com/Rikarin/Vixen/issues/737">#737</a>.</b>
///         Until this the contract published the project, the scene, the registries and the plugin
///         host and nothing device-shaped, so a third party could add a panel and could not put a
///         picture in it.
///     </para>
///     <para>
///         ⚠ <b>The device half needs no device here and that is the point of the seam.</b> What is
///         asserted is that the service tracks the host's own answer as it changes; the three
///         barriers and the copy that make an upload correct belong to <c>ThumbnailSurface</c> and
///         are asserted against a real adapter in <see cref="ThumbnailSurfaceDeviceTests" />.
///     </para>
/// </remarks>
public class PluginGraphicsTests {
    [Fact]
    public void The_editor_publishes_graphics_to_plugins() {
        using var editor = EditorSession.Start();

        editor.Open("project");

        Assert.True(editor.Application.PluginHost.Services.Contains<IEditorGraphics>());
    }

    /// <summary>⚠ The refutation: a device published once at construction would be null for ever.</summary>
    /// <remarks>
    ///     <c>PluginPoints</c> runs from <c>EditorApplication</c>'s constructor and the host sets
    ///     <c>GraphicsDevice</c> afterwards, when the window can present. #737 called
    ///     <c>.Add(device)</c> "the smallest honest fix"; this test is what says it could not have
    ///     worked, because it watches the answer change after the service was published.
    /// </remarks>
    [Fact]
    public void The_device_is_read_when_asked_rather_than_when_published() {
        using var editor = EditorSession.Start();

        editor.Open("project");

        var graphics = editor.Application.PluginHost.Services.Require<IEditorGraphics>();

        Assert.Null(graphics.Device);

        using var device = new NullDevice();

        editor.Application.GraphicsDevice = device;

        Assert.Same(device, graphics.Device);

        // And back again, which is what the host does on the way down. A plugin that had cached the
        // first answer would be holding a device the editor has released.
        editor.Application.GraphicsDevice = null;

        Assert.Null(graphics.Device);
    }

    /// <summary>An upload goes through the host's surface, and its removal comes back with it.</summary>
    /// <remarks>
    ///     ⚠ <b>Released once however many times it is disposed.</b> A plugin that releases its image
    ///     <i>and</i> hands it to <c>PluginContext.Owns</c> is doing the right thing twice, and a
    ///     second release gives the same number back to the surface's free list — which is then
    ///     handed to two owners.
    /// </remarks>
    [Fact]
    public void An_upload_is_the_hosts_and_the_handle_releases_it_once() {
        using var editor = EditorSession.Start();

        editor.Open("project");

        var surface = new Recording();

        editor.Application.ThumbnailSurface = surface;

        var graphics = editor.Application.PluginHost.Services.Require<IEditorGraphics>();
        var image = graphics.Upload(2, 1, [1, 2, 3, 4, 5, 6, 7, 8]);

        Assert.NotNull(image);
        Assert.Equal(2, image.Width);
        Assert.Equal(1, image.Height);
        Assert.NotEqual(0ul, image.Image);

        var uploaded = Assert.Single(surface.Uploads);

        Assert.Equal(8, uploaded.Length);

        image.Dispose();
        image.Dispose();

        Assert.Equal([image.Image], surface.Released);
    }

    /// <summary>A partial update reaches the surface with the rectangle and the handle's own number.</summary>
    /// <remarks>
    ///     ⚠ <b>The host half of <a href="https://github.com/Rikarin/Vixen/issues/912">#912</a> had no
    ///     test at all, and the double added beside it collected updates nothing read</b> —
    ///     <a href="https://github.com/Rikarin/Vixen/issues/958">#958</a>. What is asserted is the
    ///     forwarding, because the alternative to forwarding is a plugin whose stroke never reaches
    ///     the screen and whose fallback path — a whole re-upload — hides it perfectly.
    /// </remarks>
    [Fact]
    public void A_partial_update_is_forwarded_to_the_surface_that_made_the_image() {
        using var editor = EditorSession.Start();

        editor.Open("project");

        var surface = new Recording();

        editor.Application.ThumbnailSurface = surface;

        var graphics = editor.Application.PluginHost.Services.Require<IEditorGraphics>();
        var image = graphics.Upload(4, 4, new byte[4 * 4 * 4]);

        Assert.NotNull(image);
        Assert.True(graphics.Update(image, 1, 2, 2, 1, [9, 9, 9, 9, 8, 8, 8, 8]));

        var update = Assert.Single(surface.Updates);

        Assert.Equal((image.Image, 1, 2, 2, 1), (update.Image, update.X, update.Y, update.Width, update.Height));
        Assert.Equal([9, 9, 9, 9, 8, 8, 8, 8], update.Pixels);
    }

    /// <summary>⚠ A handle made against one surface is refused by the next one.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The stale-handle guard, which is the whole justification for the host half of
    ///         #912's contract and had nothing exercising it</b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/958">#958</a>. An image number is a
    ///         number: the window goes and comes back, the editor builds a new surface, and the
    ///         number a plugin is still holding names a live image of that new one. Without the
    ///         identity check a paint stroke would write its texels into somebody else's picture and
    ///         be told it succeeded.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The two surfaces hand out the same numbers on purpose</b>, because that is the
    ///         only arrangement in which the check can be wrong: two <c>Recording</c>s both start at
    ///         one, so the stale handle names a real image of the new surface and a guard comparing
    ///         numbers rather than surfaces would accept it.
    ///     </para>
    ///     <para>
    ///         And the refusal is a <c>false</c> rather than a throw, because <c>Update</c>'s contract
    ///         says a caller that gets one must re-upload — a caller that had to catch would be a
    ///         caller that leaves the screen showing the pixels from before the stroke.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_image_from_a_previous_surface_is_refused_rather_than_written_into_the_new_one() {
        using var editor = EditorSession.Start();

        editor.Open("project");

        var gone = new Recording();

        editor.Application.ThumbnailSurface = gone;

        var graphics = editor.Application.PluginHost.Services.Require<IEditorGraphics>();
        var stale = graphics.Upload(4, 4, new byte[4 * 4 * 4]);

        Assert.NotNull(stale);

        // The window went and came back: a different surface, handing out numbers from one again.
        var now = new Recording();

        editor.Application.ThumbnailSurface = now;

        var live = graphics.Upload(4, 4, new byte[4 * 4 * 4]);

        Assert.NotNull(live);

        // The instrument, and it is what makes the refusal below mean anything: the stale handle's
        // number is a live image of the new surface, so a guard that compared numbers would pass.
        Assert.Equal(stale.Image, live.Image);

        Assert.False(graphics.Update(stale, 0, 0, 1, 1, [1, 2, 3, 4]));
        Assert.Empty(now.Updates);

        // And the live one still works, so the refusal is about the handle rather than about the
        // surface having changed at all.
        Assert.True(graphics.Update(live, 0, 0, 1, 1, [1, 2, 3, 4]));
        Assert.Single(now.Updates);
    }

    /// <summary>⚠ And with no surface an update is refused rather than silently dropped as done.</summary>
    /// <remarks>
    ///     <c>Update</c>'s contract says <c>false</c> obliges the caller to fall back to
    ///     <see cref="IEditorGraphics.Upload" />; a host with nothing to draw on returning
    ///     <c>true</c> would be a caller told its pixels landed when there is no picture at all.
    /// </remarks>
    [Fact]
    public void With_no_surface_an_update_is_refused() {
        using var editor = EditorSession.Start();

        editor.Open("project");

        var surface = new Recording();

        editor.Application.ThumbnailSurface = surface;

        var graphics = editor.Application.PluginHost.Services.Require<IEditorGraphics>();
        var image = graphics.Upload(4, 4, new byte[4 * 4 * 4]);

        Assert.NotNull(image);

        editor.Application.ThumbnailSurface = null;

        Assert.False(graphics.Update(image, 0, 0, 1, 1, [1, 2, 3, 4]));
        Assert.Empty(surface.Updates);
    }

    /// <summary>⚠ And with no surface there is no image, rather than a number nothing can draw.</summary>
    /// <remarks>
    ///     Null is the ordinary state headless and in every test, exactly as it is for the browser's
    ///     thumbnails — the pane says so, which is what <c>Editor/Vixen.Editor.Texturing</c> does.
    /// </remarks>
    [Fact]
    public void With_no_surface_there_is_no_image() {
        using var editor = EditorSession.Start();

        editor.Open("project");

        Assert.Null(editor.Application.ThumbnailSurface);
        Assert.Null(editor.Application.PluginHost.Services.Require<IEditorGraphics>().Upload(1, 1, [0, 0, 0, 0]));
    }

    /// <summary>A surface that hands out numbers and remembers what it was given.</summary>
    sealed class Recording : IThumbnailSurface {
        ulong next = 1;

        public List<byte[]> Uploads { get; } = [];

        public List<ulong> Released { get; } = [];

        public List<(ulong Image, int X, int Y, int Width, int Height, byte[] Pixels)> Updates { get; } = [];

        public ulong Upload(int width, int height, ReadOnlySpan<byte> rgba) {
            Uploads.Add(rgba.ToArray());

            return next++;
        }

        public bool Update(ulong image, int x, int y, int width, int height, ReadOnlySpan<byte> rgba) {
            Updates.Add((image, x, y, width, height, rgba.ToArray()));

            return true;
        }

        public void Release(ulong image) => Released.Add(image);
    }
}
