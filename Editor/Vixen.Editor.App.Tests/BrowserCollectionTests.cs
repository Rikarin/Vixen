// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Testing;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Doc 20 § B1's collections: a named set of assets that survives the files moving.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A collection keeps the result and a saved filter keeps the query, which is why only
///         one of them is a preference.</b> `BrowserSavedFilterTests` proves the filter half by
///         importing a file <em>after</em> the filter is named and finding it anyway; this proves the
///         collection half the opposite way, by moving a file on disk after it is collected and
///         finding it in the same collection afterwards. A store keyed by path cannot pass that, and
///         a store that re-ran a query could not have held the file in the first place.
///     </para>
///     <para>
///         ⚠ <b>And the store is the project's rather than the user's.</b>
///         <c>EditorPreferences</c> is one file across every project somebody opens, so an
///         <c>AssetId</c> kept there is meaningless in all the others;
///         <see cref="A_collection_is_the_projects_and_survives_a_restart" /> asserts the file lands
///         under <c>ProjectSettings/</c> and not in the user's data directory.
///     </para>
/// </remarks>
public class BrowserCollectionTests {
    /// <summary>
    ///     ⚠ <b>The whole feature in one assertion: collected, then moved on disk, then still
    ///     there.</b> The move is a real one — the file and its sidecar both, the way a file manager
    ///     or a checkout moves them — and the rescan afterwards is what a project does about it. What
    ///     survives it is the identity, which is the reason a collection holds ids.
    /// </summary>
    [Fact]
    public void A_collection_holds_assets_across_a_move_on_disk() {
        using var editor = Started();

        Import(editor, "widget-a.png");
        Import(editor, "other.png");

        var widget = Id(editor, "widget-a.png");

        editor.AddToCollection("Level art", widget);
        editor.Settle();

        Choose(editor, "Level art");

        Assert.Equal(["widget-a.png"], Shown(editor));

        // A folder to move it into, and then the move itself: the asset and the `.meta` that carries
        // its id, exactly as anything outside the editor would move them.
        var moved = Path.Combine(editor.ProjectRoot, "Assets", "Props");

        Directory.CreateDirectory(moved);
        File.Move(Path.Combine(editor.ProjectRoot, "Assets", "widget-a.png"), Path.Combine(moved, "widget-a.png"));

        File.Move(
            Path.Combine(editor.ProjectRoot, "Assets", "widget-a.png.meta"),
            Path.Combine(moved, "widget-a.png.meta")
        );

        editor.Run("assets.refresh");
        editor.Settle();

        // ⚠ The id is the same one, which is what the sidecar is for — and if it were not, this test
        // would be asserting about a different asset with the same name.
        Assert.Equal(widget, Id(editor, "widget-a.png"));

        Choose(editor, "Level art");

        Assert.Equal(["widget-a.png"], Shown(editor));
    }

    /// <summary>
    ///     ⚠ <b>Choosing a collection narrows the grid to it, and the count is part of the claim.</b>
    ///     A collection that showed nothing satisfies every "does not contain" assertion by containing
    ///     nothing at all, which is exactly what a collection whose ids resolve to no asset looks
    ///     like.
    /// </summary>
    [Fact]
    public void Choosing_a_collection_narrows_the_grid_and_a_folder_takes_it_back() {
        using var editor = Started();

        Import(editor, "widget-a.png");
        Import(editor, "other.png");

        editor.AddToCollection("Level art", Id(editor, "widget-a.png"));
        editor.Settle();

        Assert.Equal(["other.png", "widget-a.png"], Shown(editor));

        Choose(editor, "Level art");

        Assert.Equal(["widget-a.png"], Shown(editor));

        // ⚠ Standing in a folder is the way out, and there is deliberately no other: the grid can
        // only be showing one thing, so a column with a folder mark and a collection mark in it at
        // once would be two answers to what it is showing.
        Choose(editor, "Assets");

        Assert.Equal(["other.png", "widget-a.png"], Shown(editor));
    }

