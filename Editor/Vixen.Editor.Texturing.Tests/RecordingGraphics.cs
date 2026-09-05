// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Plugin;
using Vixen.Graphics;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>A host's graphics, keeping what was drawn through them.</summary>
/// <remarks>
///     <para>
///         <b>The <i>host</i> is the double here, and the plugin is the subject.</b> What these
///         suites assert is what <c>Editor/Vixen.Editor.Texturing</c> produces and hands over — the
///         extent, the pixels, and that the pane draws the number it was given back. The real upload
///         is a staging buffer, a copy inside the frame that retires it and two barriers, all of
///         which belong to <c>Vixen.Editor.App</c> and are asserted there against a real device.
///     </para>
///     <para>
///         ⚠ <b>The device is the real one, and it is not optional for anything that evaluates.</b>
///         A double that answered <see cref="Upload" /> <i>and</i> pretended to be a device would
///         make a green suite that proved a black image equals a black image, which is what
///         <c>TextureKernelHarness</c> exists to prevent one assembly along.
///     </para>
/// </remarks>
sealed class RecordingGraphics(IGraphicsDevice? device) : IEditorGraphics {
    /// <summary>Every upload, oldest first, with the pixels as they arrived.</summary>
    public List<Uploaded> Uploads { get; } = [];

    /// <summary>How many uploaded images have been released.</summary>
    public int Released { get; private set; }

    /// <inheritdoc />
    public IGraphicsDevice? Device => device;

    /// <inheritdoc />
    public IEditorImage? Upload(int width, int height, ReadOnlySpan<byte> rgba) {
        // ⚠ Copied, because the caller owns the span. A recorder holding a reference to somebody
        // else's buffer would assert against whatever was in it by the time the test looked.
        var uploaded = new Uploaded((ulong) Uploads.Count + 1, width, height, rgba.ToArray());

        Uploads.Add(uploaded);

        return new Handle(this, uploaded);
    }

    /// <summary>One upload, as it arrived.</summary>
    /// <param name="Image">The number the pane is expected to draw.</param>
    /// <param name="Width">How wide.</param>
    /// <param name="Height">How tall.</param>
    /// <param name="Pixels">The pixels, four bytes each.</param>
    public sealed record Uploaded(ulong Image, int Width, int Height, byte[] Pixels);

    sealed class Handle(RecordingGraphics graphics, Uploaded uploaded) : IEditorImage {
        bool released;

        public ulong Image => uploaded.Image;

        public int Width => uploaded.Width;

        public int Height => uploaded.Height;

        public void Dispose() {
            if (released) {
                return;
            }

            released = true;
            graphics.Released++;
        }
    }
}
