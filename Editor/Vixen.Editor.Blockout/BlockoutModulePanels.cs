// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Inspector;
using Vixen.Editor.Ui;
using Vixen.Input;
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

    /// <summary>The mode the panel's three settings objects belong to.</summary>
    readonly BlockoutMode mode = new();

    EditorShell? shell;

    EditorShell Shell =>
        shell ?? throw new InvalidOperationException("The module has not been activated, so it has no shell.");

    /// <summary>Registers the settings panel.</summary>
    void SettingsPanels() {
        Shell.RegisterPanel(
            SettingsPanel,
            new StringId("editor.panel.blockout", "Blockout"),
            panel => {
                Contextual(panel, BlockoutMode.BlockoutContext);

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
    }

    /// <summary>A titled divider, which is all a section is.</summary>
    static void Section(DockPanel panel, string title) => panel.Add("World-title").Text = title;

    /// <summary>Makes the panel claim the blockout command context when it is pressed in.</summary>
    /// <remarks>
    ///     ⚠ <b>On the capture leg, so it lands before anything inside the panel handles the
    ///     press.</b> Leaving a context matters as much as entering one: clicking a settings field has
    ///     to stop Delete meaning "delete the selected entity".
    /// </remarks>
    void Contextual(DockPanel panel, string context) =>
        panel.AddHandler<PointerEvent>(
            (_, args) => {
                if (args.Action == PointerAction.Pressed) {
                    Shell.Context = context;
                }
            },
            RoutingStrategy.Capture,
            handledEventsToo: true
        );
}
