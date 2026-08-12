// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Testing;
using Vixen.Editor.Ui;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
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

    /// <summary>A session showing the grid, however the panel happened to open.</summary>
    /// <remarks>
    ///     ⚠ <b>Pressed only if it is not already showing.</b> <c>EditorSettings.ProjectGridView</c>
    ///     defaults on now — tiles are what a content browser opens as — so a helper that pressed the
    ///     toggle unconditionally would put every test below in front of the <i>tree</i> and fail
    ///     them all on the first assertion about a tile. The toggle itself is tested once, on its
    ///     own, which is where that belongs.
    /// </remarks>
    static EditorSession Started() {
        var editor = EditorSession.Start();

        editor.Open("project");

        if (Grid(editor).HasClass("hidden")) {
            Press(editor, "Grid");
        }

        return editor;
    }

    /// <summary>The same, showing the tree.</summary>
    static EditorSession StartedInTheTree() {
        var editor = EditorSession.Start();

        editor.Open("project");

        if (!Grid(editor).HasClass("hidden")) {
            Press(editor, "Grid");
        }

        return editor;
    }

    static IReadOnlyList<string> Captions(EditorSession editor) =>
        [.. Grid(editor).Items.Select(item => item.Name)];

    /// <summary>
    ///     ⚠ <b>The panel opens on the grid, which it did not use to.</b> A content browser is opened
    ///     to answer "which of these is the rock", and a tree of file names answers a different
    ///     question; the tree is one press away for the times that is the question being asked.
    /// </summary>
    [Fact]
    public void The_toggle_swaps_the_grid_for_the_tree_and_back() {
        using var editor = EditorSession.Start();

        editor.Open("project");

        Assert.False(Grid(editor).HasClass("hidden"), "the panel should open on the grid");

        // ⚠ Not `editor.Assets`, which switches the panel to the tree in order to hand it over —
        // that is what a test *driving* the tree wants and is exactly wrong for one asking which of
        // the two is showing.
        var tree = Descendants(editor.Panel("project")).OfType<TreeView>().First();

        Assert.True(tree.HasClass("hidden"), "the tree is still showing beside the grid");

        Press(editor, "Grid");

        Assert.True(Grid(editor).HasClass("hidden"));
        Assert.False(tree.HasClass("hidden"));

        Press(editor, "Grid");

        Assert.False(Grid(editor).HasClass("hidden"));
        Assert.True(tree.HasClass("hidden"));
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
        var icons = StandardIcons.Assets;

        Assert.NotSame(
            EditorArt.Of(icons, "TextureImporter", "crate.png"),
            EditorArt.Of(icons, "SceneImporter", "Main.vxscene")
        );

        // Nothing, rather than the fallback: whether an unclaimed file gets the generic page is the
        // caller's decision, and both of the Project panel's views make it in one place.
        Assert.Null(EditorArt.Of(icons, "SomethingAPluginAdded", "thing.dat"));
        Assert.NotSame(StandardIcons.Folder, StandardIcons.Unknown);
    }

    /// <summary>
    ///     ⚠ <b>F12, as a regression test.</b> The same asset was a coloured mesh glyph in the grid
    ///     and a generic page in the tree, because each pane resolved its own picture — and the
    ///     remark on the tree's line said so, as though it were a size decision. This asserts they
    ///     agree, which is the only thing that stops them drifting apart again.
    /// </summary>
    [Fact]
    public void The_tree_and_the_grid_draw_one_asset_the_same_way() {
        using var editor = Started();

        DoubleClick(editor, "Scenes");

        var tile = Tile(editor, "Main.vxscene");
        var row = Rows(editor.Assets).First(node => node.Text == "Main.vxscene");

        Assert.NotNull(tile.Glyph.Art);
        Assert.Same(tile.Glyph.Art, row.Art);

        // And it is the scene picture rather than the fallback, so "they agree" cannot be satisfied
        // by both of them drawing nothing.
        Assert.NotSame(StandardIcons.Unknown, row.Art);
    }

    static IEnumerable<TreeNode> Rows(TreeView tree) {
        return Walk(tree.Root.Children);

        static IEnumerable<TreeNode> Walk(IReadOnlyList<TreeNode> nodes) {
            foreach (var node in nodes) {
                yield return node;

                foreach (var child in Walk(node.Children)) {
                    yield return child;
                }
            }
        }
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

    /// <summary>
    ///     ⚠ <b>The claim virtualising is for.</b> A folder of two thousand files is two thousand of
    ///     the browser's own objects and a pool of tiles the size of the viewport — and the last of
    ///     them is reachable, which is what separates pooling from a cap.
    /// </summary>
    [Fact]
    public void A_folder_of_thousands_costs_a_pool_rather_than_thousands_of_elements() {
        using var editor = Started();

        var many = Path.Combine(editor.ProjectRoot, "Assets", "Many");

        Directory.CreateDirectory(many);

        for (var index = 0; index < 2000; index++) {
            File.WriteAllText(Path.Combine(many, $"file{index:0000}.png"), "x");
        }

        editor.Run("assets.refresh");
        DoubleClick(editor, "Many");

        var grid = Grid(editor);

        Assert.Equal(2000, grid.Items.Count);

        // A pool the size of the viewport, not of the folder. The bound is generous — how many fit
        // depends on the panel's width — and the point is the order of magnitude.
        Assert.True(grid.Tiles.Count < 200, $"the grid realised {grid.Tiles.Count} tiles for 2000 items");

        // And the last one can still be reached, which a cap would have made impossible.
        var last = Tile(editor, "file1999.png");

        Assert.Equal("file1999.png", last.Node?.Name);
    }

    /// <summary>
    ///     ⚠ <b>A right-click in the grid did nothing at all.</b> The context menu was attached to
    ///     the tree alone, so the view somebody actually browses assets in — tiles — had no Create,
    ///     no Import, no Rename and no Show in Explorer. One menu over both views, because every line
    ///     on it acts on the project's selection and both views write that.
    /// </summary>
    [Fact]
    public void A_right_click_on_a_tile_opens_the_asset_menu_on_that_asset() {
        using var editor = Started();

        DoubleClick(editor, "Scenes");

        var tile = Tile(editor, "Main.vxscene");

        editor.Ui.At(Centre(tile).X, Centre(tile).Y).RightClick();
        editor.Settle();

        var menu = Descendants(editor.Document.Root)
            .OfType<ContextMenu>()
            .FirstOrDefault(candidate => candidate.IsOpen)
            ?? throw editor.Fail("a right-click in the grid opened no menu");

        Assert.Contains(menu.Items, item => item.Label == "Rename");

        // ⚠ And the press selected first. Every verb on the menu acts on the selection, so a menu
        // opened on a tile that was not selected would rename whatever was clicked last.
        var chosen = Assert.Single(editor.Project.Selection);

        Assert.True(editor.Project.Assets.TryGetByGuid(chosen, out var entry));
        Assert.Equal("Main.vxscene", entry.Name);
    }

    /// <summary>
    ///     ⚠ <b>A caption longer than its tile was drawn over the tiles beside it.</b> The grid writes
    ///     each tile's width, but a flex child may be wider than its parent — and the tile centres its
    ///     children, so the caption was laid out at the full width of the file name and never given a
    ///     reason to wrap. A folder of long asset names was a wall of overlapping text.
    /// </summary>
    [Fact]
    public void A_long_name_stays_inside_its_tile() {
        using var editor = Started();

        var folder = Path.Combine(editor.ProjectRoot, "Assets", "Long");

        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "T_Crate_Diffuse_Weathered_Variant_04.png"), "x");

        editor.Run("assets.refresh");
        DoubleClick(editor, "Long");

        var tile = Tile(editor, "T_Crate_Diffuse_Weathered_Variant_04.png");
        var caption = tile.Caption;

        Assert.True(
            caption.Width <= tile.Width,
            $"the caption is {caption.Width} wide inside a {tile.Width} tile"
        );

        Assert.True(
            caption.AbsoluteLeft >= tile.AbsoluteLeft - 0.5f
            && caption.AbsoluteLeft + caption.Width <= tile.AbsoluteLeft + tile.Width + 0.5f,
            "the caption starts or ends outside the tile it belongs to"
        );
    }

    /// <summary>
    ///     ⚠ <b>The size is written as the grid's own custom properties, not as a class.</b>
    ///     <c>VirtualizingGrid</c> reads <c>--tile-width</c> to work out how many fit across and where
    ///     item 40 000 is — a size it could only discover by measuring an element would defeat the
    ///     arrangement it exists for.
    /// </summary>
    [Fact]
    public void The_tile_size_picker_resizes_the_tiles_and_refits_the_columns() {
        using var editor = Started();

        DoubleClick(editor, "Scenes");

        var grid = Grid(editor);
        var picker = Descendants(editor.Panel("project"))
            .OfType<Select>()
            .FirstOrDefault(select => select.HasClass("browser-tile-size"))
            ?? throw editor.Fail("the browser has no tile-size picker");

        var before = Tile(editor, "Main.vxscene").Width;

        picker.Value = "Huge";
        editor.Settle();

        Assert.True(
            Tile(editor, "Main.vxscene").Width > before,
            $"the tiles are still {before} wide after asking for the largest size"
        );

        picker.Value = "Small";
        editor.Settle();

        Assert.True(Tile(editor, "Main.vxscene").Width < before);
        Assert.Equal("Small", grid.TileSize);
    }

    /// <summary>The picker is meaningless in the tree, so it goes with the tiles.</summary>
    [Fact]
    public void The_tile_size_picker_is_hidden_in_the_tree() {
        using var editor = StartedInTheTree();

        var picker = Descendants(editor.Panel("project"))
            .OfType<Select>()
            .First(select => select.HasClass("browser-tile-size"));

        Assert.True(picker.HasClass("hidden"), "the tile-size picker is showing over the tree");

        Press(editor, "Grid");
        editor.Settle();

        Assert.False(picker.HasClass("hidden"));
    }

    static (float X, float Y) Centre(UiElement element) =>
        (element.Bounds.X + (element.Bounds.Width * 0.5f), element.Bounds.Y + (element.Bounds.Height * 0.5f));

    /// <summary>The realised tile for a name, scrolling it into view first.</summary>
    /// <remarks>
    ///     ⚠ <b>Scrolled to rather than searched for.</b> The tiles are a pool, so the one showing
    ///     item four hundred does not exist until the grid is scrolled to it — which is the whole
    ///     point of virtualising and the thing a test has to respect rather than work around.
    /// </remarks>
    static AssetTile Tile(EditorSession editor, string name) {
        var grid = Grid(editor);
        var index = -1;

        for (var candidate = 0; candidate < grid.Items.Count; candidate++) {
            if (grid.Items[candidate].Name == name) {
                index = candidate;
                break;
            }
        }

        if (index < 0) {
            throw editor.Fail($"no tile for '{name}'. Showing: " + string.Join(", ", Captions(editor)) + ".");
        }

        grid.ScrollIntoView(index);
        editor.Settle();

        return Grid(editor).TileOf(index)
            ?? throw editor.Fail($"the tile for '{name}' is not realised after scrolling to it");
    }

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

