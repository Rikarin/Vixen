// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Inspector;
using Vixen.Editor.Inspector.Drawers;
using Vixen.Editor.SceneView;
using Vixen.Editor.Testing;
using Vixen.Rendering.Ecs;
using Vixen.Ui;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Dragging an asset out of the browser and onto the field that should name it.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Doc 20's Content row: "drag into an inspector field ⛔", and three separate things
///         had to be true before it could be anything else.</b> The member had to be drawable —
///         <c>AssetDrawer</c> answered for <c>AssetId</c> and every reference a scene actually stores
///         is an <c>AssetReference</c>, so <c>MeshRenderable.Mesh</c> was grey text. The member had to
///         know what it takes — no runtime component can carry the editor's <c>[AssetPicker]</c>, so
///         <c>Vixen.Core</c>'s <c>[AssetType]</c> is what says it. And the filter had to work at all:
///         it compared against <c>"texture"</c> where the database holds <c>"TextureImporter"</c>,
///         which is a comparison that has never once been true.
///     </para>
///     <para>
///         The gesture is asserted through the real pointer, on real components, in the default
///         arrangement — which is the one where the browser and the inspector are in different
///         groups and this is possible at all.
///     </para>
/// </remarks>
public class DropIntoFieldTests {
    /// <summary>Writes a file into the project and gets its identity back.</summary>
    static AssetId Import(EditorSession editor, string path) {
        var absolute = Path.Combine(editor.ProjectRoot, path.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, "not really an asset");

        editor.Run("assets.refresh");

        if (!editor.Project.Assets.TryGetByPath(path, out var entry)) {
            throw editor.Fail($"'{path}' is not in the index");
        }

        return entry.Guid;
    }

    /// <summary>Selects an entity that carries a mesh renderable, so the inspector draws its fields.</summary>
    static Entity WithRenderable(EditorSession editor) {
        var entity = editor.Scene.Entities.First();

        MeshRenderables.Attach(editor.Scene.World, entity, default);

        editor.Open("inspector");
        editor.Scene.Selection.Set([entity]);
        editor.Settle();

        return entity;
    }

    /// <summary>The row for one member of the selected entity's components.</summary>
    static InspectorRow Field(EditorSession editor, string member) =>
        Descendants(editor.Panel("inspector"))
            .OfType<InspectorRow>()
            .FirstOrDefault(row => row.Field.Member.Name == member)
        ?? throw editor.Fail(
            $"the inspector has no '{member}' row. Showing: "
            + string.Join(
                ", ",
                Descendants(editor.Panel("inspector")).OfType<InspectorRow>().Select(row => row.Field.Member.Name)
            )
            + "."
        );

    static AssetReference MeshOf(EditorSession editor, Entity entity) =>
        MeshRenderables.TryGet(editor.Scene.World, entity, out var renderable)
            ? renderable.Mesh
            : throw editor.Fail("the entity lost its mesh renderable");

    /// <summary>Drags an asset's row onto a field, as one gesture.</summary>
    /// <remarks>
    ///     ⚠ <b>No click first, because the click is what used to make this impossible.</b> Pressing
    ///     a row selects the asset and a selected asset takes the inspector, so a test that clicked
    ///     and then dragged would be dragging onto a field that no longer exists — which is exactly
    ///     what a person does and exactly what failed. The press inside <c>DragTo</c> carries the row
    ///     it lands on, and the hand-over waits for the button to come up.
    /// </remarks>
    static void DragOnto(EditorSession editor, string row, Func<UiElement> target) {
        editor.Open("project");
        editor.ExpandAll(editor.Assets);

        var from = Centre(editor.Row(editor.Assets, row));
        var to = Centre(target());

        editor.Ui.At(from.X, from.Y).DragTo(to.X, to.Y);
        editor.Settle();
    }

    static (float X, float Y) Centre(UiElement element) =>
        (element.Bounds.X + (element.Bounds.Width * 0.5f), element.Bounds.Y + (element.Bounds.Height * 0.5f));

