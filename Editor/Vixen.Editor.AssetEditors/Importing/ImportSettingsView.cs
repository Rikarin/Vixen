// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Inspector;
using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.AssetEditors.Importing;

/// <summary>The three sections every import-settings editor has, whatever it is importing.</summary>
/// <remarks>
///     <para>
///         The base settings, where the asset appears in a build, and the per-target matrix. A
///         texture editor is this plus a channel viewer; a model editor is this plus a part list —
///         which is the whole reason it is a control rather than three copies of the same wiring.
///     </para>
///     <para>
///         ⚠ <b>Nothing here has an Apply button.</b> Every edit is already a command on the
///         document's stack and the document is already dirty; a second, weaker commit point would
///         mean two answers to "have my changes taken effect" and an editor where Ctrl+S and Apply
///         can disagree. What re-imports the asset is the import pass noticing that the settings
///         hash moved, which is a thing the shell runs and not a thing a panel does.
///     </para>
/// </remarks>
public sealed class ImportSettingsView : Control {
    ImportSettingsDocument? document;

    /// <inheritdoc />
    protected override string TagName => "import-settings";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The editors for the base settings.</summary>
    public InspectorView Settings { get; private set; } = null!;

    /// <summary>The address, group and labels.</summary>
    public InspectorView Addressable { get; private set; } = null!;

    /// <summary>The per-target grid.</summary>
    public TargetOverrideMatrix Overrides { get; private set; } = null!;

    /// <summary>Shown when the sidecar holds keys this build does not understand.</summary>
    public Alert Unknown { get; private set; } = null!;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Unknown = Part<Alert>();
        Unknown.AddClass("hidden");
        Unknown.Title = "This sidecar has settings this editor does not know";

        Settings = Section("Import Settings").Add<InspectorView>();
        Settings.Search.SetStyle("display", "none");

        Overrides = Section("Platform Overrides").Add<TargetOverrideMatrix>();
        Addressable = Section("Addressable").Add<InspectorView>();
        Addressable.Search.SetStyle("display", "none");
    }

    /// <summary>Shows a document's settings.</summary>
    /// <param name="settings">The document.</param>
    public void Show(ImportSettingsDocument settings) {
        ArgumentNullException.ThrowIfNull(settings);

        document = settings;

        Settings.EditedDocument = settings;
        Settings.Inspect(settings.Settings);

        Addressable.EditedDocument = settings;
        Addressable.Inspect(settings.Addressable);

        Overrides.Show(settings);

        if (settings.UnknownKeys.Count == 0) {
            Unknown.AddClass("hidden");
            return;
        }

        Unknown.RemoveClass("hidden");

        // ⚠ Named, and kept. The keys survive a save because the document edits the node tree rather
        // than a bound object; saying so is what stops somebody "cleaning up" the file by hand and
        // deleting a setting a newer editor put there on purpose.
        Unknown.Message = "They are left exactly as they are on save: "
            + string.Join(", ", settings.UnknownKeys);
    }

    /// <summary>Reads every editor back from the document, without rebuilding anything.</summary>
    public void Reload() {
        Settings.Reload();
        Addressable.Reload();
        Overrides.Reload();
    }

    /// <summary>The document being shown, or <see langword="null" />.</summary>
    /// <remarks>
    ///     Not called <c>Document</c>: a <see cref="UiElement" /> already has one of those and it is
    ///     the interface tree this control lives in — the same collision <c>InspectorView</c>
    ///     resolves by calling its own <c>EditedDocument</c>.
    /// </remarks>
    public ImportSettingsDocument? Edited => document;

    UiElement Section(string title) {
        var expander = Part<Expander>();

        expander.Label = title;
        expander.IsExpanded = true;

        return expander.Content;
    }
}
