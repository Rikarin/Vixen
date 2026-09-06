// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Core.Imaging;
using Vixen.Editor.Assets.Textures;
using Vixen.Editor.Core;
using Vixen.Editor.TextureGraph;
using Vixen.Editor.Texturing.Painting;
using Vixen.Graphics;

namespace Vixen.Editor.Texturing;

/// <summary>Fills a compiled plan's external images out of a project, for a pane that is about to draw it.</summary>
/// <remarks>
///     <para>
///         <b>The host half of <a href="https://github.com/Rikarin/Vixen/issues/818">#818</a>, and it
///         is here rather than on a preview because there are two previews.</b>
///         <c>TextureGraphExternals.Upload</c> fills the externals whose bytes the compilation
///         carries — a ramp, a curve table — and hands back the ones naming an asset, because a
///         compiler that runs on every edit must not touch an <c>AssetDatabase</c>. Everything after
///         that is this: the database resolves the reference, <c>ImageDecoders</c> reads the file,
///         and the texels go up through the same <c>TextureUploads</c>.
///     </para>
///     <para>
///         ⚠ <b>It was <c>LayerStackPreview</c>'s three private statics, and
///         <c>TextureGraphPreview</c> needed the same loop the moment it stopped evaluating a
///         checkerboard</b> (<a href="https://github.com/Rikarin/Vixen/issues/792">#792</a>). Two
///         copies of it would be two answers to "what does this pane say about a missing picture" —
///         and one of them would be the copy that never learned about <c>vxpaint:</c>.
///         <a href="https://github.com/Rikarin/Vixen/issues/849">#849</a> wants the same seam again
///         from the paint side, which is why it takes a project and a document path rather than a
///         document.
///     </para>
///     <para>
///         ⚠ <b>Every failure is a returned sentence and none is an exception, including the ones
///         that are this build's fault.</b> A preview runs on every edit and a throw out of one takes
///         the editor's frame with it — so a file that has been deleted, a format nothing decodes,
///         and a decoder that read the file and produced nothing are all the same kind of answer.
///     </para>
///     <para>
///         ⚠ <b>Rgba8 only, and it is a real limit rather than an oversight.</b> The plan's external
///         image for a <c>Source/Bitmap</c> is <c>Rgba8</c> — <c>BitmapNode</c> says why — so a KTX2
///         or DDS asset that decodes to a block-compressed format has the wrong byte count for the
///         image it would fill, and <c>TextureUploads.Add</c> would refuse it with a message about a
///         byte count rather than about a file. Named here instead.
///     </para>
/// </remarks>
static class TextureExternalImages {
    /// <summary>What a mesh map's reference starts with, rather than a path.</summary>
    /// <remarks>
    ///     ⚠ <b>Duplicated from <c>TextureMeshMaps.Scheme</c>, which is <c>internal</c> to
    ///     <c>Vixen.Editor.TextureGraph</c> and visible to its own tests alone.</b> The alternative
    ///     is resolving <c>meshmap:curvature</c> as a project path and telling an artist that a file
    ///     of that name is missing. <c>LayerStackPanelDeviceTests</c> asserts a mesh-map layer still
    ///     gets the sentence, which is the only thing that can catch the two drifting apart.
    /// </remarks>
    public const string MeshMapScheme = "meshmap:";

    /// <summary>Fills every external image a plan needs, and says which ones it could not.</summary>
    /// <param name="project">Whose assets resolve a reference.</param>
    /// <param name="documentPath">
    ///     The open document's own file, absolute — what a <c>vxpaint:</c> reference is relative to.
    /// </param>
    /// <param name="uploads">Where the textures are made, and what owns them.</param>
    /// <param name="plan">The plan, which says what format and size each image is.</param>
    /// <param name="externals">What the compilation said fills each of them.</param>
    /// <param name="canvases">The session's open <c>.vxpaint</c> canvases — see <see cref="Painted" />.</param>
    /// <returns>One sentence per external that could not be filled, in the order the plan names them.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <remarks>
    ///     ⚠ <b>Every one of them, and only then the refusal.</b> A pane that returned at the first
    ///     would send an artist round the loop once per missing picture, which for a stack that has
    ///     been moved between projects is once per layer.
    /// </remarks>
    public static List<string> Fill(
        EditorProject project,
        string documentPath,
        TextureUploads uploads,
        TexturePlan plan,
        ImmutableArray<TextureGraphExternal> externals,
        PaintCanvasStore canvases
    ) {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(uploads);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(canvases);

        var owed = TextureGraphExternals.Upload(uploads, plan, externals);
        List<string> unresolved = [];

        foreach (var entry in owed) {
            if (Resolve(project, documentPath, uploads, plan, entry, canvases) is { } why) {
                unresolved.Add(why);
            }
        }

        return unresolved;
    }

