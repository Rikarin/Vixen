// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core;
using Vixen.Editor.Core;
using Vixen.Core.Yaml;
using Vixen.Core.Yaml.Meta;
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

    /// <summary>What a build ships this asset under, or empty for its path.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Writable, where everything above is not, and the difference is where the value
    ///         lives.</b> The rows above are the database's envelope — a name is a file on disk and a
    ///         GUID is written once and never again — and an address is neither: it is a per-asset
    ///         fact stored in the sidecar's <c>Addressable</c> block, which is exactly the kind of
    ///         thing an inspector edits. Without a box for it the only way to make an asset
    ///         addressable was to open the <c>.meta</c> in a text editor, which is what
    ///         "there is no addressable UI" meant in practice.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Empty means the asset's own path, not "not shipped".</b> Every asset is
    ///         addressable by where it is, so this box is for the addresses that are <i>contracts</i>
    ///         — a level a save game names, a pack a URL is built from — which are worth setting
    ///         because they survive the file being moved and the path by definition does not.
    ///         <see cref="Excluded" /> is how something is kept out.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Clearing it clears the whole block rather than writing an empty address.</b>
    ///         <c>BuildPlanner</c> reads a null address as "use the path" and an empty string would
    ///         be a name — an asset addressed <c>""</c> is one every load of <c>""</c> would find.
    ///     </para>
    /// </remarks>
    [Inspector]
    [Header("Addressable")]
    [Tooltip("What the game loads this by. Empty is the asset's own path, which is what almost everything wants.")]
    public string Address {
        get => Meta?.Addressable?.Address ?? string.Empty;
        set => Amend(info => info with { Address = Trimmed(value) });
    }

    /// <summary>Which bundle group it belongs to, or empty to inherit its folder's.</summary>
    /// <remarks>
    ///     A name rather than a picker over the project's <c>.vxgroup</c> files, deliberately: a
    ///     group is inherited from the folder when it is not named here, and a dropdown with no
    ///     "inherit" entry would make the common case the one that needs explaining. The Addressables
    ///     panel is where the groups themselves are made.
    /// </remarks>
    [Inspector]
    [Tooltip("Which .vxgroup packs it. Empty inherits the folder's group, which is the usual answer.")]
    public string Group {
        get => Meta?.Addressable?.Group ?? string.Empty;
        set => Amend(info => info with { Group = Trimmed(value) });
    }

    /// <summary>Labels for bulk loading, comma separated.</summary>
    /// <remarks>
    ///     ⚠ <b>Comma separated rather than a list editor, which is the same call
    ///     <c>CompositorNode</c> makes about its own.</b> A list drawer over three short strings is
    ///     four rows of chrome and a set of add/remove buttons for something people type in one go.
    /// </remarks>
    [Inspector]
    [Tooltip("Labels a bulk load can ask for, comma separated.")]
    public string Labels {
        get => string.Join(", ", Meta?.Addressable?.Labels ?? []);

        set => Amend(info => info with {
                Labels = [.. (value ?? string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
            }
        );
    }

    /// <summary>Whether to keep it out of the build entirely.</summary>
    /// <remarks>
    ///     ⚠ <b>The opt-out that only became necessary when an absent address stopped meaning
    ///     "not shipped".</b> Every asset is addressable by its path now, so "leave this one out" had
    ///     to become something a project can say rather than something it expressed by saying
    ///     nothing. What it is for is the source file nothing loads at run time — a layered PSD a
    ///     texture was flattened from, a reference FBX kept beside the one that ships.
    /// </remarks>
    [Inspector]
    [Tooltip("Keep this asset out of the build. Anything addressable that depends on it then fails the build.")]
    public bool Excluded {
        get => Meta?.Addressable?.Excluded ?? false;
        set => Amend(info => info with { Excluded = value });
    }

    /// <summary>Renders it as its name.</summary>
    /// <returns>The name.</returns>
    public override string ToString() => Name;

    AssetEntry? Entry => project.Assets.TryGetByGuid(Asset, out var entry) ? entry : null;

    /// <summary>Where this asset's sidecar is, or <see langword="null" /> if it has none.</summary>
    string? Sidecar => Entry is { } entry
        ? AssetMetaFile.PathFor(project.Paths.Absolute(entry.Path))
        : null;

    AssetMeta? meta;
    DateTime stamp;

    /// <summary>The sidecar as it stands, or <see langword="null" /> if it cannot be read.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Kept until the file's timestamp moves, which is the one place this type caches
    ///         anything.</b> Everything else here is a dictionary lookup into an index that is
    ///         already in memory; a sidecar is a disk read and a YAML parse, and three rows ask for
    ///         it — so reading it per access meant three reads and three parses <i>per drawn frame</i>
    ///         for as long as an asset was selected. That is not merely wasteful: it was enough to
    ///         starve the thumbnail decoder running on the pool beside it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A stat rather than a flag, so an import is still seen.</b> An import rewrites the
    ///         sidecar behind the inspector's back — that is what an import is — and a copy held
    ///         across one would be a panel editing a version of the file that no longer exists and
    ///         then writing it back over the new one. Comparing the last-write time costs a stat and
    ///         keeps the rule this type's own remarks state.
    ///     </para>
    /// </remarks>
    AssetMeta? Meta {
        get {
            if (Sidecar is not { } path) {
                return null;
            }

            DateTime written;

            try {
                written = File.GetLastWriteTimeUtc(path);
            } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
                return null;
            }

            // A file that is not there reports a sentinel from the epoch rather than throwing, which
            // is what makes this one call rather than an Exists and a stat.
            if (written == default) {
                meta = null;
                return null;
            }

            if (meta is not null && written == stamp) {
                return meta;
            }

            stamp = written;

            try {
                meta = AssetMetaFile.ReadFile(path);
            } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or YamlParseException or YamlBindingException) {
                // A sidecar somebody is halfway through hand-editing is not an editor that falls
                // over — the rows read as blank, which is the honest answer to "what does it say".
                meta = null;
            }

            return meta;
        }
    }

    /// <summary>Rewrites the addressable block of the sidecar.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Read, amend, write — never construct.</b> The sidecar carries the GUID, the
    ///         importer's own settings and the sub-asset table, none of which this knows anything
    ///         about; writing a fresh <c>AssetMeta</c> with only an address on it would lose the
    ///         identity every other file in the project refers to this one by.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The block is dropped entirely when nothing is left in it.</b> An
    ///         <c>Addressable:</c> key holding three nulls is a diff on everybody's checkout and a
    ///         file that says the asset is configured when it is not.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Not undoable, and it says so out loud.</b> This writes a file on disk directly,
    ///         which is the same bargain the settings window makes: the undo stack belongs to a
    ///         scene, and a Ctrl+Z aimed at the viewport that silently rewrote a sidecar would be
    ///         worse than no undo at all.
    ///     </para>
    /// </remarks>
    void Amend(Func<AddressableInfo, AddressableInfo> change) {
        if (Sidecar is not { } path || Meta is not { } current) {
            return;
        }

        var updated = change(current.Addressable ?? new AddressableInfo());

        var empty = updated.Address is null or { Length: 0 }
            && updated.Group is null or { Length: 0 }
            && updated.Labels.Length == 0
            && !updated.Excluded;

        try {
            AssetMetaFile.WriteFile(path, current with { Addressable = empty ? null : updated });
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            // A read-only checkout is an ordinary thing to meet. The row reads back what is still on
            // disk on the next draw, which is the truthful outcome and is visible.
        } finally {
            // ⚠ Dropped by hand rather than left to the timestamp. A write and the read after it can
            // land inside one tick of the file system's clock, and a cache that only notices a
            // *different* stamp would then serve the version from before the edit — which is a row
            // that snaps back to its old value one frame after it was typed into.
            meta = null;
            stamp = default;
        }
    }

    static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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