    /// <summary>
    ///     ⚠ Without an entry for <c>AssetReference</c> in the registry, every mesh and every material
    ///     member in the editor fell through to the read-only last resort — so the two most-used asset
    ///     fields there are were text nobody could change, and no drag could have landed in one.
    /// </summary>
    [Fact]
    public void A_reference_member_is_an_asset_field_rather_than_grey_text() {
        using var editor = EditorSession.Start();

        WithRenderable(editor);

        var mesh = Field(editor, "Mesh");

        Assert.IsType<AssetDrawer>(mesh.Drawer);
        Assert.True(mesh.Field.CanWrite);
        Assert.Equal(typeof(AssetReference), mesh.Field.Member.MemberType);
    }

    /// <summary>
    ///     ⚠ <c>[AssetType]</c> is <c>Vixen.Core</c>'s and not the editor's, which is the only reason
    ///     a component in <c>Vixen.Rendering</c> can carry one at all — and the only reason a game's
    ///     own component can say what its fields take.
    /// </summary>
    [Fact]
    public void A_runtime_component_says_what_its_fields_take_without_an_editor_attribute() {
        Assert.Null(InspectorRegistry.Find(typeof(MeshRenderable)));

        var descriptor = ReflectedDescriptor.For(typeof(MeshRenderable))
            ?? throw new InvalidOperationException("MeshRenderable has no reflected descriptor");

        var mesh = descriptor.Members.Single(member => member.Name == "Mesh");
        var material = descriptor.Members.Single(member => member.Name == "Material");

        Assert.Equal("MeshData", mesh.AssetType?.Name);
        Assert.Equal("Material", material.AssetType?.Name);

        // ⚠ And the two differ about null, which is the point of carrying it per member. A mesh
        // renderable with no mesh draws nothing; one with no material draws in the renderer's
        // default, which that member's own remarks call a usable value rather than a mistake.
        Assert.False(mesh.AllowNull);
        Assert.True(material.AllowNull);
    }

    [Fact]
    public void Dropping_a_model_on_a_mesh_field_assigns_it() {
        using var editor = EditorSession.Start();

        editor.Step("import a model");

        var rock = Import(editor, "Assets/Models/rock.fbx");
        var entity = WithRenderable(editor);

        Assert.True(MeshOf(editor, entity).IsNull);

        editor.Step("drag it onto the mesh field");
        DragOnto(editor, "rock.fbx", () => Field(editor, "Mesh").Editor);

        Assert.Equal(rock, MeshOf(editor, entity).Asset);

        // ⚠ The main object rather than a sub-asset. Which part of a model an entity draws is its own
        // gesture; a drag of the file means the file.
        Assert.True(MeshOf(editor, entity).SubAsset.IsMain);
    }

    /// <summary>
    ///     ⚠ <b>Through the field rather than into the member, which is what makes it one Ctrl+Z.</b>
    ///     A component is read as a box and written back whole, so a drop that wrote the member
    ///     directly would change a copy nobody can see and leave the entity alone.
    /// </summary>
    [Fact]
    public void A_drop_into_a_field_is_one_undo_step() {
        using var editor = EditorSession.Start();

        Import(editor, "Assets/Models/rock.fbx");

        var entity = WithRenderable(editor);
        var before = editor.Scene.Stack.CanUndo.Value;

        DragOnto(editor, "rock.fbx", () => Field(editor, "Mesh").Editor);

        Assert.False(MeshOf(editor, entity).IsNull);
        Assert.True(editor.Scene.Stack.CanUndo.Value);

        editor.Run("edit.undo");
        editor.Settle();

        Assert.True(MeshOf(editor, entity).IsNull);
        Assert.Equal(before, editor.Scene.Stack.CanUndo.Value);
    }

    /// <summary>
    ///     ⚠ <b>The whole point of the type filter, and the case that never worked.</b> A field that
    ///     took whatever was dropped on it would let a texture be a mesh, and the scene would then
    ///     fail to resolve a reference that looks perfectly well-formed.
    /// </summary>
    [Fact]
    public void A_field_refuses_an_asset_of_the_wrong_kind() {
        using var editor = EditorSession.Start();

        Import(editor, "Assets/Textures/crate.png");

        var entity = WithRenderable(editor);

        DragOnto(editor, "crate.png", () => Field(editor, "Mesh").Editor);

        Assert.True(MeshOf(editor, entity).IsNull);

        // ⚠ And it is still *handled*: falling through to the scene would spawn an entity in the
        // middle of the level for a drop the user aimed at a field, which is a worse outcome than
        // the refusal and one they then have to undo.
        Assert.DoesNotContain(
            editor.Scene.Entities,
            candidate => AssetInstances.TryGet(editor.Scene.World, candidate, out _)
        );
    }

