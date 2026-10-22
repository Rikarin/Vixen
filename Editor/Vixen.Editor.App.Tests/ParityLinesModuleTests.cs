// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Testing;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Doc 50 § D3: the lines that left <c>EditorParity.cs</c> for a module leave with the module (#1296).</summary>
/// <remarks>
///     ⚠ <b>The negative half, which <c>ModuleReachTests</c>' manifest cannot say.</b> The manifest
///     records which module put a line there; it does not say that a line the application still
///     registered by hand would survive the module being switched off. This does, one row per line
///     moved, and it is red against the pre-move files because the application registered them all.
/// </remarks>
public class ParityLinesModuleTests {
    [Theory]
    [InlineData(SceneModule.ModuleId, "entity.set-parent")]
    [InlineData(SceneModule.ModuleId, "entity.toggle-hidden")]
    [InlineData(SceneModule.ModuleId, "entity.toggle-lock")]
    [InlineData(SceneModule.ModuleId, "entity.ungroup")]
    [InlineData(SceneModule.ModuleId, "entity.toggle-active")]
    public void Disabling_the_module_takes_its_lines_away(string module, string command) {
        using var session = EditorSession.Start();

        Assert.NotNull(session.Shell.Commands[command]);

        Assert.True(session.Plugins.Disable(module));

        Assert.Null(session.Shell.Commands[command]);

        session.Plugins.Enable(module);

        Assert.NotNull(session.Shell.Commands[command]);
    }

    /// <summary>Toggle Hidden, through the module, still hides the whole selection by the first entity's state.</summary>
    [Fact]
    public void Toggle_hidden_hides_the_selection_and_a_second_press_shows_it() {
        using var session = EditorSession.Start();

        var scene = session.Scene;
        var entities = scene.Entities.Take(2).ToArray();

        Assert.Equal(2, entities.Length);

        scene.Selection.Set(entities);

        Assert.True(session.Shell.Commands.Execute("entity.toggle-hidden"));
        Assert.All(entities, entity => Assert.True(scene.IsHiddenDirectly(entity)));
        Assert.True(session.Shell.Commands["entity.toggle-hidden"]!.IsChecked);

        Assert.True(session.Shell.Commands.Execute("entity.toggle-hidden"));
        Assert.All(entities, entity => Assert.False(scene.IsHiddenDirectly(entity)));
    }
}
