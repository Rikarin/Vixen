// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Plugin;
using Vixen.Graphics;

namespace Vixen.Editor.App;

/// <summary>The application's answer to <see cref="IEditorGraphics" />.</summary>
/// <remarks>
///     <para>
///         <b>A live view rather than a snapshot, and that is the whole reason this type exists.</b>
///         <c>EditorApplication.PluginPoints</c> runs in the constructor and there is no device then:
///         the host sets <see cref="EditorApplication.GraphicsDevice" /> once the window can present
///         and sets it back to <see langword="null" /> when the window goes.
///         <c>PluginServices.Add</c> throws on a second publish of a type, so publishing the device
///         itself was never available — <a href="https://github.com/Rikarin/Vixen/issues/737">#737</a>
///         called it a one-line fix and it is not one. This is the same shape
///         <see cref="ShownScene" /> and <see cref="ShownView" /> already take beside it.
///     </para>
///     <para>
///         ⚠ <b>The upload is the thumbnail surface's, deliberately, and not a second uploader.</b>
///         That class is the one place in the editor that does the three steps an upload needs — a
///         staging buffer, a copy inside the frame that retires it, and the two barriers — and it
///         defers every destroy past the frames in flight. A plugin's picture is exactly a thumbnail
///         with a different author, so a second implementation would be a second chance to get the
///         barriers wrong, on the path where MoltenVK forgives what a discrete card does not.
///     </para>
///     <para>
///         ⚠ <b>Null on both members is an ordinary state and not a failure.</b> Headless, in a test,
///         and during start-up there is no device and no surface; a plugin asks each time and says so
///         on screen, which is what <c>Editor/Vixen.Editor.Texturing</c>'s preview pane does.
///     </para>
/// </remarks>
sealed class PluginGraphics : IEditorGraphics {
    readonly EditorApplication application;

    /// <summary>Wires it to the application that owns the device and the surface.</summary>
    /// <param name="application">The application.</param>
    public PluginGraphics(EditorApplication application) {
        ArgumentNullException.ThrowIfNull(application);

        this.application = application;
    }

    /// <inheritdoc />
    public IGraphicsDevice? Device => application.GraphicsDevice;

    /// <inheritdoc />
    public IEditorImage? Upload(int width, int height, ReadOnlySpan<byte> rgba) {
        if (application.ThumbnailSurface is not { } surface) {
            return null;
        }

        var image = surface.Upload(width, height, rgba);

        // Zero is the surface's own "I could not make that" — an empty span, a nonsense extent — and
        // an IEditorImage wrapping it would be a handle whose Dispose released a number nobody owns.
        return image == 0 ? null : new Handle(surface, image, width, height);
    }

    /// <summary>One uploaded picture, released once whatever happens to it.</summary>
    /// <remarks>
    ///     ⚠ <b>Idempotent, because a plugin that both releases its image and hands it to
    ///     <c>PluginContext.Owns</c> is doing the right thing twice.</b> A second release would give
    ///     the same number back to the surface's free list, and the number after it would then be
    ///     handed out to two owners.
    /// </remarks>
    sealed class Handle : IEditorImage {
        readonly IThumbnailSurface surface;

        bool released;

        public Handle(IThumbnailSurface surface, ulong image, int width, int height) {
            this.surface = surface;

            Image = image;
            Width = width;
            Height = height;
        }

        /// <inheritdoc />
        public ulong Image { get; }

        /// <inheritdoc />
        public int Width { get; }

        /// <inheritdoc />
        public int Height { get; }

        /// <inheritdoc />
        public void Dispose() {
            if (released) {
                return;
            }

            released = true;
            surface.Release(Image);
        }
    }
}