    /// <summary>Reads one external image out of the project and uploads it.</summary>
    /// <param name="project">Whose assets resolve a reference.</param>
    /// <param name="documentPath">The open document's own file, for a reference relative to it.</param>
    /// <param name="uploads">Where the texture is made, and what owns it.</param>
    /// <param name="plan">The plan the image belongs to, which says what format and size it is.</param>
    /// <param name="entry">The external the compilation could not fill.</param>
    /// <param name="canvases">The session's open <c>.vxpaint</c> canvases.</param>
    /// <returns>Null when it was uploaded, or the sentence saying why it was not.</returns>
    /// <remarks>
    ///     ⚠ <b>A mesh map is not a file and is refused as one.</b> A <c>Source/Mesh Map</c> crosses
    ///     as <c>meshmap:curvature</c> rather than as a path, because what it names is a measurement
    ///     of a mesh this pane has not been told about; resolving it as a project path would be a
    ///     missing-file message about a file nobody named.
    /// </remarks>
    static string? Resolve(
        EditorProject project,
        string documentPath,
        TextureUploads uploads,
        TexturePlan plan,
        TextureGraphExternal entry,
        PaintCanvasStore canvases
    ) {
        var reference = entry.Asset.Trim();

        // A mesh map names a measurement rather than a file — see the type's remarks.
        if (reference.StartsWith(MeshMapScheme, StringComparison.Ordinal)) {
            return $"a layer reads '{reference}', which is a measurement of a mesh this pane has not been "
                + "told about rather than a file it can open.";
        }

        if (PaintReference.Claims(reference)) {
            return Painted(documentPath, uploads, plan, entry, reference, canvases);
        }

        if (!project.Assets.TryGetByPath(reference, out var asset)) {
            return $"'{reference}' is not in this project's assets, so there is nothing to read.";
        }

        var file = project.Paths.Absolute(asset.Path);
        var extension = Path.GetExtension(file);

        if (ImageDecoders.For(ImageDecoders.BuiltIn, extension) is not { } decoder) {
            return $"nothing here decodes '{extension}', so '{reference}' cannot be read.";
        }

        TextureData decoded;

        try {
            using var stream = File.OpenRead(file);

            decoded = decoder.Decode(stream, extension);
        } catch (Exception failure) when (failure is IOException
            or InvalidDataException or NotSupportedException or ArgumentException
            or UnauthorizedAccessException) {
            return $"'{reference}' would not read: {failure.Message}";
        }

        if (decoded.Format != PixelFormat.Rgba8UNorm) {
            return $"'{reference}' decoded as {decoded.Format} and a graph's imported image is Rgba8, so this "
                + "pane cannot upload it. Import it as an uncompressed 8-bit picture.";
        }

        try {
            uploads.Add(plan, entry.Image, decoded.Width, decoded.Height, decoded.Level(0));
        } catch (ArgumentException failure) {
            return $"'{reference}' could not be uploaded: {failure.Message}";
        }

        return null;
    }

