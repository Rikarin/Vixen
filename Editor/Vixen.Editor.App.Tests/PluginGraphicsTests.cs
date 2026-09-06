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
