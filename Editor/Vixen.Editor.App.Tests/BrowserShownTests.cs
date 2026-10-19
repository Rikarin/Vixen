// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Testing;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Doc 50 § P4 (#1304): the browser is a view over <c>IContentLibrary.Shown</c>.</summary>
/// <remarks>
///     ⚠ <b>The claim is that what the browser shows outlives the panel</b>, which is the property a
///     field on the panel cannot have: the factory runs again on every reopen. Before, closing the
///     Project tab and opening it again put the grid back at <c>Assets</c> with an empty search box.
/// </remarks>
public sealed class BrowserShownTests {
    /// <summary>The folder walked into and the search typed come back with a reopened panel.</summary>
    [Fact]
    public void The_folder_and_search_survive_closing_the_panel() {
        using var editor = EditorSession.Start();

        Directory.CreateDirectory(Path.Combine(editor.Project.Paths.Assets, "Textures"));
        File.WriteAllText(Path.Combine(editor.Project.Paths.Assets, "Textures", "bark.png"), "bark");

        editor.Open("project").Run("assets.refresh");

        Stand(editor, "Textures");

        Search(editor).Value = "bark";
        editor.Settle();

        Assert.Equal("Assets/Textures", editor.Library.Shown.Folder);
        Assert.Equal("bark", editor.Library.Shown.Search);

        editor.Close("project").Open("project");

        var browser = editor.Application.Browser ?? throw editor.Fail("the browser did not reopen");

        Assert.Equal("Assets/Textures", browser.Folder);
        Assert.Equal("bark", Search(editor).Value);
        Assert.Equal("bark", browser.Search);
    }

    /// <summary>The kind the dropdown is on comes back with a reopened panel, and the grid is filtered by it (#1304).</summary>
    /// <remarks>
    ///     ⚠ <b>The restore with no test of its own until now</b>, and it is the subtle one of the four: the
    ///     dropdown's options are rebuilt from what the project holds on every build, so a panel that read
    ///     its own empty dropdown rather than <c>Shown.Kind</c> would come back on every kind — which is the
    ///     sabotage that turns this red. A real kind is chosen, never "every kind", which would restore
    ///     trivially.
    /// </remarks>
    [Fact]
    public void The_kind_survives_closing_the_panel() {
        using var editor = EditorSession.Start();

        // Sidecars written by hand, naming their importers, so the kinds exist without waiting on an
        // import: the dropdown offers the tags the database read off the .meta files at scan.
        const string Tag = "MoveSetImporter";

        File.WriteAllText(Path.Combine(editor.Project.Paths.Assets, "walk.vxmoveset"), "name: walk\n");
        File.WriteAllText(
            Path.Combine(editor.Project.Paths.Assets, "walk.vxmoveset.meta"),
            "guid: 6c6aec0b818e4563ad780eb9fe6a47d7\nmetaVersion: 1\nimporter: !" + Tag + "\n"
        );

        editor.Open("project").Run("assets.refresh").Settle();

        var tag = editor.Project.Assets.TryGetByPath("Assets/walk.vxmoveset", out var walk) && walk.ImporterTag == Tag
            ? Tag
            : throw editor.Fail("the database did not read walk.vxmoveset's importer off its sidecar");

        Kinds(editor, tag).Value = tag;
        editor.Settle();

        Assert.Equal(tag, editor.Library.Shown.Kind);

        editor.Close("project").Open("project");

        var browser = editor.Application.Browser ?? throw editor.Fail("the browser did not reopen");

        Assert.Equal(tag, browser.Kind);
        Assert.Equal(tag, Kinds(editor, tag).Value);
    }

    /// <summary>Tiles-or-tree and the tile size live in the model, come back with a reopened panel, and survive a restart.</summary>
    /// <remarks>
    ///     ⚠ <b>Two lifetimes, asserted separately.</b> The reopen proves the panel reads
    ///     <c>Shown</c> on build rather than starting from a fresh toggle; the restart proves the model is
    ///     written back to the preferences and seeded from them, which a model that only lived for the
    ///     session would fail.
    /// </remarks>
    [Fact]
    public void The_view_and_tile_size_survive_closing_the_panel_and_a_restart() {
        using var editor = EditorSession.Start();

        editor.Open("project");

        var browser = editor.Application.Browser ?? throw editor.Fail("the browser did not open");

        Assert.True(browser.IsGrid, "the browser no longer opens as a grid");

        Sizes(editor).Value = "Small";
        editor.Settle();

        browser.IsGrid = false;
        editor.Settle();

        Assert.False(editor.Library.Shown.Grid);
        Assert.Equal("Small", editor.Library.Shown.TileSize);

        editor.Close("project").Open("project");

        var reopened = editor.Application.Browser ?? throw editor.Fail("the browser did not reopen");

        Assert.False(reopened.IsGrid);
        Assert.Equal("Small", reopened.Grid.TileSize);

        editor.Restart().Open("project");

        Assert.False(editor.Library.Shown.Grid);
        Assert.Equal("Small", editor.Library.Shown.TileSize);
        Assert.False((editor.Application.Browser ?? throw editor.Fail("no browser after the restart")).IsGrid);
    }

    static Select Sizes(EditorSession editor) =>
        Descendants(editor.Panel("project")).OfType<Select>().FirstOrDefault(select => select.HasClass("browser-tile-size"))
        ?? throw editor.Fail("the browser has no tile-size picker");

    static Select Kinds(EditorSession editor, string tag) =>
        Descendants(editor.Panel("project")).OfType<Select>().FirstOrDefault(select => select.Options.Any(option => option.Value == tag))
        ?? throw editor.Fail($"the browser has no kind dropdown offering '{tag}'");

    static SearchBox Search(EditorSession editor) =>
        Descendants(editor.Panel("project")).OfType<SearchBox>().FirstOrDefault()
        ?? throw editor.Fail("the browser has no search box");

    static void Stand(EditorSession editor, string folder) {
        var view = Descendants(editor.Panel("project"))
                .OfType<TreeView>()
                .FirstOrDefault(candidate => candidate.HasClass("browser-folders"))
            ?? throw editor.Fail("the browser has no folder tree");

        var node = EditorSession.NodesOf(view).FirstOrDefault(candidate => candidate.Text == folder)
            ?? throw editor.Fail($"the folder tree has no '{folder}'");

        view.Select(node);
        editor.Settle();
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
