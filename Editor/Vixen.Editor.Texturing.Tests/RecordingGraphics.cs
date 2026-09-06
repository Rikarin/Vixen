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

    /// <summary>Every partial update, oldest first, with the rectangle and its own pixels.</summary>
    /// <remarks>
    ///     ⚠ <b>Kept apart from <see cref="Uploads" /> because the distinction is the assertion.</b>
    ///     A pane that re-uploaded the atlas on every pointer move and a pane that patched a
    ///     rectangle both put the right picture on the screen —
    ///     <a href="https://github.com/Rikarin/Vixen/issues/912">#912</a> is about which of the two
    ///     it did, and a recorder that folded them together could not say.
    /// </remarks>
    public List<Patched> Updates { get; } = [];

    /// <summary>How many uploaded images have been released.</summary>
    public int Released { get; private set; }

    /// <summary>Whether this host takes partial updates at all.</summary>
    /// <remarks>
    ///     ⚠ <b>A real state and not a knob for its own sake.</b> A host with no thumbnail surface
    ///     refuses every <see cref="Update" /> — headless, in a test, and in the moments before the
    ///     window has one — so a caller's fallback to <see cref="Upload" /> is code that runs, and
    ///     without this nothing could make it run.
    /// </remarks>
    public bool Patches { get; set; } = true;

    /// <inheritdoc />
    public IGraphicsDevice? Device => device;

    /// <inheritdoc />
    public IEditorImage? Upload(int width, int height, ReadOnlySpan<byte> rgba) {
        // ⚠ Copied, because the caller owns the span. A recorder holding a reference to somebody
        // else's buffer would assert against whatever was in it by the time the test looked.
        var uploaded = new Uploaded((ulong)Uploads.Count + 1, width, height, rgba.ToArray());

        Uploads.Add(uploaded);

        return new Handle(this, uploaded);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Refused for an image this recorder did not make and for a rectangle outside it, which is
    ///     what the real host does — a double that took anything would make a caller's bounds
    ///     arithmetic untestable.
    /// </remarks>
    public bool Update(IEditorImage image, int x, int y, int width, int height, ReadOnlySpan<byte> rgba) {
        if (!Patches
            || image is not Handle handle
            || handle.Owner != this
            || width <= 0
            || height <= 0
            || x < 0
            || y < 0
            || x + width > handle.Width
            || y + height > handle.Height
            || rgba.Length < width * height * 4) {
            return false;
        }

        Updates.Add(new(handle.Image, x, y, width, height, rgba[..(width * height * 4)].ToArray()));

        return true;
    }

    /// <summary>One upload, as it arrived.</summary>
    /// <param name="Image">The number the pane is expected to draw.</param>
    /// <param name="Width">How wide.</param>
    /// <param name="Height">How tall.</param>
    /// <param name="Pixels">The pixels, four bytes each.</param>
    public sealed record Uploaded(ulong Image, int Width, int Height, byte[] Pixels);

    /// <summary>One partial update, as it arrived.</summary>
    /// <param name="Image">Which picture it went into.</param>
    /// <param name="X">The rectangle's low column.</param>
    /// <param name="Y">Its low row.</param>
    /// <param name="Width">How many columns.</param>
    /// <param name="Height">How many rows.</param>
    /// <param name="Pixels">The rectangle's own pixels, rows tightly packed.</param>
    public sealed record Patched(ulong Image, int X, int Y, int Width, int Height, byte[] Pixels);

    sealed class Handle(RecordingGraphics graphics, Uploaded uploaded) : IEditorImage {
        bool released;

        public RecordingGraphics Owner => graphics;

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
