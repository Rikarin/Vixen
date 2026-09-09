// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Testing;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>The folders-only column beside the grid.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Doc 20 § B1's "a folder tree beside the grid", and it is not "turn the tree on as
///         well".</b> The browser's <c>TreeView</c> <i>is</i> the browsing surface in list mode — it
///         shows assets as well as folders and its selection is the project's selection. This is a
///         second, folders-only view whose selection <b>narrows</b> the grid rather than being the
///         selection. Building the second by widening the first is how the two come to disagree
///         about what is selected, which is what the browser's own view-toggle remark warns about.
///     </para>
///     <para>
///         ⚠ <b>A breadcrumb answers "where am I" and does not answer "what else is there".</b> That
///         second question is the reason somebody opens a content browser at all.
///     </para>
/// </remarks>
public class BrowserFolderTreeTests {
    /// <summary>
    ///     ⚠ <b>The whole feature in one assertion: clicking a folder moves the grid and not the
    ///     selection.</b> A folder tree that selected the folder would be the list view again, and
    ///     the panel would have two views writing one selection.
    /// </summary>
    [Fact]
    public void Choosing_a_folder_narrows_the_grid_and_does_not_change_the_selection() {
        using var editor = Started();

        var grid = Grid(editor);

        Assert.Equal("Assets", grid.Folder?.Name);
        Assert.Empty(editor.Project.Selection);

        Choose(editor, "Scenes");

        Assert.Equal("Scenes", grid.Folder?.Name);
        Assert.Contains("Main.vxscene", grid.Items.Select(item => item.Name));

        // ⚠ Standing in a folder is not selecting it. The verbs — rename, delete, reveal — act on
        // the project's selection, and a folder tree that wrote to it would make walking around the
        // project change what Delete would delete.
        Assert.Empty(editor.Project.Selection);
    }

    /// <summary>
    ///     ⚠ <b>Folders only, which is what makes it a different view rather than a second copy.</b>
    ///     A column that also listed the files would be the list view in half the width, and the
    ///     grid beside it would be showing the same thing twice.
    /// </summary>
    [Fact]
    public void It_lists_the_folders_and_none_of_the_files() {
        using var editor = Started();

        var rows = Rows(editor);

        // ⚠ The count is part of the assertion. A tree that built nothing satisfies every
        // "does not contain a file" claim below by containing nothing at all.
        Assert.True(rows.Count > 1, $"the folder tree drew {rows.Count} rows");
        Assert.Contains("Assets", rows);
        Assert.Contains("Scenes", rows);
        Assert.DoesNotContain("Main.vxscene", rows);
    }

    /// <summary>
    ///     ⚠ <b>The grid and the tree are two views of one place, so walking in either moves both.</b>
    ///     A double-click that left the folder tree marking the folder above is a panel saying two
    ///     different things about where you are.
    /// </summary>
    [Fact]
    public void Walking_into_a_folder_in_the_grid_moves_the_mark_in_the_tree() {
        using var editor = Started();

        Assert.Equal("Assets", Marked(editor));

        DoubleClick(editor, "Scenes");

        Assert.Equal("Scenes", Grid(editor).Folder?.Name);
        Assert.Equal("Scenes", Marked(editor));
    }

    /// <summary>
    ///     ⚠ <b>The column belongs to the grid rather than to the panel.</b> In list mode the
    ///     browsing tree already shows the folders, so a second folders-only column beside it would
    ///     be the same information twice with two selections to keep in step.
    /// </summary>
    [Fact]
    public void The_column_is_shown_with_the_grid_and_hidden_with_the_tree() {
        using var editor = Started();

        Assert.False(Folders(editor).HasClass("hidden"));

        Press(editor, "Grid");
        editor.Settle();

        Assert.True(Grid(editor).HasClass("hidden"), "the toggle did not switch to the list");
        Assert.True(Folders(editor).HasClass("hidden"));

        Press(editor, "Grid");
        editor.Settle();

        Assert.False(Folders(editor).HasClass("hidden"));
    }

    /// <summary>
    ///     ⚠ <b>The one thing here the search does not touch.</b> This column answers "what else is
    ///     there"; a tree that shrank to the folders holding matches would answer "where are the
    ///     matches", which is what the grid beside it is already saying — and a tree that moves
    ///     while somebody types is one they cannot aim at.
    /// </summary>
    [Fact]
    public void Typing_in_the_search_box_narrows_the_grid_and_leaves_the_folder_tree_whole() {
        using var editor = Started();

        var before = Rows(editor);

        var search = Descendants(editor.Panel("project")).OfType<SearchBox>().First();

        search.Value = "zzz-nothing-matches-this";
        editor.Settle();

        Assert.Equal(before, Rows(editor));
    }

    static EditorSession Started() {
        var editor = EditorSession.Start();

        editor.Open("project");

        if (Grid(editor).HasClass("hidden")) {
            Press(editor, "Grid");
            editor.Settle();
        }

        return editor;
    }

    static AssetGrid Grid(EditorSession editor) =>
        Descendants(editor.Panel("project")).OfType<AssetGrid>().FirstOrDefault()
        ?? throw editor.Fail("the browser has no grid");

    /// <summary>
    ///     ⚠ The folder tree is found by its class rather than by being first. It is built last and
    ///     drawn first — <c>order: -1</c> — precisely so that "the first tree in the panel" stays the
    ///     browsing one, which is how the harness and three existing tests reach it.
    /// </summary>
    static TreeView Folders(EditorSession editor) =>
        Descendants(editor.Panel("project"))
            .OfType<TreeView>()
            .FirstOrDefault(view => view.HasClass("browser-folders"))
        ?? throw editor.Fail("the browser has no folder tree");

    static List<string> Rows(EditorSession editor) =>
        [.. Walk(Folders(editor).Root).Select(node => node.Text ?? string.Empty)];

    static string? Marked(EditorSession editor) =>
        Folders(editor).Selection.Select(node => node.Text).FirstOrDefault();

    static void Choose(EditorSession editor, string folder) {
        var view = Folders(editor);

        var node = Walk(view.Root).FirstOrDefault(candidate => candidate.Text == folder)
            ?? throw editor.Fail(
                $"the folder tree has no '{folder}'. Showing: " + string.Join(", ", Rows(editor)) + "."
            );

        view.Select(node);
        editor.Settle();
    }

    static void DoubleClick(EditorSession editor, string name) {
        var tile = Descendants(Grid(editor))
                .OfType<AssetTile>()
                .FirstOrDefault(candidate => candidate.Node?.Name == name)
            ?? throw editor.Fail($"no tile for '{name}'");

        editor.Ui
            .At(tile.Bounds.X + (tile.Bounds.Width * 0.5f), tile.Bounds.Y + (tile.Bounds.Height * 0.5f))
            .DoubleClick();

        editor.Settle();
    }

    static void Press(EditorSession editor, string label) =>
        Descendants(editor.Panel("project")).OfType<ButtonBase>().First(button => button.Label == label).Activate();

    static IEnumerable<TreeNode> Walk(TreeNode node) {
        foreach (var child in node.Children) {
            yield return child;

            foreach (var found in Walk(child)) {
                yield return found;
            }
        }
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
