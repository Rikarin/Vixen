// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Core;
using Xunit;

namespace Vixen.Editor.Core.Tests;

/// <summary>The shape a project browser shows, asserted without one.</summary>
public class AssetTreeTests {
    static AssetEntry File(string path) => new(AssetId.New(), path, null, 1, false);

    static AssetEntry Folder(string path) => new(AssetId.New(), path, null, 1, true);

    static IReadOnlyList<string> Names(AssetTreeNode node) => [.. node.Children.Select(child => child.Name)];

    [Fact]
    public void An_empty_project_is_a_root_with_nothing_in_it() {
        var root = AssetTree.Build([]);

        Assert.Equal(AssetTree.RootName, root.Name);
        Assert.True(root.IsFolder);
        Assert.Empty(root.Children);
    }

    [Fact]
    public void Files_hang_off_the_folder_their_path_names() {
        var root = AssetTree.Build([
            Folder("Assets/Scenes"),
            File("Assets/Scenes/Main.vxscene"),
            File("Assets/Readme.txt")
        ]);

        Assert.Equal(["Scenes", "Readme.txt"], Names(root));
        Assert.Equal(["Main.vxscene"], Names(root.Children[0]));
    }

    [Fact]
    public void A_folder_the_database_never_indexed_is_still_a_folder() {
        // What a read-only scan leaves behind: no sidecar for the folder, so no entry for it, while
        // everything inside it is indexed by path. Requiring the entry would drop the whole subtree.
        var root = AssetTree.Build([File("Assets/Textures/Crate.png")]);

        var textures = Assert.Single(root.Children);

        Assert.Equal("Textures", textures.Name);
        Assert.True(textures.IsFolder);
        Assert.False(textures.IsIndexed);
        Assert.Equal(["Crate.png"], Names(textures));
    }

    [Fact]
    public void Missing_folders_are_synthesised_all_the_way_down() {
        var root = AssetTree.Build([File("Assets/Art/Characters/Hero/Body.mesh")]);

        Assert.Equal("Assets/Art", root.Children[0].Path);
        Assert.Equal("Assets/Art/Characters", root.Children[0].Children[0].Path);
        Assert.Equal("Assets/Art/Characters/Hero", root.Children[0].Children[0].Children[0].Path);
    }

    [Fact]
    public void An_indexed_folder_keeps_its_identity() {
        var folder = Folder("Assets/Scenes");
        var root = AssetTree.Build([File("Assets/Scenes/Main.vxscene"), folder]);

        // A folder that has an entry is the ordinary case, and the one the synthesis above must not
        // cost anything: a folder built without its GUID is one nothing can reference, which is how a
        // moved folder stops updating the things pointing at it.
        Assert.Equal(folder.Guid, root.Children[0].Guid);
        Assert.True(root.Children[0].IsIndexed);
    }

    [Fact]
    public void Folders_come_before_files() {
        var root = AssetTree.Build([File("Assets/aaa.txt"), Folder("Assets/zzz")]);

        Assert.Equal(["zzz", "aaa.txt"], Names(root));
    }

    [Fact]
    public void Names_sort_the_way_a_person_reads_them() {
        var root = AssetTree.Build([File("Assets/Zebra.png"), File("Assets/apple.png")]);

        // Ordinal would put every capital first, so a browser sorted that way has `Zebra.png` above
        // `apple.png` and looks broken to everyone who is not thinking about char codes.
        Assert.Equal(["apple.png", "Zebra.png"], Names(root));
    }

    [Fact]
    public void Two_names_differing_only_in_case_still_have_an_order() {
        var first = AssetTree.Build([File("Assets/README"), File("Assets/readme")]);
        var second = AssetTree.Build([File("Assets/readme"), File("Assets/README")]);

        // Ignoring case makes these equal, and equal means the order is whichever the enumeration
        // reached first — which is a dictionary's order, which is not an order.
        Assert.Equal(Names(first), Names(second));
    }

    [Fact]
    public void The_order_does_not_come_from_the_database() {
        List<AssetEntry> entries = [
            File("Assets/c.txt"),
            Folder("Assets/b"),
            File("Assets/a.txt"),
            File("Assets/b/inner.txt")
        ];

        var forwards = AssetTree.Build(entries);
        var backwards = AssetTree.Build([.. Enumerable.Reverse(entries)]);

        Assert.Equal(Names(forwards), Names(backwards));
        Assert.Equal(["b", "a.txt", "c.txt"], Names(forwards));
    }

    [Fact]
    public void Descend_reaches_everything_parents_first() {
        var root = AssetTree.Build([Folder("Assets/Scenes"), File("Assets/Scenes/Main.vxscene")]);

        Assert.Equal(
            [AssetTree.RootName, "Scenes", "Main.vxscene"],
            root.Descend().Select(node => node.Name)
        );
    }

    [Fact]
    public void A_node_is_found_by_its_path() {
        var root = AssetTree.Build([File("Assets/Scenes/Main.vxscene")]);

        Assert.Equal("Main.vxscene", AssetTree.Find(root, "Assets/Scenes/Main.vxscene")?.Name);
        Assert.Equal("Scenes", AssetTree.Find(root, "Assets/Scenes")?.Name);
        Assert.Null(AssetTree.Find(root, "Assets/Nope"));
        Assert.Null(AssetTree.Find(root, ""));
    }

    [Fact]
    public void An_empty_search_changes_nothing() {
        var root = AssetTree.Build([File("Assets/Scenes/Main.vxscene")]);

        Assert.Same(root, AssetTree.Filter(root, null));
        Assert.Same(root, AssetTree.Filter(root, "   "));
    }

    [Fact]
    public void A_search_keeps_the_folders_holding_what_matched() {
        var root = AssetTree.Build([
            File("Assets/Scenes/Main.vxscene"),
            File("Assets/Textures/Crate.png"),
            File("Assets/Textures/Barrel.png")
        ]);

        var found = AssetTree.Filter(root, "crate");
        var textures = Assert.Single(found.Children);

        // `Scenes` is gone because nothing in it matched; `Textures` survives because something did,
        // and is narrowed to what did.
        Assert.Equal("Textures", textures.Name);
        Assert.Equal(["Crate.png"], Names(textures));
    }

    [Fact]
    public void A_matched_folder_keeps_what_is_in_it() {
        var root = AssetTree.Build([File("Assets/Textures/Crate.png"), File("Assets/Textures/Barrel.png")]);

        var found = AssetTree.Filter(root, "textures");

        // Typing a folder's name is navigation, not a search for the folder itself — a result that
        // was a folder with nothing in it would be a folder you cannot open.
        Assert.Equal(["Barrel.png", "Crate.png"], Names(found.Children[0]));
    }

    [Fact]
    public void A_search_that_matches_nothing_leaves_a_root_and_no_rows() {
        var root = AssetTree.Build([File("Assets/Scenes/Main.vxscene")]);

        var found = AssetTree.Filter(root, "nothing here");

        // A root rather than null, so a browser draws an empty tree rather than having to special-
        // case having nothing to draw at all.
        Assert.Equal(AssetTree.RootName, found.Name);
        Assert.Empty(found.Children);
    }

    [Fact]
    public void Filtering_does_not_disturb_the_tree_it_narrowed() {
        var root = AssetTree.Build([File("Assets/Textures/Crate.png"), File("Assets/Scenes/Main.vxscene")]);

        AssetTree.Filter(root, "crate");

        // The records are immutable and narrowing returns new ones, so the browser can keep the whole
        // tree and re-filter per keystroke rather than rescanning the database for each.
        Assert.Equal(2, root.Children.Count);
    }
}
