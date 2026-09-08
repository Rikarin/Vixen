// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Core.Imaging;
using Vixen.Editor.Core;
using Vixen.Graphics;

namespace Vixen.Editor.TextureGraph;

/// <summary>Fills a compiled plan's external images out of a project's own assets.</summary>
/// <remarks>
///     <para>
///         <b>The half of the editor's resolver that is about a project on disk rather than about a
///         live session</b> — <a href="https://github.com/Rikarin/Vixen/issues/1087">#1087</a>.
///         <see cref="TextureGraphExternals.Upload" /> fills the externals whose bytes the
///         compilation carries and hands back the ones naming an asset, so that a host with no asset
///         database can see before it starts that this graph is not one it can bake. Every host that
///         <em>does</em> have one then wants the same six steps, and until now those six steps
///         existed once, <see langword="internal" />, inside the texturing plugin — so
///         <c>vixen texture bake --graph</c> could bake every generator, pattern and noise graph and
///         refused any graph with a <c>Source/Bitmap</c> pointing at an imported PNG.
///     </para>
///     <para>
///         ⚠ <b>The decode is the caller's and that is a measurement rather than a preference.</b>
///         <c>ImageDecoders</c> lives in <c>Vixen.Editor.Assets</c>, whose project closure is 54
///         assemblies against this one's 32 and adds 25 of them — <c>Vixen.Engine</c>,
///         <c>Vixen.Ecs</c>, <c>Vixen.Terrain</c>, <c>Vixen.Water</c>, <c>Vixen.Navigation</c>,
///         <c>Vixen.Editor.VfxGraph</c> among them (measured over every <c>ProjectReference</c> in
///         the tree on 2026-09-08). Referencing it to save one <c>switch</c> on a file extension
///         would put the whole runtime behind an evaluator whose README says it "knows nothing about
///         a project, a document or a panel". <c>Vixen.Editor.Core</c>, which
///         <see cref="EditorProject" /> comes from, adds <b>nothing</b>: it is already in this
///         assembly's closure through <c>Vixen.Editor.NodeGraph</c>, so naming it directly widens
///         what this compilation may <em>spell</em> and not what it loads.
///     </para>
///     <para>
///         ⚠ <b>So the cut is between what drifts silently and what drifts loudly.</b> Which folder
///         an asset reference resolves against, what a missing one is called, and the refusal of a
///         format the plan's external slot cannot hold are all here, because two hosts disagreeing
///         about any of them produces a picture rather than an error. Choosing a decoder for an
///         extension is the caller's, because a host that gets it wrong fails to read a file and
///         says so.
///     </para>
///     <para>
///         ⚠ <b>Rgba8 only, and it is a real limit rather than an oversight.</b> The plan's external
///         image for a <c>Source/Bitmap</c> is <c>Rgba8</c> — <c>BitmapNode</c> says why — so a KTX2
///         or DDS asset that decodes to a block-compressed format has the wrong byte count for the
///         image it would fill, and <c>TextureUploads.Add</c> would refuse it with a message about a
///         byte count rather than about a file. Named here instead.
///     </para>
///     <para>
///         ⚠ <b>Every failure is a returned sentence and none is an exception, including the ones
///         that are this build's fault.</b> A preview runs on every edit and a throw out of one takes
///         the editor's frame with it — so a file that has been deleted, a format nothing decodes,
///         and a decoder that read the file and produced nothing are all the same kind of answer.
///     </para>
/// </remarks>
public static class TextureProjectImages {
    /// <summary>Fills every external naming a project asset, and says which ones it could not.</summary>
    /// <param name="project">Whose assets resolve a reference, and whose index has been scanned.</param>
    /// <param name="uploads">Where the textures are made, and what owns them.</param>
    /// <param name="plan">The plan, which says what format and size each image is.</param>
    /// <param name="owed">
    ///     What <see cref="TextureGraphExternals.Upload" /> handed back: the externals naming an
    ///     asset rather than carrying their own bytes.
    /// </param>
    /// <param name="read">How to turn an absolute file into texels — see <see cref="Resolve" />.</param>
    /// <returns>One sentence per external that could not be filled, in the order the plan names them.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <remarks>
    ///     ⚠ <b>Every one of them, and only then the refusal.</b> A host that returned at the first
    ///     would send an author round the loop once per missing picture, which for a stack that has
    ///     been moved between projects is once per layer.
    ///     <para>
    ///         A host that has schemes of its own — the texturing plugin has two — walks the entries
    ///         itself and calls <see cref="Resolve" /> for the ones its own schemes did not claim.
    ///         This overload is for a host that has none, which is what a build script is.
    ///     </para>
    /// </remarks>
    public static List<string> Fill(
        EditorProject project,
        TextureUploads uploads,
        TexturePlan plan,
        ImmutableArray<TextureGraphExternal> owed,
        Func<string, (TextureData? Picture, string? Unreadable)> read
    ) {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(uploads);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(read);

        List<string> unresolved = [];

        foreach (var entry in owed) {
            if (Resolve(project, uploads, plan, entry, read) is { } why) {
                unresolved.Add(why);
            }
        }

        return unresolved;
    }

