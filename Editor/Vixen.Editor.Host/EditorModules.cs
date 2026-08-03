// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.AssetEditors;
using Vixen.Editor.Blockout;
using Vixen.Editor.Diagnostics;
using Vixen.Editor.Plugin;
using Vixen.Editor.Terrain;

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
    /// <summary>What the editor ships with, in the order it registers them.</summary>
    /// <returns>The modules, freshly constructed.</returns>
    public static IReadOnlyList<(string Id, string Name, IEditorPlugin Module)> Standard() => [
        (BlockoutModule.ModuleId, BlockoutModule.ModuleName, new BlockoutModule()),
        (TerrainModule.ModuleId, TerrainModule.ModuleName, new TerrainModule()),
        (DiagnosticsModule.ModuleId, DiagnosticsModule.ModuleName, new DiagnosticsModule()),
        (AssetEditorsModule.ModuleId, AssetEditorsModule.ModuleName, new AssetEditorsModule())
    ];
}