/// <summary>What the editor notices about the project without being asked.</summary>
/// <remarks>
///     ⚠ <b>A wall-clock test, and deliberately.</b> The watcher's debounce is a real duration
///     measured off <c>Stopwatch</c> — that is the whole point of it, since the bursts it collapses
///     are a text editor's four writes in a few milliseconds — so a test that could fake the clock
///     would be testing something else. It waits for an outcome with a ceiling rather than sleeping
///     for a fixed time, so a fast machine finishes fast and a slow one still passes.
/// </remarks>
public class AssetWatchTests {
    /// <summary>How long to keep pumping frames before giving up on a change being noticed.</summary>
    /// <remarks>
    ///     Generously above the 250 ms debounce, because a loaded CI box can take a while to deliver
    ///     a file-system event — and a flaky test is worse than a slow one.
    ///
    ///     ⚠ Ten was not generous enough. The Windows leg waited the whole ten seconds for a file it
    ///     had just written and never saw it, on a runner that had a dozen test assemblies to itself
    ///     — which is the machine this number exists for. The cost of thirty is thirty seconds on a
    ///     build where the watcher is genuinely broken, and nothing at all on every other build.
    /// </remarks>
    static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     ⚠ <b>The panel used to show the project as it was when the editor started.</b> Saving a
    ///     texture from another program, or a teammate's checkout landing, left the browser and the
    ///     asset database showing neither until somebody pressed Refresh — which is a thing you only
    ///     press if you already know what is missing.
    /// </summary>
    [Fact]
    public void A_file_written_outside_the_editor_appears_without_a_refresh() {
        using var editor = EditorSession.Start();

        editor.Open("project");

        Assert.DoesNotContain(editor.Project.Assets.Entries, entry => entry.Name == "Dropped.png");

        File.WriteAllText(Path.Combine(editor.ProjectRoot, "Assets", "Dropped.png"), "x");

        Assert.True(
            Until(editor, () => editor.Project.Assets.Entries.Any(entry => entry.Name == "Dropped.png")),
            "the editor never noticed a file written into Assets/"
        );

        // And the panel followed, rather than only the database behind it.
        Assert.Contains(EditorSession.Labels(editor.Assets), label => label == "Dropped.png");
    }

