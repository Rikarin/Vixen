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

/// <summary>The list an asset field opens when its button is pressed.</summary>
/// <remarks>
///     <para>
///         <b>Doc 20's B3 calls this "small, and every asset field is dead without it", and both
///         halves were true.</b> <c>AssetDrawer</c> has raised <c>PickRequested</c> since it was
///         written and nothing has ever listened, so pressing the button in an inspector did
///         precisely nothing — which is doc 20's second bar failed in one click: a promise the editor
///         breaks the first time it is used, never mind the second.
///     </para>
///     <para>
///         ⚠ <b>A dialog rather than a panel, and that is not the end state.</b> Doc 20's B3 asks for
///         an "asset picker browser" with thumbnails, which needs the thumbnail service that arrives
///         with the grid view. What a field needs first is to be assignable at all, and a searchable
///         list of the right assets is that — through the shell's own <c>DialogService</c>, so it is
///         screenshottable and drivable like everything else.
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
    /// <summary>How many rows the list shows before it asks the user to type something.</summary>
    /// <remarks>
    ///     A project has thousands of assets and a dialog is not a content browser. The search box is
    ///     the way through a large project, and a list that tried to draw all of it would be slow to
    ///     open for no benefit — the row somebody wants is not the four-hundredth.
    /// </remarks>
    const int Limit = 200;

    readonly EditorProject project;
    readonly DialogService dialogs;

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
    public AssetPicker(EditorProject project, DialogService dialogs) {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(dialogs);

        this.project = project;
        this.dialogs = dialogs;
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
            var chosen = await dialogs.ShowAsync<AssetId?>(
                "Select " + field.Member.DisplayName,
                session => Fill(session, candidates, field.Member.AllowNull, KindOf(field.Member))
            ).ConfigureAwait(true);

            // ⚠ Through the drawer rather than straight into the field, because the member may hold
            // an `AssetReference` rather than an `AssetId` — and `Write` boxes whatever it is handed
            // for a generated setter that casts. An id written into a reference member is an
            // `InvalidCastException` raised from inside a dialog's click handler, which kills the
            // frame rather than refusing the edit.
            if (chosen is { } asset && AssetDrawer.Assign(field, asset)) {
                field.Seal();
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

    /// <summary>Builds the dialog: a search box, a list, and the two ways out.</summary>
    static void Fill(
        DialogSession<AssetId?> session,
        IReadOnlyList<AssetEntry> candidates,
        bool allowNull,
        string? kind
    ) {
        var search = session.Body.Add<SearchBox>();
        search.Placeholder = kind is null ? "Search assets…" : $"Search {kind.ToLowerInvariant()}s…";

        var list = session.Body.Add<UiElement>("asset-picker-list");
        var shown = new List<AssetEntry>(candidates);

        Rebuild();

        search.ValueChanged += (_, value) => {
            shown.Clear();

            foreach (var entry in candidates) {
                if (string.IsNullOrWhiteSpace(value)
                    || entry.Name.Contains(value, StringComparison.OrdinalIgnoreCase)) {
                    shown.Add(entry);
                }
            }

            Rebuild();
        };

        if (allowNull) {
            // ⚠ Only where the member permits it. A field that cannot be null offering a "None"
            // button is a button that either fails silently or writes something the type forbids.
            session.AddButton("None", () => AssetId.Empty);
        }

        session.AddButton(EditorStrings.DialogCancel.Text, () => null);

        void Rebuild() {
            while (list.Children.Count > 0) {
                list.Children[^1].Remove();
            }

            if (shown.Count == 0) {
                // ⚠ Names the kind when there is one. "The project has no assets of this kind" over an
                // empty list leaves the reader to work out which kind that was — and the two reasons
                // a list is empty (there are none, or the filter is wrong) look identical until it
                // says which type it was looking for.
                list.Add<TextBlock>().Text = candidates.Count == 0
                    ? kind is null
                        ? "The project has no assets."
                        : $"The project has no {kind.ToLowerInvariant()} assets."
                    : "Nothing matches.";

                return;
            }

            foreach (var entry in shown.Take(Limit)) {
                var asset = entry.Guid;

                var row = list.Add<Button>();
                row.Label = entry.Name;
                row.Variant = ControlVariant.Subtle;
                row.AddClass("asset-picker-row");

                // ⚠ The row answers the dialog rather than merely selecting: a picker where choosing
                // is a click and then a second click on OK is one people click twice by mistake and
                // once too few by habit.
                row.Clicked += _ => session.Answer(asset);
            }

            if (shown.Count > Limit) {
                list.Add<TextBlock>().Text = $"and {shown.Count - Limit} more — type to narrow it down";
            }
        }
    }
}
