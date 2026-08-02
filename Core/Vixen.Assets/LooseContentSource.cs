// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.IO;
using Vixen.Core.Serialization.Storage;

namespace Vixen.Assets;

/// <summary>Opening a directory of imported artefacts as content, with nothing packed.</summary>
/// <remarks>
///     <para>
///         <b>The half of a content mount that needs no bundles, no cache and no transport</b> — a
///         catalog whose every entry names no bundle, and the artefact store the import wrote the
///         chunks to. That is the whole of what "loose content" is, and it is what an editor has:
///         importing an asset leaves its chunk in <c>Library/ArtifactDb</c> and a catalog beside it,
///         and the cost of making a change visible should be the import of the one asset that
///         changed.
///     </para>
///     <para>
///         ⚠ <b>Here rather than in a head, because there are two heads.</b> <c>Vixen.App</c>'s
///         <c>ContentMount</c> has done this since loose content existed, and the editor cannot reach
///         it: that assembly owns a frame loop built around an ECS world and a fixed-step
///         accumulator, and the editor's loop is an interface that redraws when something changes —
///         a division its project file states and this must not undo for the sake of one factory.
///     </para>
///     <para>
///         ⚠ <b>The same addresses a shipped build resolves.</b> The catalog is written by the same
///         planner from the same sidecars; what differs is where the bytes are. A viewport that read
///         content through an editor-shaped path of its own would agree with the game by coincidence,
///         which is the property that makes testing against an editor worth anything.
///     </para>
///     <para>
///         ⚠ <b>The artefact store is mounted up front where a bundle is mounted on demand</b>, and
///         it has to be: an entry that names no bundle asks the asset manager for nothing, so a store
///         opened lazily would be opened never.
///     </para>
/// </remarks>
public static class LooseContentSource {
    /// <summary>What the artefact store is called inside the directory.</summary>
    /// <remarks>
    ///     Spelled the same as the editor's <c>LooseContent.ArtifactFolderName</c> and the app's
    ///     <c>ContentMount.ArtifactFolderName</c> — the name-spelled-twice bargain the catalog and
    ///     the shader bundle already make, and for the same reason: a writer and a reader that share
    ///     a constant share an assembly, and these three deliberately do not.
    /// </remarks>
    public const string ArtifactFolderName = "ArtifactDb";

    /// <summary>And the catalog.</summary>
    public const string CatalogFileName = "catalog.bin";

    /// <summary>Opens a mounted directory of imported artefacts.</summary>
    /// <param name="files">The file system the directory is already mounted in.</param>
    /// <param name="root">Where it is mounted.</param>
    /// <param name="refusal">Why there is no content, when this answers null.</param>
    /// <returns>The manager, or null with a reason.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="files" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A <see cref="VirtualPath" /> that is already mounted, not a directory on the
    ///         host.</b> Engine code addresses files through <see cref="VirtualFileSystem" /> and the
    ///         analyzer enforces it — mounting a physical directory is a head's job, which is exactly
    ///         the division that put this factory here instead of in one head.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A reason rather than an exception, because "not imported yet" is the ordinary
    ///         state of a new project.</b> A head that threw here could not open one, and the message
    ///         a person needs is "import first" rather than a stack.
    ///     </para>
    /// </remarks>
    public static AssetManager? Open(VirtualFileSystem files, VirtualPath root, out string? refusal) {
        ArgumentNullException.ThrowIfNull(files);

        var catalogPath = root / CatalogFileName;

        if (!files.Exists(catalogPath)) {
            refusal = $"There is no {CatalogFileName} at {root}. Import the project first.";

            return null;
        }

        ContentCatalog catalog;

        try {
            using var stream = files.OpenRead(catalogPath);
            using var buffer = new MemoryStream();

            stream.CopyTo(buffer);
            catalog = CatalogFormat.Read(buffer.ToArray());
        } catch (Exception failure) when (failure is IOException or InvalidDataException or CatalogFormatException) {
            refusal = $"{CatalogFileName} could not be read: {failure.Message}";

            return null;
        }

        var artifacts = files.Exists(root / ArtifactFolderName)
            ? new ObjectDatabase(new FileOdbBackend(files, root / ArtifactFolderName, isReadOnly: true))
            : null;

        if (artifacts is null) {
            // ⚠ Not a refusal. A catalog with no artefacts beside it is a project whose import wrote
            // nothing — an empty project, which is a thing to open and add an asset to rather than a
            // thing to refuse. Every address in it misses, and a miss is already survivable.
            refusal = $"There is no {ArtifactFolderName} at {root}; every address will miss.";
        } else {
            refusal = null;
        }

        // ⚠ A local source over the same root, and it resolves nothing for a loose catalog by
        // construction: every entry names no bundle. It exists because `AssetManager` takes one, and
        // because a directory that has *both* — a packed build somebody pointed at a project's
        // artefacts — reads the artefacts first, which is the order an editor rebuilding into them
        // needs.
        return new(catalog, new LocalBundleSource(files, root), artifacts);
    }
}
