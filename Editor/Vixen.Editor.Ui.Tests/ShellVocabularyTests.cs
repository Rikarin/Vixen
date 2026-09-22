// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Xunit;

namespace Vixen.Editor.Ui.Tests;

/// <summary>
///     <a href="https://github.com/Rikarin/Vixen/issues/1301">#1301</a>: the shell's table holds no
///     word that belongs to a toolset — a mode's title, its panels, its command category, its menus.
/// </summary>
/// <remarks>
///     <para>
///         <b>Thirty-nine ids failed this on the day it was written.</b> <c>EditorStrings</c>
///         declared <c>editor.panel.terrain</c>, <c>editor.category.water</c>,
///         <c>editor.menu.blockout-shape</c> and thirty-six more, every one read by exactly one
///         toolset assembly and by nothing in the shell. The shell cannot name a toolset's
///         declaration class (<c>StringContributions.cs</c> says why), so those were copies the
///         toolset could not own: a translator met them under the editor's own words, a template
///         taken with the toolset off still carried them, and a toolset shipped out of tree would
///         have had no way to bring its own.
///     </para>
///     <para>
///         ⚠ <b>The predicate is a vocabulary and not a list of ids</b>, because a list of ids is
///         satisfied by renaming one. A segment is what a toolset's ids are built from — the mode's
///         name, the panel's name — and the boundary is a dot or a hyphen, so <c>profiling</c>
///         (the shell's own layout) does not answer for <c>profiler</c> (the diagnostics panel) and
///         <c>frame-time</c> does not answer for <c>frame-debugger</c>. Every segment here names an
///         assembly under <c>Editor/</c> that this test project deliberately does not reference,
///         which is the whole reason the words cannot be looked up rather than listed.
///     </para>
/// </remarks>
public class ShellVocabularyTests {
    /// <summary>The words a toolset's ids are built from, one row per owner.</summary>
    static readonly string[] ToolsetSegments = [
        // Vixen.Editor.Terrain
        "terrain", "foliage", "grass", "growth", "splines",
        // Vixen.Editor.Blockout
        "blockout",
        // Vixen.Editor.Water
        "water",
        // Vixen.Editor.Texturing
        "texture", "layer-stack",
        // Vixen.Editor.Diagnostics
        "profiler", "gpu", "memory", "statistics", "network", "frame-debugger", "remote-inspector", "devices",
        // Vixen.Editor.Scripts
        "scripts",
        // Vixen.Editor.AssetEditors
        "ai-debugger", "input-debug"
    ];

    static readonly Regex Owned = new(
        "(?:^|[.-])(?:" + string.Join("|", ToolsetSegments.Select(Regex.Escape)) + ")(?:$|[.-])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant
    );

    /// <summary>No id the shell declares is built from a toolset's vocabulary.</summary>
    [Fact]
    public void The_shell_declares_no_id_a_toolset_owns() {
        var owned = EditorStrings.All.Where(id => Owned.IsMatch(id.Id)).Select(id => id.Id).ToList();

        Assert.True(
            owned.Count == 0,
            "EditorStrings declares ids that belong to a toolset's own declaration class: "
            + string.Join(", ", owned)
            + ". Move each to the *Strings class of the assembly that reads it (#1301)."
        );
    }

    /// <summary>The predicate can be false: a toolset's id shape is what it refuses.</summary>
    /// <remarks>
    ///     The instrument's own half. A vocabulary that matched nothing would leave the test above
    ///     green forever, so the shapes that were in the table on the day are what it must reject,
    ///     and the shell's own near-misses are what it must keep.
    /// </remarks>
    [Theory]
    [InlineData("editor.panel.terrain", true)]
    [InlineData("editor.panel.water.zone", true)]
    [InlineData("editor.menu.blockout-shape", true)]
    [InlineData("editor.panel.texture-paint-3d", true)]
    [InlineData("editor.command.scripts.rebuild", true)]
    [InlineData("editor.layout.profiling", false)]
    [InlineData("editor.status.frame-time", false)]
    [InlineData("editor.command.build.rebuild-shaders", false)]
    [InlineData("editor.panel.ui-diagnostics", false)]
    public void The_vocabulary_tells_a_toolsets_id_from_the_shells(string id, bool toolset) =>
        Assert.Equal(toolset, Owned.IsMatch(id));
}
