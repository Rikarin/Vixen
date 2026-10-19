// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Ai;
using Vixen.Editor.AssetEditors;
using Vixen.Editor.Assets;
using Vixen.Editor.Blockout;
using Vixen.Editor.Diagnostics;
using Vixen.Editor.Plugin;
using Vixen.Editor.Scripts;
using Vixen.Editor.ShaderGraph;
using Vixen.Editor.Terrain;
using Vixen.Editor.Terrain.Physics;
using Vixen.Editor.Texturing;
using Vixen.Editor.VfxGraph;
using Vixen.Editor.Water;

namespace Vixen.Editor.App;

/// <summary>The features this editor ships, as the list the composition root hands over.</summary>
/// <remarks>
///     <para>
///         <b>Doc 36 § P3, and the whole reason this project exists.</b> Each of these registers
///         itself through <c>PluginContext</c> — the same door a third-party plugin comes through —
///         so the only thing the editor needs to know about a feature is its name. A feature that
///         could be dropped from this list without touching anything else is a feature that could
///         have been written by somebody outside this repository, which is the claim doc 36 set out
///         to earn.
///     </para>
///     <para>
///         ⚠ <b>Order is the mode bar's order, and nothing else's.</b> Blockout is the editor's
///         second mode and Terrain its third; a list sorted alphabetically would put them the other
///         way round on a bar people navigate by position. Dependencies between modules are not
///         expressed here and should not be — a module that needs another one is two features that
///         are one.
///     </para>
///     <para>
///         ⚠ <b>Fresh instances per call.</b> A module holds the state of the editor it registered
///         into — the terrain module caches heightfields, the diagnostics module owns a transport —
///         so a shared static would be two editors in one process sharing a scene's ground. The
///         harness opens several at once.
///     </para>
/// </remarks>
static class EditorModules {
    // ⚠ Before anything reads the list, which is before the application exists — every host that
    // opens the editor (`EditorHost`, `EditorSession`) evaluates `Standard()` as an argument to
    // `EditorApplication`'s constructor, and that constructor builds `AssetPicker`, whose field
    // initializer builds an importer registry. A link made any later would leave that registry
    // importing the linked formats as byte blobs (#1300).
    static EditorModules() => LinkImporters(ImporterContributions.Linked);

    /// <summary>Puts the importers out of the assemblies this editor links into a set.</summary>
    /// <param name="linked">The set — <see cref="ImporterContributions.Linked" />, or a test's own.</param>
    /// <remarks>
    ///     <b>#1300's option A.</b> The CLI (<c>VixenCommand</c>) and the compiler worker
    ///     (<c>WorkerHost</c>) each say the same thing for themselves, and
    ///     <c>HostImporterMapTests</c> holds all three to one literal extension → importer map.
    /// </remarks>
    internal static void LinkImporters(ImporterContributions linked) {
        ArgumentNullException.ThrowIfNull(linked);

        VfxGraphImporters.Link(linked);
        ShaderGraphImporters.Link(linked);
        TerrainImporters.Link(linked);
        WaterImporters.Link(linked);
        AiImporters.Link(linked);
    }

    /// <summary>What the editor ships with, in the order it registers them.</summary>
    /// <returns>The modules, freshly constructed.</returns>
    public static IReadOnlyList<(string Id, string Name, IEditorPlugin Module)> Standard() => [
        // ⚠ The application's own verbs first (doc 50 § D3, #1289): a feature module may add to a menu
        // the application's modules fill, and none adds a mode, so the mode bar's order is untouched.
        (PlayModule.ModuleId, PlayModule.ModuleName, new PlayModule()),
        (SceneModule.ModuleId, SceneModule.ModuleName, new SceneModule()),

        // ⚠ Before every feature module, because it is the editor's own Create ▸ kinds (#1292) and
        // Create ▸ keeps registration order among equal `Order`s: a feature's kind follows them.
        (LibraryModule.ModuleId, LibraryModule.ModuleName, new LibraryModule()),
        (DeliveryModule.ModuleId, DeliveryModule.ModuleName, new DeliveryModule()),
        (HelpModule.ModuleId, HelpModule.ModuleName, new HelpModule()),
        (WorkspaceModule.ModuleId, WorkspaceModule.ModuleName, new WorkspaceModule()),
        (BlockoutModule.ModuleId, BlockoutModule.ModuleName, new BlockoutModule()),
        (TerrainModule.ModuleId, TerrainModule.ModuleName, new TerrainModule()),
        (TerrainPhysicsModule.ModuleId, TerrainPhysicsModule.ModuleName, new TerrainPhysicsModule()),
        (WaterModule.ModuleId, WaterModule.ModuleName, new WaterModule()),
        (DiagnosticsModule.ModuleId, DiagnosticsModule.ModuleName, new DiagnosticsModule()),
        (AssetEditorsModule.ModuleId, AssetEditorsModule.ModuleName, new AssetEditorsModule()),

        // ⚠ Activated by nothing a user launched until #1276: its suite activates it by hand and
        // stayed green while the layer stack, painting and Bake Material reached nobody. It adds no
        // mode, so where it sits changes no bar; after the asset editors, whose registry it fills.
        (TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule()),
        (ScriptsModule.ModuleId, ScriptsModule.ModuleName, new ScriptsModule())
    ];
}
