// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Inspector;
using Vixen.Editor.Ui;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.Blockout;

/// <summary>The settings panel doc 24 § P3 and doc 36 § P1 both owed, as one panel over three objects.</summary>
/// <remarks>
///     <para>
///         <b>What was missing was a panel, not a provider — doc 36 § P1's audit says so in as many
///         words.</b> The remesher's and the unwrapper's settings are ordinary members of ordinary
///         classes here, <c>InspectorEditProvider</c> describes them and <c>InspectorView</c> draws
///         them, which is the same arrangement the terrain panels have had all along. Nothing in this
///         file builds a row.
///     </para>
///     <para>
///         ⚠ <b>A mode panel, so it opens with Blockout and closes with it.</b>
///         <see cref="BlockoutMode.Panel" /> names it — a settings panel left behind for a tool
///         nobody is holding is what <c>IEditorMode.Panel</c> exists to prevent.
///     </para>
///     <para>
///         ⚠ <b>The verb runs through the command registry rather than by calling the mode.</b> A
///         button, a menu item and a key chord are then one implementation, and a verb that is not
///         reachable right now is greyed by the command's own enablement rather than by a second copy
///         of the rule.
///     </para>
/// </remarks>
public sealed partial class BlockoutModule {
    /// <summary>What the blockout settings panel is called in an arrangement.</summary>
    internal const string SettingsPanel = BlockoutMode.PanelId;

    /// <summary>What the UV panel is called in an arrangement.</summary>
    /// <remarks>
    ///     ⚠ <b>Not <c>IEditorMode.Panel</c>'s, because that names exactly one.</b> A registered panel
    ///     comes with its own toggle command and therefore its own View-menu line, which is what makes
    ///     a second panel discoverable without the mode having to open it.
    /// </remarks>
    internal const string UvPanel = "blockout.uv";

    /// <summary>The mode the panel's three settings objects belong to.</summary>
    readonly BlockoutMode mode = new();

    /// <summary>docs/plan/42 § D13's model, owned here because the view outlives no panel factory.</summary>
    /// <remarks>
    ///     ⚠ <b>The module's and not the view's.</b> A panel factory runs again on every reopen, so a
    ///     model made inside it would throw away the islands somebody had just unwrapped the moment
    ///     they moved the tab. The view subscribes and unsubscribes; the model stays.
    /// </remarks>
    readonly BlockoutUvPanel uv = new();

    EditorShell? shell;

    EditorShell Shell =>
        shell ?? throw new InvalidOperationException("The module has not been activated, so it has no shell.");

    /// <summary>Registers the settings panel.</summary>
    void SettingsPanels() {
        Shell.RegisterPanel(
            SettingsPanel,
            new StringId("editor.panel.blockout", "Blockout"),
            panel => {
                panel.WhenPressedIn(() => Shell.Context = BlockoutMode.BlockoutContext);

                Section(panel, "Retopology");

                var retopology = panel.Add<InspectorView>();

                retopology.EditedDocument = null;
                retopology.Inspect(mode.Retopology);

                var run = panel.Add<Button>();

                run.Label = "Retopologize";
                run.Clicked += _ => Shell.Commands.Execute(BlockoutMode.RetopologizeCommand);

                Section(panel, "UV Charting");

                var charting = panel.Add<InspectorView>();

                charting.EditedDocument = null;
                charting.Inspect(mode.Charting);

                Section(panel, "UV Packing");

                var packing = panel.Add<InspectorView>();

                packing.EditedDocument = null;
                packing.Inspect(mode.Packing);
            }
        );

        BlockoutTheme.Install(Shell.Document);

        Shell.RegisterPanel(
            UvPanel,
            new StringId("editor.panel.blockout-uv", "Blockout UV"),
            panel => {
                var view = panel.Add<BlockoutUvView>();

                // ⚠ The same two settings objects the inspector above edits, read at the moment a
                // verb runs rather than copied now — see `BlockoutUvView.Configure`.
                view.Source = () => mode.Editing?.Mesh;

                view.Configure = model => {
                    model.Settings = mode.Charting.ToUvSettings();
                    model.Packing = mode.Packing.ToPackSettings();
                };

                view.Model = uv;
            }
        );
    }

    /// <summary>A titled divider, which is all a section is.</summary>
    static void Section(DockPanel panel, string title) => panel.Add("world-title").Text = title;

}
