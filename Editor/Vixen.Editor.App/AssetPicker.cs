// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Core;
using Vixen.Editor.Inspector;
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
///         ⚠ <b>Filtered by importer tag, not by CLR type.</b> An <c>[AssetPicker(typeof(Texture))]</c>
///         member names a runtime type and the database knows which importer claimed a file; the two
///         are joined by name rather than by a table, because the table would be a third place to
///         keep in step with every importer added. A member whose type nothing recognises gets the
///         whole list, which is the honest failure — an empty picker for a field that has assets to
///         choose from is worse than a long one.
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
                session => Fill(session, candidates, field.Member.AllowNull)
            ).ConfigureAwait(true);

            if (chosen is { } asset && field.Write(asset)) {
                field.Seal();
            }
        }
    }

    /// <summary>Every asset a field of this type could hold, folders excluded.</summary>
    IReadOnlyList<AssetEntry> Candidates(Type? assetType) {
        var tag = TagFor(assetType);

        return [
            .. project.Assets.Entries
                .Where(entry => !entry.IsFolder)
                .Where(entry => tag is null || string.Equals(entry.ImporterTag, tag, StringComparison.OrdinalIgnoreCase))
                .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
        ];
    }

    /// <summary>Which importer's output a runtime type comes from.</summary>
    /// <remarks>
    ///     ⚠ <b>By name, and <see langword="null" /> for anything unrecognised.</b> The alternative is
    ///     a table mapping every runtime type to an importer tag, which would be a third place to
    ///     keep in step with every importer that is ever added — and one that fell behind would show
    ///     an empty picker for a field with plenty to choose from. Falling back to everything is the
    ///     failure a person can work around.
    /// </remarks>
    static string? TagFor(Type? assetType) =>
        assetType?.Name switch {
            "Texture" or "Texture2D" => "texture",
            "Mesh" or "Model" => "model",
            "Material" => "material",
            "AudioClip" => "audio",
            "Shader" => "shader",
            _ => null
        };

    /// <summary>Builds the dialog: a search box, a list, and the two ways out.</summary>
    static void Fill(DialogSession<AssetId?> session, IReadOnlyList<AssetEntry> candidates, bool allowNull) {
        var search = session.Body.Add<SearchBox>();
        search.Placeholder = "Search assets…";

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
                list.Add<TextBlock>().Text = candidates.Count == 0
                    ? "The project has no assets of this kind."
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