    /// <summary>
    ///     A material field beside a mesh field takes the material and not the model, which is the
    ///     assertion that the filter is per member rather than per panel.
    /// </summary>
    [Fact]
    public void The_neighbouring_field_takes_a_different_kind() {
        using var editor = EditorSession.Start();

        Import(editor, "Assets/Models/rock.fbx");

        var stone = Import(editor, "Assets/Materials/stone.vxmat");
        var entity = WithRenderable(editor);

        DragOnto(editor, "rock.fbx", () => Field(editor, "Material").Editor);

        Assert.True(MaterialOf(editor, entity).IsNull);

        DragOnto(editor, "stone.vxmat", () => Field(editor, "Material").Editor);

        Assert.Equal(stone, MaterialOf(editor, entity).Asset);
    }

    /// <summary>
    ///     ⚠ <b>Refusal has to be visible while the pointer is still down.</b> A field that lit up
    ///     identically for a texture it will not take, then did nothing on release, is the
    ///     interaction people repeat three times before concluding the editor is broken.
    /// </summary>
    [Fact]
    public void The_field_under_the_pointer_says_whether_it_would_take_the_drag() {
        using var editor = EditorSession.Start();

        Import(editor, "Assets/Models/rock.fbx");
        Import(editor, "Assets/Textures/crate.png");

        var entity = WithRenderable(editor);

        Assert.Equal(AssetDrawer.DropTargetClass, HoverClass(editor, entity, "rock.fbx", "Mesh"));
        Assert.Equal(AssetDrawer.DropRejectedClass, HoverClass(editor, entity, "crate.png", "Mesh"));
    }

    /// <summary>
    ///     ⚠ <b>The outline has to come off, and a release is not the only way a drag ends.</b> A
    ///     field left outlined after the pointer moved away is one that stays outlined for the rest of
    ///     the session — nothing else ever touches that class.
    /// </summary>
    [Fact]
    public void Moving_off_the_field_takes_the_outline_with_it() {
        using var editor = EditorSession.Start();

        Import(editor, "Assets/Models/rock.fbx");
        WithRenderable(editor);

        editor.Open("project");
        editor.ExpandAll(editor.Assets);

        var from = Centre(editor.Row(editor.Assets, "rock.fbx"));
        var over = Centre(Field(editor, "Mesh").Editor);

        editor.Ui.MovePointer(from.X, from.Y);
        editor.Ui.PressPointer();
        editor.Settle();

        // Twice, so the pointer has passed the slop threshold before the move that matters: the
        // first is what makes it a drag at all, and the second is the one over the field.
        editor.Ui.MovePointer(over.X, over.Y);
        editor.Settle();
        editor.Ui.MovePointer(over.X, over.Y);
        editor.Settle();

        Assert.True(Field(editor, "Mesh").Editor.HasClass(AssetDrawer.DropTargetClass));

        // Back over the browser, which is a drag going nowhere as far as the inspector is concerned.
        editor.Ui.MovePointer(from.X, from.Y);
        editor.Settle();

        Assert.False(Field(editor, "Mesh").Editor.HasClass(AssetDrawer.DropTargetClass));

        editor.Ui.ReleasePointer();
        editor.Settle();
    }

