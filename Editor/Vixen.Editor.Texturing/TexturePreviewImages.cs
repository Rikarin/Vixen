// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Editor.Plugin;
using Vixen.Editor.TextureGraph;

namespace Vixen.Editor.Texturing;

/// <summary>Where a per-node preview's pixels become a number the canvas draws.</summary>
/// <remarks>
///     <para>
///         <b>The plugin's half of <c>ITexturePreviewImages</c>, and the seam
///         <c>UiPreviewImages</c> is for the shader graph</b> —
///         <a href="https://github.com/Rikarin/Vixen/issues/1015">#1015</a>. The difference is which
///         door it goes through: the shader graph's sink lives in <c>Vixen.Editor.App</c> and calls
///         <c>UiRenderer.RegisterImage</c> with a <c>TextureViewHandle</c>, because that assembly
///         *is* the host. This one is a plugin and has no renderer, so it asks the contract —
///         <see cref="IEditorGraphics.Upload" /> — which is the same route
///         <c>TexturingModule.Redraw</c> already uses for the paint pane's atlas.
///     </para>
///     <para>
///         ⚠ <b>Pixels rather than a view, and that is what makes it correct on a discrete card
///         rather than only here.</b> <c>TextureGraphPreviews</c>' own remarks say why the pictures
///         come back as bytes: every image the evaluator writes is <c>ResourceSharing.Exclusive</c>
///         and written on <c>IGraphicsDevice.ComputeQueue</c>, so handing one straight to the queue
///         the interface draws on leaves its contents undefined by specification.
///         <see cref="IEditorGraphics" /> takes bytes for the matching reason on its own side — a
///         storage image lacks <c>TextureUsage.Sampled</c> and is in the wrong layout — so the two
///         halves meet at the only thing both agree about.
///     </para>
///     <para>
///         ⚠ <b>A node keeps its number across a rebuild, and that is not a micro-optimisation.</b>
///         A preview is re-registered on every edit — which is every keystroke in a settings field —
///         and <see cref="IEditorGraphics.Upload" /> makes a texture and a descriptor set each time.
///         A forty-node graph would therefore cost forty of both per edit, which is
///         <a href="https://github.com/Rikarin/Vixen/issues/912">#912</a>'s finding with the atlas
///         swapped for a swatch. <see cref="IEditorGraphics.Update" /> rewrites the texels of the
///         picture that is already there, so the number stays valid and the canvas goes on drawing
///         it. A host that refuses the partial write — one with no thumbnail surface, which is a
///         real state and not only a test's — falls back to a fresh upload, and
///         <c>TextureGraphPreviews.Rebuild</c> releases the old number when the answer changes.
///     </para>
/// </remarks>
/// <param name="graphics">The host's graphics, which is what actually owns the textures.</param>
sealed class TexturePreviewImages(IEditorGraphics graphics) : ITexturePreviewImages, IDisposable {
    readonly IEditorGraphics graphics = graphics ?? throw new ArgumentNullException(nameof(graphics));
    readonly Dictionary<ulong, IEditorImage> live = [];

    /// <summary>How many pictures this sink is holding.</summary>
    /// <remarks>
    ///     ⚠ <b>Counted because the leak it measures is invisible</b>: an image left behind is a
    ///     texture and a descriptor set the editor holds for the rest of the session, and nothing
    ///     reports it. <c>TexturingModule</c>'s own <c>Release</c> says the same about the paint
    ///     pane's one upload; this is the same fact for a graph's worth of them.
    /// </remarks>
    public int Live => live.Count;

    /// <summary>How many pictures were rewritten in place rather than uploaded again.</summary>
    /// <remarks>
    ///     ⚠ <b>The half that separates "the swatches are right" from "the swatches are right and an
    ///     edit does not cost a texture per node".</b> Both look identical on screen — which is the
    ///     reason <c>RecordingGraphics</c> keeps its uploads and its patches apart one assembly over.
    /// </remarks>
    public int Patched { get; private set; }

    /// <inheritdoc />
    public ulong Register(Bitmap picture, ulong existing) {
        if (picture.Width <= 0 || picture.Height <= 0 || picture.Pixels.Length < picture.Width * picture.Height * 4) {
            return 0;
        }

        // ⚠ The extent is compared as well as the handle. A preview is `TextureGraphPreviews.Size`
        // squared today and a partial write into a picture of another size is a refusal at best —
        // and at worst, on the day that constant becomes a graph's own resolution, texels landing in
        // the wrong rows of a texture that is still the right object.
        if (existing != 0
            && live.TryGetValue(existing, out var held)
            && held.Width == picture.Width
            && held.Height == picture.Height
            && graphics.Update(held, 0, 0, picture.Width, picture.Height, picture.Pixels)) {
            Patched++;

            return existing;
        }

        if (graphics.Upload(picture.Width, picture.Height, picture.Pixels) is not { } made) {
            // A host with nothing to draw on. Zero is what the contract calls "could not be named",
            // and the canvas draws no swatch rather than a wrong one.
            return 0;
        }

        live[made.Image] = made;

        // ⚠ The old number is deliberately *not* released here. `TextureGraphPreviews.Rebuild` owns
        // that decision — it holds the node's previous number and releases it when this answer
        // differs — and a sink that also released would be the second owner of one lifetime.
        return made.Image;
    }

    /// <inheritdoc />
    public void Release(ulong image) {
        if (live.Remove(image, out var held)) {
            held.Dispose();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Belt and braces rather than the path that runs.</b> <c>TextureGraphPreviews.Dispose</c>
    ///     releases every number it registered, so an ordinary teardown empties this first. What this
    ///     covers is the order being wrong — a sink outliving the source that fed it — which would
    ///     otherwise be a texture per node held until the editor closes.
    /// </remarks>
    public void Dispose() {
        foreach (var held in live.Values) {
            held.Dispose();
        }

        live.Clear();
    }
}
