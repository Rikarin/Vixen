// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Testing;
using Vixen.Ui;
using Vixen.Ui.Controls.Advanced;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>What a click in a panel puts in the inspector.</summary>
/// <remarks>
///     <para>
///         <b>The join, from the outside.</b> Every piece underneath this has tests of its own — the
///         tree selects, the selection is a signal, the inspector builds rows from a descriptor — and
///         all of them passed while clicking a file in the Project panel did nothing at all, because
///         what was missing was the line between two of them. That is what these press on: a real
///         pointer event into a real arrangement, and an assertion about the far end.
///     </para>
/// </remarks>
public class SelectionTests {
    [Fact]
    public void Clicking_an_entity_in_the_hierarchy_shows_it_in_the_inspector() {
        using var fixture = EditorSession.Start();

        fixture.Open("hierarchy");
        fixture.ClickRow(fixture.Hierarchy, "Main Camera");

        var target = Assert.IsType<SceneEntity>(Assert.Single(fixture.Inspector.Targets));

        Assert.Equal("Main Camera", target.Name);
        Assert.Equal("Main Camera", fixture.Shell.Status);

        // The rows themselves, not only the target: an inspector handed an object it has no
        // descriptor for shows an empty state and no rows at all, which from the far end looks
        // exactly like a selection that never arrived.
        Assert.Equal(["Name", "Position", "Rotation", "Scale"], fixture.Inspector.Rows.Select(row => row.Field.Member.Name));
    }

    [Fact]
    public void Clicking_an_asset_in_the_project_browser_shows_it_in_the_inspector() {
        using var fixture = EditorSession.Start();

        fixture.Open("project");
        fixture.ExpandAll(fixture.Assets);
        fixture.ClickRow(fixture.Assets, "Main.vxscene");

        var target = Assert.IsType<ProjectAsset>(Assert.Single(fixture.Inspector.Targets));

        Assert.Equal("Main.vxscene", target.Name);
        Assert.Equal("Assets/Scenes/Main.vxscene", target.Path);
        Assert.False(target.IsFolder);

        // The GUID is what everything else refers to the file by, so a blank one is the failure this
        // row exists to make visible.
        Assert.NotEmpty(target.Identity);
        Assert.Equal("Main.vxscene", fixture.Shell.Status);
        Assert.NotEmpty(fixture.Inspector.Rows);
    }

    /// <summary>
    ///     Two panels with a selection each, one inspector, and the rule that decides which of them
    ///     it is showing.
    /// </summary>
    [Fact]
    public void The_panel_that_was_clicked_in_wins_and_the_other_is_deselected() {
        using var fixture = EditorSession.Start();

        fixture.Open("hierarchy");
        fixture.ClickRow(fixture.Hierarchy, "Ground");

        Assert.Single(fixture.Scene.Selection);

        fixture.Open("project");
        fixture.ExpandAll(fixture.Assets);
        fixture.ClickRow(fixture.Assets, "Scenes");

        Assert.IsType<ProjectAsset>(Assert.Single(fixture.Inspector.Targets));

        // ⚠ Both ends of it. Dropping the document's selection while the tree still draws the row
        // highlighted is the half-done version, and the one that makes the next Delete look like it
        // acted on something nobody had selected.
        Assert.Empty(fixture.Scene.Selection);
        Assert.Empty(fixture.Hierarchy.Selection);

        fixture.Open("hierarchy");
        fixture.ClickRow(fixture.Hierarchy, "Ground");

        var entity = Assert.IsType<SceneEntity>(Assert.Single(fixture.Inspector.Targets));

        Assert.Equal("Ground", entity.Name);
        Assert.Empty(fixture.Project.Selection);
    }

    /// <summary>
    ///     A panel's factory runs again every time it is reopened, so what is selected has to be
    ///     pushed into the new rows rather than waited for.
    /// </summary>
    [Fact]
    public void Reopening_the_inspector_shows_what_is_already_selected() {
        using var fixture = EditorSession.Start();

        fixture.Open("project");
        fixture.ExpandAll(fixture.Assets);
        fixture.ClickRow(fixture.Assets, "Scenes");

        Assert.IsType<ProjectAsset>(Assert.Single(fixture.Inspector.Targets));

        fixture.Shell.Workspace.Close("inspector");
        fixture.Frames(2);

        fixture.Open("inspector");

        var target = Assert.IsType<ProjectAsset>(Assert.Single(fixture.Inspector.Targets));

        Assert.Equal("Scenes", target.Name);
    }

