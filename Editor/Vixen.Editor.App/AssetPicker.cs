// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Assets;
using Vixen.Editor.Assets.Content;
using Vixen.Editor.Core;
using Vixen.Editor.Inspector;
using Vixen.Editor.Inspector.Drawers;
using Vixen.Editor.Ui;
using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.App;

/// <summary>The grid of assets a field opens when its button is pressed.</summary>
/// <remarks>
///     <para>
///         <b>Doc 20's B3 calls this "small, and every asset field is dead without it", and both
///         halves were true.</b> <c>AssetDrawer</c> has raised <c>PickRequested</c> since it was
///         written and nothing has ever listened, so pressing the button in an inspector did
///         precisely nothing — which is doc 20's second bar failed in one click: a promise the editor
///         breaks the first time it is used, never mind the second.
///     </para>
///     <para>
///         ⚠ <b>A dialog rather than a panel, which is still not doc 20's end state — but the
///         thumbnails are in.</b> B3 asks for an "asset picker browser" with pictures in it, and the
///         pictures were the half that had to wait for a device: <see cref="AssetGrid" /> and
///         <c>ThumbnailCache</c> are the Project panel's, reused here rather than reimplemented.
///         What remains a dialog is the frame round it, through the shell's own
///         <c>DialogService</c> — which is what makes the picker screenshottable and drivable like
///         everything else.
///     </para>
///     <para>
///         ⚠ <b>Filtered by importer, not by CLR type.</b> A member names a runtime type — through
///         <c>[AssetPicker]</c> on an editor-side type, or <c>Vixen.Core</c>'s <c>[AssetType]</c> on
///         a component the runtime declares — and the database knows which importer claims a file.
///         <see cref="ImporterFor" /> is the join. A member whose type nothing recognises gets the
///         whole list, which is the honest failure: an empty picker for a field that has assets to
///         choose from is worse than a long one.
///     </para>
///     <para>
///         ⚠ <b>The same answer decides what a <i>drop</i> takes</b>, through <see cref="Accepts" />.
///         A drag is a second way to fill a field, and a drop path with an opinion of its own is one
///         that accepts what this list would never have offered.
///     </para>
/// </remarks>
sealed class AssetPicker {
    /// <summary>What the synthetic folder the grid is shown is called.</summary>
    /// <remarks>
    ///     ⚠ <b>Never seen: the picker hides the breadcrumb bar.</b> <see cref="AssetGrid" /> shows a
    ///     <i>folder</i>, because that is the question a browser answers — and the picker's question
    ///     is "every asset of this kind, wherever it is", which is a flat list with no folder above
    ///     it. Handing it one folder holding the filtered results is what makes the two the same
    ///     control; inventing a second grid for the flat case would be two things to keep agreeing
    ///     about what a tile looks like, which is exactly the disagreement F12 was.
    /// </remarks>
    const string Results = "Results";

    readonly EditorProject project;
    readonly DialogService dialogs;
    readonly ThumbnailCache? thumbnails;
    readonly IEditorRegistry? extensions;

    /// <summary>Which importer claims which extension, for the assets no import has run over yet.</summary>
    /// <remarks>
    ///     ⚠ <b>Because <c>AssetEntry.ImporterTag</c> is not filled in until the file has actually
    ///     been imported, and a filter that trusted it alone would refuse everything in a project
    ///     nobody has pressed Import Assets in yet.</b> A scan writes a sidecar and gives a file its
    ///     identity; which importer claims it is decided by the pipeline, which is a background task
    ///     the user may never have run. The extension is the same answer arrived at early — it is
    ///     literally what the pipeline will consult — so a freshly-dropped model is assignable to a
    ///     mesh field before anything has been built.
    /// </remarks>
    readonly ImporterRegistry importers = ProjectWorkspace.Importers();

