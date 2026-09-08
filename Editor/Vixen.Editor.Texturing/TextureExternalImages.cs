// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Core.Imaging;
using Vixen.Editor.Assets.Textures;
using Vixen.Editor.Core;
using Vixen.Editor.TextureGraph;
using Vixen.Editor.Texturing.Painting;

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
///         ⚠ <b>Rgba8 only, and it is a real limit rather than an oversight</b> — stated on
///         <see cref="TextureProjectImages" />, which now owns that refusal along with the rest of
///         what an asset reference means.
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
    ///     <para>
    ///         ⚠ <b>Every one of them, and only then the refusal.</b> A pane that returned at the
    ///         first would send an artist round the loop once per missing picture, which for a stack
    ///         that has been moved between projects is once per layer.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>One <see cref="TextureExternalPass" /> for the whole loop, which is
    ///         <a href="https://github.com/Rikarin/Vixen/issues/981">#981</a>.</b> The plan carries
    ///         one external per channel a layer writes, so without it a seven-channel paint layer
    ///         asked the store about the same file seven times — and, because the store re-reads
    ///         whatever the stamp says has changed, seven asks that straddle a write answer with two
    ///         different canvases and this one evaluation builds a map out of both.
    ///     </para>
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
        TextureExternalPass pass = new(canvases);

        foreach (var entry in owed) {
            if (Resolve(project, documentPath, uploads, plan, entry, pass) is { } why) {
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
    /// <param name="pass">
    ///     This fill's answers about the session's pixels: the open <c>.vxpaint</c> canvases, and the
    ///     imported pictures this resolver decodes into the store rather than re-reading per
    ///     evaluation — each of them resolved once for the pass rather than once per channel.
    /// </param>
    /// <returns>Null when it was uploaded, or the sentence saying why it was not.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A mesh map is not a file and is refused as one.</b> A <c>Source/Mesh Map</c>
    ///         crosses as <c>meshmap:curvature</c> rather than as a path, because what it names is a
    ///         measurement of a mesh this pane has not been told about; resolving it as a project
    ///         path would be a missing-file message about a file nobody named.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Only this plugin's own two schemes are here, and the project-asset half is
    ///         <see cref="TextureProjectImages.Resolve" /> one assembly down</b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1087">#1087</a>. It was here, and
    ///         <c>vixen texture bake --graph</c> therefore refused every graph with a
    ///         <c>Source/Bitmap</c> pointing at an imported PNG: a second copy in <c>Tools/</c> would
    ///         have been the copy that forgot a case, and referencing this plugin from there would
    ///         have dragged the editor shell into a command-line tool. What stayed is what is about a
    ///         live editor <em>session</em> — a mesh map nobody has baked here, and a canvas whose
    ///         strokes are still under the pointer.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The decode is passed down rather than moved down, and that is what keeps the
    ///         store in the loop.</b> <c>ImageDecoders</c> is <c>Vixen.Editor.Assets</c>', whose
    ///         closure is the whole runtime; and the caching this pane needs is
    ///         <see cref="PaintCanvasStore.Picture" />'s, which the evaluator's assembly has no way
    ///         to know about. Both live in the callback.
    ///     </para>
    /// </remarks>
    static string? Resolve(
        EditorProject project,
        string documentPath,
        TextureUploads uploads,
        TexturePlan plan,
        TextureGraphExternal entry,
        TextureExternalPass pass
    ) {
        var reference = entry.Asset.Trim();

        // A mesh map names a measurement rather than a file — see the type's remarks.
        if (reference.StartsWith(MeshMapScheme, StringComparison.Ordinal)) {
            return $"a layer reads '{reference}', which is a measurement of a mesh this pane has not been "
                + "told about rather than a file it can open.";
        }

        if (PaintReference.Claims(reference)) {
            return Painted(documentPath, uploads, plan, entry, reference, pass);
        }

        return TextureProjectImages.Resolve(project, uploads, plan, entry, file => Decoded(file, pass));
    }

    /// <summary>Reads one imported picture, through the session's store rather than off the disk.</summary>
    /// <param name="file">The asset's own file, absolute.</param>
    /// <param name="pass">This fill's answers, which are asked once a file rather than once a channel.</param>
    /// <returns>The picture, or the sentence saying why there is none.</returns>
    /// <remarks>
    ///     ⚠ <b>Through the store rather than straight off the disk</b> — #885's last bullet — <b>and
    ///     once per pass rather than once per channel</b>, which is #981. A preview runs on every
    ///     edit and this decoded the same unchanged PNG once per evaluation; the callback is what a
    ///     miss costs, and the stamp is taken before it. <c>Picture</c> never holds one it cannot
    ///     invalidate, so a deleted file is decoded — and refused — every time.
    /// </remarks>
    static (TextureData? Picture, string? Unreadable) Decoded(string file, TextureExternalPass pass) {
        var extension = Path.GetExtension(file);

        if (ImageDecoders.For(ImageDecoders.BuiltIn, extension) is not { } decoder) {
            return (null, $"nothing here decodes '{extension}'.");
        }

        return pass.Picture(
            file,
            path => {
                using var stream = File.OpenRead(path);

                return decoder.Decode(stream, extension);
            }
        );
    }

    /// <summary>Reads one channel of a paint layer's canvas and uploads it.</summary>
    /// <param name="documentPath">The open document, whose folder the canvas is beside.</param>
    /// <param name="uploads">Where the texture is made.</param>
    /// <param name="plan">The plan the image belongs to.</param>
    /// <param name="entry">The external to fill.</param>
    /// <param name="reference">Its <c>vxpaint:</c> reference.</param>
    /// <param name="pass">This fill's answers, which hold the open canvases and are asked once a file.</param>
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
    ///         ⚠ <b>The imported-picture path above no longer decodes its PNG on every evaluation
    ///         either, and the asymmetry that argument rested on was smaller than it looked.</b> It
    ///         was left alone for a batch on the grounds that an imported picture has no in-memory
    ///         writer and so wants an ordinary content cache rather than a store of live objects —
    ///         true, and beside the point: what the two share is the key, the stamp and the budget,
    ///         and the writer's half simply goes unused. <see cref="PaintCanvasStore.Picture" />
    ///         holds it, and the one behaviour that does not carry across is stated there — a
    ///         picture with no file is never held.
    ///     </para>
    /// </remarks>
    static string? Painted(
        string documentPath,
        TextureUploads uploads,
        TexturePlan plan,
        TextureGraphExternal entry,
        string reference,
        TextureExternalPass pass
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
        var (canvas, unreadable) = pass.Canvas(file);

        if (unreadable is not null) {
            return $"'{relative}' would not read: {unreadable}";
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
