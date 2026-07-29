// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Testing;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>The browser's other view: a folder's contents as tiles.</summary>
/// <remarks>
///     ⚠ <b>A grid is not a prettier list.</b> A tree answers "where is this" and a grid answers
///     "what is in here", which is the question somebody asks who is looking for the crate texture
///     and does not remember what it is called — so it shows one folder and you walk into the next.
/// </remarks>
public class GridViewTests {
    static AssetGrid Grid(EditorSession editor) {
        editor.Open("project");

        return Descendants(editor.Panel("project")).OfType<AssetGrid>().FirstOrDefault()
            ?? throw editor.Fail("the browser has no grid");
    }

    static EditorSession Started() {
        var editor = EditorSession.Start();

        editor.Open("project");
        Press(editor, "Grid");

        return editor;
    }

    static IReadOnlyList<string> Captions(EditorSession editor) =>
        [.. Grid(editor).Tiles.Select(tile => tile.Node?.Name ?? "")];

    [Fact]
    public void The_toggle_swaps_the_tree_for_the_grid_and_back() {
        using var editor = EditorSession.Start();

        editor.Open("project");

        Assert.True(Grid(editor).HasClass("hidden"), "the grid is showing before it was asked for");
        Assert.False(editor.Assets.HasClass("hidden"));

        Press(editor, "Grid");

        Assert.False(Grid(editor).HasClass("hidden"));
        Assert.True(editor.Assets.HasClass("hidden"), "the tree is still showing beside the grid");

        Press(editor, "Grid");

        Assert.True(Grid(editor).HasClass("hidden"));
        Assert.False(editor.Assets.HasClass("hidden"));
    }

    [Fact]
    public void It_opens_at_the_root_and_shows_what_is_in_it() {
        using var editor = Started();

        Assert.Equal("Assets", Grid(editor).Folder?.Name);
        Assert.Contains("Scenes", Captions(editor));
    }

    /// <summary>
    ///     ⚠ Walking into a folder is what makes it a folder view. A grid that flattened the project
    ///     would be a list with pictures.
    /// </summary>
    [Fact]
    public void Double_clicking_a_folder_walks_into_it() {
        using var editor = Started();

        DoubleClick(editor, "Scenes");

        Assert.Equal("Scenes", Grid(editor).Folder?.Name);
        Assert.Contains("Main.vxscene", Captions(editor));
        Assert.DoesNotContain("Scenes", Captions(editor));
    }

    [Fact]
    public void A_breadcrumb_goes_back_up() {
        using var editor = Started();

        DoubleClick(editor, "Scenes");
        Assert.Equal("Scenes", Grid(editor).Folder?.Name);

        // Two crumbs — Assets, then Scenes — and pressing the first is how you get out.
        var crumbs = Descendants(Grid(editor)).OfType<Button>().Where(button => button.HasClass("asset-crumb")).ToList();

        Assert.Equal(["Assets", "Scenes"], crumbs.Select(crumb => crumb.Label));

        crumbs[0].Activate();
        editor.Settle();

        Assert.Equal("Assets", Grid(editor).Folder?.Name);
    }

    [Fact]
    public void Clicking_a_tile_selects_the_asset_and_the_tile_shows_it() {
        using var editor = Started();

        DoubleClick(editor, "Scenes");

        var tile = Tile(editor, "Main.vxscene");

        editor.Click(tile);

        var chosen = Assert.Single(editor.Project.Selection);

        Assert.True(editor.Project.Assets.TryGetByGuid(chosen, out var entry));
        Assert.Equal("Main.vxscene", entry.Name);
        Assert.True(tile.HasClass("checked") || tile.State.HasFlag(Vixen.Ui.Styling.ElementState.Checked));
    }

