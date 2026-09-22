// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;

namespace Vixen.Editor.AssetEditors;

/// <summary>Every word the asset-editors module shows through the shell's own vocabulary, declared once.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Two panel titles, and both were the shell's until #1301.</b> <c>EditorStrings</c>
///         declared <c>editor.panel.ai-debugger</c> and <c>editor.panel.input-debug</c> one assembly
///         up from the only code that reads them — the two panels this module registers — so a
///         translator met them under the editor's own words and the module could not have been
///         shipped out of tree without leaving its panel titles behind. The shell cannot name this
///         class (<c>StringContributions.cs</c> says why), so the module hands <see cref="All" />
///         over as it activates, the way the five toolsets do.
///     </para>
///     <para>
///         Deliberately small. The views under this assembly label themselves in markup and through
///         <c>ControlStrings</c>; what belongs here is the vocabulary the <em>shell</em> shows on the
///         module's behalf — a dock tab, a menu item — which is the only vocabulary that was ever in
///         the wrong table.
///     </para>
/// </remarks>
public static class AssetEditorStrings {
    /// <summary>The <c>Agent Debugger</c> panel.</summary>
    public static StringId PanelAiDebugger { get; } = new("editor.panel.ai-debugger", "Agent Debugger");

    /// <summary>The <c>Input Debug</c> panel.</summary>
    public static StringId PanelInputDebug { get; } = new("editor.panel.input-debug", "Input Debug");

    /// <summary>What a translator's template for this module holds.</summary>
    public static IReadOnlyList<StringId> All { get; } = [PanelAiDebugger, PanelInputDebug];
}