    /// <summary>Prepares a picker over a project's assets.</summary>
    /// <param name="project">Whose assets.</param>
    /// <param name="dialogs">How the editor asks.</param>
    /// <param name="thumbnails">
    ///     Where the pictures come from, or <see langword="null" /> for a headless editor — which is
    ///     the ordinary state in every test, and where the grid draws type glyphs.
    /// </param>
    /// <param name="extensions">
    ///     Who has contributed an <c>AssetIcon</c>, so a plugin's asset kind gets its own glyph here
    ///     as well as in the browser. Null falls back to the built-in set.
    /// </param>
    public AssetPicker(
        EditorProject project,
        DialogService dialogs,
        ThumbnailCache? thumbnails = null,
        IEditorRegistry? extensions = null
    ) {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(dialogs);

        this.project = project;
        this.dialogs = dialogs;
        this.thumbnails = thumbnails;
        this.extensions = extensions;
    }

    /// <summary>What an asset is called in the picker and in the field.</summary>
    /// <param name="asset">Its id.</param>
    /// <returns>Its file name, or <see langword="null" /> when the project does not have it.</returns>
    /// <remarks>
    ///     ⚠ <b>Null rather than the id, because the drawer says something better.</b> An unresolved
    ///     GUID shows as <c>Missing (…)</c> with the id in it — an asset deleted out from under a
    ///     scene is exactly what somebody needs to see, and a name invented here would hide it.
    /// </remarks>
    public string? NameOf(AssetId asset) =>
        project.Assets.TryGetByGuid(asset, out var entry) ? entry.Name : null;

    /// <summary>Opens the picker for a field and assigns whatever is chosen.</summary>
    /// <param name="field">The field the button belongs to.</param>
    /// <remarks>
    ///     ⚠ <b>Written through the field rather than to the member, and sealed afterwards.</b> That
    ///     is what puts the assignment on the document's undo stack as one step — the same path the
    ///     text and number drawers take, which is why an asset assigned here is undone by the same
    ///     Ctrl+Z as everything else in the panel.
    /// </remarks>
    public void Open(InspectorField field) {
        ArgumentNullException.ThrowIfNull(field);

        if (!field.CanWrite) {
            return;
        }

        var candidates = Candidates(field.Member.AssetType);

        _ = Ask();

        async Task Ask() {
            AssetGrid? grid = null;

            // ⚠ Subscribed for as long as the dialog is open and dropped the moment it is not. A
            // decode lands a few frames after the tile that asked for it was drawn, so without this
            // the picker shows glyphs until something else happens to rebind — which, for a modal
            // dialog nobody is scrolling, is never. And a subscription left behind would hold a
            // removed grid alive for the rest of the session, once per asset field ever opened.
            void Arrived() => grid?.Refresh();

            if (thumbnails is { } cache) {
                cache.Changed += Arrived;
            }

            try {
                var chosen = await dialogs.ShowAsync<AssetId?>(
                    "Select " + field.Member.DisplayName,
                    session => grid = Fill(session, candidates, field.Member.AllowNull, KindOf(field.Member))
                ).ConfigureAwait(true);

                // ⚠ Through the drawer rather than straight into the field, because the member may
                // hold an `AssetReference` rather than an `AssetId` — and `Write` boxes whatever it
                // is handed for a generated setter that casts. An id written into a reference member
                // is an `InvalidCastException` raised from inside a dialog's click handler, which
                // kills the frame rather than refusing the edit.
                if (chosen is { } asset && AssetDrawer.Assign(field, asset)) {
                    field.Seal();
                }
            } finally {
                if (thumbnails is { } watched) {
                    watched.Changed -= Arrived;
                }
            }
        }
    }

