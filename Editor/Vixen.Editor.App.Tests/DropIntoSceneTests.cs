// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Core;
using Vixen.Editor.SceneView;
using Vixen.Editor.Testing;
using Vixen.Ui;
using Vixen.Ui.Controls.Advanced;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Dragging an asset out of the browser and into the scene.</summary>
/// <remarks>
///     ⚠ <b>What lands is a reference, not a renderer.</b> Nothing turns an asset into geometry yet,
///     so the crate does not appear in the viewport — and everything else is real: the entity is
///     named after the asset, the reference is authored and saved, the inspector's asset field shows
///     it, and <c>ReferenceIndex</c> counts it so deleting the asset warns about the scene.
/// </remarks>
public class DropIntoSceneTests {
    static AssetId Import(EditorSession editor, string path) {
        var absolute = Path.Combine(editor.ProjectRoot, path.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, "not really a png");

        editor.Run("assets.refresh");

        if (!editor.Project.Assets.TryGetByPath(path, out var entry)) {
            throw editor.Fail($"'{path}' is not in the index");
        }

        return entry.Guid;
    }

    /// <summary>Drags the row for an asset onto the middle of a panel.</summary>
    static void DragOnto(EditorSession editor, string row, string panel) {
        editor.Open("project");
        editor.ExpandAll(editor.Assets);
        editor.ClickRow(editor.Assets, row);

        var source = editor.Row(editor.Assets, row);
        var target = editor.Panel(panel);

        var from = Centre(source);
        var to = Centre(target);

        editor.Ui.At(from.X, from.Y).DragTo(to.X, to.Y);
        editor.Settle();
    }

    static (float X, float Y) Centre(UiElement element) =>
        (element.Bounds.X + (element.Bounds.Width * 0.5f), element.Bounds.Y + (element.Bounds.Height * 0.5f));

    static IReadOnlyList<Entity> Instances(EditorSession editor) =>
        [.. editor.Scene.Entities.Where(entity => AssetInstances.TryGet(editor.Scene.World, entity, out _))];

    [Fact]
    public void Dropping_an_asset_on_the_viewport_makes_an_entity_that_references_it() {
        using var editor = EditorSession.Start();

        editor.Step("import an asset");

        var crate = Import(editor, "Assets/Textures/crate.png");

        editor.Step("drag it into the viewport");
        editor.Open("scene");
        DragOnto(editor, "crate.png", "scene");

        var entity = Assert.Single(Instances(editor));

        Assert.True(AssetInstances.TryGet(editor.Scene.World, entity, out var referenced));
        Assert.Equal(crate, referenced);

        // Named after the asset without its extension, which is what every editor does and what
        // makes an outliner of dropped assets readable.
        Assert.Equal("crate", editor.Scene.NameOf(entity));

        // And selected, so the next thing done lands on what was just made.
        Assert.Equal(entity, Assert.Single(editor.Scene.Selection));
    }

    /// <summary>
    ///     The outliner is the scene too — one is what it looks like and the other is what is in it.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The arrangement has to be changed first, and that is not the test cheating.</b> The
    ///     default layout stacks the browser and the outliner as two tabs of one group, so only one
    ///     of them has a size at a time — dragging between them is impossible by construction, which
    ///     is why the viewport is the gesture people actually use. A layout that puts them in
    ///     different groups is the one where this branch means anything.
    /// </remarks>
    [Fact]
    public void Dropping_it_on_the_outliner_works_as_well() {
        using var editor = EditorSession.Start();

        Import(editor, "Assets/Textures/crate.png");

        editor.Shell.RegisterLayout(
            "Apart",
            new Vixen.Editor.Ui.StringId("editor.layout.apart", "Apart"),
            () => Vixen.Editor.Ui.LayoutPresets.Standard(["hierarchy"], ["scene"], ["project"])
        );

        editor.Shell.Workspace.Apply("Apart");
        editor.Open("hierarchy");
        editor.Open("project");

        DragOnto(editor, "crate.png", "hierarchy");

        Assert.Single(Instances(editor));
    }

    /// <summary>
    ///     ⚠ A drag that ends over the console or the inspector means the user changed their mind,
    ///     and an editor that spawned an entity for it is one people learn to drag carefully in.
    /// </summary>
    [Fact]
    public void Dropping_it_anywhere_else_does_nothing() {
        using var editor = EditorSession.Start();

        Import(editor, "Assets/Textures/crate.png");

        editor.Open("console");
        DragOnto(editor, "crate.png", "console");

        Assert.Empty(Instances(editor));
    }

    [Fact]
    public void A_drop_is_one_undo_step() {
        using var editor = EditorSession.Start();

        Import(editor, "Assets/Textures/crate.png");

        editor.Open("scene");
        DragOnto(editor, "crate.png", "scene");

        Assert.Single(Instances(editor));

        editor.Run("edit.undo");

        Assert.Empty(Instances(editor));
    }

    /// <summary>
    ///     ⚠ <b>The half that makes the reference worth authoring.</b> A scene that referenced an
    ///     asset the index could not see is a scene the editor would offer to delete the asset out
    ///     from under — so the file has to write it in <c>vx:</c> form, which is what the index
    ///     scans for.
    /// </summary>
    [Fact]
    public void The_reference_survives_a_restart_and_the_index_counts_it() {
        using var editor = EditorSession.Start();

        var crate = Import(editor, "Assets/Textures/crate.png");

        editor.Open("scene");
        DragOnto(editor, "crate.png", "scene");

        editor.Step("save and reopen").Run("file.save");
        editor.Restart();

        var entity = Assert.Single(Instances(editor));

        Assert.True(AssetInstances.TryGet(editor.Scene.World, entity, out var referenced));
        Assert.Equal(crate, referenced);

        // ⚠ And the reverse index sees it, which is the assertion that the file wrote a reference
        // rather than a bare id. Deleting the texture now warns about the scene.
        editor.Run("assets.refresh");

        Assert.NotEmpty(AssetOperations.Breakage(editor.Project, [crate]));
    }

    /// <summary>
    ///     Dropping four things is one gesture, so it is one undo — and the whole selection travels,
    ///     which is the rule every drag in this editor follows.
    /// </summary>
    [Fact]
    public void The_whole_selection_is_dropped_and_is_still_one_step() {
        using var editor = EditorSession.Start();

        Import(editor, "Assets/Textures/crate.png");
        Import(editor, "Assets/Textures/barrel.png");

        editor.Open("scene");
        editor.Open("project");
        editor.ExpandAll(editor.Assets);

        editor.ClickRow(editor.Assets, "crate.png");
        editor.ClickRow(editor.Assets, "barrel.png", ModifierKeys.Control);

        Assert.Equal(2, editor.Project.Selection.Count);

        var source = editor.Row(editor.Assets, "barrel.png");
        var target = editor.Panel("scene");

        var from = Centre(source);
        var to = Centre(target);

        editor.Ui.At(from.X, from.Y).DragTo(to.X, to.Y);
        editor.Settle();

        Assert.Equal(2, Instances(editor).Count);

        editor.Run("edit.undo");

        Assert.Empty(Instances(editor));
    }

    /// <summary>A folder is not something the scene can hold, so dropping one makes nothing.</summary>
    [Fact]
    public void Dropping_a_folder_makes_nothing() {
        using var editor = EditorSession.Start();

        Import(editor, "Assets/Textures/crate.png");

        editor.Open("scene");
        DragOnto(editor, "Textures", "scene");

        Assert.Empty(Instances(editor));
    }
}