    /// <summary>A deletion is a change too, and the one that leaves a row pointing at nothing.</summary>
    [Fact]
    public void A_file_deleted_outside_the_editor_goes_from_the_database() {
        using var editor = EditorSession.Start();

        editor.Open("project");

        var path = Path.Combine(editor.ProjectRoot, "Assets", "Doomed.png");

        File.WriteAllText(path, "x");

        Assert.True(
            Until(editor, () => editor.Project.Assets.Entries.Any(entry => entry.Name == "Doomed.png")),
            "the editor never noticed the file being written"
        );

        File.Delete(path);

        Assert.True(
            Until(editor, () => !editor.Project.Assets.Entries.Any(entry => entry.Name == "Doomed.png")),
            "the editor never noticed the file being deleted"
        );
    }

    /// <summary>Pumps frames until something is true, or until the ceiling.</summary>
    static bool Until(EditorSession editor, Func<bool> done) {
        var deadline = DateTime.UtcNow + Ceiling;

        while (DateTime.UtcNow < deadline) {
            editor.Settle();

            if (done()) {
                return true;
            }

            // ⚠ A real pause, because the thing being waited for is a real duration. Spinning on
            // `Settle` would burn a core for a quarter of a second to no purpose — the debounce is
            // not going to close any sooner for being asked more often.
            Thread.Sleep(25);
        }

        return false;
    }
}

