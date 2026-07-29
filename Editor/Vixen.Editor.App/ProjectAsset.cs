// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core;
using Vixen.Editor.Core;
using Vixen.Editor.Inspector;

namespace Vixen.Editor.App;

/// <summary>One asset, as a row of editors — what <see cref="SceneEntity" /> is for an entity.</summary>
/// <remarks>
///     <para>
///         <b>The other half of the join, and it lives here for the same reason the first half
///         does.</b> The project browser knows GUIDs and the inspector knows objects with members;
///         something has to be the object, and putting it in the application is what keeps
///         <c>Vixen.Editor.Inspector</c> from knowing what an asset database is. Selecting a file
///         used to set <c>EditorProject.Selection</c> and stop there — the selection was a dead end
///         with nothing reading it, so a click in the Project panel changed a highlight and nothing
///         else.
///     </para>
///     <para>
///         ⚠ <b>The envelope, not the importer's settings.</b> <see cref="AssetEntry" /> is
///         deliberately what the database knows without parsing a sidecar — its own remarks say why —
///         and this shows exactly that. Import settings are a document with an undo stack and an
///         apply/revert of its own, which is what double-clicking opens; showing an editable copy of
///         them here would be a second writer to the same file with no idea the first exists.
///     </para>
///     <para>
///         ⚠ <b>Every row is read-only, and that is a statement rather than an omission.</b> Renaming
///         an asset moves a file and rewrites every reference to it — <c>EditorContext.Touch</c>
///         exists for precisely that operation — and there is no command for it yet. A writable Name
///         box here would rename the object in memory and leave the file where it was.
///     </para>
///     <para>
///         ⚠ <b>Nothing is cached: every read goes back to the database.</b> Two of these over one
///         GUID therefore cannot disagree, and a rescan that re-indexed the file is seen rather than
///         painted over. An asset that has gone reads as blanks rather than throwing, for the reason
///         <see cref="SceneEntity" /> gives about dead entities: a selection outlives the thing it
///         names, and an inspector that threw while drawing a row takes the editor down with it.
///     </para>
/// </remarks>
public sealed class ProjectAsset {
    readonly EditorProject project;

    /// <summary>Which asset.</summary>
    public AssetId Asset { get; }

    /// <summary>Whether the database still has an entry for it.</summary>
    public bool Exists => project.Assets.TryGetByGuid(Asset, out _);

    /// <summary>Views an asset.</summary>
    /// <param name="project">The project it belongs to.</param>
    /// <param name="asset">Its GUID.</param>
    public ProjectAsset(EditorProject project, AssetId asset) {
        ArgumentNullException.ThrowIfNull(project);

        this.project = project;
        Asset = asset;
    }

    /// <summary>What the file is called.</summary>
    [Inspector]
    [Tooltip("The file's name. Renaming an asset moves the file and rewrites what refers to it, which is not something the inspector can do yet.")]
    public string Name => Entry is { } entry ? entry.Name : string.Empty;

    /// <summary>Where it is, project-relative.</summary>
    [Inspector]
    [Tooltip("Where the file is, relative to the project root.")]
    public string Path => Entry is { } entry ? entry.Path : string.Empty;

    /// <summary>Whether it is a folder rather than a file.</summary>
    [Inspector]
    [Tooltip("Folders are assets too: they carry a GUID so that moving one does not break what is inside it.")]
    public bool IsFolder => Entry is { IsFolder: true };

    /// <summary>How big the file is.</summary>
    /// <remarks>
    ///     ⚠ <b>Text rather than a number, and read from the disk rather than from the index.</b> The
    ///     database holds the envelope and not the size, so this is a stat — cheap, and taken when
    ///     the row is drawn rather than kept, so a reimport that changed the file shows the new
    ///     figure. A file that has been deleted under the editor reads as blank rather than throwing.
    /// </remarks>
    [Inspector(Name = "Size")]
    [Header("File")]
    [Tooltip("How big the file on disk is.")]
    public string FileSize {
        get {
            if (Entry is not { IsFolder: false } entry) {
                return string.Empty;
            }

            try {
                return Bytes(new FileInfo(project.Paths.Absolute(entry.Path)).Length);
            } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
                return string.Empty;
            }
        }
    }

    /// <summary>Which importer claims it.</summary>
    [Inspector]
    [Tooltip("The importer named in the sidecar. 'None' is what a file no importer has claimed says, and it is not an error.")]
    public string Importer => Entry is { } entry ? entry.ImporterTag ?? "None" : string.Empty;

    /// <summary>Its identity, as the thirty-two characters a sidecar holds.</summary>
    /// <remarks>
    ///     Shown because it is what every other file in the project refers to it by, and the thing
    ///     somebody grepping a scene file for a broken reference has in their hand.
    /// </remarks>
    [Inspector(Name = "GUID")]
    [Tooltip("The identity everything else refers to this asset by. It survives the file being moved or renamed.")]
    public string Identity => Asset.IsEmpty ? string.Empty : Asset.ToString();

    /// <summary>How many assets refer to it.</summary>
    /// <remarks>
    ///     From the reverse index the project already builds, so it costs a dictionary lookup rather
    ///     than a scan. It is the answer to "what breaks if I delete this", which is the question the
    ///     Project panel is most often open for.
    /// </remarks>
    [Inspector]
    [Header("Usage")]
    [Tooltip("How many other assets refer to this one.")]
    public int ReferencedBy => project.References.ReferrersOf(Asset).Count;

    /// <summary>Renders it as its name.</summary>
    /// <returns>The name.</returns>
    public override string ToString() => Name;

    AssetEntry? Entry => project.Assets.TryGetByGuid(Asset, out var entry) ? entry : null;

    /// <summary>A byte count somebody can read at a glance.</summary>
    static string Bytes(long count) {
        string[] units = ["B", "KB", "MB", "GB", "TB"];

        double size = count;
        var unit = 0;

        while (size >= 1024d && unit < units.Length - 1) {
            size /= 1024d;
            unit++;
        }

        return unit == 0
            ? count.ToString(CultureInfo.InvariantCulture) + " B"
            : size.ToString(size < 10d ? "0.##" : "0.#", CultureInfo.InvariantCulture) + " " + units[unit];
    }
}