    /// <summary>Whether a field of this kind would take a particular asset.</summary>
    /// <param name="member">The member being assigned.</param>
    /// <param name="asset">What is being offered to it.</param>
    /// <returns>Whether the assignment is one the picker would have offered.</returns>
    /// <remarks>
    ///     ⚠ <b>The same question the picker's list answers, asked one asset at a time — and it has
    ///     to be the same code.</b> A drag is a second way to assign, and a drop path with its own
    ///     opinion about what a mesh field accepts is one that takes a texture the picker would never
    ///     have listed. A folder is refused outright: it has an identity in the database, so it can
    ///     be dragged, and there is no member anywhere that means "a folder".
    /// </remarks>
    public bool Accepts(InspectorMember member, AssetId asset) {
        ArgumentNullException.ThrowIfNull(member);

        if (!project.Assets.TryGetByGuid(asset, out var entry) || entry.IsFolder) {
            return false;
        }

        return ImporterFor(member.AssetType) is not { } wanted
            || string.Equals(ImporterOf(entry), wanted, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Which importer an asset belongs to, whether or not one has run over it.</summary>
    /// <inheritdoc cref="importers" select="remarks" />
    string? ImporterOf(AssetEntry entry) =>
        string.IsNullOrEmpty(entry.ImporterTag)
            ? importers.TryGetForFile(entry.Path, out var importer) ? importer.Name : null
            : entry.ImporterTag;

    /// <summary>Every asset a field of this type could hold, folders excluded.</summary>
    IReadOnlyList<AssetEntry> Candidates(Type? assetType) {
        var wanted = ImporterFor(assetType);

        return [
            .. project.Assets.Entries
                .Where(entry => !entry.IsFolder)
                .Where(
                    entry => wanted is null
                        || string.Equals(ImporterOf(entry), wanted, StringComparison.OrdinalIgnoreCase)
                )
                .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
        ];
    }

    /// <summary>Which importer's output a runtime type comes from.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>These are the importers' own names, and the fact that they had not been is why no
    ///         typed picker had ever shown a single row.</b> An importer is named after its settings
    ///         type's <c>[DataContract]</c> alias — <c>TextureImporter</c>, <c>AudioImporter</c> —
    ///         and that is what a <c>.meta</c> file records and what <c>AssetEntry.ImporterTag</c>
    ///         holds. This table answered <c>"texture"</c>, which matches nothing, so every
    ///         <c>[AssetPicker(typeof(Texture))]</c> field in the editor opened onto "The project has
    ///         no assets of this kind" in a project full of them. Nothing caught it because the only
    ///         fixture with a type filter names a type this table does not recognise, which takes the
    ///         branch below instead.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>By name, and <see langword="null" /> for anything unrecognised.</b> The
    ///         alternative is asking each importer what runtime type it produces, which none of them
    ///         knows: an importer writes bytes to an artefact and the type is decided by whoever
    ///         loads it. So the join stays a table — but the *failure* stays generous. A member
    ///         naming a type nothing here recognises gets the whole list, because an empty picker for
    ///         a field with assets to choose from is worse than a long one, and a plugin's asset kind
    ///         should not be unassignable merely because this table has not heard of it.
    ///     </para>
    /// </remarks>
    internal static string? ImporterFor(Type? assetType) =>
        assetType?.Name switch {
            "Texture" or "Texture2D" or "TextureData" => "TextureImporter",
            "Mesh" or "MeshData" or "Model" => "ModelImporter",
            "Material" => "MaterialImporter",
            "AudioClip" => "AudioImporter",
            "VfxEffectContent" or "VisualEffect" => "VfxImporter",
            "VideoClip" => "VideoImporter",
            "NavMeshData" => "NavMeshImporter",
            "SceneAsset" or "PrefabAsset" => "SceneImporter",
            _ => null
        };

    /// <summary>What to call the kind of asset a member wants, in a sentence.</summary>
    /// <param name="member">The member.</param>
    /// <returns>The name, or <see langword="null" /> for a member that takes anything.</returns>
    /// <remarks>
    ///     The member's own type name, humanised — <c>AudioClip</c> becomes <c>Audio Clip</c> —
    ///     rather than the importer's, because the importer is the editor's plumbing and the type is
    ///     what the person who wrote the component wrote. Only stated when the filter is real: a
    ///     member whose type this cannot place is offered everything, and telling them the list is
    ///     "every Widget in the project" when it is in fact every asset would be a lie in the one
    ///     sentence they read.
    /// </remarks>
    static string? KindOf(InspectorMember member) =>
        ImporterFor(member.AssetType) is null ? null : InspectorMember.Humanise(member.AssetType!.Name);

    /// <summary>Builds the dialog: a search box, a grid of tiles, and the two ways out.</summary>
    /// <returns>The grid, so the caller can rebind it when a picture arrives.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Doc 20's B3 asked for an "asset picker browser" with thumbnails and this is that
    ///         half.</b> A name is what an asset is <i>called</i> and a picture is which one it is:
    ///         choosing between <c>T_Crate_01</c> and <c>T_Crate_02</c> off a list of names is
    ///         reading, guessing and undoing, which is exactly what a picker exists to avoid.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The same <see cref="AssetGrid" /> the Project panel uses, over a folder that is
    ///         invented here.</b> A second grid for the flat case would be a second answer to "what
    ///         does a tile look like", and F12 is what that costs: the same asset drawn two ways in
    ///         two panes of one panel, which nobody reports and everybody notices.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every match is shown rather than the first two hundred.</b> The list this
    ///         replaces drew a row per result and so had to stop somewhere; the grid is virtualised,
    ///         so a folder of forty thousand costs about sixty tiles. The search box is still the way
    ///         through a large project — it is just no longer the only way to see the last of it.
    ///     </para>
    /// </remarks>
    AssetGrid Fill(
        DialogSession<AssetId?> session,
        IReadOnlyList<AssetEntry> candidates,
        bool allowNull,
        string? kind
    ) {
        var search = session.Body.Add<SearchBox>();
        search.Placeholder = kind is null ? "Search assets…" : $"Search {kind.ToLowerInvariant()}s…";

        var empty = session.Body.Add<TextBlock>();
        empty.AddClass("asset-picker-empty");

        var grid = session.Body.Add<AssetGrid>();

        grid.AddClass("asset-picker-grid");
        grid.Art = Art;
        grid.Picture = Pictured;

        // ⚠ The tile answers the dialog rather than merely selecting: a picker where choosing is a
        // click and then a second click on OK is one people click twice by mistake and once too few
        // by habit. `Selected` is the press, which is the same gesture the list's rows answered on.
        grid.Selected += node => {
            if (node.IsIndexed) {
                session.Answer(node.Guid);
            }
        };

        Rebuild(candidates);

        search.ValueChanged += (_, value) => {
            List<AssetEntry> shown = [];

            foreach (var entry in candidates) {
                if (string.IsNullOrWhiteSpace(value)
                    || entry.Name.Contains(value, StringComparison.OrdinalIgnoreCase)) {
                    shown.Add(entry);
                }
            }

            Rebuild(shown);
        };

        if (allowNull) {
            // ⚠ Only where the member permits it. A field that cannot be null offering a "None"
            // button is a button that either fails silently or writes something the type forbids.
            session.AddButton("None", () => AssetId.Empty);
        }

        session.AddButton(EditorStrings.DialogCancel.Text, () => null);

        return grid;

        void Rebuild(IReadOnlyList<AssetEntry> shown) {
            grid.Show(
                new AssetTreeNode(
                    Results,
                    string.Empty,
                    IsFolder: true,
                    AssetId.Empty,
                    [.. shown.Select(entry => new AssetTreeNode(entry.Name, entry.Path, false, entry.Guid, []))]
                )
            );

            // ⚠ Names the kind when there is one. "The project has no assets of this kind" over an
            // empty grid leaves the reader to work out which kind that was — and the two reasons a
            // grid is empty (there are none, or the filter is wrong) look identical until it says
            // which type it was looking for.
            empty.Text = shown.Count > 0
                ? string.Empty
                : candidates.Count == 0
                    ? kind is null
                        ? "The project has no assets."
                        : $"The project has no {kind.ToLowerInvariant()} assets."
                    : "Nothing matches.";

            if (shown.Count > 0) {
                empty.AddClass("hidden");
            } else {
                empty.RemoveClass("hidden");
            }
        }
    }

    /// <summary>Which glyph an asset gets, resolved the way the Project panel resolves it.</summary>
    /// <remarks>
    ///     ⚠ <b>Through <c>EditorArt</c> and the <c>AssetIcon</c> contributions, not a switch of its
    ///     own.</b> A picker with its own table is a picker that draws a plugin's asset kind as a
    ///     blank page while the browser two panels away draws it properly.
    /// </remarks>
    IconArt Art(AssetTreeNode asset) =>
        EditorArt.Of(
            extensions?.All<AssetIcon>() ?? StandardIcons.Assets,
            asset.IsIndexed && project.Assets.TryGetByGuid(asset.Guid, out var entry) ? ImporterOf(entry) : null,
            asset.Name
        ) ?? StandardIcons.Unknown;

    /// <summary>The picture for an asset, asking for one if there is none yet.</summary>
    ulong Pictured(AssetTreeNode asset) =>
        asset.IsIndexed && thumbnails is { } cache && cache.TryGet(asset.Guid, out var image) ? image : 0;
}