/// <summary>What a rebuild of the project tree keeps.</summary>
public class ProjectTreeStateTests {
    /// <summary>
    ///     ⚠ <b>A rescan must not close every folder somebody opened.</b> The tree is rebuilt from
    ///     scratch on every rebuild and was re-expanded only to its top level, which was survivable
    ///     while a rebuild followed something the user had just done — and stopped being so the
    ///     moment a file watcher could cause one. A project tree that folds itself up while you are
    ///     looking at it is worse than one that does not notice the file.
    /// </summary>
    [Fact]
    public void A_rescan_leaves_the_folders_that_were_open_open() {
        using var editor = EditorSession.Start();

        editor.Open("project");
        editor.ExpandAll(editor.Assets);

        // ⚠ `Row` and not `Labels`: the labels are the *nodes*, which exist whether or not their
        // folder is open, and a rebuild that closed everything would leave them all in the list. A
        // row is what is on screen, which is the thing that stopped being there.
        Assert.NotNull(editor.Row(editor.Assets, "Main.vxscene"));

        // Whatever the rescan is for — a rename, an import, a file another program saved — it comes
        // through here.
        editor.Run("assets.refresh");

        Assert.NotNull(editor.Row(editor.Assets, "Main.vxscene"));
    }

    /// <summary>A folder that has gone does not come back, and does not throw on the way.</summary>
    [Fact]
    public void A_folder_deleted_between_rebuilds_is_simply_not_restored() {
        using var editor = EditorSession.Start();

        editor.Open("project");
        editor.ExpandAll(editor.Assets);

        var scenes = editor.Project.Assets.Entries.First(entry => entry.Path == "Assets/Scenes").Guid;

        Assert.True(Vixen.Editor.Core.AssetOperations.Delete(editor.Project, scenes).Ok);

        editor.Run("assets.refresh");

        Assert.DoesNotContain("Scenes", EditorSession.Labels(editor.Assets));
        Assert.NotNull(editor.Row(editor.Assets, "Assets"));
    }
}
