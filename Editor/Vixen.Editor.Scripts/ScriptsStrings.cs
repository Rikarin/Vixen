// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;

namespace Vixen.Editor.Scripts;

/// <summary>Every word the editor-scripts module shows, declared once.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Two ids, and both were the shell's until #1301.</b> <c>EditorStrings</c> declared
///         the module's panel title and its one command label one assembly up from the only code
///         that reads them, which meant a translator met them under the editor's own words and the
///         module could not have been shipped out of tree without leaving its two labels behind.
///         The shell cannot name this class — <c>StringContributions.cs</c> says why — so the
///         module hands its <see cref="All" /> over as it activates, the way the five toolsets do.
///     </para>
///     <para>
///         The command label is a family of one rather than a property, because the id is
///         <c>editor.command.</c> followed by <see cref="ScriptsModule.RebuildCommand" /> — the
///         convention every other module's family spells out — and a family is what keeps the label
///         and the command id from drifting apart.
///     </para>
/// </remarks>
public static class ScriptsStrings {
    /// <summary>Every command the module registers, by its command id.</summary>
    public static StringFamily Commands { get; } = new(
        "editor.command.",
        [new(ScriptsModule.RebuildCommand, "Rebuild Editor Scripts")]
    );

    /// <summary>The <c>Editor Scripts</c> panel.</summary>
    public static StringId Panel { get; } = new("editor.panel.scripts", "Editor Scripts");

    /// <summary>What a translator's template for this module holds.</summary>
    public static IReadOnlyList<StringId> All { get; } = [.. Commands.All, Panel];
}
