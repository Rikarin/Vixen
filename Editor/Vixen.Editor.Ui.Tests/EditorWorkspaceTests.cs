// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Ui.Documents;
using Vixen.Ui;
using Vixen.Ui.Controls.Advanced;
using Xunit;

namespace Vixen.Editor.Ui.Tests;

/// <summary>
///     The arrangement's verbs on <see cref="IWorkspace" /> (#1294): where a document's panel goes, the
///     asset panel's id, keeping a layout and the user's theme — moved off <c>EditorApplication</c>.
/// </summary>
public sealed class EditorWorkspaceTests : IDisposable {
    readonly string directory = Path.Combine(Path.GetTempPath(), "vixen-workspace-" + Guid.NewGuid().ToString("N"));
    readonly EditorShell shell = new(1280f, 800f);
    readonly EditorUserStore store;
    readonly EditorWorkspace workspace;

    public EditorWorkspaceTests() {
        store = new EditorUserStore(directory);
        workspace = new EditorWorkspace(shell, store);

        shell.RegisterPanel("scene", new StringId("test.panel.scene", "Scene"), _ => { });
        shell.RegisterPanel("inspector", new StringId("test.panel.inspector", "Inspector"), _ => { });
        shell.RegisterLayout(
            "Split",
            new StringId("test.layout.split", "Split"),
            () => LayoutPresets.Split(["inspector"], ["scene"], 0.3f)
        );
        shell.Workspace.Reset();
    }

    public void Dispose() {
        shell.Dispose();

        if (Directory.Exists(directory)) {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>A document's panel goes in the scene's group, not wherever the host puts new panels.</summary>
    [Fact]
    public void A_documents_panel_is_placed_with_the_scene() {
        var id = EditorWorkspace.PanelOf(AssetId.New());

        workspace.Place(id);

        var layout = shell.Workspace.Host.Layout;
        var placed = layout.Find(id);
        var scene = layout.Find("scene");

        Assert.NotNull(placed);
        Assert.NotNull(scene);
        Assert.Same(scene.Value.Group, placed.Value.Group);
    }

    /// <summary>The id written for an asset's panel is the one a restored arrangement reads back.</summary>
    [Fact]
    public void An_asset_panels_id_reads_back_as_its_asset_and_nothing_else_does() {
        var asset = AssetId.New();

        Assert.True(EditorWorkspace.TryReadPanel(EditorWorkspace.PanelOf(asset), out var read));
        Assert.Equal(asset, read);

        Assert.False(EditorWorkspace.TryReadPanel("scene", out _));
        Assert.False(EditorWorkspace.TryReadPanel(EditorWorkspace.AssetPrefix + "not-a-guid", out _));
    }

    /// <summary>Save Layout keeps what is on screen in the user's store, under the preset's name.</summary>
    [Fact]
    public void Saving_the_layout_keeps_the_arrangement_in_the_users_store() {
        var name = workspace.SaveLayout();

        Assert.Contains(name, store.Layouts());
        Assert.Equal(shell.Workspace.Save(), store.LoadLayout(name));
    }

    /// <summary>
    ///     ⚠ The kept copy of the theme is dropped by Reload Styles. The Appearance page reads it, and a
    ///     reload that left it on the old text would be an editor disagreeing with itself.
    /// </summary>
    [Fact]
    public void Reloading_the_styles_reads_the_theme_file_again() {
        Assert.Equal(string.Empty, workspace.ThemeTokens);

        store.Write(EditorWorkspace.ThemeFile, "root { --accent: #3f7fd8; }");

        // Kept, until something says the file moved on.
        Assert.Equal(string.Empty, workspace.ThemeTokens);

        workspace.ReloadStyles();

        Assert.Equal("root { --accent: #3f7fd8; }", workspace.ThemeTokens);
    }

    /// <summary>Applying tokens writes the file the editor reads at start-up.</summary>
    [Fact]
    public void Applied_tokens_are_written_where_start_up_reads_them() {
        Assert.True(workspace.ApplyThemeTokens("root { --accent: #ff0000; }"));

        Assert.Equal("root { --accent: #ff0000; }", store.Read(EditorWorkspace.ThemeFile));
        Assert.Equal("root { --accent: #ff0000; }", workspace.ThemeTokens);
    }

    /// <summary>A view list with no shell refuses the arrangement's verbs rather than pretending.</summary>
    [Fact]
    public void A_workspace_with_no_shell_refuses_the_arrangements_verbs() {
        var views = new EditorWorkspace();

        Assert.Throws<InvalidOperationException>(() => views.SaveLayout());
        Assert.Throws<InvalidOperationException>(() => views.ReloadStyles());
    }
}