    /// <summary>Reads one channel of a paint layer's canvas and uploads it.</summary>
    /// <param name="documentPath">The open document, whose folder the canvas is beside.</param>
    /// <param name="uploads">Where the texture is made.</param>
    /// <param name="plan">The plan the image belongs to.</param>
    /// <param name="entry">The external to fill.</param>
    /// <param name="reference">Its <c>vxpaint:</c> reference.</param>
    /// <param name="canvases">The session's open canvases, which this consults before the disk.</param>
    /// <returns>Null when it was uploaded, or the sentence saying why it was not.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>The host half of <a href="https://github.com/Rikarin/Vixen/issues/852">#852</a>,
    ///         and it is the imported-picture path with one substitution.</b> #818's resolver reads
    ///         a file the <c>AssetDatabase</c> knows about through <c>ImageDecoders</c>; a
    ///         <c>.vxpaint</c> is not in that database and no decoder reads it, so it is resolved
    ///         against the <em>document's own folder</em> and read by <c>PaintCanvas</c>. Everything
    ///         after that — the byte order, the format, the upload — is identical, because
    ///         <c>PaintImage</c>'s texels are already RGBA8 with red first.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Relative to the document rather than to the project.</b> <c>LayerAsset.Paint</c>
    ///         is documented as relative to the stack, and <c>LayerPaint.NameFor</c> derives a bare
    ///         file name — so a stack in a subfolder whose canvases were resolved from the project
    ///         root would read a file from the wrong folder or, worse, another stack's.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A channel the canvas does not hold is transparent rather than an error.</b> A
    ///         paint layer writes every channel it does not restrict, and an artist who has painted
    ///         base colour alone has a canvas with one image in it. Refusing here would make the
    ///         first stroke on a seven-channel set produce six sentences; an absent channel
    ///         contributes nothing, which is what not having painted it means.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Asked of the session's open canvases rather than of the disk, and where that
    ///         store lives is the whole of <a href="https://github.com/Rikarin/Vixen/issues/885">#885</a>.</b>
    ///         This used to open and read the file on every evaluation — a preview runs on every
    ///         edit, and a 4K canvas is 67 MB a channel. The cache #885 asked for was deliberately
    ///         not written into this resolver, and the reason it gives is the design: a cache this
    ///         pane owned and the paint session did not would serve the picture from <em>before</em>
    ///         the stroke, because a session writes <c>PaintImage.Texels</c> in memory and does not
    ///         touch the file until pointer-up. <c>PaintCanvasStore</c> holds the canvas objects
    ///         themselves, so the pane and the drag read the same texels and staleness cannot arise.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An open canvas is served even when there is no file, which is a behaviour change
    ///         and the point of it.</b> The refusal below now fires only when a layer names a canvas
    ///         nothing has open <em>and</em> nothing wrote — a stack moved between projects — rather
    ///         than for every stack whose first stroke is still under the pointer.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The imported-picture path above still decodes its PNG on every evaluation, and
    ///         that is now the asymmetric half.</b> It is left alone deliberately: an imported
    ///         picture has no in-memory writer, so it is an ordinary cache rather than this store,
    ///         and #885's measurement says the <c>.vxpaint</c> is the expensive one.
    ///     </para>
    /// </remarks>
    static string? Painted(
        string documentPath,
        TextureUploads uploads,
        TexturePlan plan,
        TextureGraphExternal entry,
        string reference,
        PaintCanvasStore canvases
    ) {
        if (!PaintReference.TryParse(reference, out var relative, out var usage)) {
            return $"a layer reads '{reference}', which claims to be painted pixels and does not name both a "
                + "file and a channel. That is a builder's fault rather than yours.";
        }

        var folder = Path.GetDirectoryName(documentPath);

        if (string.IsNullOrEmpty(folder)) {
            return $"'{relative}' is named relative to this stack and this stack has no folder, so there is "
                + "nothing to resolve it against.";
        }

        var file = Path.GetFullPath(Path.Combine(folder, relative));

        PaintCanvas? canvas;

        try {
            canvas = canvases.Open(file);
        } catch (Exception failure) when (failure is IOException
            or InvalidDataException or UnauthorizedAccessException or EndOfStreamException) {
            return $"'{relative}' would not read: {failure.Message}";
        }

        if (canvas is null) {
            return $"'{relative}' is the painted canvas this layer names and there is no such file beside the "
                + "stack, so its pixels cannot be read.";
        }

        if (!canvas.Has(usage)) {
            // Not a failure: an unpainted channel is an absent one. Filled with transparency so the
            // layer's blend composites nothing rather than black — `Blend.rvn` reads the foreground's
            // alpha as its amount, so zero alpha leaves the backdrop exactly as it was.
            PaintImage empty = new(canvas.Width, canvas.Height);

            return Uploaded(uploads, plan, entry, relative, empty);
        }

        return Uploaded(uploads, plan, entry, relative, canvas.Channel(usage));
    }

    /// <summary>Puts one paint image into the plan's external slot.</summary>
    static string? Uploaded(
        TextureUploads uploads,
        TexturePlan plan,
        TextureGraphExternal entry,
        string relative,
        PaintImage image
    ) {
        try {
            uploads.Add(plan, entry.Image, image.Width, image.Height, image.Texels);
        } catch (ArgumentException failure) {
            return $"'{relative}' could not be uploaded: {failure.Message}";
        }

        return null;
    }
}