    /// <summary>Reads one external image out of the project's assets and uploads it.</summary>
    /// <param name="project">Whose assets resolve the reference, and whose index has been scanned.</param>
    /// <param name="uploads">Where the texture is made, and what owns it.</param>
    /// <param name="plan">The plan the image belongs to, which says what format and size it is.</param>
    /// <param name="entry">The external the compilation could not fill.</param>
    /// <param name="read">
    ///     How to turn an absolute file into texels: the picture, or the sentence saying why there is
    ///     none. Choosing a decoder and caching what has already been decoded are both behind this,
    ///     for the reasons the type's remarks give — the editor answers out of the store that holds
    ///     the session's live pixels, and a build script opens the file.
    /// </param>
    /// <returns>Null when it was uploaded, or the sentence saying why it was not.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <remarks>
    ///     ⚠ <b>A reference carrying a scheme is refused rather than looked up as a path.</b> A
    ///     <c>Source/Mesh Map</c> crosses as <c>meshmap:curvature</c> and a paint layer's channel as
    ///     <c>vxpaint:…</c>; both name something a live editor session supplies, and resolving either
    ///     as a project path would answer with a missing-file message about a file nobody named. A
    ///     host that <em>can</em> fill one claims it before calling this.
    /// </remarks>
    public static string? Resolve(
        EditorProject project,
        TextureUploads uploads,
        TexturePlan plan,
        TextureGraphExternal entry,
        Func<string, (TextureData? Picture, string? Unreadable)> read
    ) {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(uploads);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(read);

        var reference = entry.Asset.Trim();

        if (SchemeOf(reference) is { } scheme) {
            return $"a layer reads '{reference}', and '{scheme}:' names something a live editor session "
                + "supplies — a baked mesh map, an open paint canvas — rather than a file in this "
                + "project, so nothing here can read it. Bake this graph from the editor instead.";
        }

        if (!project.Assets.TryGetByPath(reference, out var asset)) {
            return $"'{reference}' is not in this project's assets, so there is nothing to read.";
        }

        var (picture, unreadable) = read(project.Paths.Absolute(asset.Path));

        if (picture is not { } decoded) {
            return $"'{reference}' would not read: {unreadable ?? "the host gave no reason."}";
        }

        if (decoded.Format != PixelFormat.Rgba8UNorm) {
            return $"'{reference}' decoded as {decoded.Format} and a graph's imported image is Rgba8, so it "
                + "cannot be uploaded. Import it as an uncompressed 8-bit picture.";
        }

        try {
            uploads.Add(plan, entry.Image, decoded.Width, decoded.Height, decoded.Level(0));
        } catch (ArgumentException failure) {
            return $"'{reference}' could not be uploaded: {failure.Message}";
        }

        return null;
    }

    /// <summary>The scheme a reference carries, or null when it names a path.</summary>
    /// <remarks>
    ///     ⚠ <b>Two characters at least, and before the first separator</b>, so that a reference
    ///     which happens to contain a colon deeper in — a folder somebody named that way — is still
    ///     a path, and so that a Windows drive letter could never be read as one. An asset reference
    ///     is project-relative and therefore never carries a drive letter in the first place; the
    ///     rule is written to survive somebody handing this an absolute path anyway.
    /// </remarks>
    static string? SchemeOf(string reference) {
        for (var index = 0; index < reference.Length; index++) {
            var character = reference[index];

            if (character is '/' or '\\') {
                return null;
            }

            if (character == ':') {
                return index >= 2 ? reference[..index] : null;
            }
        }

        return null;
    }
}
