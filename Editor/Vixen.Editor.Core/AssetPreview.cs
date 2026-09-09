// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Core;

/// <summary>A picture of an asset, as pixels rather than as anything that needs a device.</summary>
/// <param name="Width">How wide, in pixels.</param>
/// <param name="Height">How tall.</param>
/// <param name="Rgba">The pixels, four bytes each, top row first — the same layout a decoder produces.</param>
/// <remarks>
///     ⚠ <b>Bytes and not a <c>TextureData</c>, and not an image number.</b> This travels from a pool
///     thread to the frame thread and is uploaded there, so a contributor that had to hold a texture
///     would have to hold the device — which is the coupling <c>IThumbnailSurface</c> exists to
///     refuse. It is also why this type is here rather than in an assembly that knows what a texture
///     is: a preview is a measurement of a file, and only the editor's own cache turns one into
///     something that draws.
/// </remarks>
public sealed record AssetPreviewImage(int Width, int Height, byte[] Rgba);

/// <summary>What draws the thumbnail for a kind of file the built-in decoders cannot read.</summary>
/// <param name="Extension">The file extension, with its dot, matched without regard to case.</param>
/// <param name="Render">Given the file's absolute path, its picture — or <see langword="null" />.</param>
/// <remarks>
///     <para>
///         <b>Doc 36 § D4's <c>AddPreview</c> row, on P2's terms.</b> A contribution kind is a record
///         in the assembly that owns it and <see cref="IEditorRegistry.Add" /> is the whole surface;
///         there is no method on <c>PluginContext</c> for this and deliberately is not.
///     </para>
///     <para>
///         ⚠ <b>Asked <i>before</i> the built-in decoders rather than after them.</b> The row in doc
///         36 says "asks <c>ImageDecoders</c> and falls back", and a registry consulted only on the
///         fallback path could never override the answer for an extension a built-in decoder also
///         claims — which is exactly what a plugin owning its own <c>.png</c>-shaped format needs.
///         Ties are broken by <see cref="Order" /> and then by registration order.
///     </para>
///     <para>
///         ⚠ <b><see cref="Render" /> runs on a pool thread, and it is resolved on the frame
///         thread.</b> A decode is tens of milliseconds and there are hundreds of them, so the work
///         cannot be on the frame thread; but the delegate belongs to a plugin that can be unloaded,
///         so which delegate a file uses is decided while the registry is being read and not two
///         seconds later inside the task.
///     </para>
///     <para>
///         ⚠ <b>Returning <see langword="null" /> is ordinary and is not an error.</b> A file being
///         written by another program, a truncated download, an extension that lies about its
///         contents: the answer is the type glyph, which is what the grid draws for everything with
///         no picture. A contributor that threw would take the editor down from a thread nobody is
///         watching.
///     </para>
/// </remarks>
public sealed record AssetPreview(string Extension, Func<string, AssetPreviewImage?> Render) {
    /// <summary>Which of two contributors for one extension is asked first; lower goes first.</summary>
    public int Order { get; init; }
}
