// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;
using Vixen.Editor.Inspector;
using Vixen.Editor.Testing;
using Vixen.Ui.Controls;
using Xunit;
using EditorPrefab = Vixen.Editor.AssetEditors.Prefabs.Prefab;

namespace Vixen.Editor.App.Tests;

/// <summary>The override mark and the Revert item, in the panel a person is looking at.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Nothing assigned <c>InspectorView.Prefab</c> until this landed</b>, so every test over
///         <c>InspectorField</c> or <c>PrefabSource</c> could pass with the panel showing nothing at
///         all — doc 47 § 7's row 6, and the tree's commonest defect. This is the test that fails if
///         the shell stops handing the pairing over, which is the only way that defect comes back.
///     </para>
///     <para>
///         The mark is a class on the row rather than a property, because that is what the theme
///         draws: <c>inspector-row.overridden inspector-label</c> is what makes the label the bright
///         one. ⚠ <b>No committed screenshot</b>, deliberately: a picture of the whole shell would
///         break on any unrelated change to any panel in it, which is a golden that tests everything
///         and therefore nothing. The class is the assertion, and it is the same fact the picture
///         would have carried.
///     </para>
/// </remarks>
public class PrefabOverridePanelTests {
    /// <summary>An edit to an instance marks the row, and Revert unmarks it and puts the value back.</summary>
    [Fact]
    public void An_edit_to_an_instance_marks_the_row_and_revert_puts_it_back() {
        using var editor = EditorSession.Start();

        editor.Step("place an instance of a prefab");

        var turret = ImportPrefab(editor, "Assets/Prefabs/turret.vxprefab");

        Assert.True(
            EditorPrefab.TryPlace(editor.Scene, editor.Project.Assets, turret, Entity.Null, out var root, out _)
        );

        editor.Open("hierarchy");
        editor.Open("inspector");
        editor.ClickRow(editor.Hierarchy, "Turret");
        editor.Settle();

        var name = Row(editor, nameof(SceneEntity.Name));

        // Freshly placed: the instance claims nothing, so nothing is marked.
        Assert.False(name.HasClass("overridden"));

        editor.Step("rename it in the inspector");

        var box = NameBox(editor);

        editor.Document.Focus(box);
        box.Value = "Turret (west gate)";
        editor.Document.Focus(null);
        editor.Settle();

        Assert.Equal("Turret (west gate)", editor.Scene.NameOf(root));

        // The row the person is looking at now says the member is this instance's own, and the claim
        // that says so is in the document, ready to be written into the level.
        Assert.True(name.HasClass("overridden"));
        Assert.True(editor.Scene.Prefabs.IsOverridden(root, nameof(SceneEntity.Name)));

        editor.Step("and revert gives it back to the prefab");

        Assert.True(editor.Inspector.RevertToPrefab(name));
        editor.Settle();

        Assert.Equal("Turret", editor.Scene.NameOf(root));
        Assert.False(name.HasClass("overridden"));
        Assert.Empty(editor.Scene.Prefabs.OverridesOf(root));
    }

    /// <summary>⚠ An entity that came from nowhere is never marked and has nothing to revert to.</summary>
    /// <remarks>
    ///     The seeded scene's own entities are the case: a pairing that answered for them would put a
    ///     mark on every row in the editor, which is the failure mode of getting this wrong in the
    ///     other direction.
    /// </remarks>
    [Fact]
    public void An_ordinary_entity_is_never_marked() {
        using var editor = EditorSession.Start();

        editor.Open("hierarchy");
        editor.Open("inspector");
        editor.ClickRow(editor.Hierarchy, "Directional Light");
        editor.Settle();

        var name = Row(editor, nameof(SceneEntity.Name));
        var box = NameBox(editor);

        editor.Document.Focus(box);
        box.Value = "Key Light";
        editor.Document.Focus(null);
        editor.Settle();

        Assert.False(name.HasClass("overridden"));
        Assert.False(editor.Inspector.RevertToPrefab(name));
    }

    static InspectorRow Row(EditorSession editor, string member) {
        foreach (var row in editor.Inspector.Rows) {
            if (row.Field.Member.Name == member) {
                return row;
            }
        }

        throw editor.Fail($"The inspector has no row for '{member}'.");
    }

    static TextBox NameBox(EditorSession editor) => (TextBox) Row(editor, nameof(SceneEntity.Name)).Editor;

    /// <summary>Writes a one-rooted prefab into the project and hands back its GUID.</summary>
    /// <remarks>
    ///     ⚠ <b>Written without a position</b>, for <c>DropIntoSceneTests</c>'s reason: a
    ///     <c>Vector3</c> in a file built before <c>SceneScalars.Register</c> has run reads back as a
    ///     mapping rather than as a scalar, and this fixture writes its YAML by hand.
    /// </remarks>
    static AssetId ImportPrefab(EditorSession editor, string path) {
        var absolute = Path.Combine(editor.ProjectRoot, path.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);

        File.WriteAllText(
            absolute,
            "version: 1\nname: Turret\nroots:\n"
            + $"  - id: {Guid.NewGuid():N}\n    name: Turret\n    children:\n"
            + $"      - id: {Guid.NewGuid():N}\n        name: Barrel\n"
        );

        editor.Run("assets.refresh");

        if (!editor.Project.Assets.TryGetByPath(path, out var entry)) {
            throw editor.Fail($"'{path}' is not in the index");
        }

        return entry.Guid;
    }
}