    /// <summary>
    ///     ⚠ The marks are pushed from the project's selection rather than kept by the grid.
    ///     Selecting an asset anywhere else must move them.
    /// </summary>
    [Fact]
    public void A_selection_made_elsewhere_marks_the_tile() {
        using var editor = Started();

        DoubleClick(editor, "Scenes");

        var scene = editor.Project.Assets.Entries.First(entry => entry.Name == "Main.vxscene").Guid;

        editor.Project.Selection.Set([scene]);
        editor.Settle();

        var tile = Tile(editor, "Main.vxscene");

        Assert.True(tile.State.HasFlag(Vixen.Ui.Styling.ElementState.Checked));
    }

    [Fact]
    public void Double_clicking_an_asset_opens_it() {
        using var editor = Started();

        DoubleClick(editor, "Scenes");
        DoubleClick(editor, "Main.vxscene");

        Assert.Contains(editor.Panels, panel => panel.Id.StartsWith("asset.", StringComparison.Ordinal));
    }

    /// <summary>
    ///     ⚠ The glyph and its colour are what a grid is scanned by, so a texture and a scene must
    ///     not draw the same.
    /// </summary>
    [Fact]
    public void A_tile_is_drawn_by_what_the_importer_claims_it_is() {
        Assert.NotEqual(AssetThumbnails.For("TextureImporter"), AssetThumbnails.For("SceneImporter"));
        Assert.Equal(AssetThumbnails.Unknown, AssetThumbnails.For("SomethingAPluginAdded"));
        Assert.NotEqual(AssetThumbnails.Folder, AssetThumbnails.Unknown);
    }

    /// <summary>
    ///     ⚠ The search box and the type filter are the browser's, not each view's — a grid with a
    ///     filter of its own would be a second browser that disagrees with the first.
    /// </summary>
    [Fact]
    public void The_search_box_narrows_the_grid_too() {
        using var editor = Started();

        DoubleClick(editor, "Scenes");
        Assert.Contains("Main.vxscene", Captions(editor));

        var search = Descendants(editor.Panel("project")).OfType<SearchBox>().First();

        search.Value = "nothing-matches-this";
        editor.Settle();

        Assert.DoesNotContain("Main.vxscene", Captions(editor));
    }

    /// <summary>
    ///     ⚠ A folder that goes — deleted, renamed, filtered away — must not leave the browser in one
    ///     that does not exist, with no way back to anything.
    /// </summary>
    [Fact]
    public void A_folder_that_disappears_falls_back_rather_than_showing_nothing() {
        using var editor = Started();

        DoubleClick(editor, "Scenes");
        Assert.Equal("Scenes", Grid(editor).Folder?.Name);

        var scenes = editor.Project.Assets.Entries.First(entry => entry.Path == "Assets/Scenes").Guid;

        Assert.True(Vixen.Editor.Core.AssetOperations.Delete(editor.Project, scenes).Ok);

        editor.Run("assets.refresh");

        Assert.NotNull(Grid(editor).Folder);
        Assert.Equal("Assets", Grid(editor).Folder?.Name);
    }

    static AssetTile Tile(EditorSession editor, string name) =>
        Grid(editor).Tiles.FirstOrDefault(tile => tile.Node?.Name == name)
        ?? throw editor.Fail(
            $"no tile for '{name}'. Showing: " + string.Join(", ", Captions(editor)) + "."
        );

    static void DoubleClick(EditorSession editor, string name) {
        var tile = Tile(editor, name);
        var x = tile.Bounds.X + (tile.Bounds.Width * 0.5f);
        var y = tile.Bounds.Y + (tile.Bounds.Height * 0.5f);

        editor.Ui.At(x, y).DoubleClick();
        editor.Settle();
    }

    static void Press(EditorSession editor, string label) =>
        Descendants(editor.Panel("project")).OfType<ButtonBase>().First(button => button.Label == label).Activate();

    static IEnumerable<UiElement> Descendants(UiElement element) {
        foreach (var child in element.Children) {
            yield return child;

            foreach (var found in Descendants(child)) {
                yield return found;
            }
        }
    }
}