    /// <summary>
    ///     ⚠ <b>A member names one asset, so dropping four is refused rather than resolved.</b>
    ///     Taking "the first" is a coin toss over what first means — selection order is not what the
    ///     user is looking at — and the wrong one of four is worse than none.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Driven through <c>AssetFieldDrop</c> rather than through the pointer, and the reason
    ///     is worth writing down.</b> Selecting a second asset needs a Ctrl+click, a click hands the
    ///     inspector to the assets, and the field is then gone — so the only way to aim a multiple
    ///     drop at a field is with the inspector locked. That is a real path and this is the decision
    ///     it depends on; driving the lock as well would be a test of the lock.
    /// </remarks>
    [Fact]
    public void Dropping_several_assets_on_one_field_assigns_none_of_them() {
        using var editor = EditorSession.Start();

        var rock = Import(editor, "Assets/Models/rock.fbx");
        var tree = Import(editor, "Assets/Models/tree.fbx");
        var entity = WithRenderable(editor);

        var over = Centre(Field(editor, "Mesh").Editor);
        var drop = new AssetFieldDrop(new AssetPicker(editor.Project, editor.Shell.Dialogs));

        var landed = drop.Drop(editor.Document.Root, [rock, tree], over.X, over.Y);

        Assert.Equal(AssetFieldDropOutcome.TooMany, landed.Outcome);
        Assert.True(MeshOf(editor, entity).IsNull);

        // ⚠ And it is still the field's drop rather than the scene's. Two models dropped on a mesh
        // field must not become two entities in the middle of the level.
        Assert.True(landed.IsHandled);

        // One of them alone is the same gesture and does land, which is what makes the refusal above
        // about the count rather than about anything else.
        Assert.Equal(
            AssetFieldDropOutcome.Assigned,
            drop.Drop(editor.Document.Root, [rock], over.X, over.Y).Outcome
        );

        Assert.Equal(rock, MeshOf(editor, entity).Asset);
    }

    /// <summary>
    ///     ⚠ <b>The picker and the drop have to agree, because they are two ways to do one thing.</b>
    ///     This is also the assertion the filter never had: it compared a runtime type's name against
    ///     <c>"texture"</c> where a <c>.meta</c> file records <c>"TextureImporter"</c>, so every typed
    ///     picker in the editor opened onto an empty list in a project full of assets.
    /// </summary>
    [Fact]
    public void The_picker_offers_only_what_the_member_names() {
        using var editor = EditorSession.Start();

        var rock = Import(editor, "Assets/Models/rock.fbx");
        var crate = Import(editor, "Assets/Textures/crate.png");

        WithRenderable(editor);

        var picker = new AssetPicker(editor.Project, editor.Shell.Dialogs);
        var mesh = Field(editor, "Mesh").Field.Member;

        Assert.True(picker.Accepts(mesh, rock));
        Assert.False(picker.Accepts(mesh, crate));

        // A folder has an identity and can therefore be dragged, and there is no member anywhere
        // that means "a folder".
        Assert.True(editor.Project.Assets.TryGetByPath("Assets/Models", out var folder));
        Assert.False(picker.Accepts(mesh, folder.Guid));
    }

    static AssetReference MaterialOf(EditorSession editor, Entity entity) =>
        MeshRenderables.TryGet(editor.Scene.World, entity, out var renderable)
            ? renderable.Material
            : throw editor.Fail("the entity lost its mesh renderable");

    /// <summary>Which class a field has while a drag hovers over it, if any.</summary>
    /// <remarks>
    ///     ⚠ <b>The drag is taken back to the browser before the button comes up.</b> Releasing over
    ///     the field would <i>assign</i> it, so a second probe in the same session would be asking
    ///     about a field the first one had already filled in — and the answer would be about the
    ///     wrong state rather than about the hover.
    /// </remarks>
    static string? HoverClass(EditorSession editor, Entity entity, string asset, string member) {
        // ⚠ Re-selected each time, because the previous probe ended with a release over the browser
        // — a click, as far as the editor is concerned, and a click on an asset is what hands the
        // inspector to that asset. Only a drop into a field swallows the change.
        editor.Scene.Selection.Set([entity]);
        editor.Settle();

        editor.Open("project");
        editor.ExpandAll(editor.Assets);

        var from = Centre(editor.Row(editor.Assets, asset));

        editor.Ui.MovePointer(from.X, from.Y);
        editor.Ui.PressPointer();
        editor.Settle();

        // In two steps, so the pointer has passed the slop threshold before the one that matters:
        // the first move is what makes it a drag at all, and the second is the one over the field.
        Hover(Centre(Field(editor, member).Editor));
        Hover(Centre(Field(editor, member).Editor));

        var field = Field(editor, member).Editor;

        var held = field.HasClass(AssetDrawer.DropTargetClass)
            ? AssetDrawer.DropTargetClass
            : field.HasClass(AssetDrawer.DropRejectedClass)
                ? AssetDrawer.DropRejectedClass
                : null;

        editor.Ui.MovePointer(from.X, from.Y);
        editor.Settle();

        editor.Ui.ReleasePointer();
        editor.Settle();

        return held;

        void Hover((float X, float Y) point) {
            editor.Ui.MovePointer(point.X, point.Y);
            editor.Settle();
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
