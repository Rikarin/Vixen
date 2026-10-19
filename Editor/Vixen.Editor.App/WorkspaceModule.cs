// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Plugin;
using Vixen.Editor.Ui;
using Vixen.Editor.Ui.Documents;
using Vixen.Ui.Controls;

namespace Vixen.Editor.App;

/// <summary>The editor's layout presets, as a module.</summary>
/// <remarks>
///     <para>
///         <b>Doc 50 § D3, the Workspace context's first slice</b> (#1296). The presets were
///         <c>EditorApplication.Layouts</c>, seven <c>Shell.RegisterLayout</c> calls; they are
///         <see cref="PluginContext.AddLayout" />s now, which take themselves back on unload, and the
///         one preset made of another feature's panels — Profiling — is that feature's module's.
///     </para>
///     <para>
///         <b>And Save Layout and Reload Styles</b> (#1296), which waited on <see cref="IWorkspace" />:
///         they read the user store and the theme the application used to hold, and since #1294 the
///         workspace holds both — so the module names the service's verbs and reaches nothing of the
///         application's. Switching the module off takes both lines off the menus with it.
///     </para>
/// </remarks>
sealed class WorkspaceModule : IEditorPlugin {
    /// <summary>What the host activates it under.</summary>
    public const string ModuleId = "vixen.workspace";

    /// <summary>What the plugin manager calls it.</summary>
    public const string ModuleName = "Workspace";

    /// <inheritdoc />
    public void Activate(PluginContext context) {
        ArgumentNullException.ThrowIfNull(context);

        var workspace = context.Services.Require<IWorkspace>();

        context.AddCommand(
            new EditorCommand("view.save-layout", EditorStrings.CommandSaveLayout, () => workspace.SaveLayout()) {
                Category = EditorStrings.CategoryView
            }
        );

        context.AddCommand(
            new EditorCommand("tools.reload-styles", EditorStrings.CommandToolsReloadStyles, workspace.ReloadStyles) {
                Category = EditorStrings.CategoryTools
            }
        );

        // The five doc 11 names. They differ in which panels they show and how the middle is split,
        // which is the whole of what a layout preset is — the shapes come from `LayoutPresets` and
        // the panel lists are this application's.
        // ⚠ Project is a tab *behind* the console rather than under the hierarchy, and the two halves
        // of that are one decision. A content browser is read in tiles — a grid of thumbnails wants
        // width, and a quarter-width left column gives it two tiles a row — while the hierarchy is a
        // list of names and wants height. Stacking them in the left column made each of them the
        // other's problem. The console keeps the front tab because it is the one that has something
        // to say without being asked.
        context.AddLayout(
            "Default",
            EditorStrings.LayoutDefault,
            () => LayoutPresets.Standard(
                ["hierarchy"],
                ["scene"],
                // ⚠ Build Settings beside the inspector rather than in the middle. It is a form that
                // is read down, which is the shape the right-hand column already has — and it is
                // opened *against* a scene rather than instead of one, so a preset that gave it the
                // centre would put the viewport behind a tab.
                ["inspector", DeliveryModule.BuildPanel],
                ["console", "project"]
            )
        );

        context.AddLayout(
            "Scene",
            EditorStrings.LayoutScene,
            () => LayoutPresets.Standard(["hierarchy"], ["scene"], ["inspector"])
        );

        context.AddLayout(
            "Shading",
            EditorStrings.LayoutShading,
            () => LayoutPresets.Standard(["project"], ["scene"], ["inspector"], ["console"])
        );

        context.AddLayout(
            "Animation",
            EditorStrings.LayoutAnimation,
            () => LayoutPresets.Standard(["hierarchy"], ["scene"], ["inspector"], ["console"])
        );

        context.AddLayout(
            "Debug",
            EditorStrings.LayoutDebug,
            () => LayoutPresets.Split(["scene"], ["console"], 0.6f)
        );

        // ⚠ And the second of the two A6 owes, now that B5 exists. Its shape is the Profiling
        // preset's argument turned round (that one is the diagnostics module's, #1296): a cinematic is authored *against* the viewport, so the
        // scene keeps the width and the tracks get a wide, short strip under it — a timeline in a
        // right-hand slot is one where a two-second shot is forty pixels.
        context.AddLayout(
            "Sequencing",
            EditorStrings.LayoutSequencing,
            () => LayoutPresets.Standard(["hierarchy", "scenes"], ["scene"], ["inspector"], ["console"])
        );

        context.Shell.Workspace.DefaultPreset = "Default";
    }
}