    /// <summary>
    ///     ⚠ <b>The search box narrows inside a collection rather than being ignored by it.</b> A
    ///     collection with a filter of its own would be the second browser the view toggle's own
    ///     remark refuses — the two views share the search, the kind filter, the selection and the
    ///     verbs, and a third surface that did not would disagree with both.
    /// </summary>
    [Fact]
    public void The_search_box_still_narrows_inside_a_collection() {
        using var editor = Started();

        Import(editor, "widget-a.png");
        Import(editor, "widget-b.png");

        editor.AddToCollection("Level art", Id(editor, "widget-a.png"), Id(editor, "widget-b.png"));
        editor.Settle();

        Choose(editor, "Level art");

        Assert.Equal(["widget-a.png", "widget-b.png"], Shown(editor));

        Search(editor).Value = "widget-b";
        editor.Settle();

        Assert.Equal(["widget-b.png"], Shown(editor));
    }

    /// <summary>
    ///     ⚠ <b>Per project, which is where this and the saved filters part company.</b> A saved
    ///     filter is a query and means something in any project; a collection is a set of
    ///     <c>AssetId</c>s and means nothing in another one — so it is a settings asset under
    ///     <c>ProjectSettings/</c> and the assertion names the file.
    /// </summary>
    [Fact]
    public void A_collection_is_the_projects_and_survives_a_restart() {
        using var scope = new Scratch();
        using var editor = Started(scope.Directory);

        Import(editor, "widget-a.png");

        editor.AddToCollection("Level art", Id(editor, "widget-a.png"));
        editor.Settle();

        var file = Path.Combine(editor.ProjectRoot, "ProjectSettings", "AssetCollections.vxsettings");

        Assert.True(File.Exists(file), $"the collections were not written to {file}");

        // ⚠ And to nowhere else, which is the half a "it came back after a restart" assertion cannot
        // see: a copy in the user store would come back too, and would come back in every other
        // project this person opens. ⚠ The sweep is the whole session directory rather than the user
        // store alone, because the harness puts the project *inside* it — an assertion that named
        // only the user store would be one that passed for a file written beside `preferences.yaml`
        // if the two directories were ever separated.
        Assert.Equal(
            [file],
            Directory.EnumerateFiles(scope.Directory, "AssetCollections.vxsettings", SearchOption.AllDirectories)
        );

        editor.Restart();
        editor.Open("project");
        editor.Settle();

        var kept = Assert.Single(editor.AssetCollections);

        Assert.Equal("Level art", kept.Name);
        Assert.Single(kept.Assets);

        Choose(editor, "Level art");

        Assert.Equal(["widget-a.png"], Shown(editor));
    }

    /// <summary>
    ///     ⚠ <b>Forgetting a collection touches no file it named</b>, which is what makes Forget the
    ///     honest word for it: a collection is a way of looking at the project rather than a place in
    ///     it. The grid goes back to the folder it was standing in, because a grid showing a
    ///     collection that no longer exists has nowhere else to be.
    /// </summary>
    [Fact]
    public void Forgetting_a_collection_leaves_the_assets_and_puts_the_grid_back() {
        using var editor = Started();

        Import(editor, "widget-a.png");
        Import(editor, "other.png");

        editor.AddToCollection("Level art", Id(editor, "widget-a.png"));
        editor.Settle();

        Choose(editor, "Level art");

        Assert.Equal(["widget-a.png"], Shown(editor));

        editor.ForgetCollection("Level art");
        editor.Settle();

        Assert.Empty(editor.AssetCollections);
        Assert.Equal(["other.png", "widget-a.png"], Shown(editor));
        Assert.True(
            File.Exists(Path.Combine(editor.ProjectRoot, "Assets", "widget-a.png")),
            "forgetting a collection deleted an asset it named"
        );
    }