    /// <summary>
    ///     A scene opened as an asset has a hierarchy of its own, a selection of its own and an undo
    ///     stack of its own, and clicking in it has to reach the inspector like anything else.
    /// </summary>
    /// <remarks>
    ///     The second dead end, and the one that looks most like "the hierarchy is broken": the panel
    ///     is a tree of entities, clicking a row highlights it, and the inspector — which is showing
    ///     the editor's own scene, or nothing — does not move.
    /// </remarks>
    [Fact]
    public void Clicking_in_an_opened_scenes_own_hierarchy_shows_that_scenes_entity() {
        using var fixture = EditorSession.Start();

        fixture.Open("project");
        fixture.ExpandAll(fixture.Assets);

        // ⚠ Selected and then opened through the command, because a double-click in the browser
        // begins a rename now rather than opening — see `TreeView.RenameOnActivate`, which is the
        // gesture the outliner already had. Opening is Enter, the context menu's Open, and a
        // double-click in the *grid*, where a tile is a document rather than a name edited in place.
        fixture.ClickRow(fixture.Assets, "Main.vxscene");
        fixture.Run("assets.open");

        var opened = OpenedAssetTree(fixture);

        Assert.NotEmpty(opened.Rows);
        fixture.Click(opened.Rows.First(row => row.Node is not null && !row.HasClass("parked")));

        Assert.IsType<SceneEntity>(Assert.Single(fixture.Inspector.Targets));

        // ⚠ And against that document's stack, not the editor's own scene's. An edit recorded on the
        // wrong stack is undone by a Ctrl+Z aimed at something else entirely.
        Assert.NotSame(fixture.Scene, fixture.Inspector.EditedDocument);
    }

    /// <summary>
    ///     A row the database has no identity for is not something that can be selected, which the
    ///     browser has always known and nothing downstream could see.
    /// </summary>
    /// <remarks>
    ///     The synthesised root is the case: it is a folder in the tree because the tree needs one,
    ///     and it has no sidecar and therefore no GUID. Putting <c>AssetId.Empty</c> in the selection
    ///     for it would make every such folder select the same nothing and look like one asset.
    /// </remarks>
    [Fact]
    public void A_row_with_no_guid_behind_it_is_not_a_selection() {
        using var fixture = EditorSession.Start();

        fixture.Open("hierarchy");
        fixture.ClickRow(fixture.Hierarchy, "Ground");

        fixture.Open("project");
        fixture.ClickRow(fixture.Assets, "Assets");

        var target = Assert.IsType<SceneEntity>(Assert.Single(fixture.Inspector.Targets));

        Assert.Equal("Ground", target.Name);
    }

    /// <summary>
    ///     ⚠ <b>An opened scene's hierarchy holds that scene's entities and not the editor's own.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The join this file is about, from the far end, and it was broken in a way that read as
    ///         a drawing fault. Opening a <c>.vxscene</c> as an asset builds a second
    ///         <c>SceneDocument</c> over the editor's own world — deliberately, so that an entity
    ///         handle means one thing across the application — and each document built a
    ///         <c>SceneManager</c> whose scene-id counter started at one. Both claimed scene 1, and
    ///         <c>SceneDocument.Entities</c> filters by exactly that tag, so each document returned
    ///         the other's entities alongside its own.
    ///     </para>
    ///     <para>
    ///         It presented as eight rows where there are four: every entity twice, once under the
    ///         name the file gave it and once as <c>Entity 4</c>, because a document knows only the
    ///         names it loaded itself. <c>SceneManager</c> keys the counter on the world now. This
    ///         asserts on the names and not only the count, because a count alone is satisfied by a
    ///         tree showing the right number of the wrong rows.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_opened_scenes_hierarchy_holds_its_own_entities_once_each() {
        using var fixture = EditorSession.Start();

        fixture.Open("project");
        fixture.ExpandAll(fixture.Assets);

        fixture.ClickRow(fixture.Assets, "Main.vxscene");
        fixture.Run("assets.open");

        var names = OpenedAssetTree(fixture)
            .Rows
            .Select(row => row.Node?.Text ?? "")
            .ToList();

        Assert.NotEmpty(names);

        // Nothing unnamed: `SceneDocument.NameOf` falls back to "Entity <id>" for an entity this
        // document did not load, which is precisely what a foreign scene's entities looked like.
        Assert.DoesNotContain(names, name => name.StartsWith("Entity ", StringComparison.Ordinal));

        // And each of its own exactly once.
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>The tree inside the panel an opened asset was given, whichever GUID it was named after.</summary>
    /// <remarks>
    ///     A panel per document, registered on demand and named after the asset's GUID — so there is
    ///     no fixed id to ask the session for, and the prefix is the only handle there is.
    /// </remarks>
    static TreeView OpenedAssetTree(EditorSession fixture) =>
        fixture.Panels
            .Where(panel => panel.Id.StartsWith("asset.", StringComparison.Ordinal))
            .Select(Find<TreeView>)
            .FirstOrDefault(tree => tree is not null)
        ?? throw new InvalidOperationException("no asset panel with a tree in it is open");

    static T? Find<T>(UiElement element) where T : UiElement {
        if (element is T match) {
            return match;
        }

        foreach (var child in element.Children) {
            if (Find<T>(child) is { } found) {
                return found;
            }
        }

        return null;
    }
}
