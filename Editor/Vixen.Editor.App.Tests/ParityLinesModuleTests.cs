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
    [InlineData(SceneModule.ModuleId, "edit.delete")]
    [InlineData(SceneModule.ModuleId, "edit.rename")]
    [InlineData(SceneModule.ModuleId, "edit.duplicate")]
    [InlineData(SceneModule.ModuleId, "edit.cut")]
    [InlineData(SceneModule.ModuleId, "edit.deselect-all")]
    [InlineData(PlayModule.ModuleId, "play.mode-in-editor")]
    [InlineData(PlayModule.ModuleId, "play.mute-audio")]
    [InlineData(PlayModule.ModuleId, "play.overlay.physics")]
    [InlineData(PlayModule.ModuleId, "play.maximise")]
    [InlineData(PlayModule.ModuleId, "play.clear-console")]
    [InlineData(WorkspaceModule.ModuleId, "view.clear-console")]
    [InlineData(WorkspaceModule.ModuleId, "file.no-recent")]
    [InlineData(WorkspaceModule.ModuleId, "file.new-project")]
    [InlineData(WorkspaceModule.ModuleId, "file.open-project")]
    [InlineData(WorkspaceModule.ModuleId, "edit.undo-history")]
    [InlineData(WorkspaceModule.ModuleId, "edit.preferences")]
    [InlineData(WorkspaceModule.ModuleId, "edit.keybindings")]
    [InlineData(WorkspaceModule.ModuleId, "file.project-settings")]
    [InlineData(LibraryModule.ModuleId, "assets.create")]
    [InlineData(LibraryModule.ModuleId, "assets.import-files")]
    [InlineData(LibraryModule.ModuleId, "file.export-package")]
    [InlineData(LibraryModule.ModuleId, "build.clean-library")]
    [InlineData(LibraryModule.ModuleId, "assets.reimport")]
    [InlineData(LibraryModule.ModuleId, "assets.bake-mesh-maps")]
    [InlineData(DeliveryModule.ModuleId, "build.rebuild-shaders")]
    public void Disabling_the_module_takes_its_lines_away(string module, string command) {
        using var session = EditorSession.Start();

        Assert.NotNull(session.Shell.Commands[command]);

        Assert.True(session.Plugins.Disable(module));

        Assert.Null(session.Shell.Commands[command]);

        session.Plugins.Enable(module);

        Assert.NotNull(session.Shell.Commands[command]);
    }

    /// <summary>Search Everywhere's three sources are the modules', and leave with them (#1518).</summary>
    /// <remarks>
    ///     Red against the pre-move files, where the application added all three and nothing could take
    ///     one back: the category survived its module being switched off.
    /// </remarks>
    [Theory]
    [InlineData(LibraryModule.ModuleId, "Asset")]
    [InlineData(SceneModule.ModuleId, "Entity")]
    [InlineData(WorkspaceModule.ModuleId, "Command")]
    public void Disabling_the_module_takes_its_search_source_away(string module, string category) {
        using var session = EditorSession.Start();

        var search = session.Shell.Search;

        Assert.Single(search.Sources, source => source.Category == category);

        Assert.True(session.Plugins.Disable(module));
        Assert.DoesNotContain(search.Sources, source => source.Category == category);

        session.Plugins.Enable(module);
        Assert.Single(search.Sources, source => source.Category == category);
    }

    /// <summary>File ▸ Open Recent is the workspace module's, and leaves with it rather than naming lines it took away.</summary>
    [Fact]
    public void Open_recent_is_offered_by_the_workspace_module_and_withdrawn_with_it() {
        using var session = EditorSession.Start();

        // The history holds only the project that is open, which the list leaves out.
        Assert.Equal(["file.no-recent"], session.Shell.Recent!.Invoke());

        Assert.True(session.Plugins.Disable(WorkspaceModule.ModuleId));
        Assert.Null(session.Shell.Recent);

        session.Plugins.Enable(WorkspaceModule.ModuleId);
        Assert.Equal(["file.no-recent"], session.Shell.Recent!.Invoke());
    }

    /// <summary>Clean Library, through the library module, still deletes the project's import cache.</summary>
    [Fact]
    public void Clean_library_through_the_module_deletes_the_import_cache() {
        using var session = EditorSession.Start();

        var library = session.Project.Paths.Library;
        Directory.CreateDirectory(Path.Combine(library, "Artifacts"));
        File.WriteAllText(Path.Combine(library, "Artifacts", "stale.bin"), "x");

        Assert.True(session.Shell.Commands.Execute("build.clean-library"));
        Assert.False(Directory.Exists(library));
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