    /// <summary>
    ///     ⚠ <b>The drag is the gesture doc 20 asks for, and the browser has to resolve it itself.</b>
    ///     A drag belongs to the element the press landed on for its whole life, so a tile dragged
    ///     onto the collections shelf is released over a column that never hears about it — which is
    ///     why <c>ProjectBrowser.Escaped</c> hit-tests the shelf before reporting the drop outwards.
    ///     Driven as a real pointer path rather than through the resolver, because the thing that
    ///     could be wrong is the routing.
    /// </summary>
    [Fact]
    public void An_asset_dragged_onto_a_collection_row_goes_into_it() {
        using var editor = Started();

        Import(editor, "widget-a.png");
        Import(editor, "other.png");

        editor.AddToCollection("Level art");
        editor.Settle();

        Assert.Empty(Assert.Single(editor.AssetCollections).Assets);

        var tile = Grid(editor).Tiles.First(candidate => candidate.Node?.Name == "widget-a.png");
        var row = Row(editor, "Level art");

        // ⚠ The press selects, and what a drag carries is the selection — so the gesture has to start
        // on the tile rather than anywhere in the grid.
        editor.Ui.Drag(Middle(tile).X, Middle(tile).Y, Middle(row).X, Middle(row).Y);
        editor.Settle();

        var collected = Assert.Single(editor.AssetCollections);

        Assert.Equal([Id(editor, "widget-a.png")], collected.Assets);
    }

    static EditorSession Started(string? data = null) {
        var editor = data is null
            ? EditorSession.Start()
            : EditorSession.Start(new EditorSessionOptions { DataDirectory = data });

        editor.Open("project");
        editor.Settle();

        // ⚠ The grid is what the browser opens as — `EditorPreferences.ProjectGridView` defaults to
        // true — and the collections shelf is the grid's column, because in list mode the browsing
        // tree is already a hierarchy and a second folders-only column would be the same information
        // twice. Asserted rather than pressed: a toggle pressed here would turn it *off*.
        Assert.True(
            Descendants(editor.Panel("project")).OfType<ToggleButton>().First(button => button.Label == "Grid")
                .IsChecked,
            "the browser did not open in grid mode, so the collections column is not on screen"
        );

        return editor;
    }

    static void Import(EditorSession editor, string name) {
        File.WriteAllText(Path.Combine(editor.ProjectRoot, "Assets", name), "x");
        editor.Run("assets.refresh");
        editor.Settle();
    }

    static AssetId Id(EditorSession editor, string name) {
        foreach (var entry in editor.Project.Assets.Entries) {
            if (!entry.IsFolder && Path.GetFileName(entry.Path) == name) {
                return entry.Guid;
            }
        }

        throw editor.Fail($"the project has no asset called {name}");
    }

    static AssetGrid Grid(EditorSession editor) =>
        Descendants(editor.Panel("project")).OfType<AssetGrid>().FirstOrDefault()
        ?? throw editor.Fail("the browser has no grid");

    static SearchBox Search(EditorSession editor) =>
        Descendants(editor.Panel("project")).OfType<SearchBox>().FirstOrDefault()
        ?? throw editor.Fail("the browser has no search box");

    static TreeView Folders(EditorSession editor) =>
        Descendants(editor.Panel("project"))
            .OfType<TreeView>()
            .FirstOrDefault(tree => tree.HasClass("browser-folders"))
        ?? throw editor.Fail("the browser has no folder column");

    static List<string> Shown(EditorSession editor) => [
        .. Grid(editor).Items.Where(item => !item.IsFolder).Select(item => item.Name).Order(StringComparer.Ordinal)
    ];

    /// <summary>Clicks a row in the folder column, which is either a folder or a collection.</summary>
    static void Choose(EditorSession editor, string label) {
        editor.ClickRow(Folders(editor), label);
        editor.Settle();
    }

    static TreeRow Row(EditorSession editor, string label) => editor.Row(Folders(editor), label);

    static (float X, float Y) Middle(UiElement element) {
        var bounds = element.Bounds;

        return (bounds.X + (bounds.Width / 2f), bounds.Y + (bounds.Height / 2f));
    }

    static IEnumerable<UiElement> Descendants(UiElement element) {
        foreach (var child in element.Children) {
            yield return child;

            foreach (var found in Descendants(child)) {
                yield return found;
            }
        }
    }
}
